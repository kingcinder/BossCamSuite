using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransportKind
{
    LanRest,
    LanPrivateHttp,
    EseeJuanP2P,
    Kp2p,
    LinkVision,
    OnvifRtsp,
    Rtsp,
    RtspOverHttp,
    FlvOverHttp,
    Rtmp,
    BubbleFlv,
    NativeFallback,
    RemoteCommand
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettingValueKind
{
    String,
    Number,
    Boolean,
    Object,
    Array,
    Xml,
    Binary,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaintenanceOperation
{
    Reboot,
    FactoryReset,
    FirmwareUpload,
    PasswordReset,
    RefreshUsers
}

public sealed record DeviceChannelMap
{
    public int ChannelNumber { get; init; }
    public string? ChannelId { get; init; }
    public string? Name { get; init; }
    public string? Role { get; init; }
}

public sealed record TransportProfile
{
    public TransportKind Kind { get; init; }
    public string Address { get; init; } = string.Empty;
    public int Rank { get; init; } = 100;
    public bool IsRemote { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// Connectivity health status for a camera device.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectivityStatus
{
    /// <summary>No connectivity data yet, or device just registered.</summary>
    Unknown,
    /// <summary>All transports (RTSP + HTTP API) are reachable and responding.</summary>
    Healthy,
    /// <summary>At least one transport is reachable, but degraded (e.g. HTTP works, RTSP doesn't).</summary>
    Degraded,
    /// <summary>No transport is reachable; connectivity lost.</summary>
    Offline
}

/// <summary>
/// Snapshot of a device's connectivity health, updated by <c>ConnectivityWatchdogWorker</c>.
/// </summary>
public sealed record DeviceConnectivitySnapshot
{
    public Guid DeviceId { get; init; }
    public ConnectivityStatus Status { get; init; } = ConnectivityStatus.Unknown;
    /// <summary>Which transports were tested and their individual results.</summary>
    public Dictionary<string, bool> TransportResults { get; init; } = [];
    /// <summary>When the last check was performed.</summary>
    public DateTimeOffset LastCheckedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Human-readable summary of the last diagnostic run.</summary>
    public string? LastDiagnosticSummary { get; init; }
    /// <summary>Optional reconnect actions that were attempted and their results.</summary>
    public Dictionary<string, string> ReconnectAttempts { get; init; } = [];
}

/// <summary>
/// Optional body for <c>POST /api/devices/discover</c>. When <see cref="IpRangeOverride"/> is
/// non-empty the discovery pass forces the subnet sweep (the "Scan subnet" button) instead of
/// treating it as a multicast-only fallback. "auto" scans all local /24 subnets; a CIDR such as
/// <c>10.0.0.0/24</c> restricts the sweep to that subnet.
/// </summary>
public sealed record DiscoverRequest
{
    public string? IpRangeOverride { get; init; }
}

/// <summary>
/// How the operator expects a camera to be connected — LAN (wired, usually stable) or Wi‑Fi
/// (flaky: repeated-failure-before-Offline + longer backoff applies). Discovery providers and
/// enrollment tag this when the model/fleet knowledge says so; <c>Unknown</c> is the default.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LinkHint
{
    Unknown,
    Lan,
    Wifi
}

public sealed record DeviceIdentity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? DeviceId { get; init; }
    public string? EseeId { get; init; }
    public string? Name { get; init; }
    public string? IpAddress { get; init; }
    public int Port { get; init; } = 80;
    /// <summary>
    /// NetSDK/HTTP control plane port (deviceInfo / snapshot / settings). When non-positive the
    /// consumer falls back to <see cref="Port"/> then 80. Discovery may record the ONVIF/media
    /// port in <see cref="Port"/> while the NetSDK REST surface actually listens here (5523-W:
    /// ONVIF 8888/8899 vs HTTP 80) — adapters probe <c>HttpControlPort → Port → 80</c> in order
    /// and persist the working value back via the port-learning pass.
    /// </summary>
    public int HttpControlPort { get; init; }
    /// <summary>ONVIF device/media service port when known (8888/8899 for 5523-W, WVC: 8899). Optional.</summary>
    public int? OnvifMediaPort { get; init; }
    /// <summary>RTSP port (default 554). Optional; most brands are 554.</summary>
    public int? RtspPort { get; init; }
    /// <summary>Last URL that actually served deviceInfo/snapshot on this device (credentials redacted at rest).</summary>
    public string? LastGoodControlUrl { get; init; }
    /// <summary>Last RTSP URL that passed the playability probe (credentials redacted at rest).</summary>
    public string? LastGoodRtspUrl { get; init; }
    /// <summary>Wired vs Wi‑Fi hint (drives watchdog/failover backoff aggressiveness).</summary>
    public LinkHint LinkHint { get; init; } = LinkHint.Unknown;
    /// <summary>When true, the lifecycle worker keeps a continuous recording job running for this device.</summary>
    public bool ContinuousRecord { get; init; }
    public string? MacAddress { get; init; }
    public string? WirelessMacAddress { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? HardwareModel { get; init; }
    public string? DeviceType { get; init; }
    public string? LoginName { get; init; }

    /// <summary>
    /// Plaintext password — used in-memory only. Marked <see cref="JsonIgnoreAttribute"/>
    /// so the IApplicationStore persistence layer never writes plaintext to disk. The
    /// at-rest shape is <see cref="PasswordCiphertext"/>, populated by the cipher (AES-GCM
    /// keyfile on Linux/macOS, DPAPI CurrentUser on Windows) at save time. Load code
    /// resolves <see cref="PasswordCiphertext"/> back to <see cref="Password"/> via
    /// <c>IPasswordCipher.Decrypt</c> so consumers can keep reading <c>device.Password</c>
    /// in-memory without each one needing cipher injection.
    /// </summary>
    [JsonIgnore]
    public string? Password { get; init; }

    public string? PasswordCiphertext { get; init; }
    public List<DeviceChannelMap> ChannelMap { get; init; } = [];
    public List<TransportProfile> TransportProfiles { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DeviceId ?? EseeId ?? IpAddress ?? Id.ToString() : Name!;
}

public sealed record CapabilityMap
{
    public Guid DeviceId { get; init; }
    public string? PrimaryControlAdapter { get; init; }
    public List<string> ControlAdapters { get; init; } = [];
    public List<TransportKind> VideoTransportKinds { get; init; } = [];
    public List<string> SupportedSettingGroups { get; init; } = [];
    public List<string> SupportedEndpointPaths { get; init; } = [];
    public List<string> SupportedMaintenanceOperations { get; init; } = [];
    public Dictionary<string, string> Notes { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SettingDescriptor
{
    public string Key { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public string Method { get; init; } = "GET";
    public SettingValueKind ValueKind { get; init; } = SettingValueKind.Unknown;
    public bool IsReadOnly { get; init; }
    public string? Description { get; init; }
}

public sealed record SettingValue
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public JsonNode? Value { get; init; }
    public string? SourceEndpoint { get; init; }
    public SettingValueKind ValueKind { get; init; } = SettingValueKind.Unknown;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SettingGroup
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public List<SettingDescriptor> Descriptors { get; init; } = [];
    public Dictionary<string, SettingValue> Values { get; init; } = [];
    public JsonNode? RawPayload { get; init; }
}

public sealed record SettingsSnapshot
{
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public List<SettingGroup> Groups { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
