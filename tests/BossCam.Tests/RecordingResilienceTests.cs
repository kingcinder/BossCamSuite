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
            new HttpClientFactoryMock(), // reconcile/stall tests never probe snapshot URLs
            NullLogger<RecordingService>.Instance,
            new ApplicationStoreRecordingStore(store),
            new RecordingProcessSupervisor());
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
    public async Task StartAsync_ExplicitSnapshot_Does_Not_Query_Transport_Sources()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "explicit-snapshot",
            IpAddress = "192.0.2.1",
            Port = 9,
            DeviceType = "IPC"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new CountingVideoAdapter();
        var broker = new TransportBroker([adapter], store, null, NullLogger<TransportBroker>.Instance);
        var fakeFfmpeg = Path.Combine(_outputDir, $"fake-ffmpeg-{Guid.NewGuid():N}.sh");
        Directory.CreateDirectory(_outputDir);
        await File.WriteAllTextAsync(fakeFfmpeg, "#!/usr/bin/env bash\nsleep 30\n");
        using (var chmod = Process.Start("chmod", $"+x {fakeFfmpeg}"))
        {
            chmod?.WaitForExit(2000);
        }

        var previousFfmpeg = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", fakeFfmpeg);
        RecordingService? service = null;
        try
        {
            service = new RecordingService(
                store,
                broker,
                new TestRecordingPipelineResolver(),
                NullBossCamEventBroadcaster.Instance,
                new HttpClientFactoryMock(),
                NullLogger<RecordingService>.Instance,
                new ApplicationStoreRecordingStore(store),
                new RecordingProcessSupervisor());

            var job = await service.StartAsync(new RecordingStartRequest
            {
                DeviceId = device.Id,
                SourceUrl = "snapshot"
            }, CancellationToken.None);

            Assert.Equal(0, adapter.GetSourcesCalls);
            await service.StopAsync(job.Id, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", previousFfmpeg);
        }
    }

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
    public async Task CheckStalledJobsAsync_AutoRestart_Suspends_After_Max_Consecutive_Restarts()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // needs a real killable child process
        }

        // NOTE: the capped options must be handed to the SERVICE, not just the store —
        // RecordingService's optional IOptions defaults to RecordingMaxConsecutiveRestarts=3
        // (the production value), so a cap of 1 only applies when it is injected here.
        var runtimeOptions = new BossCamRuntimeOptions
        {
            DatabasePath = _dbPath,
            RecordingMaxConsecutiveRestarts = 1
        };
        var store = new SqliteApplicationStore(Options.Create(runtimeOptions));
        await store.InitializeAsync(CancellationToken.None);
        Directory.CreateDirectory(_outputDir);
        var service = new RecordingService(
            store,
            new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            new HttpClientFactoryMock(),
            NullLogger<RecordingService>.Instance,
            new ApplicationStoreRecordingStore(store),
            new RecordingProcessSupervisor(),
            Options.Create(runtimeOptions));
        var deviceId = Guid.NewGuid();

        var staleSegment = Path.Combine(_outputDir, "dev_20260101_000000.ts");
        await File.WriteAllTextAsync(staleSegment, "x");
        File.SetLastWriteTimeUtc(staleSegment, DateTime.UtcNow.AddMinutes(-10));

        // First stall: within the cap (1 restart allowed) → auto-restart is attempted. No device
        // exists in the store, so StartAsync throws "Device not found" — which is caught and
        // logged, exactly like a source that refuses to start — and the counter advances to 1.
        RecordingJob? firstResult = null;
        using (var first = SpawnSleep())
        {
            var job = BuildJob(deviceId, first.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
            await store.SaveRecordingJobsAsync([job], CancellationToken.None);
            await service.ReconcilePersistedJobsAsync(CancellationToken.None);
            firstResult = Assert.Single(await service.CheckStalledJobsAsync(stallTimeoutSeconds: 5, autoRestart: true, CancellationToken.None));
            Assert.True(first.WaitForExit(5000), "first stalled pipeline should have been stopped");
        }
        Assert.False(firstResult!.IsRunning);

        // Second stall for the SAME device: the counter now exceeds the cap → the fast
        // auto-restart is suspended and the job is marked stopped with a clear error instead
        // of spawning yet another ffmpeg.
        RecordingJob? secondResult = null;
        using (var second = SpawnSleep())
        {
            var job = BuildJob(deviceId, second.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
            await store.SaveRecordingJobsAsync([job], CancellationToken.None);
            await service.ReconcilePersistedJobsAsync(CancellationToken.None);
            secondResult = Assert.Single(await service.CheckStalledJobsAsync(stallTimeoutSeconds: 5, autoRestart: true, CancellationToken.None));
            Assert.True(second.WaitForExit(5000), "second stalled pipeline should have been stopped");
        }
        Assert.False(secondResult!.IsRunning);
        Assert.Contains("auto-restart suspended", secondResult.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not producing media", secondResult.LastError, StringComparison.OrdinalIgnoreCase);

        // The suspension must be persisted, not just in-memory.
        var persisted = await store.GetRecordingJobsAsync(deviceId, CancellationToken.None);
        var latest = persisted.OrderByDescending(static j => j.StartedAt).First();
        Assert.Contains("auto-restart suspended", latest.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckStalledJobsAsync_AutoRestart_Cap_Clears_On_Fresh_Segment_Growth()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // needs a real killable child process
        }

        // NOTE: the capped options must be handed to the SERVICE, not just the store —
        // RecordingService's optional IOptions defaults to RecordingMaxConsecutiveRestarts=3
        // (the production value), so a cap of 1 only applies when it is injected here.
        var runtimeOptions = new BossCamRuntimeOptions
        {
            DatabasePath = _dbPath,
            RecordingMaxConsecutiveRestarts = 1
        };
        var store = new SqliteApplicationStore(Options.Create(runtimeOptions));
        await store.InitializeAsync(CancellationToken.None);
        Directory.CreateDirectory(_outputDir);
        var service = new RecordingService(
            store,
            new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            new HttpClientFactoryMock(),
            NullLogger<RecordingService>.Instance,
            new ApplicationStoreRecordingStore(store),
            new RecordingProcessSupervisor(),
            Options.Create(runtimeOptions));
        var deviceId = Guid.NewGuid();

        var staleSegment = Path.Combine(_outputDir, "dev_20260101_000000.ts");
        await File.WriteAllTextAsync(staleSegment, "x");
        File.SetLastWriteTimeUtc(staleSegment, DateTime.UtcNow.AddMinutes(-10));

        // Burn one restart (counter → 1) via a stale job whose restart fails (no device in store).
        using (var burn = SpawnSleep())
        {
            var job = BuildJob(deviceId, burn.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
            await store.SaveRecordingJobsAsync([job], CancellationToken.None);
            await service.ReconcilePersistedJobsAsync(CancellationToken.None);
            _ = await service.CheckStalledJobsAsync(stallTimeoutSeconds: 5, autoRestart: true, CancellationToken.None);
            Assert.True(burn.WaitForExit(5000));
        }

        // A fresh segment proves the source recovered → the consecutive-restart debt is cleared.
        var freshSegment = Path.Combine(_outputDir, "dev_20260102_000000.ts");
        await File.WriteAllTextAsync(freshSegment, "x");
        File.SetLastWriteTimeUtc(freshSegment, DateTime.UtcNow.AddSeconds(-1));
        RecordingJob? healthyJob = null;
        using (var healthy = SpawnSleep())
        {
            healthyJob = BuildJob(deviceId, healthy.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
            await store.SaveRecordingJobsAsync([healthyJob], CancellationToken.None);
            await service.ReconcilePersistedJobsAsync(CancellationToken.None);
            var stalled = await service.CheckStalledJobsAsync(stallTimeoutSeconds: 5, autoRestart: true, CancellationToken.None);
            Assert.Empty(stalled); // fresh growth → not stalled, and debt cleared
            Assert.False(healthy.HasExited);
            // Must stop the healthy pipeline explicitly: Process.Dispose (via the using) does
            // NOT kill the child, so a live recorder left running would be re-attached by the
            // next phase's reconcile and stall too — double-reporting the same device and
            // burning the restart budget twice.
            await service.StopAsync(healthyJob.Id, CancellationToken.None);
            Assert.True(healthy.WaitForExit(5000), "healthy pipeline should have been stopped");
        }

        // Now a new stall must restart again (counter restarted from 0 → 1 ≤ cap), NOT suspend.
        File.SetLastWriteTimeUtc(staleSegment, DateTime.UtcNow.AddMinutes(-10)); // re-stale
        File.SetLastWriteTimeUtc(freshSegment, DateTime.UtcNow.AddMinutes(-10));
        using (var third = SpawnSleep())
        {
            var job = BuildJob(deviceId, third.Id, _outputDir, startedAt: DateTimeOffset.UtcNow);
            await store.SaveRecordingJobsAsync([job], CancellationToken.None);
            await service.ReconcilePersistedJobsAsync(CancellationToken.None);
            var result = Assert.Single(await service.CheckStalledJobsAsync(stallTimeoutSeconds: 5, autoRestart: true, CancellationToken.None));
            Assert.True(third.WaitForExit(5000));
            // No suspension error — the fresh-growth reset gave the device a fresh budget.
            Assert.DoesNotContain("auto-restart suspended", result.LastError, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Exit rapid-restart (recording continuity): a continuous-record device whose recorder
    /// exits spontaneously while a fresh segment exists must be re-picked quickly (within
    /// RecordingExitRestartDelaySeconds) rather than waiting for the slow backed-off policy.
    /// Uses a fake ffmpeg that writes a fresh segment then exits, exactly like a 5523-W
    /// whose RTSP session the camera drops every few minutes.
    /// </summary>
    [Fact]
    public async Task Spontaneous_Exit_Rapid_Restarts_Continuous_Record_Device_With_Fresh_Segment()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // fake-ffmpeg script is POSIX-only
        }

        var runtimeOptions = new BossCamRuntimeOptions
        {
            DatabasePath = _dbPath,
            RecordingExitRestartDelaySeconds = 1
        };
        var store = new SqliteApplicationStore(Options.Create(runtimeOptions));
        await store.InitializeAsync(CancellationToken.None);
        Directory.CreateDirectory(_outputDir);

        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "flappy-cam",
            IpAddress = "10.0.0.77",
            Port = 80,
            DeviceType = "IPC",
            ContinuousRecord = true
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        // Fake ffmpeg: write a fresh .ts segment, then exit (simulates the camera dropping
        // the RTSP session after ~2s of healthy recording).
        var fakeFfmpeg = Path.Combine(_outputDir, $"fake-ffmpeg-exit-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(fakeFfmpeg,
            $"#!/usr/bin/env bash\n" +
            $"touch \"{_outputDir}/fresh.ts\"\n" +
            $"sleep 2\n");
        using (var chmod = Process.Start("chmod", $"+x {fakeFfmpeg}"))
        {
            chmod?.WaitForExit(2000);
        }

        var previousFfmpeg = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", fakeFfmpeg);
        try
        {
            var service = new RecordingService(
                store,
                new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
                new TestRecordingPipelineResolver(),
                NullBossCamEventBroadcaster.Instance,
                new HttpClientFactoryMock(),
                NullLogger<RecordingService>.Instance,
                new ApplicationStoreRecordingStore(store),
                new RecordingProcessSupervisor(),
                Options.Create(runtimeOptions));

            var first = await service.StartAsync(new RecordingStartRequest
            {
                DeviceId = device.Id,
                // Explicit RTSP URL → direct-ffmpeg pipeline (skips the snapshot probe). The
                // fake ffmpeg IS the tracked process, so its exit fires WireExitCleanup — the
                // snapshot pipeline's `while curl | ffmpeg` bash helper would keep the process
                // alive when the child exits and the handler would never run.
                SourceUrl = "rtsp://admin:@10.0.0.77:554/ch0_0.264",
                OutputDirectory = _outputDir
            }, CancellationToken.None);
            Assert.True(first.IsRunning);

            // Wait for the spontaneous exit (sleep 2) + rapid-restart delay (1s) + startup.
            RecordingJob? restarted = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var jobs = await service.GetJobsAsync(CancellationToken.None);
                restarted = jobs.FirstOrDefault(j => j.DeviceId == device.Id && j.Id != first.Id && j.IsRunning);
                if (restarted is not null)
                {
                    break;
                }

                await Task.Delay(500, CancellationToken.None);
            }

            Assert.NotNull(restarted);
            Assert.NotEqual(first.Id, restarted.Id);
            await service.StopAsync(restarted.Id, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", previousFfmpeg);
        }
    }

    /// <summary>
    /// Negative guard: a device that is NOT flagged continuous-record must NOT be
    /// auto-restarted on spontaneous exit — exit rapid-restart is reserved for the fleet
    /// continuous-record policy devices so operator/manual jobs stay operator-controlled.
    /// </summary>
    [Fact]
    public async Task Spontaneous_Exit_Does_Not_Rapid_Restart_Non_Continuous_Device()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // fake-ffmpeg script is POSIX-only
        }

        var runtimeOptions = new BossCamRuntimeOptions
        {
            DatabasePath = _dbPath,
            RecordingExitRestartDelaySeconds = 1
        };
        var store = new SqliteApplicationStore(Options.Create(runtimeOptions));
        await store.InitializeAsync(CancellationToken.None);
        Directory.CreateDirectory(_outputDir);

        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "manual-cam",
            IpAddress = "10.0.0.78",
            Port = 80,
            DeviceType = "IPC",
            ContinuousRecord = false
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var fakeFfmpeg = Path.Combine(_outputDir, $"fake-ffmpeg-manual-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(fakeFfmpeg,
            $"#!/usr/bin/env bash\n" +
            $"touch \"{_outputDir}/fresh.ts\"\n" +
            $"sleep 2\n");
        using (var chmod = Process.Start("chmod", $"+x {fakeFfmpeg}"))
        {
            chmod?.WaitForExit(2000);
        }

        var previousFfmpeg = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", fakeFfmpeg);
        try
        {
            var service = new RecordingService(
                store,
                new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
                new TestRecordingPipelineResolver(),
                NullBossCamEventBroadcaster.Instance,
                new HttpClientFactoryMock(),
                NullLogger<RecordingService>.Instance,
                new ApplicationStoreRecordingStore(store),
                new RecordingProcessSupervisor(),
                Options.Create(runtimeOptions));

            var first = await service.StartAsync(new RecordingStartRequest
            {
                DeviceId = device.Id,
                // Explicit RTSP URL → direct-ffmpeg pipeline so the fake ffmpeg's exit fires
                // WireExitCleanup (see the positive test for why snapshot mode can't be used).
                SourceUrl = "rtsp://admin:@10.0.0.78:554/ch0_0.264",
                OutputDirectory = _outputDir
            }, CancellationToken.None);
            Assert.True(first.IsRunning);

            // Give the spontaneous exit + rapid-restart window ample time; NO new job may appear.
            await Task.Delay(TimeSpan.FromSeconds(6), CancellationToken.None);
            var jobs = await service.GetJobsAsync(CancellationToken.None);
            Assert.DoesNotContain(jobs, j => j.DeviceId == device.Id && j.Id != first.Id && j.IsRunning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", previousFfmpeg);
        }
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

    private sealed class CountingVideoAdapter : IVideoTransportAdapter
    {
        public int GetSourcesCalls { get; private set; }
        public string Name => "Counting";
        public TransportKind TransportKind => TransportKind.Rtsp;
        public int Priority => 1;

        public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
        {
            GetSourcesCalls++;
            return Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
        }
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
