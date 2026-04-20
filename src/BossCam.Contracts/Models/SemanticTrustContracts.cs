using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceQuality
{
    Proven,
    Inferred,
    Unverified
}

public sealed record SemanticWriteObservation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "PUT";
    public string ContractKey { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public DisruptionClass DisruptionClass { get; init; } = DisruptionClass.Unknown;
    public JsonNode? IntendedValue { get; init; }
    public JsonNode? BaselineValue { get; init; }
    public JsonNode? ImmediateValue { get; init; }
    public JsonNode? DelayedValue { get; init; }
    public JsonNode? RebootValue { get; init; }
    public SemanticWriteStatus Status { get; init; } = SemanticWriteStatus.Unverified;
    public string? Notes { get; init; }
    public JsonObject Context { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record FieldConstraintProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public IReadOnlyCollection<string> SupportedValues { get; init; } = [];
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public decimal? Step { get; init; }
    public IReadOnlyCollection<ConstraintDependency> Dependencies { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
    public EvidenceQuality Quality { get; init; } = EvidenceQuality.Unverified;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ConstraintDependency
{
    public string FieldKey { get; init; } = string.Empty;
    public IReadOnlyCollection<string> AllowedValues { get; init; } = [];
    public string? Rule { get; init; }
    public EvidenceQuality Quality { get; init; } = EvidenceQuality.Unverified;
}

public sealed record DependencyMatrixProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public IReadOnlyCollection<FieldDependencyRule> Rules { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record FieldDependencyRule
{
    public string PrimaryFieldKey { get; init; } = string.Empty;
    public string DependsOnFieldKey { get; init; } = string.Empty;
    public IReadOnlyCollection<string> DependsOnValues { get; init; } = [];
    public IReadOnlyCollection<string> AllowedPrimaryValues { get; init; } = [];
    public string? Notes { get; init; }
    public EvidenceQuality Quality { get; init; } = EvidenceQuality.Unverified;
}

public sealed record ConstraintDiscoveryRequest
{
    public Guid DeviceId { get; init; }
    public IReadOnlyCollection<string> FieldKeys { get; init; } = [];
    public bool IncludeNetworkChanging { get; init; }
    public bool ExpertOverride { get; init; }
    public int DelaySeconds { get; init; } = 3;
}

public sealed record ConstraintDiscoveryResult
{
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public IReadOnlyCollection<FieldConstraintProfile> UpdatedProfiles { get; init; } = [];
    public IReadOnlyCollection<SemanticWriteObservation> Observations { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
}

public sealed record NetworkRecoveryContext
{
    public Guid DeviceId { get; init; }
    public string? PreviousIp { get; init; }
    public string? PreviousGateway { get; init; }
    public string? PreviousDns { get; init; }
    public string? PreviousControlUrl { get; init; }
    public string? PredictedControlUrl { get; init; }
}

public sealed record NetworkRecoveryResult
{
    public Guid DeviceId { get; init; }
    public bool Recovered { get; init; }
    public string? ReachableUrl { get; init; }
    public string Notes { get; init; } = string.Empty;
    public IReadOnlyCollection<string> ProbedUrls { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageInventoryStatus
{
    Readable,
    Writable,
    Blocked,
    TransportSuccessNoSemanticChange,
    HiddenAdjacentCandidate,
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HiddenCandidateClassification
{
    ReadableOnly,
    Writable,
    Ignored,
    Dangerous,
    RequiresCommitTrigger,
    AltWriteShapeRequired,
    HiddenAdjacentCandidate,
    PrivatePathCandidate,
    NoSemanticProof,
    RejectedByFirmware,
    LikelyUnsupported,
    UnsupportedOnFirmware,
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageBehaviorClass
{
    NoObservableChange,
    MinorChange,
    ModerateChange,
    ThresholdJump,
    CatastrophicDark,
    CatastrophicBright,
    TemporarySpikeThenSettles,
    Unstable,
    Clamped,
    Ignored,
    Rejected,
    Uncertain
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageCommitBehavior
{
    ImmediateApplied,
    DelayedApplied,
    StoredOnly,
    RequiresSecondaryTrigger,
    RequiresApplyEndpoint,
    RequiresReboot,
    Unknown
}

public sealed record ImageControlInventoryItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public bool Readable { get; init; }
    public bool Writable { get; init; }
    public bool WriteVerified { get; init; }
    public ContractSupportState SupportState { get; init; } = ContractSupportState.Uncertain;
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public ImageInventoryStatus Status { get; init; } = ImageInventoryStatus.Uncertain;
    public HiddenCandidateClassification CandidateClassification { get; init; } = HiddenCandidateClassification.Uncertain;
    public bool PromotedToUi { get; init; }
    public IReadOnlyCollection<string> ReasonCodes { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ImageWritableTestCase
{
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public JsonNode? BaselineValue { get; init; }
    public IReadOnlyCollection<JsonNode?> CandidateValues { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
}

public sealed record ImageWritableTestSetProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public IReadOnlyCollection<ImageWritableTestCase> Cases { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record OperationalImageMetric
{
    public string CaptureMode { get; init; } = "none";
    public double? LuminanceMeanBefore { get; init; }
    public double? LuminanceMeanAfter { get; init; }
    public double? ContrastSpreadBefore { get; init; }
    public double? ContrastSpreadAfter { get; init; }
    public double? BlackClipBefore { get; init; }
    public double? BlackClipAfter { get; init; }
    public double? WhiteClipBefore { get; init; }
    public double? WhiteClipAfter { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record ImageBehaviorPoint
{
    public JsonNode? AttemptedValue { get; init; }
    public JsonNode? BaselineValue { get; init; }
    public JsonNode? ImmediateValue { get; init; }
    public JsonNode? Delayed1sValue { get; init; }
    public JsonNode? Delayed3sValue { get; init; }
    public JsonNode? Delayed5sValue { get; init; }
    public SemanticWriteStatus SemanticStatus { get; init; } = SemanticWriteStatus.Unverified;
    public ImageBehaviorClass BehaviorClass { get; init; } = ImageBehaviorClass.Uncertain;
    public ImageCommitBehavior CommitBehavior { get; init; } = ImageCommitBehavior.Unknown;
    public OperationalImageMetric OperationalMetric { get; init; } = new();
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ImageFieldBehaviorMap
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public IReadOnlyCollection<ImageBehaviorPoint> Points { get; init; } = [];
    public decimal? SafeMin { get; init; }
    public decimal? SafeMax { get; init; }
    public IReadOnlyCollection<decimal> Thresholds { get; init; } = [];
    public IReadOnlyCollection<decimal> CatastrophicValues { get; init; } = [];
    public string RecommendedRange { get; init; } = string.Empty;
    public string TriggerSequence { get; init; } = string.Empty;
    public ContractTruthState TruthState { get; init; } = ContractTruthState.Unverified;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ImageTruthSweepResult
{
    public Guid DeviceId { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public IReadOnlyCollection<ImageControlInventoryItem> Inventory { get; init; } = [];
    public IReadOnlyCollection<ImageWritableTestCase> WritableTestSet { get; init; } = [];
    public IReadOnlyCollection<ImageFieldBehaviorMap> BehaviorMaps { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
}
