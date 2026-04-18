using BossCam.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BossCam.Service.Hosted;

public sealed class BossCamBootstrapWorker(
    IApplicationStore store,
    ProtocolCatalogService protocolCatalogService,
    DiscoveryCoordinator discoveryCoordinator,
    ILogger<BossCamBootstrapWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        await protocolCatalogService.RefreshAsync(stoppingToken);

        try
        {
            await discoveryCoordinator.RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Initial discovery run failed.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await discoveryCoordinator.RunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled discovery run failed.");
            }
        }
    }
}
