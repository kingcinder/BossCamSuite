using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BossCam.E2E;

/// <summary>
/// E2E coverage for the host-aware LAN bearer-token gate. Drives the actual
/// Program.cs startup path so the bind-vs-token matrix is asserted in an
/// in-process ASP.NET Core host rather than a static unit test.
/// </summary>
public static class LanBoundAuthFactories
{
    private const string EnvVarName = "BOSSCAM_LAN_TOKEN";

    /// <summary>
    /// LAN-bind factory that resolves the expected token ONLY via the
    /// <c>BOSSCAM_LAN_TOKEN</c> environment variable (<c>BossCam:LanAuthToken</c>
    /// stays empty). The middleware is engaged with that env-var token.
    /// </summary>
    public sealed class EnvVarTokenFactory : WebApplicationFactory<Program>
    {
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "bosscam-envvar-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(TempRoot, "bosscam-envvar.db");
        public string Token { get; } = "envvar-token-" + Guid.NewGuid().ToString("N");

        public EnvVarTokenFactory()
        {
            Environment.SetEnvironmentVariable(EnvVarName, Token);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(TempRoot);
            Directory.CreateDirectory(Path.Combine(TempRoot, "firmware"));

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Non-loopback bind so the host-aware gate engages.
                    ["BossCam:LocalApiBaseUrl"] = "http://0.0.0.0:0",
                    ["BossCam:DatabasePath"] = DatabasePath,
                    ["BossCam:FirmwareArtifactDirectory"] = Path.Combine(TempRoot, "firmware"),
                    ["BossCam:IpcamSuiteDirectory"] = string.Empty,
                    ["BossCam:EseeCloudDirectory"] = string.Empty,
                    ["BossCam:EseeCloudDataDirectory"] = Path.Combine(TempRoot, "esee"),
                    ["BossCam:DiscoveryTimeoutSeconds"] = "1",
                    ["BossCam:HttpTimeoutSeconds"] = "6",
                    ["BossCam:RecordingHousekeepingMinutes"] = "60",
                    ["BossCam:RecordingStartupReconcileDelaySeconds"] = "3600",
                    ["BossCam:DiscoveryOfflineMode"] = "true",
                    // Token is sourced from the env var; config stays empty so we exercise
                    // the env-var priority path.
                    ["BossCam:LanAuthToken"] = string.Empty,
                    ["BossCam:RateLimitEnabled"] = "false",
                    ["BossCam:StorageRoot"] = Path.Combine(TempRoot, "recordings")
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                Environment.SetEnvironmentVariable(EnvVarName, null);
            }
            catch
            {
                // best-effort: don't poison the next fixture if the env var cannot be cleared
            }
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

    /// <summary>
    /// LAN-bind factory that uses <c>BossCam:LanAuthToken</c> config as the
    /// only token source. Confirms the env-var-proxies-via-config fallback path
    /// still works on a non-loopback bind.
    /// </summary>
    public sealed class ConfigTokenFallbackFactory : WebApplicationFactory<Program>
    {
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "bosscam-cfg-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(TempRoot, "bosscam-cfg.db");
        public string Token { get; } = "cfg-token-" + Guid.NewGuid().ToString("N");

