using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldValidityState
{
    Proven,
    Inferred,
    Unverified,
    Unsupported,
    Invalid
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypedSettingGroupKind
{
    VideoImage,
    NetworkWireless,
    UsersMaintenance,
    MotionPrivacyAlarms,
    PtzOptics,
    StoragePlayback,
    Diagnostics
}

public sealed record EditorHint
{
    public string FieldKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string EditorKind { get; init; } = "text";
    public JsonArray? EnumValues { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public string? Unit { get; init; }
    public DisruptionClass DisruptionClass { get; init; } = DisruptionClass.Unknown;
    public bool ExpertOnly { get; init; }
    public bool Writable { get; init; }
    public string? ContractKey { get; init; }
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public ControlPointPrimitiveType PrimitiveType { get; init; } = ControlPointPrimitiveType.Unknown;
    public ControlPointValueType? ControlType { get; init; }
    public IReadOnlyCollection<ControlPointTrait> ControlTraits { get; init; } = [];
    public ControlPointWidgetKind RecommendedWidget { get; init; } = ControlPointWidgetKind.HiddenInNormalUi;
    public bool NormalUiEligible { get; init; }
    public string? TypeBlocker { get; init; }
}

public sealed record NormalizedSettingField
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public TypedSettingGroupKind GroupKind { get; init; } = TypedSettingGroupKind.Diagnostics;
    public string GroupName { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string ParserName { get; init; } = string.Empty;
    public JsonNode? TypedValue { get; init; }
    public JsonNode? RawValue { get; init; }
    public string SourceEndpoint { get; init; } = string.Empty;
    public string RawSourcePath { get; init; } = string.Empty;
    public string? ContractKey { get; init; }
    public string? FirmwareFingerprint { get; init; }
    public FieldValidityState Validity { get; init; } = FieldValidityState.Unverified;
    public string Confidence { get; init; } = "unverified";
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public ContractSupportState SupportState { get; init; } = ContractSupportState.Uncertain;
    public bool ReadVerified { get; init; }
    public bool WriteVerified { get; init; }
    public bool PersistsAfterReboot { get; init; }
    public bool PersistenceExpectedAfterReboot { get; init; }
    public bool ExpertOnly { get; init; }
    public DisruptionClass DisruptionClass { get; init; } = DisruptionClass.Unknown;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TypedSettingGroupSnapshot
{
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string? FirmwareFingerprint { get; init; }
    public TypedSettingGroupKind GroupKind { get; init; } = TypedSettingGroupKind.Diagnostics;
    public string GroupName { get; init; } = string.Empty;
    public IReadOnlyCollection<NormalizedSettingField> Fields { get; init; } = [];
    public IReadOnlyCollection<EditorHint> EditorHints { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record FirmwareCapabilityProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string? HardwareModel { get; init; }
    public IReadOnlyCollection<string> SupportedEndpointFamilies { get; init; } = [];
    public IReadOnlyCollection<string> SupportedSettingGroups { get; init; } = [];
    public IReadOnlyCollection<string> VerifiedWritableFields { get; init; } = [];
    public IReadOnlyCollection<string> InferredWritableFields { get; init; } = [];
    public IReadOnlyCollection<string> RebootRequiredFields { get; init; } = [];
    public IReadOnlyCollection<string> DangerousFields { get; init; } = [];
    public IReadOnlyCollection<string> ExpertOnlyFields { get; init; } = [];
    public IReadOnlyCollection<string> NativeFallbackRequiredFields { get; init; } = [];
    public IReadOnlyCollection<string> UncertainFields { get; init; } = [];
    public IReadOnlyCollection<string> FullObjectWriteFields { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TypedFieldChange(string FieldKey, JsonNode? Value);

public sealed record PersistenceEligibleField
{
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public bool RequiresRebootToTakeEffect { get; init; }
    public bool PersistenceExpectedAfterReboot { get; init; }
    public bool ExpertOnly { get; init; }
    public bool WriteVerified { get; init; }
    public bool Supported { get; init; }
}
