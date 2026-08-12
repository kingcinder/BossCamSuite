using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Locks down the post-recovery recording-continuity gate
/// (<see cref="CameraRecoveryService.VerifyRecordingAsync"/>): after a camera is
/// recovered + enrolled, the Suite must independently confirm recording is actually
/// active (RTSP reachable + job running), start one on demand when none is, retry
/// with bounded attempts, and surface the gap when recording never starts.
/// All network/process side effects are injected away (fake rtspProbe / ensureRecording).
/// </summary>
public sealed class CameraRecoveryRecordingVerificationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-recover-verify-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Verify_Active_Job_Is_Verified_Without_Starting_Another()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("Z7C34781634738", "10.0.0.29");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveRecordingJobsAsync([new RecordingJob
        {
            DeviceId = device.Id,
            IsRunning = true,
            Mode = "snapshot",
            SourceUrl = "http://redacted/snapShot"
        }], CancellationToken.None);

        var svc = BuildService(store);
        var starts = 0;

        var result = await svc.VerifyRecordingAsync(
            "JAZ7C34781634738", "10.0.0.29", CancellationToken.None,
            rtspProbe: (_, _) => Task.FromResult(true),
            ensureRecording: (_, _) => { starts++; return Task.FromResult<RecordingJob?>(new RecordingJob { DeviceId = device.Id, IsRunning = true }); });

        Assert.True(result.Verified);
        Assert.True(result.RtspReachable);
        Assert.Equal(0, starts); // never start a second job for an already-recording camera
        Assert.Contains("already active", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_No_Active_Job_Starts_One_On_Demand()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("Z7C34781634738", "10.0.0.29");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var svc = BuildService(store);
        RecordingJob started = new()
        {
            DeviceId = device.Id,
            IsRunning = true,
            Mode = "snapshot",
            SourceUrl = "http://redacted/snapShot"
        };

        var result = await svc.VerifyRecordingAsync(
            "JAZ7C34781634738", "10.0.0.29", CancellationToken.None,
            rtspProbe: (_, _) => Task.FromResult(true),
            ensureRecording: (_, _) => Task.FromResult<RecordingJob?>(started));

        Assert.True(result.Verified);
        Assert.True(result.RtspReachable);
        Assert.Equal(started.Id.ToString("N"), result.JobId);
        Assert.Contains("started job", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_Start_Keeps_Failing_Reports_Gap_After_All_Attempts()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("Z7C34781634738", "10.0.0.29");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        // Fast-fail config: 3 attempts, no inter-attempt delay.
        var svc = BuildService(store, attempts: 3, delaySeconds: 0);
        var attempts = 0;

        var result = await svc.VerifyRecordingAsync(
            "JAZ7C34781634738", "10.0.0.29", CancellationToken.None,
            rtspProbe: (_, _) => Task.FromResult(false),
            ensureRecording: (_, _) =>
            {
                attempts++;
                throw new InvalidOperationException("ffmpeg exited instantly");
            });

        Assert.False(result.Verified);
        Assert.Equal(3, attempts);
        Assert.False(result.RtspReachable);
        Assert.Contains("did NOT start after 3 attempt(s)", result.Message);
        Assert.Contains("ffmpeg exited instantly", result.Message);
    }

    [Fact]
    public async Task Verify_Rtsp_Unreachable_But_Job_Active_Still_Verified_With_Warning()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("Z7C34781634738", "10.0.0.29");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveRecordingJobsAsync([new RecordingJob
        {
            DeviceId = device.Id,
            IsRunning = true,
            Mode = "snapshot",
            SourceUrl = "http://redacted/snapShot"
        }], CancellationToken.None);

        var svc = BuildService(store);
        var result = await svc.VerifyRecordingAsync(
            "JAZ7C34781634738", "10.0.0.29", CancellationToken.None,
            rtspProbe: (_, _) => Task.FromResult(false));

        // Recording via the snapshot pipeline is real recording — the gate must pass while
        // still reporting the RTSP gap (informational signal, not a blocker).
        Assert.True(result.Verified);
        Assert.False(result.RtspReachable);
        Assert.Contains("not answering", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_Device_Not_Found_Reports_Unverified()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var svc = BuildService(store);

        var result = await svc.VerifyRecordingAsync(
            "JAZ7C34781634738", "10.0.0.99", CancellationToken.None,
            rtspProbe: (_, _) => Task.FromResult(true),
            ensureRecording: (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.False(result.Verified);
        Assert.Contains("Enrolled device not found", result.Message);
    }

    [Fact]
    public async Task Verify_Matches_Device_By_Lan_Ip_When_Serial_Unknown()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        // The search serial is ENTIRELY unrelated to the seeded device (different serial,
        // different name) — so only the LAN-IP handoff branch of FindEnrolledDeviceAsync can
        // find it. This genuinely isolates the LAN-IP fallback, unlike a device whose serial
        // also happens to match the JA-stripped search key.
        var device = NewDevice("X9Y8AAAABBBBCCCC", "10.0.0.29");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        var runningJob = new RecordingJob
        {
            DeviceId = device.Id,
            IsRunning = true,
            Mode = "snapshot"
        };
        await store.SaveRecordingJobsAsync([runningJob], CancellationToken.None);

        var svc = BuildService(store);
        var result = await svc.VerifyRecordingAsync(
            "JAZ7C34781634738", "10.0.0.29", CancellationToken.None,
            rtspProbe: (_, _) => Task.FromResult(true));

        Assert.True(result.Verified);
        // JobId is the recording JOB's id, not the device id.
        Assert.Equal(runningJob.Id.ToString("N"), result.JobId);
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static CameraRecoveryService BuildService(IApplicationStore store, int attempts = 3, int delaySeconds = 0)
    {
        var recording = new RecordingService(
            store,
            new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            new HttpClientFactoryMock(),
            NullLogger<RecordingService>.Instance,
            new ApplicationStoreRecordingStore(store),
            new RecordingProcessSupervisor());
        return new CameraRecoveryService(
            NullLogger<CameraRecoveryService>.Instance,
            Options.Create(new BossCamRuntimeOptions
            {
                RecoveryRecordingVerifyAttempts = attempts,
                RecoveryRecordingVerifyDelaySeconds = delaySeconds
            }),
            store,
            recording,
            new ApplicationStoreRecordingStore(store));
    }

    private static DeviceIdentity NewDevice(string serial, string ip)
        => new()
        {
            IpAddress = ip,
            Port = 80,
            LoginName = "admin",
            Password = "",
            DeviceId = serial,
            Name = $"5523-W-{serial}",
            HardwareModel = "5523-W",
            DeviceType = "IPC",
            ContinuousRecord = true,
            RtspPort = 554
        };

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
