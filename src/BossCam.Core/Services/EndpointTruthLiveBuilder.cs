using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BossCam.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Core;

public sealed record EndpointTruthRefreshInput(DeviceIdentity Device, CameraEndpointTruthProfile? Existing);

public interface IEndpointTruthLiveProbeClient
{
    Task<LiveOnvifProbeResult> ProbeAsync(DeviceIdentity device, IReadOnlyCollection<CameraEndpointObservation> candidates, CancellationToken cancellationToken);
}

public interface IFfprobePlaybackProbe
{
    Task<RtspPlaybackProbeMetadata> ProbeAsync(string profileToken, string uri, string? username, string? password, CancellationToken cancellationToken);
}

public sealed record LiveOnvifProbeResult
{
    public IReadOnlyCollection<CameraEndpointObservation> Endpoints { get; init; } = [];
    public IReadOnlyCollection<OnvifDeclaredStreamMetadata> DeclaredStreams { get; init; } = [];
    public IReadOnlyDictionary<string, string> StreamUrisByProfile { get; init; } = new Dictionary<string, string>();
    public PtzServiceState Ptz { get; init; } = new();
    public IReadOnlyCollection<string> DriftNotes { get; init; } = [];
}

public sealed class EndpointTruthLiveBuilder(IFfprobePlaybackProbe ffprobe, ILogger<EndpointTruthLiveBuilder> logger)
{
    public async Task<CameraEndpointTruthProfile> BuildAsync(EndpointTruthRefreshInput input, IEndpointTruthLiveProbeClient onvif, CancellationToken cancellationToken)
    {
        var candidates = CameraEndpointTruthService.BuildRankedCandidates(input.Device).ToList();
        var observed = await onvif.ProbeAsync(input.Device, candidates, cancellationToken);
        var streams = new List<RtspPlaybackProbeMetadata>();
        foreach (var stream in observed.StreamUrisByProfile)
        {
            var credentialed = BuildCredentialedRtspUri(stream.Value, input.Device.LoginName, input.Device.Password);
            var probed = await ffprobe.ProbeAsync(stream.Key, credentialed, input.Device.LoginName, input.Device.Password, cancellationToken);
            streams.Add(probed);
            logger.LogInformation("ProjectionUpdated profile={ProfileToken} uri={Uri} probedCodec={Codec}", probed.ProfileToken, probed.Uri, probed.Codec ?? string.Empty);
        }

        return new CameraEndpointTruthProfile
        {
            DeviceId = input.Device.Id,
            IpAddress = input.Device.IpAddress,
            HardwareModel = input.Device.HardwareModel,
            FirmwareVersion = input.Device.FirmwareVersion,
            CredentialState = streams.Any(s => s.CredentialState == CameraCredentialState.Verified) ? CameraCredentialState.Verified
                : string.IsNullOrWhiteSpace(input.Device.LoginName) ? CameraCredentialState.PlaybackLockedPendingCredentials : CameraCredentialState.Supplied,
            Endpoints = observed.Endpoints.ToList(),
            OnvifDeclaredStreams = observed.DeclaredStreams.ToList(),
            RtspPlaybackStreams = streams,
            Ptz = observed.Ptz,
            DriftNotes = observed.DriftNotes.ToList()
        };
    }

    private static string BuildCredentialedRtspUri(string uri, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var builder = new UriBuilder(parsed) { UserName = username, Password = password ?? string.Empty };
        return builder.Uri.ToString();
    }
}

