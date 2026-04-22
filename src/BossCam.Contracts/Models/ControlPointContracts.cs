using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ControlPointPrimitiveType
{
    Unknown,
    Boolean,
    Integer,
    Float,
    String,
    Object,
    Array
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ControlPointValueType
{
    BooleanToggle,
    SingleSelectSet,
    MultiSelectSet,
    ScalarOrCodeValue,
    FreeformSemanticValue,
    CompositeControl,
    HigherOrderComposite
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ControlPointTrait
{
    Bounded,
    EnumBacked,
    BitmaskLike,
    StringCoded,
    GroupedWriteRequired,
    CommitTriggerSensitive,
    EndpointDependent,
    InterFieldDependent
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ControlPointWidgetKind
{
    HiddenInNormalUi,
    Toggle,
    Dropdown,
    Checklist,
    NumericInput,
    Slider,
    TextInput,
    StructuredPanel,
    DependencyPanel
}

public sealed record ControlPointInventoryItem
{
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string WrapperObjectName { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ReadWriteState { get; init; } = string.Empty;
    public string Ownership { get; init; } = string.Empty;
    public string LiveEvidence { get; init; } = string.Empty;
    public ControlPointPrimitiveType PrimitiveType { get; init; } = ControlPointPrimitiveType.Unknown;
    public ControlPointValueType? ControlType { get; init; }
    public IReadOnlyCollection<ControlPointTrait> Traits { get; init; } = [];
    public IReadOnlyCollection<string> AllowedValues { get; init; } = [];
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public string? RequiredFormat { get; init; }
    public bool ValuesBounded { get; init; }
    public bool InterFieldDependent { get; init; }
    public bool GroupedWriteRequired { get; init; }
    public string WriteShape { get; init; } = string.Empty;
    public ControlPointWidgetKind RecommendedWidget { get; init; } = ControlPointWidgetKind.HiddenInNormalUi;
    public string ExistingWidget { get; init; } = string.Empty;
    public bool ExistingWidgetMismatch { get; init; }
    public bool NormalUiEligible { get; init; }
    public string ExactBlocker { get; init; } = string.Empty;
}

public sealed record ControlPointInventoryFamily
{
    public string Family { get; init; } = string.Empty;
    public IReadOnlyCollection<ControlPointInventoryItem> Controls { get; init; } = [];
}

public sealed record ControlPointInventoryReport
{
    public Guid DeviceId { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public IReadOnlyCollection<ControlPointInventoryFamily> Families { get; init; } = [];
    public IReadOnlyCollection<ControlPointInventoryItem> AmbiguousControls { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
