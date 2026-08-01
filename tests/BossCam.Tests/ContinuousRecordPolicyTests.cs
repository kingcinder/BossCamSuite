using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Fleet continuous-record policy (commit 3): devices flagged <see cref="DeviceIdentity.ContinuousRecord"/>
/// are (re)started by ReconcileContinuousAsync after the persisted-job reconcile at boot and on
/// each worker cycle — a camera whose recorder died (crash, reboot, stall) comes back automatically,
/// never double-starting a surviving recorder.
/// </summary>
public sealed class ContinuousRecordPolicyTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-continuous-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public ContinuousRecordPolicyTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task ReconcileContinuous_Starts_Jobs_For_Flagged_Devices()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var flagged = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", ContinuousRecord = true };
        var unflagged = new DeviceIdentity { IpAddress = "10.0.0.5", DeviceType = "IPC", ContinuousRecord = false };
        await store.UpsertDevicesAsync([flagged, unflagged], CancellationToken.None);

        var recording = BuildRecordingService(store);
        var started = await recording.ReconcileContinuousAsync(CancellationToken.None);

        var job = Assert.Single(started);
        Assert.Equal(flagged.Id, job.DeviceId);
        Assert.True(job.IsRunning);
        // Only the flagged device was considered; the unflagged one has no profile/job.
        Assert.Empty(await store.GetRecordingJobsAsync(unflagged.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ReconcileContinuous_Does_Not_Double_Start_A_Persisted_Running_Job()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var flagged = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", ContinuousRecord = true };
        await store.UpsertDevicesAsync([flagged], CancellationToken.None);

        var runningJob = new RecordingJob
        {
            DeviceId = flagged.Id,
            ProfileId = Guid.NewGuid(),
            SourceUrl = "rtsp://10.0.0.4:554/ch0_0.264",
            OutputDirectory = Path.Combine(_tempDirectory, "rec"),
            IsRunning = true
        };
        await store.SaveRecordingJobsAsync([runningJob], CancellationToken.None);

        var recording = BuildRecordingService(store);
        var started = await recording.ReconcileContinuousAsync(CancellationToken.None);

        Assert.Empty(started); // the persisted running job is the dedup guard — no runaway ffmpeg
        Assert.Single(await store.GetRecordingJobsAsync(flagged.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ReconcileContinuous_Isolates_Per_Device_Failures()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var broken = new DeviceIdentity { IpAddress = "10.0.0.99", DeviceType = "IPC", ContinuousRecord = true };
        var healthy = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", ContinuousRecord = true };
        await store.UpsertDevicesAsync([broken, healthy], CancellationToken.None);
        // Deterministic failure seam: an illegal OutputDirectory makes StartAsync throw during
        // Directory.CreateDirectory — that device's failure must not block the healthy one.
        await store.SaveRecordingProfilesAsync(
        [
            new RecordingProfile
            {
                DeviceId = broken.Id,
                Name = "Broken",
                OutputDirectory = "bad\0dir",
                Enabled = true
            }
        ], CancellationToken.None);

        var recording = BuildRecordingService(store);
        var started = await recording.ReconcileContinuousAsync(CancellationToken.None);

        // The healthy device still got its job; the broken one's failure was logged, not thrown.
        Assert.Contains(started, job => job.DeviceId == healthy.Id);
        Assert.DoesNotContain(started, job => job.DeviceId == broken.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
            }
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static RecordingService BuildRecordingService(IApplicationStore store)
    {
        var broker = new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance);
        // Non-throwing stub: the snapshot fallback path probes URLs; a throwing factory would
        // break the fallback (BuildSnapshotUrl is a pure string builder, so this never throws).
        var http = new StubHttpClientFactory();
        return new RecordingService(store, broker, new TestRecordingPipelineResolver(), NullBossCamEventBroadcaster.Instance, http, NullLogger<RecordingService>.Instance, new ApplicationStoreRecordingStore(store), new RecordingProcessSupervisor());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticHandler()) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class StaticHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
