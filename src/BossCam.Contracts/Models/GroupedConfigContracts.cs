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
    UserConfig
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupedApplyBehavior
{
    ImmediateApplied,
    DelayedApplied,
    RequiresSecondWrite,
    RequiresCommitTrigger,
    Unapplied,
    Unknown
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
