using BossCam.Core.Services.Recording;
using BossCam.Core.Utilities;
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
        services.AddSingleton<IRecordingStore, ApplicationStoreRecordingStore>();
        services.AddSingleton<ITypedControlStore, ApplicationStoreTypedControlStore>();
        services.AddSingleton<RecordingProcessSupervisor>();
        services.AddSingleton<TypedSettingsService>();
        services.AddSingleton<CapabilityPromotionService>();
        services.AddSingleton<PersistenceVerificationService>();
        services.AddSingleton<SemanticTrustService>();
        services.AddSingleton<GroupedConfigService>();
        services.AddSingleton<ControlPointInventoryService>();
        services.AddSingleton<EndpointSurfaceService>();
        services.AddSingleton<ImageTruthService>();
        services.AddSingleton<ProbeSessionService>();
        services.AddSingleton<TransportBroker>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<LiveStreamService>();
        services.AddSingleton<HighlightBoardService>();
        services.AddSingleton<DeviceRegistrationService>();
        services.AddSingleton<EnrollService>();
        services.AddSingleton<NvrPlaybackService>();
        services.AddSingleton<FirmwareCatalogService>();
        services.AddSingleton<TransportFailoverService>();
        services.AddSingleton<ConnectionDiagnosticService>();
        services.AddSingleton<CameraRecoveryService>();

        // Recording pipelines (refactor of P2 #12). RecordingService resolves the three
        // implementations by mode via IRecordingPipelineResolver, so swapping any pipeline
        // in tests doesn't require touching RecordingService itself.
        services.AddSingleton<SnapshotRecordingPipeline>();
        services.AddSingleton<DirectFfmpegRecordingPipeline>();
        services.AddSingleton<BubbleFlvRecordingPipeline>();
        services.AddSingleton<IRecordingPipelineResolver, RecordingPipelineResolver>();

        return services;
    }
}

/// <summary>
/// Lookup so <see cref="RecordingService"/> can pick a concrete recording pipeline by
/// <see cref="IRecordingPipeline.Mode"/> without directly depending on
/// <see cref="SnapshotRecordingPipeline"/> or <see cref="DirectFfmpegRecordingPipeline"/>.
/// </summary>
public interface IRecordingPipelineResolver
{
    SnapshotRecordingPipeline Snapshot { get; }
    DirectFfmpegRecordingPipeline DirectFfmpeg { get; }
    BubbleFlvRecordingPipeline BubbleFlv { get; }
}

internal sealed class RecordingPipelineResolver(
    SnapshotRecordingPipeline snapshot,
    DirectFfmpegRecordingPipeline direct,
    BubbleFlvRecordingPipeline bubbleFlv) : IRecordingPipelineResolver
{
    public SnapshotRecordingPipeline Snapshot { get; } = snapshot;
    public DirectFfmpegRecordingPipeline DirectFfmpeg { get; } = direct;
    public BubbleFlvRecordingPipeline BubbleFlv { get; } = bubbleFlv;
}
