using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BossCam.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DisruptionClass
{
    Safe,
    Transient,
    ServiceImpacting,
    NetworkChanging,
    Reboot,
    FactoryReset,
    FirmwareUpgrade,
    Unknown
}

public sealed record EndpointValidationResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public string? AuthMode { get; init; }
    public string? FirmwareFingerprint { get; init; }
    public bool ReadVerified { get; init; }
    public bool WriteVerified { get; init; }
    public bool PersistsAfterReboot { get; init; }
    public bool RollbackSupported { get; init; }
    public DisruptionClass DisruptionClass { get; init; } = DisruptionClass.Unknown;
    public string? RequestTemplateHash { get; init; }
    public string Status { get; init; } = "unknown";
    public string? Notes { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record EndpointTranscript
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public string? AuthMode { get; init; }
    public string? FirmwareFingerprint { get; init; }
    public string? RequestBody { get; init; }
    public string? ResponseBody { get; init; }
    public int? StatusCode { get; init; }
    public JsonNode? ParsedResponse { get; init; }
    public JsonNode? BeforeValue { get; init; }
    public JsonNode? AfterValue { get; init; }
    public bool Success { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CapabilityProbeResult
{
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string? FirmwareFingerprint { get; init; }
    public IReadOnlyCollection<EndpointValidationResult> Endpoints { get; init; } = [];
    public IReadOnlyCollection<EndpointTranscript> Transcripts { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ValidationRunOptions
{
    public bool AttemptWrites { get; init; } = true;
    public bool IncludeUnsafeWrites { get; init; }
    public bool IncludePersistenceChecks { get; init; }
    public bool IncludeRollbackChecks { get; init; } = true;
    public bool CaptureTranscripts { get; init; } = true;
    public string? AdapterName { get; init; }
    public IReadOnlyCollection<DisruptionClass> AllowedDisruptionClasses { get; init; } = [];
}
