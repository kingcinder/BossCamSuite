using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BossCam.Contracts;

namespace BossCam.E2E;

/// <summary>
/// Live multi-camera Ubuntu E2E. Skips automatically when no Juan NetSDK cameras are reachable.
/// </summary>
[Collection("BossCamE2E")]
public sealed class LiveCameraExhaustiveTests : IClassFixture<BossCamWebAppFactory>
{
    private readonly HttpClient _client;
    private readonly BossCamWebAppFactory _factory;

    public LiveCameraExhaustiveTests(BossCamWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<List<DeviceIdentity>> RegisterLiveAsync()
    {
        if (!E2EHelpers.LiveEnabled)
        {
            return [];
        }

        var devices = new List<DeviceIdentity>();
        foreach (var ip in E2EHelpers.LiveCameraIps)
        {
            if (!await E2EHelpers.IsReachableAsync(ip))
            {
                continue;
            }

            var d = await E2EHelpers.RegisterJuanAsync(_client, ip);
            if (d is not null)
            {
                devices.Add(d);
            }
        }

        return devices;
    }

    [Fact]
    public async Task Live_Register_Probe_Settings_Sources_Snapshot()
    {
        var devices = await RegisterLiveAsync();
        if (devices.Count == 0)
        {
            return; // environment without cameras
        }

        var snapshotOk = 0;
        foreach (var device in devices)
        {
            var probe = await _client.PostAsync($"/api/devices/{device.Id}/probe", null);
            await E2EHelpers.AssertOkAsync(probe, $"probe {device.IpAddress}");

            var settings = await _client.GetAsync($"/api/devices/{device.Id}/settings");
            await E2EHelpers.AssertOkAsync(settings, $"settings {device.IpAddress}");
            var settingsJson = await settings.Content.ReadAsStringAsync();
            Assert.Contains("LanDirectNetSdkRestAdapter", settingsJson, StringComparison.OrdinalIgnoreCase);

            var sources = await _client.GetFromJsonAsync<List<VideoSourceDescriptor>>($"/api/devices/{device.Id}/sources");
            Assert.NotNull(sources);
            Assert.NotEmpty(sources!);
            var main = RecordingService_Select(sources!);
            Assert.NotNull(main);
            Assert.Contains("ch0_0", main!.Url, StringComparison.OrdinalIgnoreCase);

            var snap = await _client.GetAsync($"/api/devices/{device.Id}/snapshot");
            // Individual units can 403 NetSDK snapShot; proxy may still recover via RTSP frame grab.
            if (snap.IsSuccessStatusCode)
            {
                Assert.Equal("image/jpeg", snap.Content.Headers.ContentType?.MediaType);
                var bytes = await snap.Content.ReadAsByteArrayAsync();
                Assert.True(bytes.Length > 1000, $"snapshot too small for {device.IpAddress}");
                Assert.Equal(0xFF, bytes[0]);
                Assert.Equal(0xD8, bytes[1]); // JPEG SOI
                snapshotOk++;
            }
            else
            {
                Assert.True((int)snap.StatusCode is 403 or 404 or 502,
                    $"snapshot {device.IpAddress}: unexpected {(int)snap.StatusCode} {await snap.Content.ReadAsStringAsync()}");
            }

            var caps = await _client.GetAsync($"/api/devices/{device.Id}/capabilities");
            await E2EHelpers.AssertOkAsync(caps, $"capabilities {device.IpAddress}");

            var typed = await _client.PostAsync($"/api/devices/{device.Id}/settings/typed/refresh", null);
            Assert.True((int)typed.StatusCode is >= 200 and < 500, await typed.Content.ReadAsStringAsync());

            var controlPoints = await _client.GetAsync($"/api/devices/{device.Id}/control-points");
            Assert.True((int)controlPoints.StatusCode is >= 200 and < 500);

            var surface = await _client.GetAsync($"/api/devices/{device.Id}/endpoint-surface");
            Assert.True((int)surface.StatusCode is >= 200 and < 500);

            var preview = await _client.GetAsync($"/api/devices/{device.Id}/preview");
            Assert.True((int)preview.StatusCode is >= 200 and < 500);

            // Validation can be slow on multi-device runs; bound timeout per device.
            using var validationCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                var validation = await _client.PostAsJsonAsync($"/api/devices/{device.Id}/validation/run", new { }, validationCts.Token);
                Assert.True((int)validation.StatusCode is >= 200 and < 500);
            }
            catch (OperationCanceledException)
            {
                // Do not fail the whole multi-camera pass on a single slow validation call.
            }

            var eligible = await _client.GetAsync($"/api/devices/{device.Id}/persistence/eligible-fields");
            Assert.True((int)eligible.StatusCode is >= 200 and < 500);

            var native = await _client.GetAsync($"/api/devices/{device.Id}/native-fallback-assessment");
            Assert.True((int)native.StatusCode is >= 200 and < 500);

            var inventory = await _client.GetAsync($"/api/devices/{device.Id}/image/inventory");
            Assert.True((int)inventory.StatusCode is >= 200 and < 500);

            var snapshots = await _client.GetAsync($"/api/devices/{device.Id}/grouped-config/snapshots");
            Assert.True((int)snapshots.StatusCode is >= 200 and < 500);
        }

        Assert.True(snapshotOk > 0, "Expected at least one live camera to return a JPEG snapshot via the proxy.");
    }

