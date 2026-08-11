using BossCam.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Service.Hosted;

/// <summary>
/// Tracks only the optional WAN/cloud plane. This worker never probes, stops, or restarts a
/// camera stream/recorder. LAN transports remain available whether this probe succeeds or fails.
/// </summary>
public sealed class InternetConnectivityWorker(
    IInternetConnectivityController connectivityState,
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<InternetConnectivityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.OfflineMode)
        {
            connectivityState.SetDisabled();
            logger.LogInformation("Internet connectivity worker disabled by BossCam:OfflineMode; LAN-only operation remains active.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.InternetConnectivityProbeIntervalSeconds, 5, 300));
        using var timer = new PeriodicTimer(interval);

        // Probe immediately so cloud transports become usable without waiting for the first tick.
        await ProbeAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProbeAsync(stoppingToken);
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var probeUrl = options.Value.InternetConnectivityProbeUrl;
        if (string.IsNullOrWhiteSpace(probeUrl)
            || !Uri.TryCreate(probeUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            logger.LogWarning("Internet connectivity probe URL is empty or invalid; optional cloud transports remain fail-open.");
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.InternetConnectivityProbeTimeoutSeconds, 1, 30)));
            using var client = httpClientFactory.CreateClient("internet-probe");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var wasOffline = connectivityState.Status == InternetConnectivityStatus.Offline;
            if ((int)response.StatusCode is >= 200 and < 500)
            {
                ApplyResult(true);
                if (wasOffline)
                {
                    logger.LogInformation("Internet connectivity restored; optional cloud transports are available again.");
                }
                return;
            }

            ApplyResult(false);
            logger.LogDebug("Internet connectivity probe returned HTTP {StatusCode}.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ApplyResult(false);
            logger.LogDebug("Internet connectivity probe timed out.");
        }
        catch (Exception ex)
        {
            ApplyResult(false);
            logger.LogDebug(ex, "Internet connectivity probe failed; LAN operation is unaffected.");
        }
    }

    private void ApplyResult(bool reachable)
    {
        var before = connectivityState.Status;
        connectivityState.ApplyProbeResult(reachable, Math.Clamp(options.Value.InternetConnectivityFailureThreshold, 1, 10));
        if (before != connectivityState.Status)
        {
            logger.LogInformation("Internet connectivity transition: {Previous} → {Current}", before, connectivityState.Status);
        }
    }
}
