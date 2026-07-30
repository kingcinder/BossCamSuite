using BossCam.Contracts;
using BossCam.Core;

namespace BossCam.Service;

/// <summary>
/// Maps connectivity diagnostic and recovery endpoints for cameras.
/// </summary>
public static class ApiConnectivityEndpoints
{
    public static WebApplication MapConnectivityEndpoints(this WebApplication app)
    {
        // GET /api/devices/{id}/connectivity — current connectivity snapshot
        app.MapGet("/api/devices/{id:guid}/connectivity", async (
            Guid id,
            IApplicationStore store,
            CancellationToken ct) =>
        {
            var snapshot = await store.GetDeviceConnectivitySnapshotAsync(id, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        // GET /api/devices/connectivity — all device connectivity snapshots
        app.MapGet("/api/devices/connectivity", async (
            IApplicationStore store,
            CancellationToken ct) =>
        {
            var snapshots = await store.GetAllDeviceConnectivitySnapshotsAsync(ct);
            return Results.Ok(snapshots);
        });

        // POST /api/devices/{id}/connectivity/diagnose — run diagnostic battery
        app.MapPost("/api/devices/{id:guid}/connectivity/diagnose", async (
            Guid id,
            ConnectionDiagnosticService diagnosticService,
            CancellationToken ct) =>
        {
            var report = await diagnosticService.DiagnoseAsync(id, ct);
            return report.Success ? Results.Ok(report) : Results.Ok(report); // always return report even on failure
        });

        // POST /api/devices/{id}/connectivity/reconnect — force reconnect attempt
        app.MapPost("/api/devices/{id:guid}/connectivity/reconnect", async (
            Guid id,
            IApplicationStore store,
            IHttpClientFactory httpClientFactory,
            IBossCamEventBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var device = await store.GetDeviceAsync(id, ct);
            if (device is null) return Results.NotFound(new { error = "Device not found" });

            var results = new Dictionary<string, string>();
            var ip = device.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
            {
                return Results.Ok(new { deviceId = id.ToString("N"), message = "Device has no IP address.", results });
            }

            var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
            var pass = device.Password ?? string.Empty;

            // Try primary port first
            var primaryOk = await TryHttpProbeAsync(httpClientFactory, ip, device.Port, user, pass, ct);
            results[$"primary:{device.Port}"] = primaryOk ? "reachable" : "unreachable";

            if (!primaryOk)
            {
                // Try alternate ports
                var altPorts = new[] { 80, 8080, 8000, 8899, 8888 }
                    .Where(p => p != device.Port)
                    .Distinct();

                foreach (var altPort in altPorts)
                {
                    var ok = await TryHttpProbeAsync(httpClientFactory, ip, altPort, user, pass, ct);
                    results[$"alt:{altPort}"] = ok ? "reachable" : "unreachable";
                    if (ok)
                    {
                        // Update port
                        var updated = device with { Port = altPort };
                        await store.UpsertDevicesAsync([updated], ct);
                        results["updatedPort"] = $"changed to {altPort}";
                        break;
                    }
                }
            }

            // Try RTSP
            results["rtsp:554"] = await TryTcpProbeAsync(ip, 554, ct) ? "reachable" : "unreachable";

            var anyReachable = results.Any(r => r.Value == "reachable");

            // Save updated connectivity snapshot
            var snapshot = new DeviceConnectivitySnapshot
            {
                DeviceId = device.Id,
                Status = anyReachable ? ConnectivityStatus.Degraded : ConnectivityStatus.Offline,
                TransportResults = results.ToDictionary(r => r.Key, r => r.Value == "reachable"),
                LastCheckedAt = DateTimeOffset.UtcNow,
                ReconnectAttempts = results
            };
            await store.SaveDeviceConnectivitySnapshotAsync(snapshot, ct);

            // Broadcast the connectivity state change
            _ = broadcaster.ConnectivityChangedAsync(snapshot, ct);

            return Results.Ok(new
            {
                deviceId = id.ToString("N"),
                message = anyReachable
                    ? "Device reachable on at least one transport."
                    : "Device unreachable on all tested transports.",
                results
            });
        });

        return app;
    }

    private static async Task<bool> TryHttpProbeAsync(
        IHttpClientFactory factory, string ip, int port, string user, string pass, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = factory.CreateClient("probe");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"http://{ip}:{port}/NetSDK/System/deviceInfo");
            var token = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}"));
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return true; // Any HTTP response means device is reachable
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryTcpProbeAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(host, port, cts.Token);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }
}
