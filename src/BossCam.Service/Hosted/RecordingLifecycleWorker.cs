using BossCam.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Service.Hosted;

public sealed class RecordingLifecycleWorker(
    RecordingService recordingService,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<RecordingLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PR-R1: Startup reconcile — reconcile persisted jobs + auto-start profiles
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.RecordingStartupReconcileDelaySeconds)), stoppingToken);
            var reconciled = await recordingService.ReconcilePersistedJobsAsync(stoppingToken);
            var autoStarted = await recordingService.ReconcileAutoStartAsync(stoppingToken);
            var continuous = await recordingService.ReconcileContinuousAsync(stoppingToken);
            var running = reconciled.Count(static j => j.IsRunning);
            logger.LogInformation("Recording reconcile: {Total} jobs ({Running} running), {Auto} auto-started, {Continuous} continuous-record started", reconciled.Count, running, autoStarted.Count, continuous.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording startup reconcile failed.");
        }

        // Recovery is independent of housekeeping. A WAN transition is never a reason to stop a
        // healthy LAN recorder; only an exited/stalled process is acted upon here.
        var cycleMinutes = Math.Max(1, options.Value.RecordingHousekeepingMinutes);
        var stallTimeoutSeconds = options.Value.StallTimeoutSeconds > 0 ? options.Value.StallTimeoutSeconds : cycleMinutes * 60 / 2;
        var recoveryInterval = TimeSpan.FromSeconds(Math.Clamp(options.Value.RecordingRecoveryIntervalSeconds, 1, 60));
        var policyRetryInterval = TimeSpan.FromSeconds(Math.Clamp(options.Value.RecordingRecoveryRetrySeconds, 1, 300));
        var maxRetryInterval = TimeSpan.FromSeconds(Math.Clamp(options.Value.RecordingRecoveryMaxRetrySeconds, 1, 900));
        var nextHousekeeping = DateTimeOffset.UtcNow.AddMinutes(cycleMinutes);
        var nextPolicyReconcile = DateTimeOffset.MinValue;
        var policyBackoff = policyRetryInterval;
        var stallBackoff = policyRetryInterval;
        var stallCheckGraceUntil = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(recoveryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Highest priority: check output growth frequently. StallAutoRestart remains honored,
            // but repeated failures use bounded exponential backoff instead of churn.
            if (DateTimeOffset.UtcNow >= stallCheckGraceUntil)
            {
                try
                {
                    var stalled = await recordingService.CheckStalledJobsAsync(
                        stallTimeoutSeconds,
                        options.Value.StallAutoRestart,
                        stoppingToken);
                    if (stalled.Count > 0)
                    {
                        logger.LogWarning("Fast recovery stall check: {Count} stalled job(s) handled; next stall retry in {Delay}s", stalled.Count, stallBackoff.TotalSeconds);
                        stallCheckGraceUntil = DateTimeOffset.UtcNow.Add(stallBackoff);
                        stallBackoff = TimeSpan.FromSeconds(Math.Min(maxRetryInterval.TotalSeconds, stallBackoff.TotalSeconds * 2));
                    }
                    else
                    {
                        // A healthy output stream proves the replacement recovered; return to
                        // the quick base cadence for the next independent failure.
                        stallBackoff = policyRetryInterval;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fast recovery stall check failed.");
                }
            }

            // Process/PID reconciliation stays frequent. Auto-start and continuous policy retries
            // have their own backoff so an unreachable camera does not cause a launch storm.
            try
            {
                var reconciled = await recordingService.ReconcilePersistedJobsAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextPolicyReconcile)
                {
                    var started = await recordingService.ReconcileAutoStartAsync(stoppingToken);
                    var continuous = await recordingService.ReconcileContinuousAsync(stoppingToken);
                    if (reconciled.Count > 0 || started.Count > 0 || continuous.Count > 0)
                    {
                        logger.LogInformation("Fast recording reconcile persisted={Persisted} auto={Auto} continuous={Continuous} job(s)", reconciled.Count, started.Count, continuous.Count);
                    }

                    if (started.Count > 0 || continuous.Count > 0)
                    {
                        stallCheckGraceUntil = DateTimeOffset.UtcNow.Add(policyRetryInterval);
                        policyBackoff = policyRetryInterval;
                    }
                    else
                    {
                        // Historical stopped jobs are included in every persisted reconciliation;
                        // they are not evidence that this pass made progress. Only a newly started
                        // job resets the backoff, preventing a failed camera from retrying forever
                        // at the minimum interval.
                        policyBackoff = TimeSpan.FromSeconds(Math.Min(maxRetryInterval.TotalSeconds, policyBackoff.TotalSeconds * 2));
                    }

                    nextPolicyReconcile = DateTimeOffset.UtcNow.Add(policyBackoff);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fast recording reconcile failed.");
                nextPolicyReconcile = DateTimeOffset.UtcNow.Add(policyBackoff);
            }

            if (DateTimeOffset.UtcNow < nextHousekeeping)
            {
                continue;
            }

            // Housekeeping/indexing are deliberately lower priority than capture recovery.
            try
            {
                var result = await recordingService.RunHousekeepingAsync(null, stoppingToken);
                if (result.FilesDeleted > 0)
                {
                    logger.LogInformation("Recording housekeeping checked={Checked} deleted={Deleted} bytes={Bytes}", result.ProfilesChecked, result.FilesDeleted, result.BytesDeleted);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recording housekeeping iteration failed.");
            }

            try
            {
                var indexed = await recordingService.RefreshIndexAsync(null, stoppingToken);
                if (indexed.Count > 0)
                {
                    logger.LogDebug("Recording index refresh produced {Count} segment(s)", indexed.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recording index refresh failed.");
            }

            nextHousekeeping = DateTimeOffset.UtcNow.AddMinutes(cycleMinutes);
        }
    }
}
