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

public sealed record DeviceIdentity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? DeviceId { get; init; }
    public string? EseeId { get; init; }
    public string? Name { get; init; }
    public string? IpAddress { get; init; }
    public int Port { get; init; } = 80;
    public string? MacAddress { get; init; }
    public string? WirelessMacAddress { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? HardwareModel { get; init; }
    public string? DeviceType { get; init; }
    public string? LoginName { get; init; }
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
