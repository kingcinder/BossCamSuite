using System.Net;
using System.Net.Sockets;
using System.Text;
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

    [Fact]
    public async Task ReconcileContinuous_RePromotes_Degraded_Snapshot_Job_When_Rtsp_Answers()
    {
        // Local RTSP server answering an OPTIONS handshake (RtspPlayabilityTests pattern) so the
        // re-promotion probe sees a playable source once the camera is "back". The URL host is
        // IPv6 loopback [::1] — PlayableSourcePolicy.IsSub treats the Dahua sub-stream marker
        // "/12" as a substring, and "rtsp://127.0.0.1…" contains "/12" inside "//127", which
        // would misclassify the stub main source as a sub stream and silently skip re-promotion.
        using var listener = StartTcpListener(async stream =>
        {
            await ReadUntilBlankLineAsync(stream);
            var status = "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(status));
        }, IPAddress.IPv6Loopback);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var flagged = new DeviceIdentity { IpAddress = "::1", DeviceType = "IPC", ContinuousRecord = true, Port = 80 };
        await store.UpsertDevicesAsync([flagged], CancellationToken.None);

        // Persisted degraded snapshot job — RTSP was unreachable when it started, so the fleet
        // fell back to the snapshot pipeline. The camera has since recovered.
        var degraded = new RecordingJob
        {
            DeviceId = flagged.Id,
            ProfileId = Guid.NewGuid(),
            SourceUrl = "http://127.0.0.1:80/NetSDK/Video/encode/channel/101/snapShot",
            OutputDirectory = Path.Combine(_tempDirectory, "rec"),
            SegmentPattern = Path.Combine(_tempDirectory, "rec", "seg_%Y%m%d_%H%M%S.ts"),
            IsRunning = true,
            Mode = "snapshot",
            DegradedReason = "Main RTSP unreachable — using snapshot pipeline"
        };
        await store.SaveRecordingJobsAsync([degraded], CancellationToken.None);
        // Hermetic profile: seed a temp-dir OutputDirectory so the fall-through StartAsync
        // resolves the existing profile instead of creating one in the real user home.
        await store.SaveRecordingProfilesAsync(
        [
            new RecordingProfile
            {
                DeviceId = flagged.Id,
                Name = "Default",
                OutputDirectory = Path.Combine(_tempDirectory, "rec"),
                SegmentSeconds = 30,
                Enabled = true
            }
        ], CancellationToken.None);

        var recording = BuildRecordingService(store, rtspUrl: $"rtsp://[::1]:{port}/ch0_0.264");
        var started = await recording.ReconcileContinuousAsync(CancellationToken.None);

        // The re-promotion must stop the stale snapshot job and start a fresh direct-RTSP job.
        // The returned record is authoritative: the spawned direct ffmpeg may exit quickly
        // against the fake OPTIONS-only RTSP listener, racing persisted-state assertions, so
        // store assertions are kept to the deterministic stale-record-stop + record-exists.
        var promoted = Assert.Single(started);
        Assert.Equal("direct", promoted.Mode);
        Assert.StartsWith("rtsp://", promoted.SourceUrl);
        Assert.True(promoted.IsRunning);
        var jobs = await store.GetRecordingJobsAsync(flagged.Id, CancellationToken.None);
        Assert.Contains(jobs, job => job.Id == degraded.Id && !job.IsRunning);
        Assert.Contains(jobs, job => job.Mode == "direct");
    }

    [Fact]
    public async Task ReconcileContinuous_Keeps_Degraded_Snapshot_Job_When_Rtsp_Still_Down()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var flagged = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", ContinuousRecord = true };
        await store.UpsertDevicesAsync([flagged], CancellationToken.None);

        var degraded = new RecordingJob
        {
            DeviceId = flagged.Id,
            ProfileId = Guid.NewGuid(),
            SourceUrl = "http://10.0.0.4:80/NetSDK/Video/encode/channel/101/snapShot",
            OutputDirectory = Path.Combine(_tempDirectory, "rec"),
            SegmentPattern = Path.Combine(_tempDirectory, "rec", "seg_%Y%m%d_%H%M%S.ts"),
            IsRunning = true,
            Mode = "snapshot",
            DegradedReason = "Main RTSP unreachable — using snapshot pipeline"
        };
        await store.SaveRecordingJobsAsync([degraded], CancellationToken.None);

        // Stub adapter yields an RTSP URL on a closed port — the camera is still down. IPv6
        // loopback avoids the "/12" sub-stream heuristic false positive on "//127" (see the
        // re-promotion test) so the source is classified as main and the probe is the gate.
        var closedPort = GetFreeTcpPort();
        var recording = BuildRecordingService(store, rtspUrl: $"rtsp://[::1]:{closedPort}/ch0_0.264");
        var started = await recording.ReconcileContinuousAsync(CancellationToken.None);

        // No churn: the degraded snapshot job stays untouched and keeps recording.
        Assert.Empty(started);
        var jobs = await store.GetRecordingJobsAsync(flagged.Id, CancellationToken.None);
        var sole = Assert.Single(jobs);
        Assert.True(sole.IsRunning);
        Assert.Equal("snapshot", sole.Mode);
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

    private static RecordingService BuildRecordingService(IApplicationStore store, string? rtspUrl = null)
    {
        // When rtspUrl is provided, the broker yields a live RTSP main source so the direct
        // pipeline is selected once the re-promotion probe answers.
        IVideoTransportAdapter[] adapters = rtspUrl is null
            ? []
            : [new StubRtspAdapter(rtspUrl)];
        var broker = new TransportBroker(adapters, store, null, NullLogger<TransportBroker>.Instance);
        // Non-throwing stub: the snapshot fallback path probes URLs; a throwing factory would
        // break the fallback (BuildSnapshotUrl is a pure string builder, so this never throws).
        var http = new StubHttpClientFactory();
        return new RecordingService(store, broker, new TestRecordingPipelineResolver(), NullBossCamEventBroadcaster.Instance, http, NullLogger<RecordingService>.Instance, new ApplicationStoreRecordingStore(store), new RecordingProcessSupervisor());
    }

    /// <summary>Fake transport adapter returning a single main RTSP source.</summary>
    private sealed class StubRtspAdapter(string url) : IVideoTransportAdapter
    {
        public string Name => "StubRtsp";
        public TransportKind TransportKind => TransportKind.Rtsp;
        public int Priority => 10;

        public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([new VideoSourceDescriptor
            {
                Kind = TransportKind.Rtsp,
                Url = url,
                Rank = 10,
                DisplayName = "stub main"
            }]);
    }

    private static TcpListener StartTcpListener(Func<NetworkStream, Task> respond, IPAddress? bindAddress = null)
    {
        var listener = new TcpListener(bindAddress ?? IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            await respond(stream);
                        }
                    }
                    catch
                    {
                    }
                });
            }
        });
        return listener;
    }

    private static async Task ReadUntilBlankLineAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read <= 0)
            {
                return;
            }

            total += read;
            if (HasHeaderTerminator(buffer, total))
            {
                return;
            }
        }
    }

    private static bool HasHeaderTerminator(byte[] buffer, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