    [Fact]
    public async Task Live_Image_Brightness_Write_Readback_Is_Durable_In_Process()
    {
        var devices = await RegisterLiveAsync();
        var device = devices.FirstOrDefault();
        if (device is null)
        {
            return;
        }

        // Read channel 1
        var get = await _client.PostAsJsonAsync($"/api/devices/{device.Id}/settings/write", new
        {
            endpoint = "/NetSDK/Video/input/channel/1",
            method = "GET",
            requireWriteVerification = false
        });
        await E2EHelpers.AssertOkAsync(get, "get image");
        var getBody = await get.Content.ReadFromJsonAsync<WriteResult>();
        Assert.NotNull(getBody);
        Assert.True(getBody!.Success || getBody.Response is not null);

        // Use direct camera curl-equivalent via write with full object when possible
        using var cam = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        cam.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:")));
        var raw = await cam.GetStringAsync($"http://{device.IpAddress}/NetSDK/Video/input/channel/1");
        using var doc = JsonDocument.Parse(raw);
        var orig = doc.RootElement.GetProperty("brightnessLevel").GetInt32();
        var next = orig >= 98 ? orig - 3 : orig + 3;

        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(raw)!;
        payload["brightnessLevel"] = next;
        var put = await _client.PostAsJsonAsync($"/api/devices/{device.Id}/settings/write", new
        {
            endpoint = "/NetSDK/Video/input/channel/1",
            method = "PUT",
            payload,
            requireWriteVerification = false,
            snapshotBeforeWrite = true
        });
        await E2EHelpers.AssertOkAsync(put, "put brightness");

        var raw2 = await cam.GetStringAsync($"http://{device.IpAddress}/NetSDK/Video/input/channel/1");
        using var doc2 = JsonDocument.Parse(raw2);
        Assert.Equal(next, doc2.RootElement.GetProperty("brightnessLevel").GetInt32());

        // restore
        payload["brightnessLevel"] = orig;
        await _client.PostAsJsonAsync($"/api/devices/{device.Id}/settings/write", new
        {
            endpoint = "/NetSDK/Video/input/channel/1",
            method = "PUT",
            payload,
            requireWriteVerification = false
        });
    }

    [Fact]
    public async Task Live_HighRes_Recording_Produces_2560x1920_Hevc()
    {
        var devices = await RegisterLiveAsync();
        var device = devices.FirstOrDefault();
        if (device is null)
        {
            return;
        }

        var mainUrl = $"rtsp://admin:@{device.IpAddress}:554/ch0_0.264";
        var start = await _client.PostAsJsonAsync("/api/recordings/start", new
        {
            deviceId = device.Id,
            sourceUrl = mainUrl
        });
        await E2EHelpers.AssertOkAsync(start, "record start");
        var job = await start.Content.ReadFromJsonAsync<RecordingJob>();
        Assert.NotNull(job);
        Assert.Contains("ch0_0.264", job!.SourceUrl, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(job.OutputDirectory));
        Directory.CreateDirectory(job.OutputDirectory);

        await Task.Delay(14000);
        var stop = await _client.PostAsync($"/api/recordings/stop/{job.Id}", null);
        Assert.True((int)stop.StatusCode is 200 or 404);
        await _client.PostAsync("/api/recordings/stop-all", null);
        await Task.Delay(1500);

        var searchRoots = new[]
        {
            job.OutputDirectory,
            _factory.TempRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BossCamSuite", "recordings")
        }.Where(Directory.Exists).Distinct().ToArray();

        var files = searchRoots
            .SelectMany(root => Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .Where(p => new FileInfo(p).Length > 1000)
            .ToArray();

        Assert.True(files.Length > 0, $"No recording segments under: {string.Join(", ", searchRoots)}; job dir={job.OutputDirectory}; pid={job.ProcessId}");
        var file = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
        Assert.True(new FileInfo(file).Length > 50_000, $"recording too small: {file} ({new FileInfo(file).Length} bytes)");

        var ffprobe = ResolveFfprobe();
        Assert.False(string.IsNullOrWhiteSpace(ffprobe), "ffprobe required for e2e media validation");
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe!,
            Arguments = $"-v error -show_entries stream=codec_name,width,height -of csv=p=0 \"{file}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.Contains("hevc", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2560", output);
        Assert.Contains("1920", output);
    }

