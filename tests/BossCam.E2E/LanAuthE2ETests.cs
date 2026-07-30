using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BossCam.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BossCam.E2E;

/// <summary>
/// Factory variant that enables the LAN auth middleware so gate behaviour can be exercised.
/// </summary>
public sealed class LanAuthWebAppFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "bosscam-auth-" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(TempRoot, "bosscam-auth.db");
    public string Token { get; } = "lan-test-token-" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(Path.Combine(TempRoot, "recordings"));
        Directory.CreateDirectory(Path.Combine(TempRoot, "firmware"));

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BossCam:LocalApiBaseUrl"] = "http://127.0.0.1:0",
                ["BossCam:DatabasePath"] = DatabasePath,
                ["BossCam:FirmwareArtifactDirectory"] = Path.Combine(TempRoot, "firmware"),
                ["BossCam:IpcamSuiteDirectory"] = string.Empty,
                ["BossCam:EseeCloudDirectory"] = string.Empty,
                ["BossCam:EseeCloudDataDirectory"] = Path.Combine(TempRoot, "esee"),
                ["BossCam:DiscoveryTimeoutSeconds"] = "1",
                ["BossCam:HttpTimeoutSeconds"] = "6",
                ["BossCam:RecordingHousekeepingMinutes"] = "60",
                ["BossCam:RecordingStartupReconcileDelaySeconds"] = "3600",
                ["BossCam:LanAuthToken"] = Token,
                // E2E opt-out: see BossCamWebAppFactory.cs for the rationale.
                ["BossCam:RateLimitEnabled"] = "false",
                ["BossCam:StorageRoot"] = Path.Combine(TempRoot, "recordings")
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

[Collection("BossCamE2E")]
public sealed class LanAuthE2ETests : IClassFixture<LanAuthWebAppFactory>
{
    private readonly LanAuthWebAppFactory _factory;
    private readonly HttpClient _client;

    public LanAuthE2ETests(LanAuthWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    [InlineData("/favicon.svg")]
    [InlineData("/api/health")]
    public async Task Open_Paths_Do_Not_Require_Token(string path)
    {
        var res = await _client.GetAsync(path);
        // /api/health must always return 200; static assets must always be served.
        Assert.True(res.IsSuccessStatusCode, $"{path} -> {(int)res.StatusCode}");
    }

    [Theory]
    [InlineData("/api/devices")]
    [InlineData("/api/protocols")]
    [InlineData("/api/recordings")]
    [InlineData("/api/highlights")]
    [InlineData("/swagger/index.html")]
    public async Task Gated_Paths_Return_401_When_Token_Missing(string path)
    {
        var res = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("LAN token required", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gated_Path_Accepts_X_LAN_Token_Header()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        msg.Headers.Add("X-LAN-Token", _factory.Token);
        var res = await _client.SendAsync(msg);
        Assert.True(res.IsSuccessStatusCode, $"with X-LAN-Token -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Gated_Path_Accepts_Bearer_Token()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token);
        var res = await _client.GetAsync("/api/devices");
        Assert.True(res.IsSuccessStatusCode, $"with Bearer -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Gated_Path_Rejects_Wrong_Token_With_401()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        msg.Headers.Add("X-LAN-Token", "totally-wrong-" + Guid.NewGuid().ToString("N"));
        var res = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Gated_Path_Rejects_Empty_Token_With_401()
    {
        // Sanity: empty string must not silently match an empty configured token.
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        msg.Headers.Add("X-LAN-Token", "");
        var res = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Device_Register_Without_Token_Is_Blocked()
    {
        var reg = await _client.PostAsJsonAsync("/api/devices/register", new
        {
            ipAddress = "127.0.0.1",
            port = 9,
            loginName = "admin",
            password = "",
            name = "auth-bypass-attempt",
            hardwareModel = "fake"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, reg.StatusCode);
    }

    [Fact]
    public async Task Device_Register_With_Token_Succeeds()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            Content = JsonContent.Create(new
            {
                ipAddress = "127.0.0.99",
                port = 9,
                loginName = "admin",
                password = "",
                name = "auth-ok",
                hardwareModel = "fake"
            })
        };
        msg.Headers.Add("X-LAN-Token", _factory.Token);
        var res = await _client.SendAsync(msg);
        Assert.True(res.IsSuccessStatusCode, $"register with token -> {(int)res.StatusCode}");
    }
}
