using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class CameraEndpointTruthService(IApplicationStore store, EndpointTruthLiveBuilder liveBuilder, IEndpointTruthLiveProbeClient liveProbeClient, ILogger<CameraEndpointTruthService> logger)
{
    public async Task<CameraEndpointTruthSummary?> GetSummaryAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var profile = await store.GetCameraEndpointTruthProfileAsync(deviceId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var all = await store.GetCameraEndpointTruthProfilesAsync(cancellationToken);
        var siblings = all
            .Where(candidate => candidate.DeviceId != deviceId
                && Same(candidate.HardwareModel, profile.HardwareModel)
                && Same(candidate.FirmwareVersion, profile.FirmwareVersion))
            .ToList();
        var notes = profile.DriftNotes.Concat(Compare(profile, siblings)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new CameraEndpointTruthSummary
        {
            Profile = profile,
            SameModelProfiles = siblings,
            EndpointDriftDetected = notes.Count > 0,
            DriftNotes = notes
        };
    }

    public async Task<CameraEndpointTruthProfile> SaveObservedProfileAsync(CameraEndpointTruthProfile profile, CancellationToken cancellationToken)
    {
        var existing = await store.GetCameraEndpointTruthProfileAsync(profile.DeviceId, cancellationToken);
        var guarded = EndpointTruthIntegrityGuard.Merge(existing, profile);
        await store.SaveCameraEndpointTruthProfileAsync(guarded, cancellationToken);
        logger.LogInformation("TruthPersisted device={DeviceId} endpoints={EndpointCount} streams={StreamCount}", guarded.DeviceId, guarded.Endpoints.Count, guarded.RtspPlaybackStreams.Count);
        return guarded;
    }

    public async Task<CameraEndpointTruthProfile?> RefreshAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await store.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        logger.LogInformation("ObserveStarted device={DeviceId} ip={Ip}", device.Id, device.IpAddress);
        var existing = await store.GetCameraEndpointTruthProfileAsync(device.Id, cancellationToken);
        var profile = await liveBuilder.BuildAsync(new EndpointTruthRefreshInput(device, existing), liveProbeClient, cancellationToken);
        foreach (var endpoint in profile.Endpoints)
        {
            logger.LogInformation("EndpointCandidateProbed device={DeviceId} endpoint={Endpoint} state={State} source={Source}", device.Id, endpoint.Endpoint, endpoint.State, endpoint.CandidateSource);
        }

        return await SaveObservedProfileAsync(profile, cancellationToken);
    }

    public static CameraEndpointTruthProfile CreateVerified5523wSample(Guid deviceId)
        => new()
        {
            DeviceId = deviceId,
            IpAddress = "10.0.0.29",
            HardwareModel = "5523-W",
            CredentialState = CameraCredentialState.Verified,
            Endpoints =
            [
                Verified("Device", "http://10.0.0.29:8888/onvif/device_service", "candidate verified for this camera only; verify per camera"),
                Verified("Media", "http://10.0.0.29:8888/onvif/media_service", "GetProfiles and GetStreamUri returned 200 unauthenticated"),
                Verified("PTZ", "http://10.0.0.29:8888/onvif/ptz_service", "GetStatus returned 200 OK"),
                Candidate("Imaging", "http://10.0.0.29:8888/onvif/imaging_service"),
                Candidate("Events", "http://10.0.0.29:8888/onvif/event_service"),
                Candidate("Events", "http://10.0.0.29:8888/onvif/events_service"),
                Candidate("Snapshot", "http://10.0.0.29/snapshot.jpg"),
                Candidate("Snapshot", "http://10.0.0.29/tmpfs/snap.jpg"),
                Candidate("VendorPrivateHttp", "http://10.0.0.29/web/cgi-bin/hi3510/param.cgi")
            ],
            OnvifDeclaredStreams =
            [
                new() { ProfileToken = "PROFILE_000", VideoSourceToken = "V_SRC_000", EncoderToken = "V_ENC_000", Encoding = "H264", Width = 2560, Height = 1920, Fps = 15, BitrateKbps = 5120, Gop = 30, H264Profile = "Main" },
                new() { ProfileToken = "PROFILE_001", VideoSourceToken = "V_SRC_000", EncoderToken = "V_ENC_001", Encoding = "JPEG", Width = 704, Height = 480, Fps = 15, BitrateKbps = 384 }
            ],
            RtspPlaybackStreams =
            [
                new() { ProfileToken = "PROFILE_000", Uri = "rtsp://admin:@10.0.0.29:554/ch0_0.264", State = CameraEndpointVerificationState.Verified, CredentialState = CameraCredentialState.Verified, VerifiedUsername = "admin", Codec = "h264", Width = 2560, Height = 1920, Fps = "13/1", Evidence = "ffprobe playback metadata" },
                new() { ProfileToken = "PROFILE_001", Uri = "rtsp://admin:@10.0.0.29:554/ch0_1.264", State = CameraEndpointVerificationState.Verified, CredentialState = CameraCredentialState.Verified, VerifiedUsername = "admin", Codec = "hevc", Width = 704, Height = 480, Fps = "15/1", Evidence = "ffprobe playback metadata; ONVIF declared JPEG is wrong" },
                new() { ProfileToken = "ADMIN_NEGATIVE", Uri = "rtsp://ADMIN:@10.0.0.29:554/ch0_0.264", State = CameraEndpointVerificationState.Unauthorized, CredentialState = CameraCredentialState.Rejected, VerifiedUsername = "ADMIN", Evidence = "uppercase ADMIN failed" }
            ],
            Ptz = new PtzServiceState
            {
                ServiceState = CameraEndpointVerificationState.Verified,
                ServiceEndpoint = "http://10.0.0.29:8888/onvif/ptz_service",
                GetStatusVerified = true,
                MechanicalCapability = MechanicalPtzCapability.NotInstalledOrUnknown,
                MovementControlsEnabled = false,
                OperatorMessage = "PTZ service detected; board/firmware appears PTZ-capable; actuator not installed/unknown; advanced hardware variant only."
            },
            DriftNotes = ["Same model/firmware is a hint only; this endpoint map is per camera."]
        };

    private static CameraEndpointObservation Verified(string capability, string endpoint, string evidence)
        => new() { Capability = capability, Endpoint = endpoint, State = CameraEndpointVerificationState.Verified, Source = "live", CandidateSource = EndpointCandidateSource.LiveProbe, TruthStrength = TruthStrength.LiveVerified, Evidence = evidence };

    private static CameraEndpointObservation Candidate(string capability, string endpoint)
        => new() { Capability = capability, Endpoint = endpoint, State = CameraEndpointVerificationState.UnverifiedCandidate, Source = "template seed", CandidateSource = EndpointCandidateSource.ModelTemplate, TruthStrength = TruthStrength.Candidate, Evidence = "unverified candidate; live probe required" };

    public static IEnumerable<CameraEndpointObservation> BuildRankedCandidates(DeviceIdentity device)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            yield break;
        }

        var onvifPort = device.Metadata.TryGetValue("onvifPort", out var value) && int.TryParse(value, out var parsed) ? parsed : 8888;
        foreach (var item in new[]
        {
            ("Device", $"http://{device.IpAddress}:{onvifPort}/onvif/device_service", EndpointCandidateSource.ModelTemplate),
            ("Media", $"http://{device.IpAddress}:{onvifPort}/onvif/media_service", EndpointCandidateSource.ModelTemplate),
            ("PTZ", $"http://{device.IpAddress}:{onvifPort}/onvif/ptz_service", EndpointCandidateSource.ModelTemplate),
            ("Imaging", $"http://{device.IpAddress}:{onvifPort}/onvif/imaging_service", EndpointCandidateSource.ModelTemplate),
            ("Events", $"http://{device.IpAddress}:{onvifPort}/onvif/event_service", EndpointCandidateSource.ModelTemplate),
            ("Events", $"http://{device.IpAddress}:{onvifPort}/onvif/events_service", EndpointCandidateSource.ModelTemplate),
            ("DeviceIO", $"http://{device.IpAddress}:{onvifPort}/onvif/deviceio_service", EndpointCandidateSource.ModelTemplate),
            ("Analytics", $"http://{device.IpAddress}:{onvifPort}/onvif/analytics_service", EndpointCandidateSource.ModelTemplate),
            ("VendorPrivateHttp", $"http://{device.IpAddress}/web/cgi-bin/hi3510/param.cgi", EndpointCandidateSource.VendorFallback),
            ("VendorPrivateHttp", $"http://{device.IpAddress}/cgi-bin/hi3510/param.cgi", EndpointCandidateSource.VendorFallback),
            ("VendorPrivateHttp", $"http://{device.IpAddress}/param.cgi", EndpointCandidateSource.VendorFallback),
            ("Snapshot", $"http://{device.IpAddress}/snapshot.jpg", EndpointCandidateSource.VendorFallback),
            ("Snapshot", $"http://{device.IpAddress}/tmpfs/snap.jpg", EndpointCandidateSource.VendorFallback),
            ("Snapshot", $"http://{device.IpAddress}/cgi-bin/snapshot.cgi", EndpointCandidateSource.VendorFallback)
        })
        {
            yield return new CameraEndpointObservation { Capability = item.Item1, Endpoint = item.Item2, State = CameraEndpointVerificationState.UnverifiedCandidate, Source = "ranked candidate", CandidateSource = item.Item3, TruthStrength = TruthStrength.Candidate, Evidence = "candidate only until live probe verifies" };
        }
    }

    private static IEnumerable<string> Compare(CameraEndpointTruthProfile profile, IEnumerable<CameraEndpointTruthProfile> siblings)
    {
        var own = profile.Endpoints.Where(e => e.State == CameraEndpointVerificationState.Verified).Select(e => e.Endpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in siblings)
        {
            var other = sibling.Endpoints.Where(e => e.State == CameraEndpointVerificationState.Verified).Select(e => e.Endpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!own.SetEquals(other))
            {
                yield return $"Endpoint drift detected versus same model/firmware camera {sibling.DeviceId}.";
            }
        }
    }

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