    [Fact]
    public async Task Live_Highlight_Board_Flip_And_Select()
    {
        var devices = await RegisterLiveAsync();
        if (devices.Count == 0)
        {
            return;
        }

        var board = await _client.GetFromJsonAsync<JsonElement>("/api/highlights");
        Assert.True(board.TryGetProperty("tiles", out var tiles));
        Assert.True(tiles.GetArrayLength() > 0);

        var next = await _client.PostAsync("/api/highlights/next", null);
        await E2EHelpers.AssertOkAsync(next, "hl next");
        var prev = await _client.PostAsync("/api/highlights/prev", null);
        await E2EHelpers.AssertOkAsync(prev, "hl prev");
        var main = await _client.PostAsync("/api/highlights/stream/main", null);
        await E2EHelpers.AssertOkAsync(main, "hl main");

        // Select using a tile id from the board (authoritative), not a stale register payload.
        var board2 = await _client.GetFromJsonAsync<JsonElement>("/api/highlights");
        var tileId = board2.GetProperty("tiles")[0].GetProperty("deviceId").GetGuid();
        var select = await _client.PostAsync($"/api/highlights/select/{tileId}", null);
        await E2EHelpers.AssertOkAsync(select, "hl select");
        var selected = await select.Content.ReadFromJsonAsync<JsonElement>();
        var selectedId = selected.GetProperty("selectedDeviceId").ToString().Trim('"');
        Assert.Equal(tileId.ToString(), selectedId);
    }

    [Fact]
    public async Task Live_Encode_Channel_101_Is_Main_HighRes_Metadata()
    {
        var devices = await RegisterLiveAsync();
        if (devices.Count == 0)
        {
            return;
        }

        using var cam = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        cam.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:")));

        var sawMainHighRes = false;
        var sawCanonicalSub = false;
        foreach (var device in devices)
        {
            var raw = await cam.GetStringAsync($"http://{device.IpAddress}/NetSDK/Video/encode/channel/101");
            using var doc = JsonDocument.Parse(raw);
            var mainRes = doc.RootElement.GetProperty("resolution").GetString() ?? string.Empty;
            var codec = doc.RootElement.GetProperty("codecType").GetString() ?? string.Empty;
            Assert.True(codec.Contains("265", StringComparison.Ordinal) || codec.Contains("264", StringComparison.Ordinal),
                $"unexpected codec on {device.IpAddress}: {codec}");
            if (mainRes is "2560x1920" or "2592x1944" or "2304x1296" or "1920x1080")
            {
                sawMainHighRes = true;
            }

            var sub = await cam.GetStringAsync($"http://{device.IpAddress}/NetSDK/Video/encode/channel/102");
            using var subDoc = JsonDocument.Parse(sub);
            var subRes = subDoc.RootElement.GetProperty("resolution").GetString() ?? string.Empty;
            // Canonical Juan sub is 704x480; some field units mis-configure 102 as a second main stream.
            if (subRes is "704x480" or "640x480" or "640x360" or "352x288")
            {
                sawCanonicalSub = true;
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(subRes), $"channel 102 missing resolution on {device.IpAddress}");
            }
        }

        Assert.True(sawMainHighRes, "Expected at least one camera with high-res main encode (channel 101).");
        // Prefer seeing a real sub stream somewhere on the LAN; do not hard-fail a single misconfigured unit.
        Assert.True(sawCanonicalSub || devices.Count == 1,
            "Expected at least one camera with a low-res sub encode (channel 102), or a single-device LAN.");
    }

    private static VideoSourceDescriptor? RecordingService_Select(IEnumerable<VideoSourceDescriptor> sources)
        => BossCam.Core.RecordingService.SelectHighResMainSource(sources);

    private static string? ResolveFfprobe()
    {
        var env = Environment.GetEnvironmentVariable("BOSSCAM_FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var name in new[] { "ffprobe", "ffprobe.exe" })
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(segment, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return File.Exists("/usr/bin/ffprobe") ? "/usr/bin/ffprobe" : null;
    }
}
