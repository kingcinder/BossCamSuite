using Microsoft.Extensions.DependencyInjection;

namespace BossCam.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddBossCamCore(this IServiceCollection services)
    {
        services.AddSingleton<DiscoveryCoordinator>();
        services.AddSingleton<ProtocolCatalogService>();
        services.AddSingleton<CapabilityProbeService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<TransportBroker>();
        services.AddSingleton<FirmwareCatalogService>();
        return services;
    }
}
