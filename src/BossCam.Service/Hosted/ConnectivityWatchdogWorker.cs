using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Service.Hosted;

/// <summary>
/// Background worker that monitors camera connectivity on a configurable interval.
/// For each registered device:
///   1. Probes HTTP API reachability (quick check)
///   2. Probes RTSP port reachability (TCP :554)
///   3. Updates the persistent DeviceConnectivitySnapshot
///   4. If offline: runs diagnostics and attempts reconnect actions
///   5. Broadcasts state transitions via SignalR
///
/// This is the "aggressive fallback" heart of the camera stability system.
/// </summary>
#pragma warning disable CS9113 // failoverService + options are reserved for configurable-interval and failover-driven modes
public sealed class ConnectivityWatchdogWorker(
    IApplicationStore store,
    ConnectionDiagnosticService diagnosticService,
    TransportFailoverService failoverService,
    IHttpClientFactory httpClientFactory,
    IBossCamEventBroadcaster broadcaster,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<ConnectivityWatchdogWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QuickProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);

    // Track previous status per device so we can detect transitions
    private readonly Dictionary<Guid, ConnectivityStatus> _previousStatus = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warmup delay so the system can finish bootstrapping
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        logger.LogInformation("ConnectivityWatchdogWorker started");

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunWatchdogCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ConnectivityWatchdog cycle failed");
            }
        }
    }

    private async Task RunWatchdogCycleAsync(CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(cancellationToken);
        if (devices.Count == 0) return;

        logger.LogDebug("ConnectivityWatchdog checking {Count} device(s)", devices.Count);

        // Parallel check with concurrency limit for large deployments
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 5,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(devices, parallelOptions, async (device, ct) =>
        {
            try
            {
                await CheckDeviceAsync(device, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Swallow
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ConnectivityWatchdog check failed for {Device}", device.DisplayName);
            }
        });
    }

    private async Task CheckDeviceAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            // Device has no IP — may be P2P-only; skip active probing
            return;
        }

        var ip = device.IpAddress;
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var pass = device.Password ?? string.Empty;

        // Quick probe: HTTP device info + RTSP :554. The HTTP surface listens on :80 while
        // discovery may have recorded an ONVIF/media port — probe recorded-first, then :80.
        // RTSP "up" requires an OPTIONS handshake (not a bare TCP connect) so a non-RTSP
        // service on :554 isn't misreported as a live/recordable stream (see RtspProbe).
        var httpOk = await QuickHttpProbeAsync(device, user, pass, cancellationToken);
        var rtspOk = await RtspProbe.ProbeAsync(ip, 554, cancellationToken);

        var currentStatus = (httpOk, rtspOk) switch
        {
            (true, true) => ConnectivityStatus.Healthy,
            (true, false) => ConnectivityStatus.Degraded,
            (false, true) => ConnectivityStatus.Degraded,
            (false, false) when await QuickSnapshotProbeAsync(device, cancellationToken) => ConnectivityStatus.Degraded,
            _ => ConnectivityStatus.Offline
        };

        // Build transport results
        var transportResults = new Dictionary<string, bool>
        {
            ["http:deviceInfo"] = httpOk,
            ["rtsp:playable"] = rtspOk
        };

        // Save snapshot
        var snapshot = new DeviceConnectivitySnapshot
        {
            DeviceId = device.Id,
            Status = currentStatus,
            TransportResults = transportResults,
            LastCheckedAt = DateTimeOffset.UtcNow
        };
        await store.SaveDeviceConnectivitySnapshotAsync(snapshot, cancellationToken);

        // Detect transition
        var previous = _previousStatus.GetValueOrDefault(device.Id, ConnectivityStatus.Unknown);
        _previousStatus[device.Id] = currentStatus;

        if (previous != currentStatus)
        {
            logger.LogInformation(
                "Connectivity transition for {Device}: {Previous} → {Current}",
                device.DisplayName, previous, currentStatus);

            // Broadcast dedicated connectivity change event
            await BroadcastConnectivityChangeAsync(snapshot, cancellationToken);
        }

        // If offline, run diagnostics + attempt reconnect
        if (currentStatus == ConnectivityStatus.Offline)
        {
            await HandleOfflineDeviceAsync(device, cancellationToken);
        }

        // If degraded (HTTP works, RTSP doesn't), log a warning
        if (currentStatus == ConnectivityStatus.Degraded && httpOk && !rtspOk)
        {
            logger.LogWarning(
                "Device {Device} is degraded: API reachable but RTSP port 554 not responding",
                device.DisplayName);
        }
    }
