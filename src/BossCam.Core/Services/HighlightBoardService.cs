using BossCam.Contracts;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Core;

/// <summary>
/// Multi-camera highlight board: select one "highlighted" camera at a time and
/// flip next/previous across the inventory. Backed by ranked stream sources and
/// JPEG snapshot URLs proven on 5523-W firmware.
/// </summary>
public sealed class HighlightBoardService(
    IApplicationStore store,
    TransportBroker transportBroker,
    RecordingService recordingService,
    IHttpClientFactory httpClientFactory,
    IBossCamEventBroadcaster broadcaster,
    ILogger<HighlightBoardService> logger)
{
    private readonly object _lock = new();
    private Guid? _selectedDeviceId;
    private int _selectedIndex;
    private string _preferredStream = "main"; // main | sub | snapshot

    // Tile snapshot probe memoization: BuildTilesAsync runs on every GetState/Flip/Select and
    // would otherwise probe up to two snapshot candidates per device on each refresh. A fully
    // offline camera (adapters still emit static snapshot descriptors) would add up to 2× the
    // per-probe timeout to *every* board refresh. Cache the per-device result for a short TTL
    // and use a tighter probe bound than the recording path so repeated refreshes are cheap.
    private static readonly TimeSpan TileSnapshotProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TileSnapshotProbeTtl = TimeSpan.FromSeconds(15);
    private readonly object _snapshotProbeLock = new();
    private readonly Dictionary<Guid, (VideoSourceDescriptor? Snapshot, DateTimeOffset ProbedAt)> _snapshotProbeCache = [];

    // Tile transport-source memoization: BuildTilesAsync calls transportBroker.GetSourcesAsync for
    // every device on every board refresh (GetState/Flip/Select). A device whose primary adapters
    // yield nothing triggers TransportBroker's failover fallback, which probes each RTSP descriptor
    // with a 2s OPTIONS handshake (RtspProbe) — re-running that whole chain for every offline
    // camera on every refresh would waste seconds of socket timeouts per refresh. Cache the
    // per-device source resolution for the same short TTL; a failed/empty resolution is cached too
    // so offline devices short-circuit instead of re-triggering the failover chain.
    private static readonly TimeSpan TileSourcesProbeTtl = TimeSpan.FromSeconds(15);
    private readonly object _sourcesLock = new();
    private readonly Dictionary<Guid, (IReadOnlyCollection<VideoSourceDescriptor>? Sources, DateTimeOffset ProbedAt)> _sourcesCache = [];

    public async Task<HighlightBoardState> GetStateAsync(CancellationToken cancellationToken)
    {
        var tiles = await BuildTilesAsync(cancellationToken);
        lock (_lock)
        {
            if (_selectedDeviceId is null && tiles.Count > 0)
            {
                _selectedDeviceId = tiles[0].DeviceId;
                _selectedIndex = 0;
            }
            else if (_selectedDeviceId is Guid id)
            {
                var idx = tiles.FindIndex(t => t.DeviceId == id);
                if (idx >= 0)
                {
                    _selectedIndex = idx;
                }
                else if (tiles.Count > 0)
                {
                    _selectedIndex = 0;
                    _selectedDeviceId = tiles[0].DeviceId;
                }
            }

            return new HighlightBoardState
            {
                SelectedDeviceId = _selectedDeviceId,
                SelectedIndex = _selectedIndex,
                PreferredStream = _preferredStream,
                Tiles = tiles,
                Selected = tiles.ElementAtOrDefault(_selectedIndex)
            };
        }
    }

    public async Task<HighlightBoardState> SelectAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var tiles = await BuildTilesAsync(cancellationToken);
        var idx = tiles.FindIndex(t => t.DeviceId == deviceId);
        if (idx < 0)
        {
            throw new InvalidOperationException($"Device {deviceId} is not on the highlight board.");
        }

        lock (_lock)
        {
            _selectedDeviceId = deviceId;
            _selectedIndex = idx;
        }

        logger.LogInformation("Highlight selected device={DeviceId} index={Index}", deviceId, idx);
        var state = await GetStateAsync(cancellationToken);
        _ = broadcaster.HighlightStateChangedAsync(state, cancellationToken);
        return state;
    }

    public async Task<HighlightBoardState> FlipAsync(int direction, CancellationToken cancellationToken)
    {
        var tiles = await BuildTilesAsync(cancellationToken);
        if (tiles.Count == 0)
        {
            return await GetStateAsync(cancellationToken);
        }

        lock (_lock)
        {
            var delta = direction >= 0 ? 1 : -1;
            _selectedIndex = ((_selectedIndex + delta) % tiles.Count + tiles.Count) % tiles.Count;
            _selectedDeviceId = tiles[_selectedIndex].DeviceId;
        }

        logger.LogInformation("Highlight flipped direction={Direction} index={Index}", direction, _selectedIndex);
        var state = await GetStateAsync(cancellationToken);
        _ = broadcaster.HighlightStateChangedAsync(state, cancellationToken);
        return state;
    }

    public async Task<HighlightBoardState> SetPreferredStreamAsync(string preferredStream, CancellationToken cancellationToken)
    {
        preferredStream = preferredStream?.Trim().ToLowerInvariant() switch
        {
            "sub" or "secondary" or "12" => "sub",
            "snapshot" or "jpeg" or "still" => "snapshot",
            _ => "main"
        };

        lock (_lock)
        {
            _preferredStream = preferredStream;
        }

        var state = await GetStateAsync(cancellationToken);
        _ = broadcaster.HighlightStateChangedAsync(state, cancellationToken);
        return state;
    }

    public async Task<IReadOnlyCollection<RecordingJob>> RecordSelectedAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        if (state.Selected is null)
        {
            return [];
        }

        var job = await recordingService.StartAsync(new RecordingStartRequest
        {
            DeviceId = state.Selected.DeviceId,
            SourceUrl = state.Selected.RecordUrl ?? state.Selected.LiveUrl
        }, cancellationToken);
        return [job];
    }

    private async Task<List<HighlightTile>> BuildTilesAsync(CancellationToken cancellationToken)
    {
        var devices = (await store.GetDevicesAsync(cancellationToken))
            .Where(static d => !string.IsNullOrWhiteSpace(d.IpAddress))
            .GroupBy(d => d.IpAddress!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(static d => string.Equals(d.HardwareModel, "5523-W", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static d => string.Equals(d.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static d => d.DiscoveredAt)
                .First())
            // Prefer controllable cameras; still include any device with an IP so selection never 500s for registered inventory.
            .Where(static d =>
                string.Equals(d.DeviceType, "IPC", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DeviceType, "ONVIF", StringComparison.OrdinalIgnoreCase)
                || (d.HardwareModel?.Contains("5523", StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.HardwareModel?.Contains("W5C", StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.HardwareModel?.Contains("Lorex", StringComparison.OrdinalIgnoreCase) ?? false)
                || !string.IsNullOrWhiteSpace(d.EseeId)
                || d.TransportProfiles.Count > 0
                || !string.IsNullOrWhiteSpace(d.IpAddress))
            .OrderBy(static d => d.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string preferred;
        lock (_lock)
        {
            preferred = _preferredStream;
        }

        var tiles = new List<HighlightTile>();
        foreach (var device in devices)
        {
            var sources = await ResolveTileSourcesAsync(device.Id, cancellationToken);

            var sourceDecision = PlayableSourcePolicy.Resolve(sources);
            var mainRtsp = sourceDecision.Main;
            var subRtsp = sourceDecision.Sub;
            // Probe the snapshot-kind descriptors in rank order (recorded port first, then the
            // :80 fallback the adapters emit) so the tile's SnapshotUrl / live fallback picks a
            // genuinely reachable JPEG — self-healing dead recorded ports like the recording path.
            // Memoized per device (15s TTL) with a tighter 2s per-probe bound so offline cameras
            // don't stall every board refresh (see ResolveTileSnapshotAsync).
            var snapshot = await ResolveTileSnapshotAsync(device.Id, sources, cancellationToken);
            var bubble = sources.FirstOrDefault(s => s.Kind == TransportKind.BubbleFlv);

            var live = preferred switch
            {
                "sub" => subRtsp ?? mainRtsp ?? bubble ?? snapshot,
                "snapshot" => snapshot ?? mainRtsp ?? subRtsp,
                _ => mainRtsp ?? subRtsp ?? bubble ?? snapshot
            };

            tiles.Add(new HighlightTile
            {
                DeviceId = device.Id,
                DisplayName = device.DisplayName,
                IpAddress = device.IpAddress ?? string.Empty,
                HardwareModel = device.HardwareModel,
                ChannelName = device.Name,
                LiveUrl = live?.Url,
                SnapshotUrl = snapshot?.Url,
                RecordUrl = mainRtsp?.Url ?? subRtsp?.Url ?? bubble?.Url,
                MainRtspUrl = mainRtsp?.Url,
                SubRtspUrl = subRtsp?.Url,
                BubbleUrl = bubble?.Url,
                Sources = sources.ToList()
            });
        }

        return tiles;
    }

    /// <summary>
    /// Resolves the tile's transport sources, memoized per device for a short TTL so repeated board
    /// refreshes (GetState/Flip/Select) never re-run the failover chain — a device whose primary
    /// adapters yield nothing triggers <see cref="TransportBroker.GetSourcesAsync"/>'s
    /// TransportFailoverService fallback, which probes each RTSP descriptor with a 2s OPTIONS
    /// handshake. A cached failure/empty result is also honored so offline cameras short-circuit
    /// until the TTL expires instead of re-probing on every refresh.
    /// </summary>
    private async Task<IReadOnlyCollection<VideoSourceDescriptor>> ResolveTileSourcesAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        lock (_sourcesLock)
        {
            if (_sourcesCache.TryGetValue(deviceId, out var cached)
                && DateTimeOffset.UtcNow - cached.ProbedAt < TileSourcesProbeTtl)
            {
                return cached.Sources ?? [];
            }
        }

        IReadOnlyCollection<VideoSourceDescriptor> sources;
        try
        {
            sources = await transportBroker.GetSourcesAsync(deviceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve sources for device={DeviceId}; tile sources empty", deviceId);
            sources = [];
        }

        lock (_sourcesLock)
        {
            _sourcesCache[deviceId] = (sources, DateTimeOffset.UtcNow);
        }

        return sources;
    }

    /// <summary>
    /// Resolves the tile's snapshot descriptor, memoized per device for a short TTL so repeated
    /// board refreshes (GetState/Flip/Select) never re-probe an offline camera. The tile path
    /// probes with a tighter 2s per-candidate bound than the recording path's 4s default and a
    /// headers-only reachability check (no JPEG body download) — a fully-dead device costs at most
    /// one 2s timeout per candidate per TTL window instead of stalling every refresh. A cached
    /// null (nothing answered on any candidate) is also honored so offline devices short-circuit
    /// without a probe until the TTL expires.
    /// </summary>
    private async Task<VideoSourceDescriptor?> ResolveTileSnapshotAsync(
        Guid deviceId,
        IReadOnlyCollection<VideoSourceDescriptor> sources,
        CancellationToken cancellationToken)
    {
        lock (_snapshotProbeLock)
        {
            if (_snapshotProbeCache.TryGetValue(deviceId, out var cached)
                && DateTimeOffset.UtcNow - cached.ProbedAt < TileSnapshotProbeTtl)
            {
                return cached.Snapshot;
            }
        }

        VideoSourceDescriptor? snapshot;
        try
        {
            snapshot = await NetSdkPortCandidates.FirstReachableSnapshotAsync(
                httpClientFactory, sources, cancellationToken, TileSnapshotProbeTimeout, requireJpeg: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // FirstReachableSnapshotAsync swallows per-candidate transport failures, so this is
            // only reachable for infrastructure errors; degrade to a null tile rather than
            // failing the whole board refresh.
            logger.LogDebug(ex, "Snapshot probe failed for device={DeviceId}; tile snapshot null", deviceId);
            snapshot = null;
        }

        lock (_snapshotProbeLock)
        {
            _snapshotProbeCache[deviceId] = (snapshot, DateTimeOffset.UtcNow);
        }

        return snapshot;
    }
}

public sealed record HighlightTile
{
    public Guid DeviceId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string? HardwareModel { get; init; }
    public string? ChannelName { get; init; }
    public string? LiveUrl { get; init; }
    public string? SnapshotUrl { get; init; }
    public string? RecordUrl { get; init; }
    public string? MainRtspUrl { get; init; }
    public string? SubRtspUrl { get; init; }
    public string? BubbleUrl { get; init; }
    public IReadOnlyList<VideoSourceDescriptor> Sources { get; init; } = [];
}

public sealed record HighlightBoardState
{
    public Guid? SelectedDeviceId { get; init; }
    public int SelectedIndex { get; init; }
    public string PreferredStream { get; init; } = "main";
    public HighlightTile? Selected { get; init; }
    public IReadOnlyList<HighlightTile> Tiles { get; init; } = [];
}

public sealed record DeviceRegisterRequest
{
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 80;
    public string LoginName { get; init; } = "admin";
    public string? Password { get; init; }
    public string? Name { get; init; }
    public string? HardwareModel { get; init; }
}

public sealed class DeviceRegistrationService(
    IApplicationStore store,
    IHttpClientFactory httpClientFactory,
    CapabilityProbeService probeService,
    SettingsService settingsService,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<DeviceRegistrationService> logger)
{
    public async Task<IReadOnlyCollection<DeviceIdentity>> RegisterManyAsync(IEnumerable<DeviceRegisterRequest> requests, CancellationToken cancellationToken)
    {
        var results = new List<DeviceIdentity>();
        foreach (var request in requests)
        {
            results.Add(await RegisterAsync(request, cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// Registers the operator's known cameras from <see cref="BossCamRuntimeOptions.AegonLanDevices"/>
    /// (previously hardcoded home-LAN addresses that leaked a private topology into the repo).
    /// WVC/Lorex passwords may still be supplied per-call and are matched by hardware model.
    /// </summary>
    public async Task<IReadOnlyCollection<DeviceIdentity>> RegisterAegonLanDefaultsAsync(
        string? lorexPassword,
        string? wvcPassword,
        CancellationToken cancellationToken)
    {
        var devices = options.Value.AegonLanDevices;
        if (devices.Length == 0)
        {
            logger.LogWarning(
                "register-aegon-lan called but BossCam:AegonLanDevices is empty; nothing registered. Add entries in appsettings to enable this convenience batch.");
            return [];
        }

        return await RegisterManyAsync(BuildAegonLanRequests(devices, lorexPassword, wvcPassword), cancellationToken);
    }

    /// <summary>Maps configured <see cref="AegonLanDeviceOptions"/> entries to register requests,
    /// applying the per-call brand passwords by hardware model. Entries without an IP are skipped.</summary>
    internal static IReadOnlyCollection<DeviceRegisterRequest> BuildAegonLanRequests(
        IReadOnlyCollection<AegonLanDeviceOptions> devices,
        string? lorexPassword,
        string? wvcPassword)
    {
        var requests = new List<DeviceRegisterRequest>();
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.IpAddress))
            {
                continue;
            }

            requests.Add(new DeviceRegisterRequest
            {
                IpAddress = device.IpAddress,
                Port = device.Port > 0 ? device.Port : 80,
                LoginName = string.IsNullOrWhiteSpace(device.LoginName) ? "admin" : device.LoginName,
                Password = ResolveBrandPassword(device.HardwareModel, lorexPassword, wvcPassword),
                Name = string.IsNullOrWhiteSpace(device.Name) ? null : device.Name,
                HardwareModel = string.IsNullOrWhiteSpace(device.HardwareModel) ? null : device.HardwareModel
            });
        }

        return requests;
    }

    private static string? ResolveBrandPassword(string? hardwareModel, string? lorexPassword, string? wvcPassword)
    {
        if (hardwareModel?.Contains("W5C", StringComparison.OrdinalIgnoreCase) == true)
        {
            return wvcPassword;
        }

        return hardwareModel?.Contains("Lorex", StringComparison.OrdinalIgnoreCase) == true ? lorexPassword : null;
    }

    public async Task<DeviceIdentity> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IpAddress))
        {
            throw new ArgumentException("IpAddress is required.", nameof(request));
        }

        var existing = (await store.GetDevicesAsync(cancellationToken))
            .FirstOrDefault(d => string.Equals(d.IpAddress, request.IpAddress, StringComparison.OrdinalIgnoreCase));

        var user = string.IsNullOrWhiteSpace(request.LoginName) ? "admin" : request.LoginName;
        var password = request.Password ?? string.Empty;
        var auth = $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@";
        var port = request.Port <= 0 ? 80 : request.Port;

        DeviceIdentity? enriched = null;

        // Brand A: Juan / NetSDK
        try
        {
            using var client = httpClientFactory.CreateClient("probe");
            client.Timeout = TimeSpan.FromSeconds(6);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"http://{request.IpAddress}:{port}/NetSDK/System/deviceInfo");
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            using var response = await client.SendAsync(httpRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                var node = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject;
                enriched = new DeviceIdentity
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    IpAddress = request.IpAddress,
                    Port = port,
                    LoginName = user,
                    Password = password,
                    Name = request.Name ?? node?["deviceName"]?.GetValue<string>() ?? $"Camera {request.IpAddress}",
                    HardwareModel = request.HardwareModel ?? node?["model"]?.GetValue<string>(),
                    FirmwareVersion = node?["firmwareVersion"]?.GetValue<string>(),
                    DeviceId = node?["serialNumber"]?.GetValue<string>(),
                    EseeId = node?["eseeID"]?.GetValue<string>(),
                    // Also populate the MacAddress property (not just Metadata): MAC-first merge
                    // keying in the coordinator/store means a registration that only fills
                    // Metadata["macAddress"] would fragment from the HiChip-discovered copy of the
                    // same camera into a separate identity.
                    MacAddress = node?["macAddress"]?.GetValue<string>() ?? node?["mac"]?.GetValue<string>(),
                    DeviceType = "IPC",
                    DiscoveredAt = DateTimeOffset.UtcNow,
                    TransportProfiles =
                    [
                        new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{request.IpAddress}:{port}", Rank = 5 },
                        // High-res main first (proven 2560x1920 HEVC)
                        new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/ch0_0.264", Rank = 0, Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true", ["resolution"] = "2560x1920" } },
                        new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/ch0_1.264", Rank = 50, Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["highRes"] = "false" } },
                        new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = $"http://{request.IpAddress}:8888/onvif/device_service", Rank = 8 },
                        new TransportProfile { Kind = TransportKind.BubbleFlv, Address = $"http://{auth}{request.IpAddress}:{port}/bubble/live?ch=1&stream=0", Rank = 30 }
                    ],
                    Metadata = new Dictionary<string, string>
                    {
                        ["macAddress"] = node?["macAddress"]?.GetValue<string>() ?? string.Empty,
                        ["sdkVersion"] = node?["sdkVersion"]?.GetValue<string>() ?? string.Empty,
                        ["manufacturer"] = node?["manufacturer"]?.GetValue<string>() ?? "GUANGZHOU",
                        ["brand"] = "JuanNetSdk",
                        ["highResStream"] = $"/ch0_0.264",
                        ["highResEncodeChannel"] = "101"
                    }
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "NetSDK deviceInfo failed for {Ip}", request.IpAddress);
        }

        // Brand B/C: ONVIF (WVC W5C / generic) when NetSDK missing
        if (enriched is null)
        {
            foreach (var onvifPort in new[] { port, 8899, 8888, 80 }.Distinct())
            {
                try
                {
                    using var client = httpClientFactory.CreateClient("onvif");
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var soap = """
                        <?xml version="1.0" encoding="UTF-8"?>
                        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
                          <s:Body><tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/></s:Body>
                        </s:Envelope>
                        """;
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{request.IpAddress}:{onvifPort}/onvif/device_service");
                    req.Content = new StringContent(soap, System.Text.Encoding.UTF8, "application/soap+xml");
                    var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
                    req.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
                    using var response = await client.SendAsync(req, cancellationToken);
                    var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!xml.Contains("Manufacturer", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? Tag(string name)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(xml, $@"<(?:\w+:)?{name}[^>]*>([^<]*)</(?:\w+:)?{name}>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        return m.Success ? m.Groups[1].Value : null;
                    }

                    var manufacturer = Tag("Manufacturer");
                    var model = Tag("Model") ?? request.HardwareModel;
                    var brand = manufacturer?.Contains("WVC", StringComparison.OrdinalIgnoreCase) == true
                        || model?.Contains("W5C", StringComparison.OrdinalIgnoreCase) == true
                        ? "WvcOnvif"
                        : "GenericOnvif";

                    enriched = new DeviceIdentity
                    {
                        Id = existing?.Id ?? Guid.NewGuid(),
                        IpAddress = request.IpAddress,
                        Port = onvifPort,
                        LoginName = user,
                        Password = password,
                        Name = request.Name ?? model ?? $"ONVIF {request.IpAddress}",
                        HardwareModel = model,
                        FirmwareVersion = Tag("FirmwareVersion"),
                        DeviceId = Tag("SerialNumber"),
                        DeviceType = "ONVIF",
                        DiscoveredAt = DateTimeOffset.UtcNow,
                        TransportProfiles =
                        [
                            new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = $"http://{request.IpAddress}:{onvifPort}/onvif/device_service", Rank = 1 },
                            new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/stream1", Rank = 2, Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" } },
                            new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/stream0", Rank = 3, Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" } }
                        ],
                        Metadata = new Dictionary<string, string>
                        {
                            ["manufacturer"] = manufacturer ?? string.Empty,
                            ["brand"] = brand,
                            ["onvifPort"] = onvifPort.ToString()
                        }
                    };
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "ONVIF probe failed {Ip}:{Port}", request.IpAddress, onvifPort);
                }
            }
        }

        // Brand C: Lorex / Dahua web shell
        if (enriched is null)
        {
            try
            {
                using var client = httpClientFactory.CreateClient("probe");
                client.Timeout = TimeSpan.FromSeconds(4);
                var html = await client.GetStringAsync($"http://{request.IpAddress}:{port}/", cancellationToken);
                if (html.Contains("flirLorex", StringComparison.OrdinalIgnoreCase) || html.Contains("WEB SERVICE", StringComparison.OrdinalIgnoreCase))
                {
                    enriched = new DeviceIdentity
                    {
                        Id = existing?.Id ?? Guid.NewGuid(),
                        IpAddress = request.IpAddress,
                        Port = port,
                        LoginName = user,
                        Password = password,
                        Name = request.Name ?? "Lorex",
                        HardwareModel = request.HardwareModel ?? "Lorex",
                        DeviceType = "IPC",
                        DiscoveredAt = DateTimeOffset.UtcNow,
                        TransportProfiles =
                        [
                            new TransportProfile { Kind = TransportKind.LanPrivateHttp, Address = $"http://{request.IpAddress}:{port}", Rank = 5 },
                            new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/cam/realmonitor?channel=1&subtype=0", Rank = 0, Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" } },
                            new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/cam/realmonitor?channel=1&subtype=1", Rank = 50, Metadata = new Dictionary<string, string> { ["stream"] = "sub", ["highRes"] = "false" } },
                            new TransportProfile { Kind = TransportKind.OnvifRtsp, Address = $"http://{request.IpAddress}:{port}/onvif/device_service", Rank = 8 }
                        ],
                        Metadata = new Dictionary<string, string>
                        {
                            ["brand"] = "DahuaLorex",
                            ["auth"] = "digest",
                            ["highResStream"] = "subtype=0"
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Lorex shell probe failed for {Ip}", request.IpAddress);
            }
        }

        enriched ??= new DeviceIdentity
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            IpAddress = request.IpAddress,
            Port = port,
            LoginName = user,
            Password = password,
            Name = request.Name ?? $"Camera {request.IpAddress}",
            HardwareModel = request.HardwareModel,
            DeviceType = "IPC",
            DiscoveredAt = DateTimeOffset.UtcNow,
            TransportProfiles =
            [
                new TransportProfile { Kind = TransportKind.LanRest, Address = $"http://{request.IpAddress}:{port}", Rank = 5 },
                new TransportProfile { Kind = TransportKind.Rtsp, Address = $"rtsp://{auth}{request.IpAddress}:554/ch0_0.264", Rank = 0, Metadata = new Dictionary<string, string> { ["stream"] = "main", ["highRes"] = "true" } }
            ]
        };

        await store.UpsertDevicesAsync([enriched], cancellationToken);
        try
        {
            _ = await probeService.ProbeAsync(enriched, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Probe after register failed for {Ip}", request.IpAddress);
        }

        // Auto-sync the 5523-W OSD clock on registration so a freshly-registered camera shows
        // the correct time without pressing the "Sync Camera Clock" button. Best-effort: the
        // sync never throws and can never fail the registration.
        await settingsService.AutoSyncClockAsync(enriched, cancellationToken);

        return enriched;
    }
}
