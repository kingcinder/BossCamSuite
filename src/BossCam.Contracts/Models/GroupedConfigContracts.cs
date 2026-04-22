using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupedConfigKind
{
    ImageConfig,
    VideoEncodeConfig,
    NetworkConfig,
    WifiConfig,
    UserConfig,
    AlarmConfig,
    StorageConfig
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupedApplyBehavior
{
    ImmediateApplied,
    DelayedApplied,
    RequiresSecondWrite,
    RequiresRelatedFieldWrite,
    RequiresCommitTrigger,
    StoredButNotOperational,
    Unapplied,
    Uncertain,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ForcedFieldClassification
{
    Writable,
    WritableNeedsCommitTrigger,
    ReadableOnly,
    Ignored,
    RequiresGroupedWrite,
    RequiresCommitTrigger,
    DelayedApply,
    Uncertain,
    Unsupported
}

public sealed record GroupedConfigSnapshot
{
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public GroupedConfigKind GroupKind { get; init; } = GroupedConfigKind.ImageConfig;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "PUT";
    public JsonObject Payload { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GroupedRetestRequest
{
    public bool RefreshFromDevice { get; init; } = true;
    public bool IncludeDangerous { get; init; }
    public bool ExpertOverride { get; init; } = true;
    public IReadOnlyCollection<string> FieldKeys { get; init; } = [];
}

public sealed record ForcedEnumerationRequest
{
    public bool RefreshFromDevice { get; init; } = true;
    public bool IncludeDangerous { get; init; }
    public bool ExpertOverride { get; init; } = true;
    public IReadOnlyCollection<GroupedConfigKind> Groups { get; init; } = [];
    public IReadOnlyCollection<string> FieldKeys { get; init; } = [];
}

public sealed record GroupedFamilyProbeRequest
{
    public bool RefreshFromDevice { get; init; } = true;
    public bool IncludePrivacyMasks { get; init; } = true;
    public bool ExpertOverride { get; init; } = true;
    public IReadOnlyCollection<string> Families { get; init; } = [];
    public IReadOnlyCollection<string> FieldKeys { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldPipelineGroup
{
    Isp,
    TransformDisplay,
    ModeHardware,
    VideoEncode
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OwnershipWriteClassification
{
    Writable,
    WritableDifferentEndpoint,
    ReadableOnly,
    Unsupported
}

public sealed record PipelineOwnershipProbeRequest
{
    public bool RefreshFromDevice { get; init; } = true;
    public bool ExpertOverride { get; init; } = true;
}

public sealed record PipelineOwnershipFieldResult
{
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public FieldPipelineGroup Pipeline { get; init; } = FieldPipelineGroup.Isp;
    public string RequestedEndpoint { get; init; } = string.Empty;
    public string? AlternateEndpoint { get; init; }
    public string EffectiveEndpoint { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public OwnershipWriteClassification Classification { get; init; } = OwnershipWriteClassification.ReadableOnly;
    public JsonNode? BaselineValue { get; init; }
    public JsonNode? AttemptedValue { get; init; }
    public JsonNode? ResultValue { get; init; }
    public bool AlternateEndpointAvailable { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record EncodeFullObjectProbeResult
{
    public string Endpoint { get; init; } = string.Empty;
    public JsonObject? BaselinePayload { get; init; }
    public JsonObject? AttemptedPayload { get; init; }
    public JsonNode? ResultValue { get; init; }
    public bool WriteAccepted { get; init; }
    public OwnershipWriteClassification Classification { get; init; } = OwnershipWriteClassification.ReadableOnly;
    public string Notes { get; init; } = string.Empty;
}

public sealed record PipelineOwnershipProbeReport
{
    public Guid DeviceId { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public JsonObject? VideoInputShape { get; init; }
    public JsonObject? ImageShape { get; init; }
    public IReadOnlyCollection<PipelineOwnershipFieldResult> Fields { get; init; } = [];
    public EncodeFullObjectProbeResult? EncodeProbe { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SdkFieldDefinition
{
    public GroupedConfigKind GroupKind { get; init; } = GroupedConfigKind.ImageConfig;
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string EndpointPattern { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public ContractFieldKind Kind { get; init; } = ContractFieldKind.Opaque;
    public bool Writable { get; init; } = true;
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public IReadOnlyCollection<string> EnumValues { get; init; } = [];
    public string SourceEvidence { get; init; } = "ipc-sdk-v1.4";
    public string Notes { get; init; } = string.Empty;
}

public sealed record GroupedUnsupportedRetestResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public GroupedConfigKind GroupKind { get; init; } = GroupedConfigKind.ImageConfig;
    public string ContractKey { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public JsonNode? BaselineValue { get; init; }
    public JsonNode? AttemptedValue { get; init; }
    public JsonNode? ImmediateValue { get; init; }
    public JsonNode? Delayed1sValue { get; init; }
    public JsonNode? Delayed3sValue { get; init; }
    public JsonNode? Delayed5sValue { get; init; }
    public bool FirstWriteSucceeded { get; init; }
    public bool SecondaryWriteSucceeded { get; init; }
    public bool ResendWriteSucceeded { get; init; }
    public GroupedApplyBehavior Behavior { get; init; } = GroupedApplyBehavior.Unknown;
    public ForcedFieldClassification Classification { get; init; } = ForcedFieldClassification.Unsupported;
    public bool InjectedMissingField { get; init; }
    public bool BaselineFieldPresent { get; init; }
    public string DefinitionSource { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GroupedApplyProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public GroupedConfigKind GroupKind { get; init; } = GroupedConfigKind.ImageConfig;
    public GroupedApplyBehavior DominantBehavior { get; init; } = GroupedApplyBehavior.Unknown;
    public int ImmediateAppliedCount { get; init; }
    public int DelayedAppliedCount { get; init; }
    public int RequiresSecondWriteCount { get; init; }
    public int RequiresCommitTriggerCount { get; init; }
    public int UnappliedCount { get; init; }
    public IReadOnlyCollection<string> Endpoints { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Notes { get; init; } = string.Empty;
}