        public ConfigTokenFallbackFactory()
        {
            // Clear any stray BOSSCAM_LAN_TOKEN that might have leaked from a sibling
            // fixture so the cfg-token fallback path is the only thing exercised here.
            Environment.SetEnvironmentVariable(EnvVarName, null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(TempRoot);
            Directory.CreateDirectory(Path.Combine(TempRoot, "firmware"));

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BossCam:LocalApiBaseUrl"] = "http://0.0.0.0:0",
                    ["BossCam:DatabasePath"] = DatabasePath,
                    ["BossCam:FirmwareArtifactDirectory"] = Path.Combine(TempRoot, "firmware"),
                    ["BossCam:IpcamSuiteDirectory"] = string.Empty,
                    ["BossCam:EseeCloudDirectory"] = string.Empty,
                    ["BossCam:EseeCloudDataDirectory"] = Path.Combine(TempRoot, "esee"),
                    ["BossCam:DiscoveryTimeoutSeconds"] = "1",
                    ["BossCam:HttpTimeoutSeconds"] = "6",
                    ["BossCam:RecordingHousekeepingMinutes"] = "60",
                    ["BossCam:RecordingStartupReconcileDelaySeconds"] = "3600",
                    ["BossCam:DiscoveryOfflineMode"] = "true",
                    ["BossCam:LanAuthToken"] = Token,
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

    /// <summary>
    /// LAN-bind factory with NO token configured anywhere. Program.cs must
    /// refuse to start with <see cref="InvalidOperationException"/> describing
    /// how to set the <c>BOSSCAM_LAN_TOKEN</c> env var.
    /// </summary>
    public sealed class FailFastFactory : WebApplicationFactory<Program>
    {
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "bosscam-fail-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(TempRoot, "bosscam-fail.db");

        public FailFastFactory()
        {
            // Ensure no stray env var from a sibling fixture survives into this test.
            Environment.SetEnvironmentVariable(EnvVarName, null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(TempRoot);

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BossCam:LocalApiBaseUrl"] = "http://0.0.0.0:0",
                    ["BossCam:DatabasePath"] = DatabasePath,
                    ["BossCam:FirmwareArtifactDirectory"] = Path.Combine(TempRoot, "firmware"),
                    ["BossCam:IpcamSuiteDirectory"] = string.Empty,
                    ["BossCam:EseeCloudDirectory"] = string.Empty,
                    ["BossCam:EseeCloudDataDirectory"] = Path.Combine(TempRoot, "esee"),
                    ["BossCam:DiscoveryTimeoutSeconds"] = "1",
                    ["BossCam:HttpTimeoutSeconds"] = "6",
                    ["BossCam:RecordingHousekeepingMinutes"] = "60",
                    ["BossCam:RecordingStartupReconcileDelaySeconds"] = "3600",
                    ["BossCam:DiscoveryOfflineMode"] = "true",
                    ["BossCam:LanAuthToken"] = string.Empty
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
}

[Collection("BossCamE2E")]
public sealed class LanBoundEnvVarAuthE2ETests : IClassFixture<LanBoundAuthFactories.EnvVarTokenFactory>
{
    private readonly LanBoundAuthFactories.EnvVarTokenFactory _factory;
    private readonly HttpClient _client;

    public LanBoundEnvVarAuthE2ETests(LanBoundAuthFactories.EnvVarTokenFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Open_Path_Health_Stays_Open_Under_Lan_Bind_Envar_Token()
    {
        var res = await _client.GetAsync("/api/health");
        Assert.True(res.IsSuccessStatusCode, $"/api/health -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Static_Asset_Gets_Served_Under_Lan_Bind_Envar_Token()
    {
        // SPA bundles now emit content-hashed files under /assets/, so a hardcoded
        // /app.js would 404 after every rebuild. Pick a real file from wwwroot at
        // test time (same discovery pattern as UbuntuPlatformAndStaticUiTests), and
        // fall back to the stable /index.html entry point.
        var webRoot = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "BossCam.Service", "wwwroot");
        var assetsDir = Path.Combine(webRoot, "assets");
        var assetPath = "/index.html";
        if (Directory.Exists(assetsDir))
        {
            var file = Directory.EnumerateFiles(assetsDir).FirstOrDefault();
            if (file is not null)
            {
                assetPath = "/assets/" + Path.GetFileName(file);
            }
        }

        var res = await _client.GetAsync(assetPath);
        Assert.True(res.IsSuccessStatusCode, $"{assetPath} -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Gated_Path_Returns_401_When_Env_Var_Token_Missing()
    {
        var res = await _client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("LAN token required", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gated_Path_Accepts_Env_Var_Token_Via_X_LAN_Header()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        msg.Headers.Add("X-LAN-Token", _factory.Token);
        var res = await _client.SendAsync(msg);
        Assert.True(res.IsSuccessStatusCode, $"X-LAN-Token path -> {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Gated_Path_Accepts_Env_Var_Token_Via_Bearer()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token);
        var res = await _client.GetAsync("/api/devices");
        Assert.True(res.IsSuccessStatusCode, $"Bearer path -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [Fact]
    public async Task Gated_Path_With_Wrong_Token_Returns_401()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        msg.Headers.Add("X-LAN-Token", "nope-" + Guid.NewGuid().ToString("N"));
        var res = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

[Collection("BossCamE2E")]
public sealed class LanBoundConfigAuthE2ETests : IClassFixture<LanBoundAuthFactories.ConfigTokenFallbackFactory>
{
    private readonly LanBoundAuthFactories.ConfigTokenFallbackFactory _factory;
    private readonly HttpClient _client;

    public LanBoundConfigAuthE2ETests(LanBoundAuthFactories.ConfigTokenFallbackFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Config_Token_Fallback_Engages_Gate_On_Lan_Bind()
    {
        // Without BOSSCAM_LAN_TOKEN env var (cleared by sibling fixtures / xUnit collection
        // ordering), the BossCam:LanAuthToken config value is the only token source.
        // Sanity: the gate is engaged, so an unauthenticated /api/devices request 401s.
        var noToken = await _client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // With the config token the gate accepts the request.
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        msg.Headers.Add("X-LAN-Token", _factory.Token);
        var yesToken = await _client.SendAsync(msg);
        Assert.True(yesToken.IsSuccessStatusCode, $"with config token -> {(int)yesToken.StatusCode}");
    }
}

/// <summary>
/// Independent test class to keep the fail-fast factory lifecycle separate.
/// </summary>
public sealed class LanBoundFailFastE2ETests
{
    [Fact]
    public void NonLoopback_Bind_Without_Token_Refuses_To_Start()
    {
        // Constructing the factory is lazy; the throw happens when WebApplicationFactory
        // builds the host (which runs Program.cs and resolves bind-vs-token policy).
        Environment.SetEnvironmentVariable("BOSSCAM_LAN_TOKEN", null);
        using var factory = new LanBoundAuthFactories.FailFastFactory();

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("BOSSCAM_LAN_TOKEN", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LAN bearer token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", ex.Message, StringComparison.OrdinalIgnoreCase); // mentions loopback fallback
    }
}
