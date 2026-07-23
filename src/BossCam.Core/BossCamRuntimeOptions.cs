namespace BossCam.Core;

public sealed class BossCamRuntimeOptions
{
    public string ProtocolAssetsPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    /// <summary>Optional Windows OEM install dir; unused on pure Linux LAN/NetSDK operation.</summary>
    public string IpcamSuiteDirectory { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\Program Files\IPCamSuite"
        : string.Empty;
    /// <summary>Optional Windows OEM install dir; unused on pure Linux LAN/NetSDK operation.</summary>
    public string EseeCloudDirectory { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\Program Files (x86)\EseeCloud"
        : string.Empty;
    public string EseeCloudDataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EseeCloud");
    public string FirmwareArtifactDirectory { get; set; } = string.Empty;
    public string? RemoteCommandEndpoint { get; set; }
    public int DiscoveryTimeoutSeconds { get; set; } = 3;
    public int HttpTimeoutSeconds { get; set; } = 8;
    public string LocalApiBaseUrl { get; set; } = "http://127.0.0.1:5317";
    public int RecordingHousekeepingMinutes { get; set; } = 15;
    public int RecordingStartupReconcileDelaySeconds { get; set; } = 8;
}
