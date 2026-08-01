using System.Text.Json.Serialization;

namespace BossCam.Contracts;

/// <summary>
/// Request for the ONVIF PTZ capability capture probe. Either <see cref="DeviceId"/> (uses stored
/// credentials/metadata/XAddr) or <see cref="IpAddress"/> + optional credentials (for a not-yet
/// enrolled camera) must be supplied.
/// </summary>
public sealed record OnvifPtzCaptureRequest
{
    public string? DeviceId { get; init; }
    public string? IpAddress { get; init; }
    public string? LoginName { get; init; }
    public string? Password { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OnvifPtzVerdict
{
    /// <summary>No device resolved (missing DeviceId/IpAddress).</summary>
    NoDevice,
    /// <summary>No ONVIF device service answered GetCapabilities on any candidate (device down or not ONVIF).</summary>
    DeviceUnreachable,
    /// <summary>Device service answered 401/403 on every candidate — check credentials.</summary>
    AuthFailure,
    /// <summary>GetCapabilities answered but advertises no PTZ service — ONVIF PTZ is not offered (proprietary CGI should be investigated separately).</summary>
    NoPtzService,
    /// <summary>PTZ service XAddr present but GetConfigurations returned zero configurations — PTZ service stub, likely non-functional.</summary>
    PtzAdvertisedNoConfigs,
    /// <summary>At least one PTZConfiguration with a token — ONVIF PTZ is real and ContinuousMove/Stop/GotoPreset are implementable.</summary>
    PtzReady
}

/// <summary>
/// Result of an ONVIF PTZ capability capture. Carries the raw SOAP fixtures (so the operator can
/// persist them under <c>src/BossCam.Service/fixtures/&lt;brand&gt;/__ONVIF/</c> matching the
/// 5523-W pattern) plus the structured verdict that drives the P4 milestone decision.
/// </summary>
public sealed record OnvifPtzCaptureResult
{
    /// <summary>True when the capture completed with a definitive verdict (a GetCapabilities response was received).</summary>
    public bool Success { get; init; }
    public OnvifPtzVerdict Verdict { get; init; }
    /// <summary>The device-service URL that answered GetCapabilities.</summary>
    public string? DeviceServiceUrl { get; init; }
    /// <summary>The PTZ service XAddr advertised in Capabilities (null when no PTZ service).</summary>
    public string? PtzServiceUrl { get; init; }
    public int PtzConfigurationCount { get; init; }
    public IReadOnlyCollection<string> PtzConfigurationTokens { get; init; } = [];
    public string? Message { get; init; }
    /// <summary>Raw GetCapabilities SOAP response (redaction: never contains credentials).</summary>
    public string? CapabilitiesXml { get; init; }
    /// <summary>Raw GetConfigurations SOAP response when a PTZ service was advertised.</summary>
    public string? ConfigurationsXml { get; init; }
    public int SavedFixtureCount { get; init; }
}
