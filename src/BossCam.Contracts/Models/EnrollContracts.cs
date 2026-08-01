namespace BossCam.Contracts;

/// <summary>
/// One-click enroll request. The pipeline probes the camera (NetSDK REST deviceInfo with the
/// recorded-port → :80 fallback), merges the identity MAC-first, ranks/probes playable video
/// sources, persists last-good ports/URLs, and — when <see cref="StartContinuousRecord"/> — starts
/// a continuous recording job. Credentials may be supplied inline or resolved from a named
/// credential profile / brand env var (BOSSCAM_CRED_&lt;PROFILE&gt;_PASSWORD, BOSSCAM_LOREX_PASSWORD,
/// BOSSCAM_WVC_PASSWORD, BOSSCAM_PASSWORD).
/// </summary>
public sealed record EnrollDeviceRequest
{
    public string IpAddress { get; init; } = string.Empty;
    /// <summary>Recorded/assumed HTTP port. Null or &lt;= 0 defaults to 80; the pipeline also tries the :80 fallback.</summary>
    public int? Port { get; init; }
    public string? LoginName { get; init; }
    public string? Password { get; init; }
    public string? DisplayName { get; init; }
    public string? HardwareModel { get; init; }
    /// <summary>Named credential profile used to resolve a password from the environment when <see cref="Password"/> is empty.</summary>
    public string? CredentialProfile { get; init; }
    /// <summary>When true, start a continuous recording job on the best playable source (snapshot pipeline if no RTSP).</summary>
    public bool StartContinuousRecord { get; init; }
    /// <summary>Optional link hint (Wifi cameras get looser watchdog/backoff handling).</summary>
    public LinkHint? LinkHint { get; init; }
}

/// <summary>One pipeline step (identity probe, source ranking, continuous record) with success/failure detail.</summary>
public sealed record EnrollStepResult
{
    public string Step { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Message { get; init; }
}

/// <summary>Structured enroll outcome; the operator console renders these fields directly.</summary>
public sealed record EnrollDeviceResult
{
    public Guid DeviceId { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public bool Enrolled { get; init; }
    public string? DisplayName { get; init; }
    public string? HardwareModel { get; init; }
    public int HttpControlPort { get; init; }
    public string? CredentialProfile { get; init; }
    public IReadOnlyCollection<EnrollStepResult> Steps { get; init; } = [];
    /// <summary>Best playable source chosen for live/record (credentials redacted). Null when only the snapshot pipeline is available.</summary>
    public string? ChosenSourceUrl { get; init; }
    /// <summary>"main", "sub", or "snapshot".</summary>
    public string? SourceRole { get; init; }
    /// <summary>Set when no playable RTSP source was found and recording must degrade to snapshots.</summary>
    public string? DegradedReason { get; init; }
    /// <summary>Recording job id when <see cref="EnrollDeviceRequest.StartContinuousRecord"/> succeeded.</summary>
    public string? ContinuousJobId { get; init; }
}