public sealed class HttpOnvifLiveProbeClient(IOptions<BossCamRuntimeOptions> options, ILogger<HttpOnvifLiveProbeClient> logger) : IEndpointTruthLiveProbeClient
{
    public async Task<LiveOnvifProbeResult> ProbeAsync(DeviceIdentity device, IReadOnlyCollection<CameraEndpointObservation> candidates, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.HttpTimeoutSeconds)) };
        var endpoints = new List<CameraEndpointObservation>();
        var declared = new List<OnvifDeclaredStreamMetadata>();
        var uris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PtzServiceState ptz = new();
        foreach (var candidate in candidates)
        {
            try
            {
                var response = await http.PostAsync(candidate.Endpoint, new StringContent(Envelope("GetSystemDateAndTime"), Encoding.UTF8, "application/soap+xml"), cancellationToken);
                var state = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? CameraEndpointVerificationState.Unauthorized
                    : response.StatusCode == HttpStatusCode.NotFound ? CameraEndpointVerificationState.Unsupported
                    : response.IsSuccessStatusCode ? CameraEndpointVerificationState.Verified
                    : CameraEndpointVerificationState.Failed;
                endpoints.Add(candidate with { State = state, TruthStrength = state == CameraEndpointVerificationState.Verified ? TruthStrength.LiveVerified : TruthStrength.FailedProbe, Evidence = $"HTTP {(int)response.StatusCode}" });
                logger.LogInformation("EndpointCandidateProbed endpoint={Endpoint} state={State}", candidate.Endpoint, state);
            }
            catch (TaskCanceledException)
            {
                endpoints.Add(candidate with { State = CameraEndpointVerificationState.Timeout, TruthStrength = TruthStrength.FailedProbe, Evidence = "timeout" });
                logger.LogWarning("EndpointTimeout endpoint={Endpoint}", candidate.Endpoint);
            }
            catch (Exception ex)
            {
                endpoints.Add(candidate with { State = CameraEndpointVerificationState.Failed, TruthStrength = TruthStrength.FailedProbe, Evidence = ex.Message });
            }
        }

        var media = endpoints.FirstOrDefault(e => e.Capability.Equals("Media", StringComparison.OrdinalIgnoreCase) && e.State == CameraEndpointVerificationState.Verified);
        if (media is not null)
        {
            var profilesXml = await PostSoapAsync(http, media.Endpoint, "GetProfiles", cancellationToken);
            foreach (var profile in ParseProfiles(profilesXml))
            {
                declared.Add(profile);
                var uriXml = await PostSoapAsync(http, media.Endpoint, "GetStreamUri", cancellationToken);
                var uri = XDocument.Parse(uriXml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    uris[profile.ProfileToken] = uri;
                }
            }
        }

        var ptzEndpoint = endpoints.FirstOrDefault(e => e.Capability.Equals("PTZ", StringComparison.OrdinalIgnoreCase) && e.State == CameraEndpointVerificationState.Verified);
        if (ptzEndpoint is not null)
        {
            ptz = new PtzServiceState { ServiceState = CameraEndpointVerificationState.Verified, ServiceEndpoint = ptzEndpoint.Endpoint, GetStatusVerified = true, MechanicalCapability = MechanicalPtzCapability.NotInstalledOrUnknown, MovementControlsEnabled = false, OperatorMessage = "PTZ service detected; mechanical actuator not enabled." };
        }

        return new LiveOnvifProbeResult { Endpoints = endpoints, DeclaredStreams = declared, StreamUrisByProfile = uris, Ptz = ptz };
    }

    private static async Task<string> PostSoapAsync(HttpClient http, string endpoint, string action, CancellationToken cancellationToken)
        => await (await http.PostAsync(endpoint, new StringContent(Envelope(action), Encoding.UTF8, "application/soap+xml"), cancellationToken)).Content.ReadAsStringAsync(cancellationToken);

    private static string Envelope(string action)
        => $"""<?xml version="1.0"?><s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><{action}/></s:Body></s:Envelope>""";

    private static IEnumerable<OnvifDeclaredStreamMetadata> ParseProfiles(string xml)
    {
        var doc = XDocument.Parse(xml);
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            yield return new OnvifDeclaredStreamMetadata
            {
                ProfileToken = p.Attribute("token")?.Value ?? p.Descendants().FirstOrDefault(e => e.Name.LocalName == "token")?.Value ?? Guid.NewGuid().ToString("N"),
                Encoding = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value,
                Width = ReadInt(p, "Width"),
                Height = ReadInt(p, "Height"),
                BitrateKbps = ReadInt(p, "BitrateLimit"),
                Gop = ReadInt(p, "GovLength")
            };
        }
    }

    private static int? ReadInt(XElement root, string name)
        => int.TryParse(root.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value, out var value) ? value : null;
}

public sealed class FfprobePlaybackProbe(IOptions<BossCamRuntimeOptions> options) : IFfprobePlaybackProbe
{
    public async Task<RtspPlaybackProbeMetadata> ProbeAsync(string profileToken, string uri, string? username, string? password, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(options.Value.FfprobePath) ? "ffprobe" : options.Value.FfprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{uri}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return Candidate(profileToken, uri, "ffprobe unavailable");
            }

            var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                return Candidate(profileToken, uri, "ffprobe failed");
            }

            using var doc = JsonDocument.Parse(json);
            var stream = doc.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
            return new RtspPlaybackProbeMetadata
            {
                ProfileToken = profileToken,
                Uri = uri,
                State = CameraEndpointVerificationState.Verified,
                CredentialState = CameraCredentialState.Verified,
                VerifiedUsername = username,
                Codec = stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null,
                Width = stream.TryGetProperty("width", out var width) ? width.GetInt32() : null,
                Height = stream.TryGetProperty("height", out var height) ? height.GetInt32() : null,
                Fps = stream.TryGetProperty("avg_frame_rate", out var fps) ? fps.GetString() : null,
                Evidence = "ffprobe playback metadata"
            };
        }
        catch
        {
            return Candidate(profileToken, uri, "ffprobe unavailable or timed out");
        }
    }

    private static RtspPlaybackProbeMetadata Candidate(string profileToken, string uri, string evidence)
        => new() { ProfileToken = profileToken, Uri = uri, State = CameraEndpointVerificationState.UnverifiedCandidate, CredentialState = CameraCredentialState.PlaybackLockedPendingCredentials, Evidence = evidence };
}
