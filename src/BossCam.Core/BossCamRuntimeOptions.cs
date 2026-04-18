namespace BossCam.Core;

public sealed class BossCamRuntimeOptions
{
    public string ProtocolAssetsPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public string IpcamSuiteDirectory { get; set; } = @"C:\Program Files\IPCamSuite";
    public string EseeCloudDirectory { get; set; } = @"C:\Program Files (x86)\EseeCloud";
    public string EseeCloudDataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EseeCloud");
    public string FirmwareArtifactDirectory { get; set; } = string.Empty;
    public string? RemoteCommandEndpoint { get; set; }
    public int DiscoveryTimeoutSeconds { get; set; } = 3;
    public int HttpTimeoutSeconds { get; set; } = 8;
    public string LocalApiBaseUrl { get; set; } = "http://127.0.0.1:5317";
}
