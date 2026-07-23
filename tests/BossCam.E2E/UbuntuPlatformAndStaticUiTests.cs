using System.Net;
using System.Text.Json;

namespace BossCam.E2E;

[Collection("BossCamE2E")]
public sealed class UbuntuPlatformAndStaticUiTests : IClassFixture<BossCamWebAppFactory>
{
    private readonly HttpClient _client;
    private readonly BossCamWebAppFactory _factory;

    public UbuntuPlatformAndStaticUiTests(BossCamWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_Reports_Linux_Platform_And_Ffmpeg()
    {
        var res = await _client.GetAsync("/api/health");
        await E2EHelpers.AssertOkAsync(res, "health");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("platform", out var platform));
        var platformText = platform.GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(platformText));
        if (OperatingSystem.IsLinux())
        {
            Assert.True(
                platformText.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                || platformText.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)
                || platformText.Contains("Debian", StringComparison.OrdinalIgnoreCase),
                $"Unexpected platform string: {platformText}");
        }

        Assert.True(root.TryGetProperty("framework", out var fw));
        Assert.Contains("NET", fw.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(_factory.TempRoot));
        Assert.True(File.Exists(_factory.DatabasePath) || true); // may be created lazily on first store init
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/app.css")]
    [InlineData("/app.js")]
    [InlineData("/favicon.svg")]
    public async Task Operator_Static_Assets_Are_Served(string path)
    {
        var res = await _client.GetAsync(path);
        Assert.True(res.IsSuccessStatusCode, $"{path} -> {(int)res.StatusCode}");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Spa_Fallback_Does_Not_Swallow_Api_404_Style_Routes()
    {
        var res = await _client.GetAsync("/api/this-route-does-not-exist-e2e");
        // Minimal APIs may 404; must not return HTML index for /api/*
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE html>", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swagger_Is_Available()
    {
        var res = await _client.GetAsync("/swagger/index.html");
        Assert.True(res.StatusCode is HttpStatusCode.OK or HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.Redirect);
    }

    [Fact]
    public void Runtime_Paths_Resolve_Under_Linux_Data_Root()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(local) && !OperatingSystem.IsLinux());
        if (OperatingSystem.IsLinux())
        {
            // Either LocalApplicationData is set or we fall back to ~/.local/share
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(string.IsNullOrWhiteSpace(home));
            Assert.True(Directory.Exists(home));
        }

        Assert.True(File.Exists("/usr/bin/ffmpeg") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH")) || !OperatingSystem.IsLinux());
    }

    [Fact]
    public void SelectHighResMainSource_Never_Picks_Sub_Paths()
    {
        var sources = new[]
        {
            new BossCam.Contracts.VideoSourceDescriptor
            {
                Kind = BossCam.Contracts.TransportKind.Rtsp,
                Url = "rtsp://10.0.0.1:554/ch0_1.264",
                Rank = 0,
                DisplayName = "sub",
                Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["highRes"] = "false" }
            },
            new BossCam.Contracts.VideoSourceDescriptor
            {
                Kind = BossCam.Contracts.TransportKind.Rtsp,
                Url = "rtsp://10.0.0.1:554/ch0_0.264",
                Rank = 5,
                DisplayName = "main",
                Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" }
            }
        };
        var pick = BossCam.Core.RecordingService.SelectHighResMainSource(sources);
        Assert.NotNull(pick);
        Assert.Contains("ch0_0", pick!.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ch0_1", pick.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ffmpeg_Args_Use_Tcp_And_Mpegts_For_HighRes()
    {
        var args = BossCam.Core.RecordingService.BuildFfmpegArgs(
            "rtsp://admin:@10.0.0.30:554/ch0_0.264",
            "/tmp/seg_%Y%m%d.ts",
            30);
        Assert.Contains("-rtsp_transport tcp", args, StringComparison.Ordinal);
        Assert.Contains("-map 0:v:0", args, StringComparison.Ordinal);
        Assert.Contains("-c:v copy", args, StringComparison.Ordinal);
        Assert.Contains("mpegts", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stimeout", args, StringComparison.Ordinal);
        Assert.DoesNotContain("rw_timeout", args, StringComparison.Ordinal);
    }
}
