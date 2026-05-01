using BossCam.Contracts;

namespace BossCam.Core;

public static class EndpointTruthIntegrityGuard
{
    public static CameraEndpointTruthProfile Merge(CameraEndpointTruthProfile? existing, CameraEndpointTruthProfile incoming)
    {
        if (existing is null)
        {
            return Normalize(incoming);
        }

        var drift = existing.DriftNotes.Concat(incoming.DriftNotes).ToList();
        var observations = existing.DriftObservations.Concat(incoming.DriftObservations).ToList();
        var endpoints = MergeEndpoints(existing.Endpoints, incoming.Endpoints, drift, observations);
        var streams = MergeStreams(existing.RtspPlaybackStreams, incoming.RtspPlaybackStreams, drift, observations);
        var declared = MergeDeclared(existing.OnvifDeclaredStreams, incoming.OnvifDeclaredStreams);
        var ptz = MergePtz(existing.Ptz, incoming.Ptz, drift, observations);

        return incoming with
        {
            DeviceId = existing.DeviceId == Guid.Empty ? incoming.DeviceId : existing.DeviceId,
            CredentialState = StrongerCredential(existing.CredentialState, incoming.CredentialState),
            Endpoints = endpoints,
            RtspPlaybackStreams = streams,
            OnvifDeclaredStreams = declared,
            Ptz = ptz,
            DriftNotes = drift.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DriftObservations = observations
        };
    }

    public static CameraEndpointTruthProfile Normalize(CameraEndpointTruthProfile profile)
        => profile with
        {
            Endpoints = profile.Endpoints.Select(endpoint => endpoint with
            {
                TruthStrength = endpoint.State == CameraEndpointVerificationState.Verified ? TruthStrength.LiveVerified
                    : endpoint.State == CameraEndpointVerificationState.UnverifiedCandidate ? TruthStrength.Candidate
                    : TruthStrength.FailedProbe
            }).ToList(),
            Ptz = profile.Ptz with
            {
                MovementControlsEnabled = profile.Ptz.MovementControlsEnabled && profile.Ptz.MechanicalCapability == MechanicalPtzCapability.Installed
            }
        };

    private static List<CameraEndpointObservation> MergeEndpoints(IEnumerable<CameraEndpointObservation> existing, IEnumerable<CameraEndpointObservation> incoming, List<string> drift, List<DriftObservation> observations)
    {
        var merged = existing.ToDictionary(e => $"{e.Capability}:{e.Endpoint}", StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            var key = $"{item.Capability}:{item.Endpoint}";
            if (merged.TryGetValue(key, out var current) && IsStronger(current.State, item.State))
            {
                if (item.State == CameraEndpointVerificationState.UnverifiedCandidate && current.State == CameraEndpointVerificationState.Verified)
                {
                    AddDrift(drift, observations, item.Capability, item.Endpoint, current.Endpoint, "Verified endpoint preserved over weaker candidate.");
                }
                continue;
            }

            merged[key] = item with
            {
                TruthStrength = item.State == CameraEndpointVerificationState.Verified ? TruthStrength.LiveVerified
                    : item.State == CameraEndpointVerificationState.UnverifiedCandidate ? TruthStrength.Candidate
                    : TruthStrength.FailedProbe
            };
        }

        return merged.Values.OrderBy(e => e.Capability).ThenBy(e => e.Endpoint).ToList();
    }

    private static List<RtspPlaybackProbeMetadata> MergeStreams(IEnumerable<RtspPlaybackProbeMetadata> existing, IEnumerable<RtspPlaybackProbeMetadata> incoming, List<string> drift, List<DriftObservation> observations)
    {
        var merged = existing.ToDictionary(s => s.ProfileToken, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            if (merged.TryGetValue(item.ProfileToken, out var current))
            {
                if (!string.IsNullOrWhiteSpace(current.Codec) && !string.IsNullOrWhiteSpace(item.Codec) && !current.Codec.Equals(item.Codec, StringComparison.OrdinalIgnoreCase))
                {
                    AddDrift(drift, observations, item.ProfileToken, item.Codec!, current.Codec!, "Probed playback codec preserved over weaker or contradictory codec.");
                    continue;
                }

                if (current.State == CameraEndpointVerificationState.Verified && item.State != CameraEndpointVerificationState.Verified)
                {
                    continue;
                }
            }

            merged[item.ProfileToken] = item;
        }

        return merged.Values.ToList();
    }

    private static List<OnvifDeclaredStreamMetadata> MergeDeclared(IEnumerable<OnvifDeclaredStreamMetadata> existing, IEnumerable<OnvifDeclaredStreamMetadata> incoming)
        => existing.Concat(incoming).GroupBy(s => s.ProfileToken, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

    private static PtzServiceState MergePtz(PtzServiceState existing, PtzServiceState incoming, List<string> drift, List<DriftObservation> observations)
    {
        if (incoming.ServiceState == CameraEndpointVerificationState.Verified && incoming.MovementControlsEnabled && incoming.MechanicalCapability != MechanicalPtzCapability.Installed)
        {
            AddDrift(drift, observations, "PTZ", "service present implies movement", "service present only", "PTZ service cannot enable mechanical movement.");
        }

        return incoming with
        {
            MovementControlsEnabled = incoming.MovementControlsEnabled && incoming.MechanicalCapability == MechanicalPtzCapability.Installed
        };
    }

    private static bool IsStronger(CameraEndpointVerificationState current, CameraEndpointVerificationState incoming)
        => Rank(current) > Rank(incoming);

    private static int Rank(CameraEndpointVerificationState state)
        => state switch
        {
            CameraEndpointVerificationState.Verified => 5,
            CameraEndpointVerificationState.Unauthorized => 4,
            CameraEndpointVerificationState.Timeout => 3,
            CameraEndpointVerificationState.Failed or CameraEndpointVerificationState.Unsupported => 2,
            CameraEndpointVerificationState.UnverifiedCandidate => 1,
            _ => 0
        };

    private static CameraCredentialState StrongerCredential(CameraCredentialState left, CameraCredentialState right)
        => CredentialRank(left) >= CredentialRank(right) ? left : right;

    private static int CredentialRank(CameraCredentialState state)
        => state switch
        {
            CameraCredentialState.Verified => 5,
            CameraCredentialState.Rejected => 4,
            CameraCredentialState.PlaybackLockedPendingCredentials => 3,
            CameraCredentialState.Supplied => 2,
            CameraCredentialState.Missing => 1,
            _ => 0
        };

    private static void AddDrift(List<string> notes, List<DriftObservation> observations, string subject, string expected, string observed, string evidence)
    {
        notes.Add($"{subject}: {evidence}");
        observations.Add(new DriftObservation { Subject = subject, Expected = expected, Observed = observed, Evidence = evidence });
    }
}
