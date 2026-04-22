using Microsoft.Extensions.DependencyInjection;

namespace BossCam.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddBossCamCore(this IServiceCollection services)
    {
        services.AddSingleton<DiscoveryCoordinator>();
        services.AddSingleton<ProtocolCatalogService>();
        services.AddSingleton<IEndpointContractCatalog, EndpointContractCatalogService>();
        services.AddSingleton<IContractEvidenceService, ContractEvidenceService>();
        services.AddSingleton<CapabilityProbeService>();
        services.AddSingleton<ProtocolValidationService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<TypedSettingsService>();
        services.AddSingleton<CapabilityPromotionService>();
        services.AddSingleton<PersistenceVerificationService>();
        services.AddSingleton<SemanticTrustService>();
        services.AddSingleton<GroupedConfigService>();
        services.AddSingleton<ImageTruthService>();
        services.AddSingleton<ProbeSessionService>();
        services.AddSingleton<TransportBroker>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<NvrPlaybackService>();
        services.AddSingleton<FirmwareCatalogService>();
        return services;
    }
}
