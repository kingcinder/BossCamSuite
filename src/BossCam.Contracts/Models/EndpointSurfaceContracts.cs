using System.Text.Json.Nodes;

namespace BossCam.Contracts;

public sealed record EndpointSurfaceItem
{
    public Guid DeviceId { get; init; }
    public string Family { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public string Surface { get; init; } = string.Empty;
    public string WrapperObjectName { get; init; } = string.Empty;
    public string AuthMode { get; init; } = string.Empty;
    public bool ExpertOnly { get; init; }
    public bool Writable { get; init; }
    public bool RequiresConfirmation { get; init; }
    public string DisruptionClass { get; init; } = string.Empty;
    public string TruthState { get; init; } = string.Empty;
    public JsonNode? CurrentPayload { get; init; }
    public JsonObject? SuggestedPayload { get; init; }
    public bool CurrentPayloadAvailable { get; init; }
    public bool SupportsExecution { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record EndpointSurfaceReport
{
    public Guid DeviceId { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public IReadOnlyCollection<EndpointSurfaceItem> Endpoints { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
