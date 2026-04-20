using BossCam.Contracts;
using Microsoft.Extensions.Logging;

namespace BossCam.Core;

public sealed class DiscoveryCoordinator(
    IEnumerable<IDiscoveryProvider> discoveryProviders,
    IEnumerable<IDeviceImportProvider> importProviders,
    IApplicationStore store,
    ILogger<DiscoveryCoordinator> logger)
{
    public async Task<IReadOnlyCollection<DeviceIdentity>> RunAsync(CancellationToken cancellationToken)
    {
        var all = new List<DeviceIdentity>();
        // Include existing inventory so discovery updates enrich instead of fragmenting identities.
        all.AddRange(await store.GetDevicesAsync(cancellationToken));

        foreach (var importer in importProviders)
        {
            try
            {
                all.AddRange(await importer.ImportAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Import provider {Provider} failed", importer.Name);
            }
        }

        foreach (var discoveryProvider in discoveryProviders)
        {
            try
            {
                all.AddRange(await discoveryProvider.DiscoverAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discovery provider {Provider} failed", discoveryProvider.Name);
            }
        }

        var merged = Merge(all).Values.ToList();
        await store.UpsertDevicesAsync(merged, cancellationToken);
        return merged;
    }

    private static Dictionary<string, DeviceIdentity> Merge(IEnumerable<DeviceIdentity> devices)
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

            var primary = PickPrimary(existing, device);
            var secondary = ReferenceEquals(primary, existing) ? device : existing;
            merged[key] = primary with
            {
                Id = existing.Id,
                DeviceId = Pick(primary.DeviceId, secondary.DeviceId),
                EseeId = Pick(primary.EseeId, secondary.EseeId),
                Name = Pick(primary.Name, secondary.Name),
                IpAddress = Pick(primary.IpAddress, secondary.IpAddress),
                Port = primary.Port != 80 ? primary.Port : secondary.Port,
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

        return merged;
    }

    private static string BuildMergeKey(DeviceIdentity device)
        => !string.IsNullOrWhiteSpace(device.IpAddress)
            ? device.IpAddress
            : device.DeviceId ?? device.EseeId ?? device.Id.ToString("N");

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

public sealed class ProtocolCatalogService(IProtocolManifestProvider provider, IApplicationStore store)
{
    public async Task<IReadOnlyCollection<ProtocolManifest>> RefreshAsync(CancellationToken cancellationToken)
    {
        var manifests = await provider.LoadAsync(cancellationToken);
        await store.SaveProtocolManifestsAsync(manifests, cancellationToken);
        return manifests;
    }

    public Task<IReadOnlyCollection<ProtocolManifest>> GetAsync(CancellationToken cancellationToken)
        => store.GetProtocolManifestsAsync(cancellationToken);
}
