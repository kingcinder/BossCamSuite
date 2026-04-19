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
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.RecordingStartupReconcileDelaySeconds)), stoppingToken);
            var started = await recordingService.ReconcileAutoStartAsync(stoppingToken);
            logger.LogInformation("Recording reconcile started {Count} job(s)", started.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording startup reconcile failed.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.RecordingHousekeepingMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _ = await recordingService.RefreshIndexAsync(null, stoppingToken);
                var result = await recordingService.RunHousekeepingAsync(null, stoppingToken);
                logger.LogInformation("Recording housekeeping checked={Checked} deleted={Deleted} bytes={Bytes}", result.ProfilesChecked, result.FilesDeleted, result.BytesDeleted);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recording housekeeping iteration failed.");
            }
        }
    }
}
