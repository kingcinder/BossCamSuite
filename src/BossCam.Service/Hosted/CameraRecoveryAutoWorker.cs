using BossCam.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Service.Hosted;

/// <summary>
/// Autonomous camera-recovery worker. Periodically scans the host WiFi for factory-reset
/// camera APs (SSID IPCZ7C34… = serial without the "JA" prefix) and, for any visible AP
/// not in cooldown, runs the full recover-and-enroll pipeline — join the camera AP, write
/// station-mode WiFi config, the camera rejoins the home network, rediscover it on the LAN
/// by MAC, enroll it, and start recording — with NO human interaction.
///
/// Safety rails (all derived from the live 2026-08-11 recoveries):
///  • Only acts while the host is connected to <see cref="BossCamRuntimeOptions.RecoveryStaSsid"/> —
///    never while the host itself is on a camera AP or on an unrelated network.
///  • Never runs more than one recovery at a time (one WiFi radio, one AP join at a time).
///  • Per-serial cooldown so a camera that keeps beaconing isn't hammered.
///  • No enrolled-skip: a camera broadcasting its AP is off the LAN by definition, so an old
///    Suite record is stale evidence — the AP must be recovered regardless (both live-verified
///    2026-08-11 units had pre-existing records yet needed recovery after reset).
///  • Best-effort every cycle: any failure is logged and the next tick retries.
/// </summary>
public sealed class CameraRecoveryAutoWorker(
    CameraRecoveryService recoveryService,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<CameraRecoveryAutoWorker> logger) : BackgroundService
{
    // serial (JA… or Z7C… normalized) → last attempted UTC. Bounded by pruning.
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        if (!cfg.RecoveryAutoScanEnabled)
        {
            logger.LogInformation("Autonomous camera-recovery scan disabled by BossCam:RecoveryAutoScanEnabled.");
            return;
        }

        // Give the service a moment to finish bootstrapping before the first scan.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Clamp(cfg.RecoveryAutoScanIntervalSeconds, 15, 600));
        using var timer = new PeriodicTimer(interval);
        logger.LogInformation(
            "Autonomous camera-recovery scan started: every {Interval}s while on '{StaSsid}', cooldown {Cooldown}m",
            interval.TotalSeconds, cfg.RecoveryStaSsid, cfg.RecoveryAutoCooldownMinutes);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Autonomous camera-recovery scan cycle failed.");
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var cfg = options.Value;
        var now = DateTimeOffset.UtcNow;

        // 1. Serialize: one recovery at a time (one radio, one AP join).
        if (recoveryService.HasActiveRun)
        {
            recoveryService.RecordAutoScan(now, 0, "waiting: a recovery run is already active");
            return;
        }

        // 2. Only act while on the home STA network.
        var ssid = await recoveryService.GetCurrentSsidAsync(cancellationToken);
        if (!string.Equals(ssid, cfg.RecoveryStaSsid, StringComparison.OrdinalIgnoreCase))
        {
            recoveryService.RecordAutoScan(now, 0,
                string.IsNullOrEmpty(ssid) ? "host not on any WiFi — waiting" : $"host on '{ssid}' — waiting for '{cfg.RecoveryStaSsid}'");
            return;
        }

        // 3. Scan for camera APs.
        var aps = await recoveryService.ScanCameraApsAsync(cancellationToken);
        if (aps.Count == 0)
        {
            recoveryService.RecordAutoScan(now, 0, "no camera APs visible");
            PruneCooldown(now);
            return;
        }

        // 4. Pick the strongest new camera (shared, unit-tested selection logic). NO
        // enrolled-skip: a camera broadcasting its AP is by definition off the LAN and needs
        // recovery — its old Suite record is stale, not a reason to skip (both live-verified
        // 2026-08-11 units had pre-existing records yet required recovery after reset).
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(cfg.RecoveryAutoCooldownMinutes, 1, 1440));
        var candidate = RecoveryAutoSelection.PickCandidate(aps, _cooldown, now, cooldown);
        if (candidate is null)
        {
            recoveryService.RecordAutoScan(now, aps.Count, $"{aps.Count} camera AP(s) visible — all in cooldown");
            PruneCooldown(now);
            return;
        }

        var serial = candidate.Serial;
        // Defensive: the scan derives serials from the IPC… SSID so this is normally populated,
        // but never start (or cooldown-key) a run with no serial — an empty key would collide
        // every serial-less AP into one cooldown slot.
        if (string.IsNullOrWhiteSpace(serial))
        {
            recoveryService.RecordAutoScan(now, aps.Count, $"skipping AP '{candidate.Ssid}' — no serial derivable");
            return;
        }

        _cooldown[RecoveryAutoSelection.NormalizeIdentity(serial)] = now;
        logger.LogInformation(
            "Autonomous recovery: new camera AP {Ssid} (signal {Signal}) → starting recover-and-enroll for {Serial}",
            candidate.Ssid, candidate.Signal, serial);

        try
        {
            recoveryService.StartRecovery(serial, CancellationToken.None);
            recoveryService.RecordAutoScan(now, aps.Count, $"auto-started recovery for {serial}");
        }
        catch (Exception ex)
        {
            // Honest status: record that the start was refused (e.g. a manual run raced us)
            // rather than leaving a misleading "auto-starting" action behind.
            recoveryService.RecordAutoScan(now, aps.Count, $"recovery start for {serial} refused: {ex.Message}");
            throw;
        }
    }

    /// <summary>Drop cooldown entries older than twice the cooldown window (bounded memory).</summary>
    private void PruneCooldown(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Clamp(options.Value.RecoveryAutoCooldownMinutes, 1, 1440)) * 2;
        foreach (var key in _cooldown.Keys.Where(k => now - _cooldown[k] > window).ToList())
        {
            _cooldown.Remove(key);
        }
    }
}
