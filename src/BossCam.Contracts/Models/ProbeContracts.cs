using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeStageMode
{
    InventoryOnly,
    SafeReadOnly,
    SafeWriteVerify,
    NetworkImpacting,
    RebootRequired,
    ExpertFull
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeSessionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Partial
}

public sealed record ProbeSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string DeviceDisplayName { get; init; } = string.Empty;
    public string? DeviceIp { get; init; }
    public string? FirmwareFingerprint { get; init; }
    public string? AuthMode { get; init; }
    public ProbeStageMode Mode { get; init; } = ProbeStageMode.SafeReadOnly;
    public ProbeSessionStatus Status { get; init; } = ProbeSessionStatus.Pending;
    public bool ResumeRequested { get; init; }
    public bool IncludePersistenceChecks { get; init; }
    public bool IncludeRollbackChecks { get; init; } = true;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public string? TranscriptBundlePath { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record ProbeStageResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public Guid DeviceId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public ProbeStageMode Mode { get; init; } = ProbeStageMode.SafeReadOnly;
    public int EndpointsTotal { get; init; }
    public int ReadVerifiedCount { get; init; }
    public int WriteVerifiedCount { get; init; }
    public int PersistenceVerifiedCount { get; init; }
    public int RollbackSupportedCount { get; init; }
    public bool RebootEncountered { get; init; }
    public string Summary { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ProbeSessionRequest
{
    public Guid? DeviceId { get; init; }
    public string? DeviceIp { get; init; }
    public string? ProfileName { get; init; }
    public ProbeStageMode Mode { get; init; } = ProbeStageMode.SafeReadOnly;
    public bool DiscoverIfMissing { get; init; }
    public bool ResumeIfExists { get; init; }
    public bool IncludePersistenceChecks { get; init; }
    public bool IncludeRollbackChecks { get; init; } = true;
    public bool RequestRebootVerification { get; init; }
    public string? TranscriptExportDirectory { get; init; }
}

public sealed record PersistenceVerificationRequest
{
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "PUT";
    public JsonObject Payload { get; init; } = [];
    public bool RebootForVerification { get; init; }
    public int RebootWaitSeconds { get; init; } = 35;
}

public sealed record PersistenceVerificationResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public bool ImmediateVerifyPassed { get; init; }
    public bool RebootRequested { get; init; }
    public bool RebootVerifyPassed { get; init; }
    public JsonNode? PreValue { get; init; }
    public JsonNode? PostValue { get; init; }
    public JsonNode? PostRebootValue { get; init; }
    public SemanticWriteStatus ImmediateStatus { get; init; } = SemanticWriteStatus.Unverified;
    public SemanticWriteStatus PersistenceStatus { get; init; } = SemanticWriteStatus.Unverified;
    public string? Notes { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record DeviceTruthProfile
{
    public Guid DeviceId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string? FirmwareVersion { get; init; }
    public string? HardwareModel { get; init; }
    public string? DeviceType { get; init; }
    public int EndpointsObserved { get; init; }
    public int EndpointsReadVerified { get; init; }
    public int EndpointsWriteVerified { get; init; }
    public int TopGroupFieldsSupported { get; init; }
    public int TopGroupFieldsUncertain { get; init; }
    public int TopGroupFieldsUnsupported { get; init; }
    public IReadOnlyCollection<string> ResponsiveEndpoints { get; init; } = [];
    public IReadOnlyCollection<string> AuthModesObserved { get; init; } = [];
    public IReadOnlyCollection<string> StreamDescriptorEndpoints { get; init; } = [];
    public IReadOnlyCollection<string> Notes { get; init; } = [];
}

public sealed record FirmwareTruthCluster
{
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public IReadOnlyCollection<Guid> DeviceIds { get; init; } = [];
    public IReadOnlyCollection<string> Ips { get; init; } = [];
    public int EndpointsReadVerified { get; init; }
    public int EndpointsWriteVerified { get; init; }
    public int SupportedTopGroupFields { get; init; }
    public int UnsupportedTopGroupFields { get; init; }
}

public sealed record TruthSweepReport
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyCollection<DeviceTruthProfile> Devices { get; init; } = [];
    public IReadOnlyCollection<FirmwareTruthCluster> Clusters { get; init; } = [];
}
