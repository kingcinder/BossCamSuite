namespace BossCam.Core;

/// <summary>One camera entry for the <see cref="BossCamRuntimeOptions.AegonLanDevices"/> batch.</summary>
public sealed class AegonLanDeviceOptions
{
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public string LoginName { get; set; } = "admin";
    public string Name { get; set; } = string.Empty;
    public string HardwareModel { get; set; } = string.Empty;
}

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
    /// <summary>
    /// Extra absolute directories (beyond <see cref="FirmwareArtifactDirectory"/>) that
    /// <c>POST /api/firmware/register</c> and the firmware-upload maintenance path accept
    /// firmware files from. Empty means only <see cref="FirmwareArtifactDirectory"/> is allowed.
    /// Path containment is segment-aware, so a sibling like <c>/opt/firmware-evil</c> cannot
    /// masquerade as inside <c>/opt/firmware</c>.
    /// </summary>
    public string[] FirmwareAllowedDirectories { get; set; } = [];
    /// <summary>
    /// Absolute directories that <c>POST /api/recordings/export</c> may write clip files into.
    /// Empty (default) disables clip exports entirely until the operator sets at least one root.
    /// Path containment is segment-aware, so a sibling like <c>/mnt/exports-evil</c> cannot
    /// masquerade as inside <c>/mnt/exports</c>.
    /// </summary>
    public string[] ExportAllowedDirectories { get; set; } = [];
    /// <summary>
    /// Optional list of known cameras registered by <c>POST /api/devices/register-aegon-lan</c>.
    /// Defaults to empty — the historic hardcoded home-LAN topology was removed from the repo.
    /// W5C / Lorex passwords can still be supplied per-call to the endpoint.
    /// </summary>
    public AegonLanDeviceOptions[] AegonLanDevices { get; set; } = [];
    public string? RemoteCommandEndpoint { get; set; }
    public int DiscoveryTimeoutSeconds { get; set; } = 3;
    public int HttpTimeoutSeconds { get; set; } = 8;
    /// <summary>
    /// How long a successful NetSDK family probe (deviceInfo) stays trusted in the device's
    /// store metadata before <see cref="Video.NativeNetSdkStreamAdapter"/> re-probes. A fresh
    /// verdict skips the network probe on every stream-request source resolution; expiry or
    /// failed playback forces a re-probe. Zero/negative falls back to the default below.
    /// </summary>
    public int NetSdkProbeCacheTtlMinutes { get; set; } = 30;
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
    /// LAN-only / air-gapped operation. When true, the service refuses to touch anything that
    /// needs internet egress: cloud/P2P transport adapters (ESEE/Juan, KP2P, LinkVision) emit no
    /// sources, the remote-command relay is disabled, and the EseeCloud importer only keeps
    /// LAN profiles for devices that carry an IP. LAN discovery, streaming, snapshots, recording,
    /// settings control, and the operator UI all keep working against the local network.
    /// Set via <c>BossCam:OfflineMode=true</c> or <c>BOSSCAM_OFFLINE=1</c>.
    /// Default false — an internet-connected deployment keeps P2P/cloud paths available.
    /// </summary>
    public bool OfflineMode { get; set; }

    /// <summary>
    /// Lightweight external reachability URL used only to classify the optional internet/cloud
    /// plane. It never replaces LAN camera probes and is not contacted when OfflineMode is true.
    /// </summary>
    public string InternetConnectivityProbeUrl { get; set; } = "https://www.msftconnecttest.com/connecttest.txt";

    /// <summary>Seconds between automatic internet reachability probes.</summary>
    public int InternetConnectivityProbeIntervalSeconds { get; set; } = 15;

    /// <summary>Per-probe timeout for the optional internet reachability check.</summary>
    public int InternetConnectivityProbeTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// Consecutive failed internet probes required before optional cloud/P2P transports are gated.
    /// A successful probe restores them immediately.
    /// </summary>
    public int InternetConnectivityFailureThreshold { get; set; } = 2;

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

    /// <summary>
    /// Maximum consecutive stall auto-restarts allowed for one device before the fast
    /// auto-restart is suspended and the job is marked failed with a clear error
    /// ("camera source not producing media"). Prevents an ffmpeg spawn storm against a
    /// permanently-dead source — e.g. a 5523-W whose encoder pipeline locked up (RTSP
    /// answers but serves zero media) — from restarting a doomed job every stall cycle
    /// forever. The continuous-record policy remains the slow, backed-off recovery path
    /// so a camera that genuinely returns is still picked up. Zero disables the cap.
    /// Default 3.
    /// </summary>
    public int RecordingMaxConsecutiveRestarts { get; set; } = 3;

    /// <summary>Per-minute cap for /api/devices/{id}/snapshot. Each snapshot is one or more HTTP GETs against the camera.</summary>
    public int RateLimitSnapshotPerMinute { get; set; } = 30;

    /// <summary>
    /// Hard wall-clock budget (seconds) for each network-bound call inside the single-threaded
    /// <c>RecordingLifecycleWorker</c> supervision loop (stall check, auto-start reconcile,
    /// continuous-record reconcile). Source resolution probes unreachable cameras with
    /// HttpClient timeouts that can stack into minutes; without a budget, one offline camera
    /// starves stall detection for every other device. The abandoned call keeps running in the
    /// background (a late job start is deduped by the supervisor), so recording continuity is
    /// preserved while the loop always progresses. Default 60s.
    /// </summary>
    public int RecordingSupervisionCallTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Fast recovery cadence for recording supervision. This is intentionally independent of
    /// housekeeping: a transient LAN/camera flap must not wait for the normal 15-minute cycle.
    /// Healthy recorder processes are left alone; only exited or stalled jobs are reconciled.
    /// </summary>
    public int RecordingRecoveryIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Minimum delay between continuous-record restart attempts after a failed camera recovery.
    /// This protects ffmpeg and the camera from a tight restart loop while retaining automatic
    /// recovery when the LAN path returns.
    /// </summary>
    public int RecordingRecoveryRetrySeconds { get; set; } = 15;

    /// <summary>Upper bound for exponential retry delay after repeated recorder recovery failures.</summary>
    public int RecordingRecoveryMaxRetrySeconds { get; set; } = 300;

    /// <summary>
    /// Seconds before a spontaneous recorder exit for a continuous-record device is restarted
    /// when the source was still producing fresh segments (e.g. a 5523-W whose RTSP session
    /// the camera drops every few minutes). The continuous-record policy otherwise waits up to
    /// <see cref="RecordingRecoveryMaxRetrySeconds"/> before re-picking the device, leaving a
    /// multi-minute recording gap; this fast path re-arms the recorder in seconds. The same
    /// value acts as the per-device cooldown so a flapping camera cannot spawn a tight ffmpeg
    /// loop. Zero disables exit rapid-restart.
    /// </summary>
    public int RecordingExitRestartDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Absolute path to <c>scripts/recover-and-enroll-camera.sh</c>. When empty, the
    /// <c>CameraRecoveryService</c> falls back to <c>&lt;content-root&gt;/scripts/</c>
    /// then <c>&lt;cwd&gt;/scripts/</c>.
    /// </summary>
    public string RecoveryScriptPath { get; set; } = string.Empty;

    /// <summary>
    /// When true, <see cref="CameraRecoveryAutoWorker"/> autonomously scans the host WiFi
    /// for factory-reset camera APs (IPCZ7C34…) and runs the full recover-and-enroll
    /// pipeline for any AP that is not already enrolled and is not in cooldown — no human
    /// interaction required. The worker only acts while the host is connected to
    /// <see cref="RecoveryStaSsid"/> and never runs more than one recovery at a time.
    /// Set via <c>BossCam:RecoveryAutoScanEnabled</c> (default true).
    /// </summary>
    public bool RecoveryAutoScanEnabled { get; set; } = true;

    /// <summary>Seconds between autonomous camera-AP scans (clamped 15–600).</summary>
    public int RecoveryAutoScanIntervalSeconds { get; set; } = 45;

    /// <summary>Cooldown (minutes) before the same camera serial is auto-recovered again after a run starts.</summary>
    public int RecoveryAutoCooldownMinutes { get; set; } = 30;

    /// <summary>
    /// The home/STA SSID the host must be connected to for the auto-worker to scan and recover,
    /// and the network re-provisioned cameras are joined to. Defaults match the deployed fleet.
    /// </summary>
    public string RecoveryStaSsid { get; set; } = "Aegon";

    /// <summary>Passphrase for <see cref="RecoveryStaSsid"/> (re-provision target network).</summary>
    public string RecoveryStaPass { get; set; } = "812354444";

    /// <summary>Camera-AP hotspot passphrase used by the recovery script's factory-default try-list. The fleet's confirmed value is 11111111.</summary>
    public string RecoveryApPass { get; set; } = "11111111";

    /// <summary>
    /// Post-recovery recording-verification attempts. After the recover-and-enroll script
    /// reports success, the Suite independently confirms the camera actually records
    /// (RTSP reachable + a recording job active). When no job is running it retries starting
    /// one this many times before reporting the gap — recording continuity is the top
    /// priority, so a camera that came back but is NOT recording is surfaced, not assumed.
    /// </summary>
    public int RecoveryRecordingVerifyAttempts { get; set; } = 3;

    /// <summary>Seconds between post-recovery recording-verification retry attempts.</summary>
    public int RecoveryRecordingVerifyDelaySeconds { get; set; } = 10;
}
