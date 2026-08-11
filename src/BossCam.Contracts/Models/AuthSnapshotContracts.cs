namespace BossCam.Contracts;

/// <summary>
/// Request to snapshot the ONVIF / RTSP / NetSDK auth state of one or more cameras.
/// An empty body (or null) snapshots every stored device that has an IP address.
/// <see cref="DeviceIds"/> selects stored devices; <see cref="IpAddresses"/> adds bare
/// (ephemeral, un-enrolled) targets. Both can be combined; targets are deduplicated by IP.
/// </summary>
public sealed record AuthSnapshotRequest
{
    public IReadOnlyCollection<Guid> DeviceIds { get; init; } = [];
    public IReadOnlyCollection<string> IpAddresses { get; init; } = [];
}

/// <summary>
/// One plane's probe outcome within a camera auth snapshot. A plane is considered
/// reachable when the peer answered (even with 401 — that is the auth verdict);
/// <see cref="HttpStatusCode"/> carries the exact status the peer returned.
/// </summary>
public sealed record AuthPlaneResult
{
    /// <summary>True when the plane answered at the transport/HTTP level (401 counts as reachable).</summary>
    public bool Reachable { get; init; }
    /// <summary>The exact HTTP status returned (NetSDK/ONVIF/web-gate probes), or the RTSP status line code.</summary>
    public int? HttpStatusCode { get; init; }
    public string? Detail { get; init; }
    public int? LatencyMs { get; init; }
}

/// <summary>
/// The auth-state snapshot for a single camera: one entry per plane probed
/// (NetSDK REST, web user-management gate, ONVIF device service, RTSP).
/// </summary>
public sealed record AuthSnapshotEntry
{
    public Guid? DeviceId { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? HardwareModel { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? LoginName { get; init; }
    /// <summary>True when the stored device record carries a login name (credential available).</summary>
    public bool HasStoredCredential { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Composite verdict: "semi-open", "web-open", "locked", or "offline".</summary>
    public string? Verdict { get; init; }

    /// <summary>NetSDK REST <c>deviceInfo</c> probed with <c>admin:</c> (blank password).</summary>
    public AuthPlaneResult NetSdkBlank { get; init; } = new();
    /// <summary>NetSDK REST <c>deviceInfo</c> probed with <c>admin:admin</c>.</summary>
    public AuthPlaneResult NetSdkAdminAdmin { get; init; } = new();
    /// <summary>Web user-management gate <c>/user/user_list.xml</c> (no auth).</summary>
    public AuthPlaneResult WebGate { get; init; } = new();
    /// <summary>"open", "closed" (check in falied), or null when the gate did not answer.</summary>
    public string? WebGateState { get; init; }
    /// <summary>ONVIF <c>GetUsers</c> probed with <c>admin:admin</c> on the device-service ports.</summary>
    public AuthPlaneResult Onvif { get; init; } = new();
    /// <summary>Usernames returned by the ONVIF GetUsers response (empty when gated/unreachable).</summary>
    public IReadOnlyCollection<string> OnvifUsers { get; init; } = [];
    /// <summary>RTSP TCP connect on :554 (proves something is listening).</summary>
    public AuthPlaneResult RtspTcp { get; init; } = new();
    /// <summary>RTSP OPTIONS handshake result — true only when an RTSP server answered (playable).</summary>
    public AuthPlaneResult RtspPlayable { get; init; } = new();
    /// <summary>RTSP DESCRIBE status (e.g. 200, 401) and the auth scheme the plane challenges with ("Digest"/"Basic"/null).</summary>
    public AuthPlaneResult RtspDescribe { get; init; } = new();
    public string? RtspChallengeScheme { get; init; }
}

/// <summary>
/// Structured report from <c>POST /api/devices/auth-snapshot</c> — the ONVIF / RTSP / NetSDK
/// auth-state matrix for every requested camera, so fleet auth state is one click to re-check.
/// </summary>
public sealed record AuthSnapshotResult
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyCollection<AuthSnapshotEntry> Devices { get; init; } = [];
    public string? Message { get; init; }
}
