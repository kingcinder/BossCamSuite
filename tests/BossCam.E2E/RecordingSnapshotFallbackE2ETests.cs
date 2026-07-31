using System.Diagnostics;
using System.Net.Http.Json;
using BossCam.Contracts;

namespace BossCam.E2E;

/// <summary>
/// E2E coverage for <c>/api/recordings/start</c> snapshot self-heal: a 5523-W whose discovery
/// recorded a dead ONVIF/media port records from the :80 NetSDK REST surface instead. A real
/// listener on 127.0.0.1:80 serves a decodable JPEG; the registered device carries a
/// <em>closed</em> ephemeral recorded port, so the rank-25 recorded-port snapshot descriptor
/// transport-fails and <see cref="BossCam.Core.RecordingService.ResolveSnapshotUrlAsync"/>
/// probes through to the rank-26 :80 fallback descriptor.
///
/// The positive test requires binding :80, so it early-returns on unprivileged runners (same
/// convention as SnapshotPortFallbackE2ETests); run the suite elevated to exercise it. The
/// negative test (nothing reachable → the job still starts on the recorded-port URL, no false
/// self-heal) runs everywhere — loopback connection-refused is instant.
/// </summary>
[Collection("BossCamE2E")]
public sealed class RecordingSnapshotFallbackE2ETests : IClassFixture<BossCamWebAppFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly BossCamWebAppFactory _factory;

    public RecordingSnapshotFallbackE2ETests(BossCamWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose()
    {
        // Best-effort sweep of any orphaned snapshot-pipeline processes (bash/curl/ffmpeg) in case
        // a stop didn't fully kill the process tree — same convention as SimulatedLanCleanupTests.
        KillSnapshotPipelineProcesses();
    }

    [Fact]
    public async Task Recording_Start_Self_Heals_Snapshot_To_Port80()
    {
        using var cts = new CancellationTokenSource();
        using var server = Port80JpegServer.TryStart(cts.Token);
        if (server is null)
        {
            // Environment gate (same convention as SnapshotPortFallbackE2ETests): binding :80 needs
            // elevation. Early-return so unprivileged runners don't fail; log a breadcrumb so the
            // gating is visible in CI output instead of looking like an unconditional pass.
            Console.WriteLine("[RecordingSnapshotFallbackE2ETests] SKIPPED (could not bind 127.0.0.1:80) — run elevated to exercise the recording port fallback.");
            return;
        }

        var recordedPort = Port80JpegServer.GetFreeTcpPort(); // closed — the rank-25 candidate transport-fails
        var device = await RegisterDeviceAsync(recordedPort);
        Guid? jobId = null;
        try
        {
            // sourceUrl "snapshot" forces the snapshot pipeline (same request the Management UI
            // sends for 5523-W). Without it StartAsync picks the static RTSP main descriptor and
            // never probes the :80 snapshot fallback.
            var start = await _client.PostAsJsonAsync("/api/recordings/start", new
            {
                deviceId = device.Id,
                sourceUrl = "snapshot",
                outputDirectory = Path.Combine(_factory.TempRoot, "recordings")
            });
            await E2EHelpers.AssertOkAsync(start, "recordings/start");

            var job = await start.Content.ReadFromJsonAsync<RecordingJob>();
            Assert.NotNull(job);
            jobId = job!.Id;

            Assert.Equal("snapshot", job.Mode);
            Assert.True(job.IsRunning, "recording should be running against the :80 fallback");
            // Uri normalization strips the default :80 from the redacted URL string
            // ("http://admin:***@127.0.0.1/..."), so assert on the parsed port rather than a
            // raw ":80" substring — Uri.Port correctly resolves the default http port to 80.
            var sourceUri = new Uri(job.SourceUrl);
            Assert.Equal(80, sourceUri.Port);
            Assert.Equal("127.0.0.1", sourceUri.Host);
            Assert.Contains("/NetSDK/Video/encode/channel/101/snapShot", sourceUri.AbsolutePath);
            Assert.DoesNotContain($":{recordedPort}", job.SourceUrl);
        }
        finally
        {
            if (jobId is not null)
            {
                try { await _client.PostAsync($"/api/recordings/stop/{jobId}", null); }
                catch { /* teardown is best-effort; Dispose sweep covers orphans */ }
            }
        }
    }

    [Fact]
    public async Task Recording_Start_Keeps_Recorded_Port_When_Nothing_Answers()
    {
        var recordedPort = Port80JpegServer.GetFreeTcpPort();
        var device = await RegisterDeviceAsync(recordedPort);
        Guid? jobId = null;
        try
        {
            var start = await _client.PostAsJsonAsync("/api/recordings/start", new
            {
                deviceId = device.Id,
                sourceUrl = "snapshot",
                outputDirectory = Path.Combine(_factory.TempRoot, "recordings")
            });
            await E2EHelpers.AssertOkAsync(start, "recordings/start");

            var job = await start.Content.ReadFromJsonAsync<RecordingJob>();
            Assert.NotNull(job);
            jobId = job!.Id;

            // No candidate answers (recorded port and :80 both connection-refused) → the probe
            // returns null and BuildSnapshotUrl's last-resort URL keeps the recorded port. This
            // pins the "no false self-heal to a silent :80" contract.
            Assert.Equal("snapshot", job.Mode);
            Assert.Contains($":{recordedPort}", job.SourceUrl);
            Assert.DoesNotContain(":80/", job.SourceUrl);
        }
        finally
        {
            if (jobId is not null)
            {
                try { await _client.PostAsync($"/api/recordings/stop/{jobId}", null); }
                catch { /* teardown is best-effort; Dispose sweep covers orphans */ }
            }
        }
    }

    private async Task<DeviceIdentity> RegisterDeviceAsync(int port)
    {
        var res = await _client.PostAsJsonAsync("/api/devices/register", new
        {
            ipAddress = "127.0.0.1",
            port,
            loginName = "admin",
            password = "",
            name = $"recording-fallback-e2e-{port}",
            hardwareModel = "5523-W"
        });
        await E2EHelpers.AssertOkAsync(res, $"register 127.0.0.1:{port}");
        return (await res.Content.ReadFromJsonAsync<DeviceIdentity>())!;
    }

    private static void KillSnapshotPipelineProcesses()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        // Match the bash wrapper plus the unique NetSDK snapshot URL the pipeline curls (never
        // collides with a user's normal curls). Mirrors SimulatedLanCleanupTests' teardown.
        var patterns = new[]
        {
            "-f bosscam-rec-",
            "-f 'curl.*NetSDK.*snapShot'",
        };

        foreach (var pattern in patterns)
        {
            try
            {
                using var pkill = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = pattern,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                pkill?.WaitForExit(2000);
            }
            catch
            {
                // pkill not available; nothing else we can do without /proc walking.
            }
        }
    }
}
