using BossCam.Core;
using BossCam.Infrastructure.Control;
using BossCam.Infrastructure.Discovery;
using BossCam.Infrastructure.Firmware;
using BossCam.Infrastructure.Imports;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Protocol;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BossCam.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBossCamInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BossCamRuntimeOptions>()
            .Bind(configuration.GetSection("BossCam"))
            .PostConfigure(options =>
            {
                var baseDirectory = AppContext.BaseDirectory;
                options.ProtocolAssetsPath = string.IsNullOrWhiteSpace(options.ProtocolAssetsPath) ? Path.Combine(baseDirectory, "assets", "protocols") : options.ProtocolAssetsPath;
                options.DatabasePath = string.IsNullOrWhiteSpace(options.DatabasePath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BossCamSuite", "bosscam.db") : options.DatabasePath;
                options.FirmwareArtifactDirectory = string.IsNullOrWhiteSpace(options.FirmwareArtifactDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BossCamSuite", "firmware") : options.FirmwareArtifactDirectory;
                options.ProtocolAssetsPath = NormalizePath(options.ProtocolAssetsPath);
                options.DatabasePath = NormalizePath(options.DatabasePath);
                options.IpcamSuiteDirectory = NormalizePath(options.IpcamSuiteDirectory);
                options.EseeCloudDirectory = NormalizePath(options.EseeCloudDirectory);
                options.EseeCloudDataDirectory = NormalizePath(options.EseeCloudDataDirectory);
                options.FirmwareArtifactDirectory = NormalizePath(options.FirmwareArtifactDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
                Directory.CreateDirectory(options.FirmwareArtifactDirectory);
            });

        services.AddSingleton<IApplicationStore, SqliteApplicationStore>();
        services.AddSingleton<IProtocolManifestProvider, JsonProtocolManifestProvider>();
        services.AddSingleton<IFirmwareArtifactAnalyzer, FirmwareArtifactAnalyzer>();

        services.AddSingleton<IDeviceImportProvider, IpcamSuiteImportProvider>();
        services.AddSingleton<IDeviceImportProvider, EseeCloudImportProvider>();

        services.AddSingleton<IDiscoveryProvider, HiChipMulticastDiscoveryProvider>();
        services.AddSingleton<IDiscoveryProvider, DvrBroadcastDiscoveryProvider>();
        services.AddSingleton<IDiscoveryProvider, OnvifDiscoveryProvider>();

        services.AddSingleton<IControlAdapter, LanDirectNetSdkRestAdapter>();
        services.AddSingleton<IControlAdapter, LanPrivateVendorHttpAdapter>();
        services.AddSingleton<IControlAdapter, OwnedRemoteCommandAdapter>();
        services.AddSingleton<IControlAdapter, NativeFallbackAdapter>();

        services.AddSingleton<IVideoTransportAdapter, StreamDescriptorAdapter>();
        services.AddSingleton<IVideoTransportAdapter, BubbleFlvAdapter>();
        services.AddSingleton<IVideoTransportAdapter, EseeJuanP2PAdapter>();
        services.AddSingleton<IVideoTransportAdapter, Kp2pAdapter>();
        services.AddSingleton<IVideoTransportAdapter, LinkVisionAdapter>();

        return services;
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? path
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
}
