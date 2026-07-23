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
                // Linux: ~/.local/share/BossCamSuite ; Windows: %LocalAppData%\BossCamSuite
                var dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BossCamSuite");
                if (string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
                {
                    dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "BossCamSuite");
                }

                options.ProtocolAssetsPath = string.IsNullOrWhiteSpace(options.ProtocolAssetsPath)
                    ? Path.Combine(baseDirectory, "assets", "protocols")
                    : options.ProtocolAssetsPath;
                options.DatabasePath = string.IsNullOrWhiteSpace(options.DatabasePath)
                    ? Path.Combine(dataRoot, "bosscam.db")
                    : options.DatabasePath;
                options.FirmwareArtifactDirectory = string.IsNullOrWhiteSpace(options.FirmwareArtifactDirectory)
                    ? Path.Combine(dataRoot, "firmware")
                    : options.FirmwareArtifactDirectory;
                if (string.IsNullOrWhiteSpace(options.IpcamSuiteDirectory) && OperatingSystem.IsWindows())
                {
                    options.IpcamSuiteDirectory = @"C:\Program Files\IPCamSuite";
                }

                if (string.IsNullOrWhiteSpace(options.EseeCloudDirectory) && OperatingSystem.IsWindows())
                {
                    options.EseeCloudDirectory = @"C:\Program Files (x86)\EseeCloud";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
                Directory.CreateDirectory(options.FirmwareArtifactDirectory);
                Directory.CreateDirectory(Path.Combine(dataRoot, "recordings"));
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
        services.AddSingleton<IControlAdapter, DahuaLorexControlAdapter>();
        services.AddSingleton<IControlAdapter, OnvifImagingControlAdapter>();
        services.AddSingleton<IControlAdapter, OwnedRemoteCommandAdapter>();
        services.AddSingleton<IControlAdapter, NativeFallbackAdapter>();

        services.AddSingleton<IVideoTransportAdapter, MultiBrandHighResTransportAdapter>();
        services.AddSingleton<IVideoTransportAdapter, StreamDescriptorAdapter>();
        services.AddSingleton<IVideoTransportAdapter, BubbleFlvAdapter>();
        services.AddSingleton<IVideoTransportAdapter, EseeJuanP2PAdapter>();
        services.AddSingleton<IVideoTransportAdapter, Kp2pAdapter>();
        services.AddSingleton<IVideoTransportAdapter, LinkVisionAdapter>();

        return services;
    }
}
