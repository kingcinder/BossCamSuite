using BossCam.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Core;

public sealed class DiscoveryCoordinator(
    IEnumerable<IDiscoveryProvider> discoveryProviders,
    IEnumerable<IDeviceImportProvider> importProviders,
    IApplicationStore store,
    IBossCamEventBroadcaster broadcaster,
    IHostEnvironment environment,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<DiscoveryCoordinator> logger)
{
    public async Task<IReadOnlyCollection<DeviceIdentity>> RunAsync(CancellationToken cancellationToken)
    {
        return await RunAsync(null, cancellationToken);
    }

    public async Task<IReadOnlyCollection<DeviceIdentity>> RunAsync(string? ipRangeOverride, CancellationToken cancellationToken)
    {
        var all = new List<DeviceIdentity>();

        // Offline / E2E gate. Three independent triggers, any of which engages the gate:
        //   1. IHostEnvironment.IsDevelopment() (set by WebApplicationFactory.UseEnvironment or ASPNETCORE_ENVIRONMENT).
        //   2. Explicit BossCamRuntimeOptions.DiscoveryOfflineMode = true (factory-set for tests).
        //   3. BOSSCAM_E2E_LIVE=0 env var (matches scripts/run-exhaustive-ubuntu-e2e.sh export).
        // The gate is only honoured while NONE of them is set when running in Development: a
        // production shell (IsDevelopment==false, no flag, no env var) NEVER skips discovery.
        var isDevelopment = environment.IsDevelopment();
        var flagOffline = options.Value.DiscoveryOfflineMode;
        var envOffline = string.Equals(
            Environment.GetEnvironmentVariable("BOSSCAM_E2E_LIVE"),
            "0",
            StringComparison.OrdinalIgnoreCase);
        if ((isDevelopment && (flagOffline || envOffline)) || (!isDevelopment && flagOffline))
        {
            all.AddRange(await store.GetDevicesAsync(cancellationToken));
            if (flagOffline)
            {
                logger.LogInformation("DiscoveryCoordinator skipped providers: BossCam:DiscoveryOfflineMode=true.");
            }
            else
            {
                logger.LogInformation("DiscoveryCoordinator skipped providers: BOSSCAM_E2E_LIVE=0 (Development).");
            }
            return all;
        }

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

        // Subnet scans are fallback-only by design (see ISubnetScanDiscoveryProvider): they sweep
        // every /24 × 6 ports (~1,500 probes), so running them unconditionally on every cycle
        // fights the documented "fallback when multicast yields nothing" contract. Partition the
        // providers so passive (multicast/broadcast) discovery runs first, and the subnet sweep
        // only when it found nothing OR the caller explicitly requested a range scan.
        var passiveProviders = new List<IDiscoveryProvider>();
        var subnetProviders = new List<ISubnetScanDiscoveryProvider>();
        foreach (var discoveryProvider in discoveryProviders)
        {
            if (discoveryProvider is ISubnetScanDiscoveryProvider subnet)
            {
                subnetProviders.Add(subnet);
            }
            else
            {
                passiveProviders.Add(discoveryProvider);
            }
        }

        var passiveFound = 0;
        foreach (var discoveryProvider in passiveProviders)
        {
            try
            {
                // PR: Report discovery progress per provider
                _ = broadcaster.DiscoveryProgressAsync(all.Count, discoveryProvider.Name, false, null, cancellationToken);
                var found = await discoveryProvider.DiscoverAsync(cancellationToken);
                passiveFound += found.Count;
                all.AddRange(found);
                _ = broadcaster.DiscoveryProgressAsync(all.Count, discoveryProvider.Name, true, null, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discovery provider {Provider} failed", discoveryProvider.Name);
                _ = broadcaster.DiscoveryProgressAsync(all.Count, discoveryProvider.Name, true, ex.Message, cancellationToken);
            }
        }

        // Subnet sweep: only when passive discovery yielded nothing, or on explicit request.
        if (ipRangeOverride is not null || passiveFound == 0)
        {
            foreach (var subnetProvider in subnetProviders)
            {
                subnetProvider.SubnetRangeOverride = ipRangeOverride;
                try
                {
                    _ = broadcaster.DiscoveryProgressAsync(all.Count, subnetProvider.Name, false, null, cancellationToken);
                    var found = await subnetProvider.DiscoverAsync(cancellationToken);
                    all.AddRange(found);
                    _ = broadcaster.DiscoveryProgressAsync(all.Count, subnetProvider.Name, true, null, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Discovery provider {Provider} failed", subnetProvider.Name);
                    _ = broadcaster.DiscoveryProgressAsync(all.Count, subnetProvider.Name, true, ex.Message, cancellationToken);
                }
                finally
                {
                    subnetProvider.SubnetRangeOverride = null;
                }
            }
        }
        else if (subnetProviders.Count > 0)
        {
            logger.LogDebug(
                "Subnet scan skipped: passive discovery found {Count} device(s); run it explicitly via a range scan to force a sweep",
                passiveFound);
        }

        var merged = Merge(all).Values.ToList();
        await store.UpsertDevicesAsync(merged, cancellationToken);

        // Push the updated device list to all connected SPA clients.
        _ = broadcaster.DevicesChangedAsync(merged, cancellationToken);

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
    {
        // MAC address is the durable identity across DHCP lease changes and IP reuse. Keying on
        // the bare IP means a 5523-W whose lease renews to a new address fragments into a
        // duplicate, and — worse — a foreign host (laptop/phone) that inherits a dead camera's IP
        // silently merges into its slot and can inherit its credentials. Key on MAC first, falling
        // back to IP only when no MAC was captured, then to stable identifiers.
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
