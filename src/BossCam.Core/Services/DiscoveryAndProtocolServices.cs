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

        var merged = DeviceIdentityMerger.Merge(all);
        await store.UpsertDevicesAsync(merged, cancellationToken);

        // Push the updated device list to all connected SPA clients.
        _ = broadcaster.DevicesChangedAsync(merged, cancellationToken);

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
