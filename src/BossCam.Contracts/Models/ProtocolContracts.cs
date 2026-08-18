using System.Text.Json.Nodes;

namespace BossCam.Contracts;

public sealed record WritePlan
{
    public string GroupName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "PUT";
    public JsonObject? Payload { get; init; }
    public string? AdapterName { get; init; }
    public bool SnapshotBeforeWrite { get; init; } = true;
    public bool RequireWriteVerification { get; init; } = true;
    public bool AllowRollback { get; init; }
    public string? ContractKey { get; init; }
    public IReadOnlyCollection<string> SensitivePaths { get; init; } = [];
}

public sealed record WriteResult
{
    public bool Success { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string? Message { get; init; }
    public int? StatusCode { get; init; }
    public JsonNode? Response { get; init; }
    public SettingsSnapshot? SnapshotBeforeWrite { get; init; }
    public bool PreReadVerified { get; init; }
    public bool PostReadVerified { get; init; }
    public bool RollbackAttempted { get; init; }
    public bool RollbackSucceeded { get; init; }
    public JsonNode? PreWriteValue { get; init; }
    public JsonNode? PostWriteValue { get; init; }
    public SemanticWriteStatus SemanticStatus { get; init; } = SemanticWriteStatus.Unverified;
    public string? ContractKey { get; init; }
    public IReadOnlyCollection<string> ContractViolations { get; init; } = [];
}

public sealed record WriteAuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string? RequestContent { get; init; }
    public string? ResponseContent { get; init; }
    public bool Success { get; init; }
    public SemanticWriteStatus SemanticStatus { get; init; } = SemanticWriteStatus.Unverified;
    public string? BlockReason { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MaintenanceResult
{
    public bool Success { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public MaintenanceOperation Operation { get; init; }
    public string? Message { get; init; }
    public JsonNode? Response { get; init; }
}

/// <summary>
/// One device's clock-verification outcome. Produced by <c>MaintenanceOperation.ClockVerify</c>
/// on the 5523-W NetSDK surface: the OSD rtc epoch (bare unix-seconds int) and timeZone (bare
/// string) are probed, a TimeSync runs (bare-scalar PUTs), then both are re-read and the OSD
/// epoch is compared against the host epoch within the configured tolerance.
/// </summary>
public sealed record ClockVerificationResult
{
    public Guid DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public bool Success { get; init; }
    public string AdapterName { get; init; } = string.Empty;
    public string? Message { get; init; }
    /// <summary>OSD rtc epoch read BEFORE the sync (bare unix-seconds int).</summary>
    public long? RtcBefore { get; init; }
    /// <summary>OSD timeZone string read BEFORE the sync (e.g. "GMT+08:00").</summary>
    public string? TimeZoneBefore { get; init; }
    /// <summary>Host unix-seconds epoch at sync time (what we PUT to the camera).</summary>
    public long HostEpoch { get; init; }
    /// <summary>OSD rtc epoch re-read AFTER the sync.</summary>
    public long? RtcAfter { get; init; }
    /// <summary>OSD timeZone string re-read AFTER the sync.</summary>
    public string? TimeZoneAfter { get; init; }
    /// <summary>Absolute OSD-vs-host drift in seconds after the sync (RtcAfter − HostEpoch).</summary>
    public long? DriftSeconds { get; init; }
    /// <summary>Configured tolerance in seconds; verification succeeds when |DriftSeconds| ≤ tolerance.</summary>
    public int ToleranceSeconds { get; init; } = 30;
    /// <summary>
    /// Diagnostic (never a hard failure): whether the camera's timeZone string matched the host
    /// offset after the sync. A mismatch means the camera normalized the zone differently from
    /// the host's GMT token (e.g. "GMT+8" vs "GMT+08:00") — surfaced so the operator can decide.
    /// </summary>
    public bool TimeZoneMatchesHost { get; init; }
}

/// <summary>
/// Fleet-wide clock verification: per-device results for every registered 5523-W, with a
/// summary of how many were reachable and how many came out in-sync.
/// </summary>
public sealed record ClockFleetReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public int DevicesChecked { get; init; }
    public int DevicesVerified { get; init; }
    public int DevicesFailed { get; init; }
    public IReadOnlyCollection<ClockVerificationResult> Results { get; init; } = [];
}

public sealed record VideoSourceDescriptor
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public TransportKind Kind { get; init; }
    public string Url { get; init; } = string.Empty;
    public int Rank { get; init; }
    public string? DisplayName { get; init; }
    public bool RequiresTunnel { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record PlaybackSourceDescriptor
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public Guid DeviceId { get; init; }
    public TransportKind Kind { get; init; }
    public string Url { get; init; } = string.Empty;
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public enum NvrStreamMode
{
    Live,
    Playback
}

public enum NvrStreamStatus
{
    Empty,
    Resolving,
    Starting,
    Running,
    Stopped,
    Failed
}

public sealed record NvrTileStream
{
    public int TileId { get; init; }
    public Guid? DeviceId { get; init; }
    public NvrStreamMode Mode { get; init; } = NvrStreamMode.Live;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset? BeginTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public NvrStreamStatus Status { get; init; } = NvrStreamStatus.Empty;
    public string? Message { get; init; }
}

public sealed record NvrTileLayoutSlot
{
    public int TileId { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
}

public static class NvrLayoutCatalog
{
    public static IReadOnlyList<int> SupportedLayouts { get; } = [1, 3, 4, 5, 6, 9];

    public static IReadOnlyList<NvrTileLayoutSlot> GetSlots(int layout)
        => layout switch
        {
            1 => [Slot(0, 0, 0, 1, 1)],
            3 => [Slot(0, 0, 0, 2, 2), Slot(1, 0, 2), Slot(2, 1, 2)],
            4 => [Slot(0, 0, 0), Slot(1, 0, 1), Slot(2, 1, 0), Slot(3, 1, 1)],
            5 => [Slot(0, 0, 0, 2, 2), Slot(1, 0, 2), Slot(2, 1, 2), Slot(3, 2, 0), Slot(4, 2, 1)],
            6 => [Slot(0, 0, 0), Slot(1, 0, 1), Slot(2, 0, 2), Slot(3, 1, 0), Slot(4, 1, 1), Slot(5, 1, 2)],
            9 => [Slot(0, 0, 0), Slot(1, 0, 1), Slot(2, 0, 2), Slot(3, 1, 0), Slot(4, 1, 1), Slot(5, 1, 2), Slot(6, 2, 0), Slot(7, 2, 1), Slot(8, 2, 2)],
            _ => throw new ArgumentOutOfRangeException(nameof(layout), $"Unsupported NVR layout '{layout}'.")
        };

    public static (int Rows, int Columns) GetGridSize(int layout)
        => layout switch
        {
            1 => (1, 1),
            3 => (2, 3),
            4 => (2, 2),
            5 or 9 => (3, 3),
            6 => (2, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), $"Unsupported NVR layout '{layout}'.")
        };

    private static NvrTileLayoutSlot Slot(int tileId, int row, int column, int rowSpan = 1, int columnSpan = 1)
        => new()
        {
            TileId = tileId,
            Row = row,
            Column = column,
            RowSpan = rowSpan,
            ColumnSpan = columnSpan
        };
}

public sealed record PreviewSession
{
    public Guid DeviceId { get; init; }
    public VideoSourceDescriptor Source { get; init; } = new();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RecordingProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string? SourceId { get; init; }
    public bool Enabled { get; init; }
    public bool AutoStart { get; init; } = true;
    public int SegmentSeconds { get; init; } = 300;
    public int RetentionDays { get; init; } = 14;
    public long MaxStorageBytes { get; init; } = 0;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}



public sealed record RecordingStartRequest
{
    public Guid DeviceId { get; init; }
    public Guid? ProfileId { get; init; }
    public string? SourceUrl { get; init; }
    /// <summary>Optional override for continuous recording output folder.</summary>
    public string? OutputDirectory { get; init; }
}

/// <summary>Operator-configurable media folders (Linux paths).</summary>
public sealed record MediaStoragePaths
{
    public string ContinuousRecordings { get; init; } = string.Empty;
    public string Highlights { get; init; } = string.Empty;
    public string Snapshots { get; init; } = string.Empty;
}

public sealed record RecordingJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public Guid ProfileId { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string SegmentPattern { get; init; } = string.Empty;
    public int SegmentSeconds { get; init; } = 300;
    public bool IsRunning { get; init; }
    public string? LastError { get; init; }
    public int? ProcessId { get; init; }
    public string Mode { get; init; } = "direct";
    public string? SourceRole { get; init; }
    public string? DegradedReason { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StoppedAt { get; init; }
}

public sealed record RecordingSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceId { get; init; }
    public Guid ProfileId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public double DurationSec { get; init; }
    public string StreamRole { get; init; } = "main";
    public string Container { get; init; } = "ts";
    public bool HasAudio { get; init; }
    public Guid? JobId { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ClipExportRequest
{
    public Guid DeviceId { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public string OutputPath { get; init; } = string.Empty;
}

public sealed record ClipExportResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public double DurationSec { get; init; }
    public bool ReEncoded { get; init; }
    public string? Message { get; init; }
}

public sealed record RecordingHousekeepingResult
{
    public int ProfilesChecked { get; init; }
    public int FilesDeleted { get; init; }
    public long BytesDeleted { get; init; }
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record NvrPlaybackRequest
{
    public int SessionId { get; init; } = 0;
    public int ChannelId { get; init; } = 1;
    public DateTimeOffset BeginTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public string Type { get; init; } = "all";
    public string? Cursor { get; init; }
    public string? FileName { get; init; }
    public string? SavePath { get; init; }
    public int? HandleId { get; init; }
}

public sealed record NvrPlaybackCallResult
{
    public bool Success { get; init; }
    public string Operation { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public int? StatusCode { get; init; }
    public string? Message { get; init; }
    public JsonNode? Response { get; init; }
    public Dictionary<string, string> Query { get; init; } = [];
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record NativeFallbackRequirement
{
    public string FieldKey { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? LibraryHint { get; init; }
}

public sealed record NativeFallbackAssessment
{
    public Guid DeviceId { get; init; }
    public string? FirmwareFingerprint { get; init; }
    public IReadOnlyCollection<NativeFallbackRequirement> RequiredFields { get; init; } = [];
    public IReadOnlyCollection<string> AvailableLibraries { get; init; } = [];
    public DateTimeOffset AssessedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AlertEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? DeviceId { get; init; }
    public AlertSeverity Severity { get; init; } = AlertSeverity.Info;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record RemoteAuthorization
{
    public string? Verify { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed record RemoteCommandEnvelope
{
    public string Version { get; init; } = "1.0.0";
    public string Method { get; init; } = "get";
    public JsonObject CapabilitySet { get; init; } = [];
    public JsonObject? IPCam { get; init; }
    public JsonObject? Xvr { get; init; }
    public string? Dev { get; init; }
    public string? Api { get; init; }
    public RemoteAuthorization Authorization { get; init; } = new();
}

public sealed record RemoteCommandResult
{
    public bool Success { get; init; }
    public string? Endpoint { get; init; }
    public string? Message { get; init; }
    public JsonNode? Response { get; init; }
}

public sealed record ProtocolEndpoint
{
    public string Path { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public List<string> Methods { get; init; } = [];
    public string? Description { get; init; }
    public string? RequestSchema { get; init; }
    public string? ResponseSchema { get; init; }
    public string Safety { get; init; } = "read-only";
    public List<string> Notes { get; init; } = [];
}

public sealed record ProtocolManifest
{
    public string ManifestId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? Family { get; init; }
    public string? AuthMode { get; init; }
    public List<ProtocolEndpoint> Endpoints { get; init; } = [];
}

public sealed record FirmwareArtifact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string? Family { get; init; }
    public List<string> Signatures { get; init; } = [];
    public List<string> HttpPaths { get; init; } = [];
    public List<string> ModelStrings { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}
