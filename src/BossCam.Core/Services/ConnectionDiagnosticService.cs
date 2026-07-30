using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

/// <summary>
/// Battery of connectivity diagnostics for a camera device. Probes all known
/// transports (RTSP, HTTP, ONVIF, Ping) and produces a structured report with
/// individual pass/fail per path, a composite health verdict, and suggested
/// recovery actions.
/// </summary>
// CS9113: 'logger' is reserved for future diagnostic logging; supressed intentionally
#pragma warning disable CS9113
public sealed class ConnectionDiagnosticService(
    IApplicationStore store,
    IHttpClientFactory httpClientFactory,
    TransportBroker transportBroker,
    ILogger<ConnectionDiagnosticService> logger)
#pragma warning restore CS9113
{
    /// <summary>
    /// Run the full diagnostic battery against a device and return the report.
    /// </summary>
    public async Task<DeviceDiagnosticReport> DiagnoseAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return new DeviceDiagnosticReport
            {
                DeviceId = deviceId,
                Success = false,
                Summary = "Device not found in store.",
                Verdict = ConnectivityDiagnosticVerdict.DeviceNotFound
            };
        }

        var results = new Dictionary<string, ProbeResult>();
        var ip = device.IpAddress;
        var port = device.Port <= 0 ? 80 : device.Port;
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var pass = device.Password ?? string.Empty;

        // 1. Ping test
        results["ping"] = await ProbePingAsync(ip!, cancellationToken);

        // 2. TCP port scan (common camera ports)
        foreach (var probePort in new[] { 80, 554, 8080, 8000, 8899, 8888, 37777, 34567 })
        {
            if (cancellationToken.IsCancellationRequested) break;
            results[$"tcp:{probePort}"] = await ProbeTcpPortAsync(ip!, probePort, cancellationToken);
        }

        // 3. HTTP API reachability
        results["http:deviceInfo"] = await ProbeHttpGetAsync(
            $"http://{ip}:{port}/NetSDK/System/deviceInfo", user, pass, cancellationToken);

        // 4. RTSP reachability (TCP :554)
        results["rtsp:main"] = await ProbeTcpPortAsync(ip!, 554, cancellationToken);

        // 5. ONVIF device service (try common ports)
        foreach (var onvifPort in new[] { 8899, 8888, 80 })
        {
            if (cancellationToken.IsCancellationRequested) break;
            var r = await ProbeHttpGetAsync(
                $"http://{ip}:{onvifPort}/onvif/device_service", user, pass, cancellationToken);
            results[$"onvif:{onvifPort}"] = r;
        }

        // 6. Known transport sources
        try
        {
            var sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);
            results["transportSources"] = new ProbeResult
            {
                Success = sources.Count > 0,
                Detail = $"{sources.Count} source(s) discovered"
            };
        }
        catch (Exception ex)
        {
            results["transportSources"] = new ProbeResult
            {
                Success = false,
                Detail = $"Exception: {ex.Message}"
            };
        }

        // 7. Try snapshot
        results["snapshot"] = await ProbeHttpGetAsync(
            $"http://{ip}:{port}/NetSDK/Video/encode/channel/101/snapShot", user, pass, cancellationToken);

        // Derive composite verdict
        var summary = BuildSummary(results);
        var verdict = ComputeVerdict(results);
        var connectivity = ComputeConnectivityStatus(results);

        // Build recovery suggestions
        var recoveryActions = BuildRecoveryActions(results, device, connectivity);

        return new DeviceDiagnosticReport
        {
            DeviceId = deviceId,
            DisplayName = device.DisplayName,
            IpAddress = ip,
            Port = port,
            Timestamp = DateTimeOffset.UtcNow,
            Success = verdict != ConnectivityDiagnosticVerdict.CriticalFailure,
            Summary = summary,
            Verdict = verdict,
            ConnectivityStatus = connectivity,
            ProbeResults = results,
            SuggestedRecoveryActions = recoveryActions
        };
    }

    private static string BuildSummary(Dictionary<string, ProbeResult> results)
    {
        var passed = results.Count(r => r.Value.Success);
        var total = results.Count;
        return $"Probes: {passed}/{total} passed.";
    }

    private static ConnectivityDiagnosticVerdict ComputeVerdict(Dictionary<string, ProbeResult> results)
    {
        var pingOk = results.GetValueOrDefault("ping")?.Success == true;
        var httpOk = results.GetValueOrDefault("http:deviceInfo")?.Success == true;
        var rtspOk = results.GetValueOrDefault("tcp:554")?.Success == true;
        var onvifOk = results.Any(r => r.Key.StartsWith("onvif:") && r.Value.Success);
        var snapOk = results.GetValueOrDefault("snapshot")?.Success == true;

        if (!pingOk && !httpOk && !rtspOk && !onvifOk)
            return ConnectivityDiagnosticVerdict.CriticalFailure;

        if (!httpOk && !rtspOk)
            return ConnectivityDiagnosticVerdict.SeverelyDegraded;

        if (!rtspOk && snapOk)
            return ConnectivityDiagnosticVerdict.RtspDownSnapshotOnly;

        if (httpOk && rtspOk)
            return ConnectivityDiagnosticVerdict.Healthy;

        return ConnectivityDiagnosticVerdict.Degraded;
    }

    private static ConnectivityStatus ComputeConnectivityStatus(Dictionary<string, ProbeResult> results)
    {
        var httpOk = results.GetValueOrDefault("http:deviceInfo")?.Success == true;
        var rtspOk = results.GetValueOrDefault("tcp:554")?.Success == true;
        var snapOk = results.GetValueOrDefault("snapshot")?.Success == true;

        if (httpOk && rtspOk) return ConnectivityStatus.Healthy;
        if (httpOk || snapOk) return ConnectivityStatus.Degraded;
        return ConnectivityStatus.Offline;
    }

    private static List<string> BuildRecoveryActions(
        Dictionary<string, ProbeResult> results,
        DeviceIdentity device,
        ConnectivityStatus status)
    {
        var actions = new List<string>();
        var pingOk = results.GetValueOrDefault("ping")?.Success == true;

        if (!pingOk)
        {
            actions.Add("Device not responding to ping. Check power and network cable.");
            actions.Add("Verify the IP address is correct and the device is on the same subnet.");
            return actions;
        }

        var httpOk = results.GetValueOrDefault("http:deviceInfo")?.Success == true;
        if (!httpOk)
        {
            actions.Add($"HTTP API unreachable on port {device.Port}. Try alternative ports (8080, 8000, 8899, 8888).");
            actions.Add("The device may require a different transport protocol.");
        }

        var rtspOk = results.GetValueOrDefault("tcp:554")?.Success == true;
        if (!rtspOk)
        {
            actions.Add("RTSP port 554 not reachable. Live streaming will fall back to snapshot-only.");
            actions.Add("Verify RTSP is enabled in the camera's stream settings.");
        }

        if (status == ConnectivityStatus.Degraded)
        {
            actions.Add("Limited connectivity. Settings may be readable but live video will be degraded (snapshot only).");
            actions.Add("Consider configuring a secondary RTSP profile or enabling ONVIF on the camera.");
        }

        if (actions.Count == 0)
            actions.Add("All transports healthy. No recovery actions needed.");

        return actions;
    }

    private async Task<ProbeResult> ProbePingAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            return new ProbeResult
            {
                Success = reply.Status == IPStatus.Success,
                Detail = reply.Status == IPStatus.Success
                    ? $"Reply in {reply.RoundtripTime}ms"
                    : $"Ping failed: {reply.Status}",
                LatencyMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult { Success = false, Detail = $"Ping exception: {ex.Message}" };
        }
    }

    private static async Task<ProbeResult> ProbeTcpPortAsync(string host, int port, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var socket = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(3000);
            await socket.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new ProbeResult
            {
                Success = socket.Connected,
                Detail = socket.Connected
                    ? $"TCP {host}:{port} open in {sw.ElapsedMilliseconds}ms"
                    : "Connection failed",
                LatencyMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult
            {
                Success = false,
                Detail = $"TCP {host}:{port} — {ex.GetType().Name}: {ex.Message}",
                LatencyMs = (int)sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<ProbeResult> ProbeHttpGetAsync(
        string url, string user, string password, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(5000);
            using var client = httpClientFactory.CreateClient("probe");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            return new ProbeResult
            {
                Success = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                Detail = $"HTTP {url} → {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms",
                LatencyMs = (int)sw.ElapsedMilliseconds,
                HttpStatusCode = (int)response.StatusCode
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult
            {
                Success = false,
                Detail = $"HTTP {url} — {ex.GetType().Name}: {ex.Message}",
                LatencyMs = (int)sw.ElapsedMilliseconds
            };
        }
    }
}

public sealed record DeviceDiagnosticReport
{
    public Guid DeviceId { get; init; }
    public string? DisplayName { get; init; }
    public string? IpAddress { get; init; }
    public int Port { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool Success { get; init; }
    public string? Summary { get; init; }
    public ConnectivityDiagnosticVerdict Verdict { get; init; }
    public ConnectivityStatus ConnectivityStatus { get; init; }
    public Dictionary<string, ProbeResult> ProbeResults { get; init; } = [];
    public List<string> SuggestedRecoveryActions { get; init; } = [];
}

public sealed record ProbeResult
{
    public bool Success { get; init; }
    public string? Detail { get; init; }
    public int? LatencyMs { get; init; }
    public int? HttpStatusCode { get; init; }
}

public enum ConnectivityDiagnosticVerdict
{
    Healthy,
    Degraded,
    RtspDownSnapshotOnly,
    SeverelyDegraded,
    CriticalFailure,
    DeviceNotFound
}
