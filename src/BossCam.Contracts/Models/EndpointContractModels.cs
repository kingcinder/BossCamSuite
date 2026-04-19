using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractFieldKind
{
    String,
    Number,
    Integer,
    Boolean,
    Enum,
    Object,
    Array,
    IpAddress,
    Port,
    Password,
    Opaque
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTruthState
{
    Proven,
    Inferred,
    Unverified
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractSurface
{
    NetSdkRest,
    PrivateCgiXml,
    NativeFallback
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractSupportState
{
    Supported,
    Unsupported,
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemanticWriteStatus
{
    TransportFailed,
    EndpointRejected,
    AcceptedNoObservableChange,
    AcceptedClamped,
    AcceptedTranslated,
    AcceptedChanged,
    AcceptedChangedThenReverted,
    AcceptedPersistedAfterDelay,
    AcceptedPersistedAfterReboot,
    AcceptedLostAfterReboot,
    ContractViolation,
    ShapeMismatch,
    Uncertain,
    Unverified
}

public sealed record ContractScope
{
    public string FirmwareFingerprintPattern { get; init; } = "*";
    public string? DeviceType { get; init; }
    public string? HardwareModelPattern { get; init; }
}

public sealed record ContractEnumValue
{
    public string Value { get; init; } = string.Empty;
    public string? Label { get; init; }
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
}

public sealed record ContractValidationRule
{
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Regex { get; init; }
    public bool Sensitive { get; init; }
}

public sealed record ContractEvidence
{
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public string Source { get; init; } = "manifest";
    public string? FixturePath { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public string? Notes { get; init; }
}

public sealed record ContractField
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public ContractFieldKind Kind { get; init; } = ContractFieldKind.Opaque;
    public bool Required { get; init; }
    public bool Nullable { get; init; }
    public bool ExpertOnly { get; init; }
    public bool Writable { get; init; }
    public bool PersistExpectedAfterReboot { get; init; }
    public DisruptionClass DisruptionClass { get; init; } = DisruptionClass.Unknown;
    public ContractValidationRule Validation { get; init; } = new();
    public IReadOnlyCollection<ContractEnumValue> EnumValues { get; init; } = [];
    public ContractEvidence Evidence { get; init; } = new();
}

public sealed record ContractObjectShape
{
    public string RootPath { get; init; } = "$";
    public bool FullObjectWriteRequired { get; init; } = true;
    public bool PartialWriteAllowed { get; init; }
    public IReadOnlyCollection<string> RequiredRootFields { get; init; } = [];
}

public sealed record EndpointContract
{
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "PUT";
    public ContractSurface Surface { get; init; } = ContractSurface.NetSdkRest;
    public string AuthMode { get; init; } = "basic-or-digest";
    public TypedSettingGroupKind GroupKind { get; init; } = TypedSettingGroupKind.Diagnostics;
    public string GroupName { get; init; } = string.Empty;
    public ContractScope Scope { get; init; } = new();
    public ContractObjectShape ObjectShape { get; init; } = new();
    public DisruptionClass DisruptionClass { get; init; } = DisruptionClass.Unknown;
    public bool ExpertOnly { get; init; }
    public bool RequiresRebootToTakeEffect { get; init; }
    public bool PersistenceExpectedAfterReboot { get; init; }
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public IReadOnlyCollection<ContractField> Fields { get; init; } = [];
}

public sealed record EndpointContractFixture
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public string ContractKey { get; init; } = string.Empty;
    public string? FirmwareFingerprint { get; init; }
    public string? AuthMode { get; init; }
    public string FixturePath { get; init; } = string.Empty;
    public JsonNode? RequestBody { get; init; }
    public JsonNode? ResponseBody { get; init; }
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SemanticWriteResult
{
    public bool Success { get; init; }
    public SemanticWriteStatus SemanticStatus { get; init; } = SemanticWriteStatus.Unverified;
    public string? Message { get; init; }
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public JsonNode? IntendedValue { get; init; }
    public JsonNode? ImmediateActualValue { get; init; }
    public JsonNode? PostRebootValue { get; init; }
    public IReadOnlyCollection<string> Violations { get; init; } = [];
}

public sealed record ContractValidationResult
{
    public bool IsValid { get; init; }
    public bool Blocked { get; init; }
    public bool ExpertOverrideUsed { get; init; }
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public IReadOnlyCollection<string> Errors { get; init; } = [];
}
