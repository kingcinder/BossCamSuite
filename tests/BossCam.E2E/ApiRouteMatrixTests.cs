using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;

namespace BossCam.E2E;

/// <summary>
/// Exhaustive HTTP surface matrix: every documented route is exercised at least once.
/// Unknown/missing device routes must not 500.
/// </summary>
[Collection("BossCamE2E")]
public sealed class ApiRouteMatrixTests : IClassFixture<BossCamWebAppFactory>
{
    private readonly HttpClient _client;
    private static readonly Guid MissingId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    public ApiRouteMatrixTests(BossCamWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    public static IEnumerable<object[]> SafeGetRoutes()
    {
        yield return ["/api/health"];
        yield return ["/api/devices"];
        yield return ["/api/protocols"];
        yield return ["/api/firmware"];
        yield return ["/api/firmware/capabilities"];
        yield return ["/api/contracts/endpoints"];
        yield return ["/api/contracts/fixtures"];
        yield return ["/api/diagnostics/audit"];
        yield return ["/api/diagnostics/transcripts"];
        yield return ["/api/probe/sessions"];
        yield return ["/api/truth/sweep"];
        yield return ["/api/grouped-config/sdk-field-catalog"];
        yield return ["/api/recordings"];
        yield return ["/api/recordings/jobs"];
        yield return ["/api/recordings/index"];
        yield return ["/api/highlights"];
    }

    [Theory]
    [MemberData(nameof(SafeGetRoutes))]
    public async Task Global_Get_Routes_Do_Not_Crash(string path)
    {
        var res = await _client.GetAsync(path);
        Assert.True((int)res.StatusCode is >= 200 and < 500, $"{path} returned {(int)res.StatusCode}");
        // body must be readable
        _ = await res.Content.ReadAsStringAsync();
    }

    public static IEnumerable<object[]> MissingDeviceGetRoutes()
    {
        var id = MissingId;
        yield return [$"/api/devices/{id}/capabilities"];
        yield return [$"/api/devices/{id}/settings"];
        yield return [$"/api/devices/{id}/settings/last"];
        yield return [$"/api/devices/{id}/settings/typed"];
        yield return [$"/api/devices/{id}/control-points"];
        yield return [$"/api/devices/{id}/endpoint-surface"];
        yield return [$"/api/devices/{id}/sources"];
        yield return [$"/api/devices/{id}/preview"];
        yield return [$"/api/devices/{id}/snapshot"];
        yield return [$"/api/devices/{id}/validation"];
        yield return [$"/api/devices/{id}/validation/transcripts"];
        yield return [$"/api/devices/{id}/persistence"];
        yield return [$"/api/devices/{id}/persistence/eligible-fields"];
        yield return [$"/api/devices/{id}/semantic/history"];
        yield return [$"/api/devices/{id}/constraints"];
        yield return [$"/api/devices/{id}/dependencies"];
        yield return [$"/api/devices/{id}/image/inventory"];
        yield return [$"/api/devices/{id}/image/writable-test-set"];
        yield return [$"/api/devices/{id}/image/behavior-maps"];
        yield return [$"/api/devices/{id}/grouped-config/snapshots"];
        yield return [$"/api/devices/{id}/grouped-config/profiles"];
        yield return [$"/api/devices/{id}/grouped-config/retest-results"];
        yield return [$"/api/devices/{id}/native-fallback-assessment"];
    }

    [Theory]
    [MemberData(nameof(MissingDeviceGetRoutes))]
    public async Task Missing_Device_Get_Routes_Are_Stable(string path)
    {
        var res = await _client.GetAsync(path);
        // Prefer 404/200 empty; never unhandled 500
        Assert.True((int)res.StatusCode is 200 or 204 or 404 or 400 or 405,
            $"{path} -> {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Protocols_Refresh_And_List()
    {
        var refresh = await _client.PostAsync("/api/protocols/refresh", null);
        await E2EHelpers.AssertOkAsync(refresh, "protocols/refresh");
        var list = await _client.GetAsync("/api/protocols");
        await E2EHelpers.AssertOkAsync(list, "protocols");
        var body = await list.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public async Task Discover_And_Register_Paths_Accept_Requests()
    {
        var discover = await _client.PostAsync("/api/devices/discover", null);
        Assert.True((int)discover.StatusCode is >= 200 and < 500);

        var reg = await _client.PostAsJsonAsync("/api/devices/register", new
        {
            ipAddress = "127.0.0.1",
            port = 9,
            loginName = "admin",
            password = "x",
            name = "e2e-dummy",
            hardwareModel = "dummy"
        });
        // May succeed as inventory entry even if device unreachable
        Assert.True((int)reg.StatusCode is >= 200 and < 500, await reg.Content.ReadAsStringAsync());

        var many = await _client.PostAsJsonAsync("/api/devices/register-many", new[]
        {
            new { ipAddress = "127.0.0.2", port = 9, loginName = "admin", password = "", hardwareModel = "dummy" }
        });
        Assert.True((int)many.StatusCode is >= 200 and < 500);

        var aegon = await _client.PostAsJsonAsync("/api/devices/register-aegon-lan", new { lorexPassword = "", wvcPassword = "" });
        Assert.True((int)aegon.StatusCode is >= 200 and < 500, await aegon.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recording_Control_Surface_Without_Devices_Is_Safe()
    {
        var jobs = await _client.GetAsync("/api/recordings/jobs");
        await E2EHelpers.AssertOkAsync(jobs, "jobs");

        var stopAll = await _client.PostAsync("/api/recordings/stop-all", null);
        await E2EHelpers.AssertOkAsync(stopAll, "stop-all");

        var house = await _client.PostAsync("/api/recordings/housekeeping", null);
        Assert.True((int)house.StatusCode is >= 200 and < 500);

        var index = await _client.PostAsync("/api/recordings/index/refresh", null);
        await E2EHelpers.AssertOkAsync(index, "index/refresh");

        var reconcile = await _client.PostAsync("/api/recordings/reconcile", null);
        Assert.True((int)reconcile.StatusCode is >= 200 and < 500);

        var profiles = await _client.PostAsJsonAsync("/api/recordings", new[]
        {
            new RecordingProfile
            {
                DeviceId = MissingId,
                Name = "e2e",
                OutputDirectory = Path.Combine(Path.GetTempPath(), "bosscam-e2e-profiles"),
                SegmentSeconds = 30,
                Enabled = false,
                AutoStart = false
            }
        });
        Assert.True(profiles.IsSuccessStatusCode || profiles.StatusCode == HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Highlights_Board_Surface_Works_Empty_And_With_Devices()
    {
        var state = await _client.GetAsync("/api/highlights");
        await E2EHelpers.AssertOkAsync(state, "highlights");

        var next = await _client.PostAsync("/api/highlights/next", null);
        await E2EHelpers.AssertOkAsync(next, "highlights/next");
        var prev = await _client.PostAsync("/api/highlights/prev", null);
        await E2EHelpers.AssertOkAsync(prev, "highlights/prev");
        var main = await _client.PostAsync("/api/highlights/stream/main", null);
        await E2EHelpers.AssertOkAsync(main, "highlights/stream/main");
        var sub = await _client.PostAsync("/api/highlights/stream/sub", null);
        await E2EHelpers.AssertOkAsync(sub, "highlights/stream/sub");
    }

    [Fact]
    public async Task Missing_Device_Write_And_Probe_Posts_Do_Not_500()
    {
        var id = MissingId;
        var posts = new (string Path, HttpContent? Body)[]
        {
            ($"/api/devices/{id}/probe", null),
            ($"/api/devices/{id}/validation/run", JsonContent.Create(new { })),
            ($"/api/devices/{id}/settings/typed/refresh", null),
            ($"/api/devices/{id}/settings/typed/apply", JsonContent.Create(new { fieldKey = "brightness", value = 50, expertOverride = true })),
            ($"/api/devices/{id}/settings/typed/apply-batch", JsonContent.Create(new { changes = Array.Empty<object>(), expertOverride = true })),
            ($"/api/devices/{id}/settings/write", JsonContent.Create(new { endpoint = "/NetSDK/System/deviceInfo", method = "GET" })),
            ($"/api/devices/{id}/maintenance/Reboot", JsonContent.Create(new { })),
            ($"/api/devices/{id}/image/truth-sweep", JsonContent.Create(new { includeBehaviorMapping = false, refreshFromDevice = false })),
            ($"/api/devices/{id}/grouped-config/retest-unsupported", JsonContent.Create(new { })),
            ($"/api/devices/{id}/grouped-config/probe-families", JsonContent.Create(new { })),
            ($"/api/devices/{id}/grouped-config/probe-pipeline-ownership", JsonContent.Create(new { })),
            ($"/api/devices/{id}/grouped-config/force-enumerate-sdk-fields", JsonContent.Create(new { })),
            ($"/api/devices/{id}/network/recovery", JsonContent.Create(new { deviceId = id })),
            ($"/api/devices/{id}/persistence/verify", JsonContent.Create(new { deviceId = id, fieldKey = "brightness" })),
            ($"/api/devices/{id}/persistence/verify-field", JsonContent.Create(new { fieldKey = "brightness", value = 50, rebootForVerification = false, expertOverride = true })),
            ($"/api/devices/{id}/constraints/discover", JsonContent.Create(new { deviceId = id, fieldKeys = new[] { "brightness" } })),
            ($"/api/devices/{id}/playback/find-file", JsonContent.Create(new { beginTime = DateTimeOffset.UtcNow.AddHours(-1), endTime = DateTimeOffset.UtcNow })),
            ($"/api/devices/{id}/playback/find-next-file", JsonContent.Create(new { beginTime = DateTimeOffset.UtcNow.AddHours(-1), endTime = DateTimeOffset.UtcNow })),
            ($"/api/devices/{id}/playback/get-file-by-time", JsonContent.Create(new { beginTime = DateTimeOffset.UtcNow.AddHours(-1), endTime = DateTimeOffset.UtcNow })),
            ($"/api/devices/{id}/playback/playback-by-time", JsonContent.Create(new { beginTime = DateTimeOffset.UtcNow.AddHours(-1), endTime = DateTimeOffset.UtcNow })),
            ($"/api/devices/{id}/playback/find-close", JsonContent.Create(new { })),
            ($"/api/devices/{id}/playback/playback-by-name", JsonContent.Create(new { fileName = "x" })),
            ($"/api/devices/{id}/playback/get-file-by-name", JsonContent.Create(new { fileName = "x" })),
            ($"/api/devices/{id}/playback/stop-get-file", JsonContent.Create(new { })),
            ($"/api/devices/{id}/playback/playback-save-data", JsonContent.Create(new { })),
            ($"/api/devices/{id}/playback/stop-playback-save", JsonContent.Create(new { })),
            ($"/api/contracts/fixtures/promote/{id}", JsonContent.Create(new { exportRoot = Path.GetTempPath() })),
            ($"/api/probe/sessions/start", JsonContent.Create(new { deviceIp = "127.0.0.1", mode = "InventoryOnly" })),
            ($"/api/firmware/register", JsonContent.Create(new { filePath = "/tmp/no-such-firmware.bin" })),
            ($"/api/recordings/export", JsonContent.Create(new { deviceId = id, startTime = DateTimeOffset.UtcNow.AddHours(-1), endTime = DateTimeOffset.UtcNow, outputPath = Path.Combine(Path.GetTempPath(), "clip.mp4") })),
            ($"/api/recordings/start", JsonContent.Create(new { deviceId = id })),
            ($"/api/recordings/start-all", null),
            ($"/api/recordings/stop/{MissingId}", null),
            ($"/api/highlights/select/{MissingId}", null),
            ($"/api/highlights/record-selected", null),
        };

        foreach (var (pathTemplate, body) in posts)
        {
            var path = pathTemplate
                .Replace("{id}", id.ToString())
                .Replace("{MissingId}", MissingId.ToString());
            using var content = body;
            var res = await _client.PostAsync(path, content);
            Assert.True((int)res.StatusCode is >= 200 and < 500 || res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.BadRequest || res.StatusCode == HttpStatusCode.UnsupportedMediaType,
                $"{path} -> {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Sdk_Field_Catalog_Is_Non_Empty()
    {
        var res = await _client.GetAsync("/api/grouped-config/sdk-field-catalog");
        await E2EHelpers.AssertOkAsync(res, "sdk-field-catalog");
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() > 0);
    }
}
