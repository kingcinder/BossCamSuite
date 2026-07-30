namespace BossCam.Core;

public sealed class BossCamRuntimeOptions
{
    public string ProtocolAssetsPath { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    /// <summary>Windows OEM install dir for NativeBridge interop. Defaults to empty on Linux
    /// (the Windows edition at github.com/kingcinder/BossCam-Suite---Windows-Edition populates this).</summary>
    public string IpcamSuiteDirectory { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\Program Files\IPCamSuite"
        : string.Empty;
    /// <summary>Windows OEM install dir for EseeCloud P2P transport. Defaults to empty on Linux
    /// (the Windows edition at github.com/kingcinder/BossCam-Suite---Windows-Edition populates this).</summary>
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

    /// <summary>
    /// Optional override for the local-machine ciphertext key path. On Linux/macOS this is the
    /// path of a 0600-permissioned AES-GCM keyfile (32 random bytes) backing <c>IPasswordCipher</c>.
    /// Windows ignores this; DPAPI/ProtectedData with CurrentUser scope is used instead.
    /// Defaults to <c>$XDG_DATA_HOME/BossCamSuite/secret.key</c> (Linux) or
    /// <c>%LocalAppData%/BossCamSuite/secret.key</c> (macOS).
    /// </summary>
    public string SecretKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Single absolute path that bounds <c>POST /api/storage/paths</c> submissions. Operator
    /// must explicitly set this to redirect recordings to a NAS / disk other than the default.
    /// Empty disables the override; default = LocalAppData/BossCamSuite/recordings.
    /// </summary>
    public string StorageRoot { get; set; } = string.Empty;

    /// <summary>
    /// Toggles <c>AddRateLimiter</c>/<c>UseRateLimiter</c> middleware. Tests set this false
    /// to permit tight retry loops; production should leave it true.
    /// </summary>
    public bool RateLimitEnabled { get; set; } = true;

    /// <summary>Per-minute cap for /api/devices/{id}/probe. Each probe spins one or more HTTP + ONVIF round-trips.</summary>
    public int RateLimitProbePerMinute { get; set; } = 6;

    /// <summary>Per-minute cap for /api/recordings/start. Each start spawns an ffmpeg process tree.</summary>
    public int RateLimitRecordingStartPerMinute { get; set; } = 10;

    /// <summary>PR-R4: Seconds of no segment growth before a recording job is considered stalled.
    /// Default = 3× segment length (90s for 30s segments). Zero disables stall detection.</summary>
    public int StallTimeoutSeconds { get; set; } = 90;

    /// <summary>PR-R4: When true, stalled pipelines auto-restart once instead of staying stopped.</summary>
    public bool StallAutoRestart { get; set; } = true;

    /// <summary>Per-minute cap for /api/devices/{id}/snapshot. Each snapshot is one or more HTTP GETs against the camera.</summary>
    public int RateLimitSnapshotPerMinute { get; set; } = 30;
}
