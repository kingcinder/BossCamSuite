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
    /// <summary>
    /// Canonical ONVIF ports probed during brand detection / device-info scans, in the order
    /// they should be tried (<c>WVC</c>: 8899, <c>Dahua/ONVIF media</c>: 8888, OEM HTTP: 80).
    /// Adapters append <c>device.Port</c> as a tail element if non-default. Override via
    /// <c>BossCam:OnvifProbePorts</c> for brands that expose ONVIF on an idiosyncratic port.
    /// The 5 facts in <c>OnvifImagingControlAdapterTimeoutTests</c> pin the asymmetry between
    /// the brand-probe (half-timeout) and the device-info probe (full timeout); changing this
    /// list's default order is a behavioural change for those tests.
    /// </summary>
    public int[] OnvifProbePorts { get; set; } = [8899, 8888, 80];
    public string LocalApiBaseUrl { get; set; } = "http://127.0.0.1:5317";
    public int RecordingHousekeepingMinutes { get; set; } = 15;
    public int RecordingStartupReconcileDelaySeconds { get; set; } = 8;
    /// <summary>
    /// Optional LAN bearer token. When non-empty, all /api/* and /swagger/* requests must
    /// present a matching <c>X-LAN-Token</c> header (or <c>Authorization: Bearer ...</c>, or a
    /// query parameter <c>?lanToken=...</c>) or respond 401. CORS tightens to <see cref="AllowedOrigins"/>.
    /// Leave empty on dev machines bound to 127.0.0.1 only.
    /// </summary>
    public string LanAuthToken { get; set; } = string.Empty;
    /// <summary>
    /// Cross-origin allowlist used when <see cref="LanAuthToken"/> is non-empty. Same-origin
    /// requests are unaffected regardless. Empty list denies all cross-origin calls.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
    /// <summary>
    /// When true (typically only in tests via WebApplicationFactory in-memory config), skip LAN
    /// discovery providers so the suite does not depend on 5523-W or other LAN hardware being
    /// reachable. Production must leave this false.
    /// </summary>
    public bool DiscoveryOfflineMode { get; set; }
}
