using BossCam.Contracts;

namespace BossCam.Core;

/// <summary>
/// Shared device-identity merge/dedupe logic. Discovery and enrollment both feed multiple
/// representations of the same physical camera (HiChip multicast + ONVIF WS-Discovery + subnet
/// scan + operator-enrolled) into one <see cref="DeviceIdentity"/>, so the merge key and the
/// field-level merge rules must be identical across both paths or the same camera fragments.
/// </summary>
public static class DeviceIdentityMerger
{
    /// <summary>
    /// Durable identity key. MAC address first (stable across DHCP lease changes and IP reuse);
    /// falling back to IP when no MAC was captured, then stable identifiers. See
    /// <see cref="DiscoveryCoordinator"/> for the rationale.
    /// </summary>
    public static string BuildMergeKey(DeviceIdentity device)
    {
        if (!string.IsNullOrWhiteSpace(device.MacAddress))
        {
            return $"mac:{device.MacAddress}";
        }

        if (!string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return $"ip:{device.IpAddress}";
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceId))
        {
            return $"deviceId:{device.DeviceId}";
        }

        return $"esee:{device.EseeId ?? device.Id.ToString("N")}";
    }

    /// <summary>
    /// Merges a collection of device representations into deduplicated identities, keeping the
    /// highest-scoring (most complete / credentialed) record as the primary and filling gaps from
    /// the secondary. Duplicates share the key of the first record's stable identity.
    /// </summary>
    public static IReadOnlyCollection<DeviceIdentity> Merge(IEnumerable<DeviceIdentity> devices)
    {
        var merged = new Dictionary<string, DeviceIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            var key = BuildMergeKey(device);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = device;
                continue;
            }

            merged[key] = MergePair(existing, device);
        }

        return merged.Values.ToList();
    }

    /// <summary>
    /// Merges two representations of the same camera. <paramref name="existing"/> keeps its
    /// identity/primary fields unless the incoming record scores higher; port hints, last-good
    /// URLs, <see cref="LinkHint"/> and <see cref="DeviceIdentity.ContinuousRecord"/> are filled
    /// from whichever side carries them.
    /// </summary>
    public static DeviceIdentity MergePair(DeviceIdentity existing, DeviceIdentity incoming)
    {
        var primary = PickPrimary(existing, incoming);
        var secondary = ReferenceEquals(primary, existing) ? incoming : existing;
        return primary with
        {
            Id = existing.Id,
            DeviceId = Pick(primary.DeviceId, secondary.DeviceId),
            EseeId = Pick(primary.EseeId, secondary.EseeId),
            Name = Pick(primary.Name, secondary.Name),
            IpAddress = Pick(primary.IpAddress, secondary.IpAddress),
            // Port is the legacy field the NetSDK adapters drive NetSdkPortCandidates from. Prefer
            // an explicitly-known control port over the discovery-recorded value: on 5523-W the
            // recorded port is often the ONVIF/media port (8888/8899) while control lives on :80,
            // and preferring the known control port saves the adapters a dead-port probe. When no
            // side knows HttpControlPort, keep the pre-existing "prefer non-80 recorded port" rule
            // so an ONVIF-only identity still drives the {recorded, 80} fallback.
            Port = primary.HttpControlPort > 0
                ? primary.HttpControlPort
                : secondary.HttpControlPort > 0
                    ? secondary.HttpControlPort
                    : primary.Port != 80 ? primary.Port : secondary.Port,
            HttpControlPort = primary.HttpControlPort > 0 ? primary.HttpControlPort : secondary.HttpControlPort,
            OnvifMediaPort = primary.OnvifMediaPort ?? secondary.OnvifMediaPort,
            RtspPort = primary.RtspPort ?? secondary.RtspPort,
            LastGoodControlUrl = Pick(primary.LastGoodControlUrl, secondary.LastGoodControlUrl),
            LastGoodRtspUrl = Pick(primary.LastGoodRtspUrl, secondary.LastGoodRtspUrl),
            LinkHint = primary.LinkHint != LinkHint.Unknown ? primary.LinkHint : secondary.LinkHint,
            ContinuousRecord = primary.ContinuousRecord || secondary.ContinuousRecord,
            MacAddress = Pick(primary.MacAddress, secondary.MacAddress),
            WirelessMacAddress = Pick(primary.WirelessMacAddress, secondary.WirelessMacAddress),
            FirmwareVersion = Pick(primary.FirmwareVersion, secondary.FirmwareVersion),
            HardwareModel = Pick(primary.HardwareModel, secondary.HardwareModel),
            DeviceType = Pick(primary.DeviceType, secondary.DeviceType),
            LoginName = Pick(primary.LoginName, secondary.LoginName),
            Password = Pick(primary.Password, secondary.Password),
            PasswordCiphertext = Pick(primary.PasswordCiphertext, secondary.PasswordCiphertext),
            Metadata = MergeDictionary(primary.Metadata, secondary.Metadata),
            ChannelMap = primary.ChannelMap.Concat(secondary.ChannelMap)
                .GroupBy(static channel => $"{channel.ChannelNumber}:{channel.ChannelId}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static channel => channel.ChannelNumber)
                .ToList(),
            TransportProfiles = primary.TransportProfiles.Concat(secondary.TransportProfiles)
                .GroupBy(static transport => $"{transport.Kind}:{transport.Address}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.OrderBy(t => t.Rank).First())
                .OrderBy(static transport => transport.Rank)
                .ToList(),
            DiscoveredAt = primary.DiscoveredAt <= secondary.DiscoveredAt ? primary.DiscoveredAt : secondary.DiscoveredAt
        };
    }

    private static DeviceIdentity PickPrimary(DeviceIdentity left, DeviceIdentity right)
    {
        var leftScore = Score(left);
        var rightScore = Score(right);
        return leftScore >= rightScore ? left : right;
    }

    private static int Score(DeviceIdentity device)
    {
        var score = 0;
        if (string.Equals(device.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }
        if (!string.IsNullOrWhiteSpace(device.Name) && device.Name.Contains("5523", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }
        if (!string.IsNullOrWhiteSpace(device.LoginName))
        {
            score += 25;
        }
        if (!string.IsNullOrWhiteSpace(device.Password) || !string.IsNullOrWhiteSpace(device.PasswordCiphertext))
        {
            score += 20;
        }
        if (!string.IsNullOrWhiteSpace(device.FirmwareVersion))
        {
            score += 10;
        }
        if (!string.IsNullOrWhiteSpace(device.HardwareModel))
        {
            score += 10;
        }
        score += Math.Min(10, device.TransportProfiles.Count);
        return score;
    }

    private static string? Pick(string? left, string? right)
        => string.IsNullOrWhiteSpace(left) ? right : left;

    private static Dictionary<string, string> MergeDictionary(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        var merged = new Dictionary<string, string>(left, StringComparer.OrdinalIgnoreCase);
        foreach (var item in right)
        {
            if (!merged.ContainsKey(item.Key) || string.IsNullOrWhiteSpace(merged[item.Key]))
            {
                merged[item.Key] = item.Value;
            }
        }

        return merged;
    }
}
