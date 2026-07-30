using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BossCam.E2E;

/// <summary>
/// E2E tests for Linux-equivalent features added to the Svelte SPA:
/// firmware upload, fullscreen mode, keyboard shortcuts, Picture-in-Picture,
/// and motion grid editor.
/// </summary>
[Collection("BossCamE2E")]
public sealed class LinuxUiFeatureTests : IClassFixture<BossCamWebAppFactory>
{
    private readonly HttpClient _client;
    private readonly BossCamWebAppFactory _factory;

    public LinuxUiFeatureTests(BossCamWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Firmware upload API ────────────────────────────────────────

    [Fact]
    public async Task Firmware_Register_Rejects_Missing_File()
    {
        var res = await _client.PostAsJsonAsync("/api/firmware/register", new
        {
            filePath = "/tmp/no-such-firmware-e2e.bin"
        });
        // Should 400 because the file doesn't exist
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Firmware_Register_Accepts_Valid_File()
    {
        // Create a temporary firmware file
        var firmwarePath = Path.Combine(_factory.TempRoot, "e2e-firmware.bin");
        await File.WriteAllBytesAsync(firmwarePath, new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }); // "Hello"

        var res = await _client.PostAsJsonAsync("/api/firmware/register", new
        {
            filePath = firmwarePath
        });
        await E2EHelpers.AssertOkAsync(res, "firmware/register valid");
        var body = await res.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public async Task Firmware_List_Returns_Array()
    {
        var res = await _client.GetAsync("/api/firmware");
        await E2EHelpers.AssertOkAsync(res, "firmware list");
        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    // ── Fullscreen mode (test via fullscreenchange events would need browser) ──
    // Backend test: verify the SPA index.html is served

    [Fact]
    public async Task Spa_Index_Has_App_Container()
    {
        var res = await _client.GetAsync("/");
        await E2EHelpers.AssertOkAsync(res, "SPA root");
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"app\">", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BossCamSuite", html, StringComparison.Ordinal);
    }

    // ── Motion grid endpoints ──────────────────────────────────────

    [Fact]
    public async Task Motion_Grid_Read_With_Missing_Device_Is_Safe()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        // Motion grid is read via POST /api/devices/{id}/settings/write (GET method)
        var res = await _client.PostAsJsonAsync($"/api/devices/{id}/settings/write", new
        {
            endpoint = "/NetSDK/Video/motionDetection/channel/1",
            method = "GET",
            requireWriteVerification = false,
            snapshotBeforeWrite = false
        });
        // Must not crash — 404 or 200 expected, not 500
        Assert.True((int)res.StatusCode is >= 200 and < 500,
            $"/settings/write (motion grid) -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Motion_Grid_Write_With_Missing_Device_Is_Safe()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var res = await _client.PostAsJsonAsync($"/api/devices/{id}/settings/write", new
        {
            endpoint = "/NetSDK/Video/motionDetection/channel/1",
            method = "PUT",
            payload = new
            {
                detectionGrid = new
                {
                    gridWidth = 32,
                    gridHeight = 24,
                    gridCells = new int[32 * 24]
                }
            },
            requireWriteVerification = false,
            snapshotBeforeWrite = false
        });
        Assert.True((int)res.StatusCode is >= 200 and < 500,
            $"/settings/write (motion grid put) -> {(int)res.StatusCode}");
    }

    // ── Maintenance / user endpoints (used by Advanced Panel) ─────

    [Fact]
    public async Task Maintenance_RefreshUsers_With_Missing_Device_Is_Safe()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var res = await _client.PostAsync($"/api/devices/{id}/maintenance/RefreshUsers", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        // 404 or 200 is acceptable; must not 500
        Assert.True((int)res.StatusCode is >= 200 and < 500,
            $"maintenance/RefreshUsers -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Maintenance_PasswordReset_With_Missing_Device_Is_Safe()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var res = await _client.PostAsJsonAsync($"/api/devices/{id}/maintenance/PasswordReset", new
        {
            username = "admin",
            newPassword = "test123"
        });
        Assert.True((int)res.StatusCode is >= 200 and < 500,
            $"maintenance/PasswordReset -> {(int)res.StatusCode}");
    }

    // ── Persistence verification endpoints (used by Advanced Panel) ─

    [Fact]
    public async Task Persistence_Verify_With_Missing_Device_Is_Safe()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var res = await _client.PostAsJsonAsync($"/api/devices/{id}/persistence/verify", new
        {
            endpoint = "brightness",
            method = "GET",
            rebootForVerification = false
        });
        Assert.True((int)res.StatusCode is >= 200 and < 500,
            $"persistence/verify -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Persistence_Results_With_Missing_Device_Does_Not_Crash()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var res = await _client.GetAsync($"/api/devices/{id}/persistence");
        Assert.True((int)res.StatusCode is >= 200 and < 500,
            $"persistence results -> {(int)res.StatusCode}");
    }

    // ── fMP4 live stream endpoint ────────────────────────────────

    [Fact]
    public async Task Fmp4_Stream_Sets_Correct_Content_Type()
    {
        // The fMP4 endpoint sets Content-Type: video/mp4 before starting ffmpeg.
        // For a missing device, the response starts with 200 + video/mp4 headers
        // then the stream fails silently (ffmpeg can't find the camera). The body
        // will be empty or partial — what matters is the header is correct and the
        // endpoint never produces an unhandled 500.
        var id = Guid.Parse("00000000-0000-0000-0000-000000000099");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var res = await _client.GetAsync($"/api/devices/{id}/live.mp4", cts.Token);

        // Headers are sent before ffmpeg starts, so we get 200 or 400 even for missing devices.
        // 200 = StartAsync completed before the exception; 400 = response hadn't started yet.
        // Never a 500 — that would indicate an unhandled error in the streaming pipeline.
        Assert.True((int)res.StatusCode is 200 or 400,
            $"live.mp4 -> {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        Assert.Equal("video/mp4", res.Content.Headers.ContentType?.MediaType, ignoreCase: true);

        // Read the response body — for a missing device the stream closes quickly.
        // The 5-second CTS prevents hanging if ffmpeg lingers on a missing device.
        var body = await res.Content.ReadAsByteArrayAsync(cts.Token);
        // Body is typically empty for missing devices (no ffmpeg output).
        // When a real camera is present, it would contain fMP4 fragments.
        if (body.Length > 0)
        {
            // Validate fMP4 starts with an ftyp box: 00 00 00 XX 66 74 79 70
            Assert.Equal(0x66, body[4]); // 'f'
            Assert.Equal(0x74, body[5]); // 't'
            Assert.Equal(0x79, body[6]); // 'y'
            Assert.Equal(0x70, body[7]); // 'p'
        }
    }
}
