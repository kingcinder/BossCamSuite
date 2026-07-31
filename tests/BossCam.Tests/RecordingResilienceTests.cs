using System.Diagnostics;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Locks down the PR-R1 reconcile + PR-R4 stall paths of <see cref="RecordingService"/>:
///  - A persisted job whose OS process is gone must reconcile to stopped (StoppedAt set, persisted).
///  - A persisted job whose OS process is still alive must be re-attached (stays running).
///  - A running job whose output directory has not grown within the stall timeout must be
///    stopped, persisted, and broadcast.
/// Uses a real SQLite store on a temp DB and real (short-lived) child processes.
/// </summary>
public sealed class RecordingResilienceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-rec-{Guid.NewGuid():N}.db");
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"bosscam-seg-{Guid.NewGuid():N}");
    private readonly List<Process> _spawned = [];

    private async Task<(SqliteApplicationStore Store, RecordingService Service)> BuildAsync()
    {
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        Directory.CreateDirectory(_outputDir);
        var service = new RecordingService(
            store,
            new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            NullLogger<RecordingService>.Instance);
        return (store, service);
    }

    private Process SpawnSleep()
    {
        var psi = new ProcessStartInfo("sleep", "60")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi)!;
        _spawned.Add(proc);
        return proc;
    }

    private static RecordingJob BuildJob(Guid deviceId, int? processId, string outputDirectory, bool running = true, DateTimeOffset? startedAt = null)
        => new()
        {
            DeviceId = deviceId,
            ProfileId = Guid.NewGuid(),
            SourceUrl = "http://redacted@10.0.0.4/snapshot.jpg",
            OutputDirectory = outputDirectory,
            SegmentPattern = Path.Combine(outputDirectory, "dev_%Y%m%d_%H%M%S.ts"),
            SegmentSeconds = 30,
            IsRunning = running,
            ProcessId = processId,
            Mode = "direct",
            SourceRole = "main",
            // For live-PID tests the start time MUST postdate the spawned process, otherwise
            // RecordingService's PID-reuse guard (StartTime > StartedAt ⇒ recycled PID) would
            // reject the legitimately-surviving process and mark the job stopped instead.
            StartedAt = startedAt ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task ReconcilePersistedJobsAsync_DeadPid_Marks_Job_Stopped()
    {
        var (store, service) = await BuildAsync();
        var deviceId = Guid.NewGuid();
        // int.MaxValue can never be an existing PID → TryGetLiveProcess returns null.
        var job = BuildJob(deviceId, int.MaxValue, _outputDir);
        await store.SaveRecordingJobsAsync([job], CancellationToken.None);

        var reconciled = await service.ReconcilePersistedJobsAsync(CancellationToken.None);

        var result = Assert.Single(reconciled);
        Assert.Equal(job.Id, result.Id);
        Assert.False(result.IsRunning);
        Assert.NotNull(result.StoppedAt);

        // The stopped state must be persisted, not just returned in memory.
        var persisted = await store.GetRecordingJobsAsync(deviceId, CancellationToken.None);
        var persistedJob = Assert.Single(persisted);
        Assert.False(persistedJob.IsRunning);
        Assert.NotNull(persistedJob.StoppedAt);
    }

    [Fact]
    public async Task ReconcilePersistedJobsAsync_LivePid_Reattaches_Job_As_Running()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // 'sleep' availability is POSIX-only; other tests cover the dead-PID path.
        }

        var (store, service) = await BuildAsync();
        var deviceId = Guid.NewGuid();
        using var live = SpawnSleep();
        // StartedAt must postdate the spawned process so the PID-reuse guard accepts it.
        var job = BuildJob(deviceId, live.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
        await store.SaveRecordingJobsAsync([job], CancellationToken.None);

        var reconciled = await service.ReconcilePersistedJobsAsync(CancellationToken.None);

        var result = Assert.Single(reconciled);
        Assert.Equal(job.Id, result.Id);
        Assert.True(result.IsRunning, "a job whose OS process is alive must be re-attached, not stopped");

        // Re-attached into the in-memory table → visible via GetJobsAsync as running.
        var jobs = await service.GetJobsAsync(CancellationToken.None);
        var attached = Assert.Single(jobs, j => j.Id == job.Id);
        Assert.True(attached.IsRunning);
    }

    [Fact]
    public async Task ReconcilePersistedJobsAsync_No_Pid_Marks_Job_Stopped()
    {
        var (store, service) = await BuildAsync();
        var deviceId = Guid.NewGuid();
        var job = BuildJob(deviceId, processId: null, _outputDir);
        await store.SaveRecordingJobsAsync([job], CancellationToken.None);

        var reconciled = await service.ReconcilePersistedJobsAsync(CancellationToken.None);

        var result = Assert.Single(reconciled);
        Assert.False(result.IsRunning);
        Assert.NotNull(result.StoppedAt);
    }

    [Fact]
    public async Task CheckStalledJobsAsync_No_Segment_Growth_Stops_And_Persists()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // needs a real killable child process
        }

        var (store, service) = await BuildAsync();
        var deviceId = Guid.NewGuid();
        using var live = SpawnSleep();

        // A stale segment file: written long before the stall window.
        var staleSegment = Path.Combine(_outputDir, "dev_20260101_000000.ts");
        await File.WriteAllTextAsync(staleSegment, "x");
        File.SetLastWriteTimeUtc(staleSegment, DateTime.UtcNow.AddMinutes(-10));

        // StartedAt must postdate the spawned process so the PID-reuse guard accepts it.
        var job = BuildJob(deviceId, live.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
        await store.SaveRecordingJobsAsync([job], CancellationToken.None);

        // Re-attach the job (same path the lifecycle worker uses after restart) so the
        // watchdog sees it as running before we probe the stall path.
        await service.ReconcilePersistedJobsAsync(CancellationToken.None);

        var stalled = await service.CheckStalledJobsAsync(stallTimeoutSeconds: 5, autoRestart: false, CancellationToken.None);

        var result = Assert.Single(stalled);
        Assert.Equal(job.Id, result.Id);
        Assert.False(result.IsRunning);
        Assert.Contains("Stalled", result.LastError, StringComparison.OrdinalIgnoreCase);

        // Persisted truth updated + process was stopped. Use WaitForExit (not an
        // immediate HasExited check) because the killed PID is observed through a
        // different Process object on Linux and HasExited can lag a tick after SIGKILL.
        var persisted = await store.GetRecordingJobsAsync(deviceId, CancellationToken.None);
        Assert.False(Assert.Single(persisted).IsRunning);
        Assert.True(live.WaitForExit(5000), "stalled pipeline process should have been stopped");
    }

    [Fact]
    public async Task CheckStalledJobsAsync_Fresh_Segment_Does_Not_Stall()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var (store, service) = await BuildAsync();
        var deviceId = Guid.NewGuid();
        using var live = SpawnSleep();

        // Fresh segment: written within the stall window.
        var freshSegment = Path.Combine(_outputDir, "dev_20260101_000000.ts");
        await File.WriteAllTextAsync(freshSegment, "x");
        File.SetLastWriteTimeUtc(freshSegment, DateTime.UtcNow.AddSeconds(-1));

        // StartedAt must postdate the spawned process so the PID-reuse guard accepts it.
        var job = BuildJob(deviceId, live.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
        await store.SaveRecordingJobsAsync([job], CancellationToken.None);
        await service.ReconcilePersistedJobsAsync(CancellationToken.None);

        var stalled = await service.CheckStalledJobsAsync(stallTimeoutSeconds: 60, autoRestart: false, CancellationToken.None);

        Assert.Empty(stalled);
        Assert.False(live.HasExited);
    }

    [Fact]
    public async Task CheckStalledJobsAsync_Disabled_When_Timeout_Zero()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var (store, service) = await BuildAsync();
        var deviceId = Guid.NewGuid();
        using var live = SpawnSleep();

        var staleSegment = Path.Combine(_outputDir, "dev_20260101_000000.ts");
        await File.WriteAllTextAsync(staleSegment, "x");
        File.SetLastWriteTimeUtc(staleSegment, DateTime.UtcNow.AddMinutes(-10));

        // StartedAt must postdate the spawned process so the PID-reuse guard accepts it.
        var job = BuildJob(deviceId, live.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
        await store.SaveRecordingJobsAsync([job], CancellationToken.None);
        await service.ReconcilePersistedJobsAsync(CancellationToken.None);

        var stalled = await service.CheckStalledJobsAsync(stallTimeoutSeconds: 0, autoRestart: false, CancellationToken.None);

        Assert.Empty(stalled);
        Assert.False(live.HasExited);
    }

    public void Dispose()
    {
        foreach (var proc in _spawned)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
            }
            catch
            {
                // best-effort cleanup
            }
            proc.Dispose();
        }

        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, true); } catch { }
    }
}