#pragma warning restore CS9113

    private async Task HandleOfflineDeviceAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        logger.LogWarning("Device {Device} is OFFLINE. Running diagnostics…", device.DisplayName);

        try
        {
            // Run full diagnostics
            var report = await diagnosticService.DiagnoseAsync(device.Id, cancellationToken);

            // Save diagnostic summary
            var snapshot = await store.GetDeviceConnectivitySnapshotAsync(device.Id, cancellationToken);
            if (snapshot != null)
            {
                var updated = snapshot with
                {
                    LastDiagnosticSummary = report.Summary,
                    ReconnectAttempts = new Dictionary<string, string>
                    {
                        ["diagnosedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["verdict"] = report.Verdict.ToString(),
                        ["suggestedActions"] = string.Join("; ", report.SuggestedRecoveryActions.Take(3))
                    }
                };
                await store.SaveDeviceConnectivitySnapshotAsync(updated, cancellationToken);
            }

            // Attempt reconnect: try alternate ports if primary was wrong
            if (!string.IsNullOrWhiteSpace(device.IpAddress))
            {
                await AttemptReconnectAsync(device, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Offline handling failed for {Device}", device.DisplayName);
        }
    }

    private async Task AttemptReconnectAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var ip = device.IpAddress!;
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var pass = device.Password ?? string.Empty;

        // Try alternate HTTP ports
        var alternatePorts = new[] { 80, 8080, 8000, 8899, 8888 }
            .Where(p => p != device.Port)
            .Distinct()
            .ToList();

        foreach (var altPort in alternatePorts)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var ok = await QuickHttpProbeAsync(ip, altPort, user, pass, cancellationToken);
            if (ok)
            {
                logger.LogInformation(
                    "Device {Device} reachable on alternate port {Port}. Updating registration.",
                    device.DisplayName, altPort);

                // Save the corrected port back to the device record
                var updated = device with { Port = altPort };
                await store.UpsertDevicesAsync([updated], cancellationToken);

                await BroadcastConnectivityChangeAsync(
                    new DeviceConnectivitySnapshot
                    {
                        DeviceId = device.Id,
                        Status = ConnectivityStatus.Degraded,
                        TransportResults = new Dictionary<string, bool> { [$"http:alt:{altPort}"] = true },
                        LastCheckedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
                return;
            }
        }

        logger.LogWarning(
            "Reconnect attempt for {Device} failed on all alternate ports ({Ports})",
            device.DisplayName, string.Join(", ", alternatePorts));
    }

    /// <summary>Probes deviceInfo across candidate ports (recorded first, then :80).</summary>
    internal async Task<bool> QuickHttpProbeAsync(
        DeviceIdentity device, string user, string pass, CancellationToken cancellationToken)
        => await NetSdkPortCandidates.AnyPortSucceedsAsync(
            device.Port,
            (port, ct) => QuickHttpProbeAsync(device.IpAddress!, port, user, pass, ct),
            cancellationToken);

    private async Task<bool> QuickHttpProbeAsync(
        string ip, int port, string user, string pass, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(QuickProbeTimeout);
            using var client = httpClientFactory.CreateClient("probe");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"http://{ip}:{port}/NetSDK/System/deviceInfo");
            var token = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}"));
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // Any HTTP response (even 401) means the device is reachable
            return true;
        }
        catch (Exception ex)
        {
            // Per-probe failures are expected on offline devices; Debug keeps the watchdog's
            // probe traffic traceable without Warning-spam on every dead camera.
            logger.LogDebug(ex, "Quick HTTP probe failed for {Ip}:{Port}", ip, port);
            return false;
        }
    }


    /// <summary>
    /// Probes the NetSDK snapshot JPEG across candidate ports (recorded first, then :80) so a
    /// 5523-W with a recorded ONVIF/media port is not misreported offline when :80 serves it.
    /// </summary>
    internal async Task<bool> QuickSnapshotProbeAsync(
        DeviceIdentity device, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName;
        var pass = device.Password ?? string.Empty;
        var ip = device.IpAddress ?? string.Empty;
        return await NetSdkPortCandidates.AnyPortSucceedsAsync(device.Port, async (port, ct) =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(QuickProbeTimeout);
                using var client = httpClientFactory.CreateClient("probe");
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"http://{ip}:{port}/NetSDK/Video/encode/channel/101/snapShot");
                var token = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}"));
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                return bytes.Length > 500 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            }
            catch
            {
                return false; // next candidate port
            }
        }, cancellationToken);
    }

    private async Task BroadcastConnectivityChangeAsync(
        DeviceConnectivitySnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await broadcaster.ConnectivityChangedAsync(snapshot, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to broadcast connectivity change");
        }
    }
}
