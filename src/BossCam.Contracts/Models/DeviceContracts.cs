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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraEndpointVerificationState
{
    Untested,
    Verified,
    Failed,
    Unauthorized,
    Unsupported,
    Timeout,
    UnverifiedCandidate
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointCandidateSource
{
    LiveProbe,
    OnvifGetCapabilities,
    OnvifGetServices,
    OnvifGetStreamUri,
    ModelTemplate,
    VendorFallback,
    UserSupplied,
    SampleFixture
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TruthStrength
{
    None,
    Candidate,
    FailedProbe,
    DeclaredByDevice,
    LiveVerified,
    PlaybackProbed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HardwareVariantCapability
{
    Unknown,
    ServiceOnly,
    BoardCapable,
    HardwareInstalled,
    NotInstalledOrUnknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraCredentialState
{
    Unknown,
    Missing,
    Supplied,
    Verified,
    Rejected,
    PlaybackLockedPendingCredentials
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MechanicalPtzCapability
{
    Unknown,
    NotInstalledOrUnknown,
    Installed,
    DisabledByDefault
}

public sealed record CameraEndpointObservation
{
    public string Capability { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public CameraEndpointVerificationState State { get; init; } = CameraEndpointVerificationState.Untested;
    public string Source { get; init; } = "candidate";
    public EndpointCandidateSource CandidateSource { get; init; } = EndpointCandidateSource.ModelTemplate;
    public TruthStrength TruthStrength { get; init; } = TruthStrength.Candidate;
    public string? Evidence { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record EndpointProbeResult
{
    public string Capability { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public CameraEndpointVerificationState State { get; init; } = CameraEndpointVerificationState.Untested;
    public EndpointCandidateSource Source { get; init; } = EndpointCandidateSource.LiveProbe;
    public string? Evidence { get; init; }
}

public sealed record OnvifDeclaredStreamMetadata
{
    public string ProfileToken { get; init; } = string.Empty;
    public string? VideoSourceToken { get; init; }
    public string? EncoderToken { get; init; }
    public string? Encoding { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public decimal? Fps { get; init; }
    public int? BitrateKbps { get; init; }
    public int? Gop { get; init; }
    public string? H264Profile { get; init; }
}

public sealed record DeclaredOnvifStreamMetadata
{
    public OnvifDeclaredStreamMetadata Metadata { get; init; } = new();
}

public sealed record RtspPlaybackProbeMetadata
{
    public string ProfileToken { get; init; } = string.Empty;
    public string Uri { get; init; } = string.Empty;
    public CameraEndpointVerificationState State { get; init; } = CameraEndpointVerificationState.Untested;
    public CameraCredentialState CredentialState { get; init; } = CameraCredentialState.Unknown;
    public string? VerifiedUsername { get; init; }
    public string? Codec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Fps { get; init; }
    public string? Evidence { get; init; }
}

public sealed record ProbedPlaybackStreamMetadata
{
    public RtspPlaybackProbeMetadata Metadata { get; init; } = new();
}

public sealed record DriftObservation
{
    public string Subject { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
    public string Observed { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PtzServiceState
{
    public CameraEndpointVerificationState ServiceState { get; init; } = CameraEndpointVerificationState.Untested;
    public string? ServiceEndpoint { get; init; }
    public bool GetStatusVerified { get; init; }
    public MechanicalPtzCapability MechanicalCapability { get; init; } = MechanicalPtzCapability.Unknown;
    public bool MovementControlsEnabled { get; init; }
    public string? OperatorMessage { get; init; }
}

public sealed record CameraEndpointTruthProfile
{
    public Guid DeviceId { get; init; }
    public string? IpAddress { get; init; }
    public string? HardwareModel { get; init; }
    public string? FirmwareVersion { get; init; }
    public CameraCredentialState CredentialState { get; init; } = CameraCredentialState.Unknown;
    public List<CameraEndpointObservation> Endpoints { get; init; } = [];
    public List<OnvifDeclaredStreamMetadata> OnvifDeclaredStreams { get; init; } = [];
    public List<RtspPlaybackProbeMetadata> RtspPlaybackStreams { get; init; } = [];
    public PtzServiceState Ptz { get; init; } = new();
    public List<string> DriftNotes { get; init; } = [];
    public List<DriftObservation> DriftObservations { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PerCameraEndpointTruth
{
    public CameraEndpointTruthProfile Profile { get; init; } = new();
}

public sealed record CameraEndpointTruthSummary
{
    public CameraEndpointTruthProfile? Profile { get; init; }
    public IReadOnlyCollection<CameraEndpointTruthProfile> SameModelProfiles { get; init; } = [];
    public bool EndpointDriftDetected { get; init; }
    public IReadOnlyCollection<string> DriftNotes { get; init; } = [];
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
