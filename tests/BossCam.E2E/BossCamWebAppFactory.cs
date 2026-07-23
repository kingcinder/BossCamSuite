using System.Net.Http.Json;
using BossCam.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BossCam.E2E;

/// <summary>
/// In-process Ubuntu-compatible host for exhaustive API E2E tests.
/// Uses an isolated temp SQLite DB so production data is never touched.
/// </summary>
public sealed class BossCamWebAppFactory : WebApplicationFactory<Program>
{
    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "bosscam-e2e-" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(TempRoot, "bosscam-e2e.db");

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
                ["BossCam:RecordingStartupReconcileDelaySeconds"] = "3600"
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

public static class E2EHelpers
{
    // Prefer units with healthy NetSDK snapShot when BOSSCAM_E2E_IPS is unset (.170/.228 proven; .30 may 403).
    public static readonly string[] LiveCameraIps =
        (Environment.GetEnvironmentVariable("BOSSCAM_E2E_IPS") ?? "10.0.0.170,10.0.0.228,10.0.0.30")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool LiveEnabled
        => !string.Equals(Environment.GetEnvironmentVariable("BOSSCAM_E2E_LIVE"), "0", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> IsReachableAsync(string ip, int port = 80, int timeoutMs = 1500)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}:{port}/NetSDK/System/deviceInfo");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:")));
            using var res = await client.SendAsync(req, cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<DeviceIdentity?> RegisterJuanAsync(HttpClient client, string ip)
    {
        var result = await client.PostAsJsonAsync("/api/devices/register", new
        {
            ipAddress = ip,
            port = 80,
            loginName = "admin",
            password = "",
            name = $"E2E-{ip}",
            hardwareModel = "5523-W"
        });
        result.EnsureSuccessStatusCode();
        return await result.Content.ReadFromJsonAsync<DeviceIdentity>();
    }

    public static async Task AssertOkAsync(HttpResponseMessage response, string context)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        Assert.Fail($"{context}: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
    }
}
