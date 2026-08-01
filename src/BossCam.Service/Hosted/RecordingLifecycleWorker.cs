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
            // Fleet policy: enrolled devices flagged ContinuousRecord are restarted after the
            // persisted-job reconcile, so a camera whose recorder died before restart comes back
            // automatically (no duplicate runaway: a persisted running job is the dedup guard).
            var continuous = await recordingService.ReconcileContinuousAsync(stoppingToken);
            var running = reconciled.Count(static j => j.IsRunning);
            logger.LogInformation("Recording reconcile: {Total} jobs ({Running} running), {Auto} auto-started, {Continuous} continuous-record started", reconciled.Count, running, autoStarted.Count, continuous.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording startup reconcile failed.");
        }

        // PR-R9: Cycle ticks — housekeeping, index refresh, stall checks
        var cycleMinutes = Math.Max(1, options.Value.RecordingHousekeepingMinutes);
        var stallTimeoutSeconds = options.Value.StallTimeoutSeconds > 0 ? options.Value.StallTimeoutSeconds : cycleMinutes * 60 / 2;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(cycleMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // PR-R5: Automatic retention housekeeping
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

            // PR-R2: Incremental index refresh
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

            // PR-R4: Periodic stall check
            try
            {
                var stalled = await recordingService.CheckStalledJobsAsync(options.Value.StallTimeoutSeconds, options.Value.StallAutoRestart, stoppingToken);
                if (stalled.Count > 0)
                {
                    logger.LogWarning("Stall check: {Count} stalled job(s) handled", stalled.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Stall check iteration failed.");
            }

            // PR-R9: Reconcile auto-start profiles (catches missed start events)
            try
            {
                var started = await recordingService.ReconcileAutoStartAsync(stoppingToken);
                if (started.Count > 0)
                {
                    logger.LogInformation("Recording reconcile started {Count} job(s)", started.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recording reconcile failed.");
            }

            // Fleet continuous-record policy (the cycle is the restart/backoff cadence)
            try
            {
                var continuous = await recordingService.ReconcileContinuousAsync(stoppingToken);
                if (continuous.Count > 0)
                {
                    logger.LogInformation("Continuous-record reconcile started {Count} job(s)", continuous.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Continuous-record reconcile failed.");
            }
        }
    }
}
