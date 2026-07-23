using BossCam.Contracts;
using Microsoft.Extensions.Logging;

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
    ILogger<HighlightBoardService> logger)
{
    private readonly object _lock = new();
    private Guid? _selectedDeviceId;
    private int _selectedIndex;
    private string _preferredStream = "main"; // main | sub | snapshot

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
        return await GetStateAsync(cancellationToken);
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
        return await GetStateAsync(cancellationToken);
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

        return await GetStateAsync(cancellationToken);
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
            IReadOnlyCollection<VideoSourceDescriptor> sources;
            try
            {
                sources = await transportBroker.GetSourcesAsync(device.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to resolve sources for {Device}", device.DisplayName);
                sources = [];
            }

            var mainRtsp = RecordingService.SelectHighResMainSource(sources)
                ?? sources.FirstOrDefault(s => s.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp
                    && !(s.Metadata.TryGetValue("stream", out var st) && st.Equals("sub", StringComparison.OrdinalIgnoreCase)));
            var subRtsp = sources.FirstOrDefault(s =>
                (s.Kind is TransportKind.Rtsp or TransportKind.OnvifRtsp)
                && (s.Url.Contains("ch0_1", StringComparison.OrdinalIgnoreCase)
                    || s.Url.Contains("/12", StringComparison.OrdinalIgnoreCase)
                    || s.Url.Contains("subtype=1", StringComparison.OrdinalIgnoreCase)
                    || (s.Metadata.TryGetValue("stream", out var st) && st.Equals("sub", StringComparison.OrdinalIgnoreCase))));
            var snapshot = sources.FirstOrDefault(s =>
                s.Metadata.TryGetValue("kind", out var kind) && kind.Equals("snapshot", StringComparison.OrdinalIgnoreCase));
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
    CapabilityProbeService probeService,
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
    /// Registers the known multi-brand cameras on the Aegon LAN (Juan 5523-W, WVC W5C, Lorex/Dahua).
    /// Credentials for non-default brands should be provided by the caller when needed.
    /// </summary>
    public Task<IReadOnlyCollection<DeviceIdentity>> RegisterAegonLanDefaultsAsync(
        string? lorexPassword,
        string? wvcPassword,
        CancellationToken cancellationToken)
        => RegisterManyAsync(
        [
            new DeviceRegisterRequest { IpAddress = "10.0.0.30", Port = 80, LoginName = "admin", Password = "", Name = "Cam-30", HardwareModel = "5523-W" },
            new DeviceRegisterRequest { IpAddress = "10.0.0.170", Port = 80, LoginName = "admin", Password = "", Name = "Driveway", HardwareModel = "5523-W" },
            new DeviceRegisterRequest { IpAddress = "10.0.0.228", Port = 80, LoginName = "admin", Password = "", Name = "Cam-228", HardwareModel = "5523-W" },
            new DeviceRegisterRequest { IpAddress = "10.0.0.129", Port = 8899, LoginName = "admin", Password = wvcPassword ?? "", Name = "WVC-W5C", HardwareModel = "W5C" },
            new DeviceRegisterRequest { IpAddress = "10.0.0.128", Port = 80, LoginName = "admin", Password = lorexPassword ?? "", Name = "Lorex", HardwareModel = "Lorex" }
        ], cancellationToken);

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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
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
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
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
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
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

        return enriched;
    }
}
