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
            var key = device.DeviceId ?? device.EseeId ?? device.IpAddress ?? device.Id.ToString("N");
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = device;
                continue;
            }

            merged[key] = existing with
            {
                DeviceId = Pick(existing.DeviceId, device.DeviceId),
                EseeId = Pick(existing.EseeId, device.EseeId),
                Name = Pick(existing.Name, device.Name),
                IpAddress = Pick(existing.IpAddress, device.IpAddress),
                Port = existing.Port != 80 ? existing.Port : device.Port,
                MacAddress = Pick(existing.MacAddress, device.MacAddress),
                WirelessMacAddress = Pick(existing.WirelessMacAddress, device.WirelessMacAddress),
                FirmwareVersion = Pick(existing.FirmwareVersion, device.FirmwareVersion),
                HardwareModel = Pick(existing.HardwareModel, device.HardwareModel),
                DeviceType = Pick(existing.DeviceType, device.DeviceType),
                LoginName = Pick(existing.LoginName, device.LoginName),
                Password = Pick(existing.Password, device.Password),
                PasswordCiphertext = Pick(existing.PasswordCiphertext, device.PasswordCiphertext),
                Metadata = MergeDictionary(existing.Metadata, device.Metadata),
                ChannelMap = existing.ChannelMap.Concat(device.ChannelMap)
                    .GroupBy(static channel => $"{channel.ChannelNumber}:{channel.ChannelId}", StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .OrderBy(static channel => channel.ChannelNumber)
                    .ToList(),
                TransportProfiles = existing.TransportProfiles.Concat(device.TransportProfiles)
                    .GroupBy(static transport => $"{transport.Kind}:{transport.Address}", StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.OrderBy(t => t.Rank).First())
                    .OrderBy(static transport => transport.Rank)
                    .ToList(),
                DiscoveredAt = existing.DiscoveredAt <= device.DiscoveredAt ? existing.DiscoveredAt : device.DiscoveredAt
            };
        }

        return merged;
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
