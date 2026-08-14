using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Main window shell: owns navigation between sections, the shared device
/// selection, and the live-preview section (this VM is itself the first
/// section in the list). All other sections are hosted as child ViewModels.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, ISectionViewModel, IDisposable
{
    private readonly IBossCamApiClient _api;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IBossCamServiceStarter _serviceStarter;
    private Timer? _liveTimer;
    private Timer? _healthPollTimer;
    private CancellationTokenSource? _liveVideoCts;
    private Task? _liveVideoTask;
    private Process? _liveVideoProcess;
    private readonly CancellationTokenSource _startupCts = new();
    private int _handshakeInProgress;
    private bool _disposed;

    /// <summary>How often the connection indicator re-checks /api/health.</summary>
    internal static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Default constructor — real HTTP client at http://127.0.0.1:5317.</summary>
    public MainWindowViewModel()
        : this(new HttpBossCamApiClient())
    {
    }

    /// <summary>DI-friendly constructor for production wiring and tests.</summary>
    public MainWindowViewModel(
        IBossCamApiClient apiClient,
        ILogger<MainWindowViewModel>? logger = null,
        IBossCamServiceStarter? serviceStarter = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        _api = apiClient;
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;
        _serviceStarter = serviceStarter ?? new BossCamServiceStarter(_logger);

        DashboardSection = new DashboardViewModel(apiClient, this);
        DevicesSection = new DevicesViewModel(apiClient, this);
        FeaturesSection = new FeaturesViewModel(apiClient, this);
        RecordingsSection = new RecordingsViewModel(apiClient, this);
        HighlightsSection = new HighlightsViewModel(apiClient, this);
        PlaybackSection = new PlaybackViewModel(apiClient, this);
        DiagnosticsSection = new DiagnosticsViewModel(apiClient, this);
        FirmwareSection = new FirmwareViewModel(apiClient, this);
        ConnectivitySection = new ConnectivityViewModel(apiClient, this);
        StorageSection = new StorageViewModel(apiClient, this);

        Sections = [this, DashboardSection, DevicesSection, FeaturesSection, RecordingsSection,
                    HighlightsSection, PlaybackSection, DiagnosticsSection, FirmwareSection,
                    ConnectivitySection, StorageSection];
        _selectedSection = this;

        // Persistent connection indicator: re-probe /api/health periodically so the
        // color-coded status (and the status text) reflect the live service state even
        // when the service stops or starts while the window is open. Best-effort: the
        // poll never throws and never starts the service itself — Retry does that.
        _healthPollTimer = new Timer(async _ => await PollHealthAsync(), null, HealthPollInterval, HealthPollInterval);
    }

    // ── Navigation ───────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<ISectionViewModel> _sections;

    [ObservableProperty]
    private object? _selectedSection;

    partial void OnSelectedSectionChanged(object? value)
    {
        if (value is ISectionViewModel section)
        {
            _ = ActivateSectionAsync(section);
        }
    }

    private async Task ActivateSectionAsync(ISectionViewModel section)
    {
        try
        {
            await section.ActivateAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"{section.Title} failed to load: {ex.Message}";
        }
    }

    // ── Section children (bound from MainWindow.xaml) ─────────────

    public DashboardViewModel DashboardSection { get; }
    public DevicesViewModel DevicesSection { get; }
    public FeaturesViewModel FeaturesSection { get; }
    public RecordingsViewModel RecordingsSection { get; }
    public HighlightsViewModel HighlightsSection { get; }
    public PlaybackViewModel PlaybackSection { get; }
    public DiagnosticsViewModel DiagnosticsSection { get; }
    public FirmwareViewModel FirmwareSection { get; }
    public ConnectivityViewModel ConnectivitySection { get; }
    public StorageViewModel StorageSection { get; }

    // ── ISectionViewModel (this shell is the "Live View" section) ─

    public string Title => "Live View";
    public string Glyph => "\U0001F4FA";
    public string Explain =>
        "Live View streams a snapshot of the selected camera and shows its identity " +
        "details. Click a camera in the sidebar device list, then use Refresh, Snapshot " +
        "or Save to manage the current view.";

    public async Task ActivateAsync()
    {
        if (Devices.Count == 0)
        {
            await LoadDevicesAsync();
        }
        if (SelectedDevice is not null)
        {
            await RefreshDeviceAsync();
        }
    }

    /// <summary>
    /// Startup handshake, called once from App.axaml.cs when the window is created.
    /// Verifies the local BossCamService is reachable and healthy, then preloads the
    /// camera list so the status bar reflects real connection state instead of the
    /// static hint shown before first contact. When the service is down, the startup
    /// handshake first asks <see cref="_serviceStarter"/> to bring it up (systemd unit,
    /// then a direct spawn), waits for health, and only then loads devices. All failures
    /// collapse into a clear offline status; the user can retry with the Retry button
    /// or Load Devices.
    /// </summary>
    public async Task InitializeAsync() => await RunConnectionHandshakeAsync();

    /// <summary>
    /// Re-runs the full connection handshake (health probe → auto-start if needed →
    /// device load) from the Retry button, so the indicator and camera list recover
    /// when the service comes back or needs a manual kick.
    /// </summary>
    [RelayCommand]
    private async Task RetryConnectionAsync() => await RunConnectionHandshakeAsync();

    private async Task RunConnectionHandshakeAsync()
    {
        // Non-reentrant: startup (InitializeAsync) and the Retry button share this
        // handshake, and it can run for 25-45s in the systemd/spawn path. If another
        // handshake is already in flight (double-click Retry, or Retry during startup),
        // skip instead of running two concurrent start attempts that could spawn a
        // duplicate service instance or let a poll clobber the indicator mid-handshake.
        // Deliberately no StatusText write here: the in-flight handshake writes its
        // final status inside its try before the flag is cleared in finally, so a hint
        // written now could land after that final write and linger.
        if (Interlocked.CompareExchange(ref _handshakeInProgress, 1, 0) != 0)
        {
            return;
        }

        // Capture the token before the first await: a CancellationToken is an immutable
        // struct that stays valid after its source is disposed. Reading .Token later
        // (on the post-await continuation) could throw ObjectDisposedException if the
        // window closed while the health probe was in flight.
        var startupToken = _startupCts.Token;
        try
        {
            if (!IsHealthy(await _api.GetHealthAsync()))
            {
                ConnectionStatus = ServiceConnectionStatus.Starting;
                StatusText = "BossCamService offline at http://127.0.0.1:5317 \u2014 starting it\u2026";
                var started = await _serviceStarter.TryStartAsync(
                    async () => IsHealthy(await _api.GetHealthAsync()),
                    startupToken);
                if (!started)
                {
                    ConnectionStatus = ServiceConnectionStatus.Offline;
                    StatusText =
                        "BossCamService offline at http://127.0.0.1:5317 \u2014 could not be started " +
                        "automatically. Start it manually (systemctl start bosscam) or retry.";
                    return;
                }
            }

            ConnectionStatus = ServiceConnectionStatus.Online;
            if (await TryLoadDevicesAsync())
            {
                StatusText = $"Connected to BossCamService \u2014 {Devices.Count} camera(s)";
            }
            // On failure TryLoadDevicesAsync already surfaced "Failed to load devices: …"
            // and the handshake leaves that visible rather than masking it.
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            // App is shutting down — the window is closing, do not overwrite the status.
        }
        catch (Exception ex)
        {
            ConnectionStatus = ServiceConnectionStatus.Offline;
            StatusText = $"BossCamService offline at http://127.0.0.1:5317 \u2014 {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _handshakeInProgress, 0);
        }
    }

    /// <summary>
    /// Periodic /api/health probe backing the connection indicator. Reflects the live
    /// state but never auto-starts the service (that is the Retry button's job) and
    /// never clobbers an in-flight handshake (its own status updates win). Best-effort:
    /// probe failures just mark the service offline.
    /// </summary>
    internal async Task PollHealthAsync()
    {
        // Read the flag once: a handshake running concurrently owns the indicator.
        var handshakeInProgress = Volatile.Read(ref _handshakeInProgress) != 0;
        try
        {
            var healthy = IsHealthy(await _api.GetHealthAsync());
            if (!handshakeInProgress)
            {
                ConnectionStatus = healthy
                    ? ServiceConnectionStatus.Online
                    : ServiceConnectionStatus.Offline;
            }
        }
        catch (Exception ex)
        {
            if (!handshakeInProgress)
            {
                ConnectionStatus = ServiceConnectionStatus.Offline;
            }
            _logger.LogDebug(ex, "Health poll failed");
        }
    }

    /// <summary>True when /api/health reported status "ok".</summary>
    internal static bool IsHealthy(JsonElement? health)
        => health is { } h
           && h.ValueKind == JsonValueKind.Object
           && h.TryGetProperty("status", out var status)
           && status.ValueKind == JsonValueKind.String
           && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);

    // ── Shared device list / selection ────────────────────────────

    [ObservableProperty]
    private ObservableCollection<DeviceIdentity> _devices = [];

    [ObservableProperty]
    private DeviceIdentity? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "Connecting to BossCamService at http://127.0.0.1:5317\u2026";

    // ── Starred (pinned-to-landing) cameras ───────────────────────
    // Server-side authoritative set (mirrors the web SPA): stars added on the
    // desktop app appear on the web landing and vice-versa.

    [ObservableProperty]
    private IReadOnlyCollection<Guid> _starredDeviceIds = [];

    /// <summary>True when the landing board shows starred cameras only (all when none are starred).</summary>
    [ObservableProperty]
    private bool _starredOnly = true;

    [ObservableProperty]
    private ObservableCollection<BoardTileViewModel> _boardTiles = [];

    /// <summary>Raised whenever the starred set changes so tiles can refresh their glyph.</summary>
    internal event Action? StarsChanged;

    /// <summary>
    /// Pins whose server save failed (offline-tolerant). Only these are re-applied on the
    /// next authoritative load — pins the user removed elsewhere are never resurrected.
    /// </summary>
    private readonly HashSet<Guid> _unsyncedStars = [];

    public bool IsStarred(Guid id) => StarredDeviceIds.Contains(id);

    /// <summary>Star count shown in the Live View toolbar chip.</summary>
    public int StarredCount => StarredDeviceIds.Count;

    /// <summary>Label for the landing-board filter chip (starred-only vs all cameras).</summary>
    public string StarredFilterText => StarredOnly
        ? (StarredCount > 0 ? $"⭐ Starred ({StarredCount})" : "⭐ Starred (0)")
        : "All cameras";

    /// <summary>Hint shown under the landing board when it is empty.</summary>
    public string StarredBoardHint => Devices.Count == 0
        ? "No cameras registered yet — use 📡 Load Devices or the Devices section to add cameras."
        : (StarredOnly && StarredCount == 0
            ? "No starred cameras yet — click ☆ on a camera below (or in the camera list) to pin it to this landing board."
            : string.Empty);

    /// <summary>
    /// Equal-size landing-board column count so every starred camera occupies exactly
    /// the same cell and the whole board fills the available space (no fixed-size tiles,
    /// no wasted room). Kept deliberately small enough that tiles stay watchable on the
    /// default 1280px window: 1–3 cameras get one row each, 4 gets 2×2, more wrap in 3–4.
    /// </summary>
    public int BoardColumns
    {
        get
        {
            var count = BoardTiles.Count;
            return count switch
            {
                <= 1 => 1,
                2 => 2,
                3 => 3,
                4 => 2,
                <= 9 => 3,
                _ => 4
            };
        }
    }


    partial void OnStarredDeviceIdsChanged(IReadOnlyCollection<Guid> value)
    {
        StarsChanged?.Invoke();
        OnPropertyChanged(nameof(StarredCount));
        OnPropertyChanged(nameof(StarredFilterText));
        RebuildBoardAsync();
    }

    partial void OnStarredOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(StarredFilterText));
        RebuildBoardAsync();
    }

    partial void OnDevicesChanged(ObservableCollection<DeviceIdentity> value)
    {
        OnPropertyChanged(nameof(StarredCount));
        OnPropertyChanged(nameof(StarredBoardHint));
        RebuildBoardAsync();
    }

    partial void OnBoardTilesChanged(ObservableCollection<BoardTileViewModel> value)
        => OnPropertyChanged(nameof(BoardColumns));

    /// <summary>Toggles the landing board between starred-only and every camera.</summary>
    [RelayCommand]
    private void ToggleStarredOnly() => StarredOnly = !StarredOnly;

    /// <summary>
    /// Toggles the hollow/gold star for a camera. Optimistic local update first
    /// (instant UI), then persists server-side; a failed save keeps the local pin
    /// (offline-tolerant, like the SPA) and reports it in the status bar.
    /// </summary>
    [RelayCommand]
    private async Task ToggleStarAsync(DeviceIdentity device)
    {
        if (device is null) return;
        var target = !IsStarred(device.Id);
        StarredDeviceIds = target
            ? StarredDeviceIds.Append(device.Id).Distinct().ToList()
            : StarredDeviceIds.Where(id => id != device.Id).ToList();
        StatusText = target
            ? $"⭐ {device.DisplayName} pinned to the landing board"
            : $"☆ {device.DisplayName} unpinned from the landing board";
        try
        {
            await _api.SetDeviceStarredAsync(device.Id, target);
            _unsyncedStars.Remove(device.Id);
        }
        catch (Exception ex)
        {
            if (target)
            {
                _unsyncedStars.Add(device.Id);
            }
            StatusText = $"Star kept locally (offline): {ex.Message}";
        }
    }

    /// <summary>
    /// Rebuilds the landing board tiles from the current device list. Shows starred
    /// cameras when any are starred and StarredOnly is on; otherwise every camera
    /// (so the board is never empty — same fallback as the SPA).
    /// </summary>
    internal void RebuildBoardAsync()
    {
        foreach (var tile in BoardTiles)
        {
            tile.Dispose();
        }
        var source = Devices.ToList();
        var starred = source.Where(d => IsStarred(d.Id)).ToList();
        var shown = StarredOnly && starred.Count > 0 ? starred : source;
        BoardTiles = new ObservableCollection<BoardTileViewModel>(
            shown.Select(d => new BoardTileViewModel(_api, this, d)));
    }

    /// <summary>Loads the authoritative starred set from the service (startup handshake).</summary>
    private async Task LoadStarsAsync()
    {
        try
        {
            var serverSet = (await _api.GetStarredDeviceIdsAsync()).ToList();
            // Re-apply only pins whose save previously failed (offline-tolerant): never
            // resurrect stars the user removed elsewhere. A confirmed re-persist clears the
            // unsynced marker; a failed one keeps it for the next reload.
            foreach (var id in _unsyncedStars.Where(id => !serverSet.Contains(id)).ToList())
            {
                serverSet.Add(id);
                try
                {
                    await _api.SetDeviceStarredAsync(id, true);
                    _unsyncedStars.Remove(id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Re-persisting offline star {DeviceId} failed; keeping local pin", id);
                }
            }
            StarredDeviceIds = serverSet;
        }
        catch (Exception ex)
        {
            // Offline-tolerant: keep whatever the local cache had (empty on first run).
            _logger.LogDebug(ex, "Starred set load failed");
        }
    }

    /// <summary>
    /// Banner tile → management section mapping. Tile names (Display/Audio/Network/…)
    /// intentionally differ from section titles, so each tile resolves to the section
    /// that actually holds its controls; unknown tiles fall back to the literal title.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FullscreenTileSectionMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Display"] = "Features",      // image/display controls live in the Features section
            ["Audio"] = "Features",        // audio output options share the control-point surface
            ["Network"] = "Connectivity",  // AP / networking capabilities
            ["Hotspot"] = "Connectivity",  // hotspot daisy-chain lives with the wireless surface
            ["Features"] = "Features",
            ["Settings"] = "Features",
            ["Record"] = "Recordings",
            ["Advanced"] = "Diagnostics",
            ["Firmware"] = "Firmware",
            ["Recovery"] = "Devices"
        };

    /// <summary>
    /// Opens the immersive fullscreen camera view for the given camera. The fullscreen
    /// view owns its own HD-main decode loop, so it is independent of the shell's
    /// selected-camera loop; it also receives the section-navigation callback so its
    /// option tiles can jump to the matching management section (Features/Record/…).
    /// </summary>
    [RelayCommand]
    private void OpenFullscreen(DeviceIdentity device)
    {
        if (device is null) return;
        SelectedDevice = device;
        var fullscreen = new FullscreenCameraViewModel(_api, device, _api.LanToken);
        var window = new Views.FullscreenCameraWindow
        {
            DataContext = fullscreen
        };
        fullscreen.RequestOpenSection += section =>
        {
            var title = FullscreenTileSectionMap.TryGetValue(section, out var mapped)
                ? mapped
                : section;
            var target = Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                SelectedSection = target;
            }
            // Dismiss the Topmost fullscreen window so the user actually lands in the
            // section instead of navigating invisibly behind the video (the window
            // subscribes RequestClose → Close via the VM's ExitFullscreen command).
            fullscreen.ExitFullscreenCommand.Execute(null);
        };
        window.Show();
    }

    // ── Persistent connection indicator ────────────────────────────

    [ObservableProperty]
    private ServiceConnectionStatus _connectionStatus = ServiceConnectionStatus.Starting;

    [ObservableProperty]
    private string _connectionStatusText = "Starting\u2026";

    partial void OnConnectionStatusChanged(ServiceConnectionStatus value)
    {
        ConnectionStatusText = value switch
        {
            ServiceConnectionStatus.Online => "Service online",
            ServiceConnectionStatus.Starting => "Starting\u2026",
            _ => "Service offline"
        };
    }

    [ObservableProperty]
    private string _deviceInfoText = string.Empty;

    [ObservableProperty]
    private Bitmap? _liveFrame;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private WriteableBitmap? _liveVideoFrame;

    [ObservableProperty]
    private string _livePlaybackStatus = "Not started";

    partial void OnSelectedDeviceChanged(DeviceIdentity? value)
    {
        if (value is not null)
        {
            _ = RefreshDeviceAsync();
        }

        // Keep board tiles' selection highlight in sync.
        foreach (var tile in BoardTiles)
        {
            tile.RefreshSelectionState();
        }

        // Keep the Features section in sync with the camera being managed.
        _ = FeaturesSection.DeviceChangedAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync() => await TryLoadDevicesAsync();

    /// <summary>
    /// Fetches the camera list into <see cref="Devices"/> and updates the status bar.
    /// Returns true on success so the startup handshake can distinguish "connected but
    /// camera list failed" from a healthy load without parsing status strings.
    /// </summary>
    private async Task<bool> TryLoadDevicesAsync()
    {
        try
        {
            var devices = await _api.GetDevicesAsync();
            Devices = new ObservableCollection<DeviceIdentity>(devices);
            // Authoritative starred set from the service (mirrors the SPA). Best-effort:
            // an offline load keeps the previous local set and the board still shows all.
            await LoadStarsAsync();
            RebuildBoardAsync();
            // Starred cameras auto-load on the landing page: select the first one so its
            // HD main stream starts with no further interaction (fallback: first camera).
            if (SelectedDevice is null && devices.Count > 0)
            {
                SelectedDevice = devices.FirstOrDefault(d => IsStarred(d.Id)) ?? devices[0];
            }
            StatusText = $"Loaded {devices.Count} device(s)";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load devices: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private async Task RefreshDeviceAsync()
    {
        if (SelectedDevice is null) return;

        var id = SelectedDevice.Id;
        DeviceInfoText = $"Model: {SelectedDevice.HardwareModel ?? "\u2014"}\n"
            + $"IP: {SelectedDevice.IpAddress ?? "\u2014"}\n"
            + $"Firmware: {SelectedDevice.FirmwareVersion ?? "\u2014"}\n"
            + $"Type: {SelectedDevice.DeviceType ?? "\u2014"}";
        IsLive = true;

        try
        {
            var info = await _api.GetLiveInfoAsync(id);
            if (info.HasValue && info.Value.TryGetProperty("mainRtsp", out var main))
            {
                DeviceInfoText += $"\nMain RTSP: {main}";
            }
        }
        catch (Exception ex)
        {
            // Optional enrichment only — GetLiveInfoAsync already returns null on failure,
            // so the live view must not break if live-info is temporarily unavailable. The
            // Debug log keeps transient failures traceable without spamming the 2s poll loop.
            _logger.LogDebug(ex, "Live-info enrichment failed for device {DeviceId}", id);
        }

        _liveTimer?.Dispose();
        _liveTimer = new Timer(async _ =>
        {
            var bytes = await _api.GetSnapshotAsync(id);
            if (bytes is { Length: > 100 })
            {
                using var ms = new MemoryStream(bytes);
                LiveFrame = new Bitmap(ms);
            }
        }, null, 0, 2000);

        await StartLiveVideoAsync(id);
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (SelectedDevice is null) return;
        try
        {
            var saved = await _api.SaveSnapshotAsync(SelectedDevice.Id);
            StatusText = saved ? "Snapshot saved" : "Snapshot failed";
        }
        catch (Exception ex)
        {
            StatusText = $"Snapshot failed: {ex.Message}";
        }
    }

    private async Task StartLiveVideoAsync(Guid deviceId)
    {
        await StopLiveVideoAsync();
        // HD main is the always-preferred stream (mirrors the SPA's default). The
        // backend negotiates down to sub/MJPEG/snapshot only when main is unavailable.
        var manifest = await _api.GetLiveManifestAsync(deviceId, "main");
        var streamUrls = manifest is null ? [] : SelectDesktopStreamUrls(manifest);
        if (streamUrls.Count == 0)
        {
            LivePlaybackStatus = "Live manifest unavailable; snapshot view remains active.";
            return;
        }

        var cts = new CancellationTokenSource();
        _liveVideoCts = cts;
        var lanToken = _api.LanToken;
        _liveVideoTask = Task.Run(() => RunLiveVideoLoopAsync(streamUrls, lanToken, cts.Token));
    }

    internal static IReadOnlyList<string> SelectDesktopStreamUrls(LiveMediaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Follow the backend's ordered negotiation result. The old "first non-empty URL"
        // behavior could launch fMP4 even when RTSP had already degraded to MJPEG/snapshot.
        var modes = new[] { manifest.PreferredMode }
            .Concat(manifest.FallbackModes)
            .Distinct()
            .ToList();
        var urls = new List<string>();
        foreach (var mode in modes)
        {
            var url = mode switch
            {
                LiveMediaModeContract.H264Fmp4 => manifest.H264Fmp4Url,
                LiveMediaModeContract.H264MpegTs => manifest.MpegTsUrl,
                LiveMediaModeContract.Mjpeg => manifest.MjpegUrl,
                LiveMediaModeContract.Snapshot when manifest.SnapshotAvailable => manifest.SnapshotUrl,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        // Compatibility with older manifests that omitted FallbackModes.
        foreach (var url in new[] { manifest.H264Fmp4Url, manifest.MpegTsUrl, manifest.MjpegUrl,
                                    manifest.SnapshotAvailable ? manifest.SnapshotUrl : string.Empty })
        {
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }
        return urls;
    }

    internal static string? SelectDesktopStreamUrl(LiveMediaManifest manifest)
        => SelectDesktopStreamUrls(manifest).FirstOrDefault();

    internal static int NextDesktopStreamIndex(int currentIndex, int streamCount)
        => streamCount <= 0 ? 0 : (currentIndex + 1) % streamCount;

    private async Task RunLiveVideoLoopAsync(IReadOnlyList<string> streamUrls, string? lanToken, CancellationToken cancellationToken)
    {
        var streamIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                var streamUrl = streamUrls[streamIndex];
                LivePlaybackStatus = $"Connecting compatibility stream {streamIndex + 1}/{streamUrls.Count}…";
                var ffmpeg = ResolveFfmpegPath();
                if (ffmpeg is null)
                {
                    LivePlaybackStatus = "FFmpeg unavailable; snapshot view remains active.";
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in BuildLiveVideoFfmpegArguments(streamUrl, lanToken))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg failed to start.");
                }
                // stderr is redirected; drain it continuously or repeated camera/network
                // errors can fill the OS pipe and stall ffmpeg's stdout playback stream.
                _ = Task.Run(async () =>
                {
                    try { await process.StandardError.ReadToEndAsync(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        _logger.LogDebug(ex, "FFmpeg stderr drain ended");
                    }
                });
                _liveVideoProcess = process;
                LivePlaybackStatus = $"Playing compatibility stream {streamIndex + 1}/{streamUrls.Count}";

                const int width = 960;
                const int height = 540;
                var frameSize = width * height * 4;
                var buffer = new byte[frameSize];
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(process.StandardOutput.BaseStream, buffer, cancellationToken))
                    {
                        break;
                    }

                    var frame = buffer.ToArray();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LiveVideoFrame ??= new WriteableBitmap(
                            new global::Avalonia.PixelSize(width, height),
                            new global::Avalonia.Vector(96, 96),
                            global::Avalonia.Platform.PixelFormat.Bgra8888,
                            global::Avalonia.Platform.AlphaFormat.Opaque);
                        using var locked = LiveVideoFrame.Lock();
                        System.Runtime.InteropServices.Marshal.Copy(frame, 0, locked.Address, frame.Length);
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LivePlaybackStatus = $"Playback reconnecting: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_liveVideoProcess, process))
                {
                    _liveVideoProcess = null;
                }
                await StopProcessSafelyAsync(process);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                // Move through the backend-negotiated representations after a decoder or
                // transport failure instead of retrying a dead fMP4 URL forever.
                streamIndex = NextDesktopStreamIndex(streamIndex, streamUrls.Count);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Reads a full rawvideo frame, bounding each read with a stall timeout. A silent RTSP
    /// stall (5523-W drops media but keeps the socket open) otherwise blocks ReadAsync forever
    /// and freezes the last frame on screen for minutes. A timed-out read returns false so the
    /// caller advances to the next negotiated representation and reconnects.
    /// </summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), readTimeout.Token);
                if (read == 0) return false;
                offset += read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false; // stalled — fail over to the next stream representation
            }
        }
        return true;
    }

    internal static IReadOnlyList<string> BuildLiveVideoFfmpegArguments(string streamUrl, string? lanToken)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error"
        };
        // ffmpeg's HTTP input accepts custom headers. Keep the token out of the URL and
        // reject line breaks so a malformed token cannot create additional HTTP headers.
        if (!string.IsNullOrWhiteSpace(lanToken)
            && !lanToken.Contains('\r', StringComparison.Ordinal)
            && !lanToken.Contains('\n', StringComparison.Ordinal))
        {
            args.Add("-headers");
            args.Add($"X-LAN-Token: {lanToken}\r\n");
        }
        args.AddRange([
            "-i", streamUrl,
            "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ]);
        return args;
    }

    private static string? ResolveFfmpegPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null;
    }

    private async Task StopLiveVideoAsync()
    {
        var cts = Interlocked.Exchange(ref _liveVideoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _liveVideoProcess, null);
        await StopProcessSafelyAsync(process);

        var task = Interlocked.Exchange(ref _liveVideoTask, null);
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // The process was already terminated; do not block navigation forever on a
                // decoder that ignored cancellation.
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live video task ended during stop");
            }
        }
        cts?.Dispose();
    }

    private static async Task StopProcessSafelyAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch (ObjectDisposedException)
        {
            // Another teardown path already disposed the process.
        }
        catch (InvalidOperationException)
        {
            // The process may have exited or been disposed concurrently during a device switch.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS can report a transient teardown race; cleanup remains best effort.
        }
        catch (OperationCanceledException)
        {
            // Bounded cleanup: never hang the UI on a broken decoder process.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Cancel any in-flight startup handshake so a spawned service is torn down
        // promptly when the window closes instead of waiting out its health poll.
        // The CTS stays non-nullable: cancellation must always reach the starter's
        // ThrowIfCancellationRequested, and reading IsCancellationRequested remains
        // safe after Dispose.
        _startupCts.Cancel();
        _serviceStarter.Dispose();
        _startupCts.Dispose();

        _healthPollTimer?.Dispose();
        _liveTimer?.Dispose();
        foreach (var tile in BoardTiles)
        {
            tile.Dispose();
        }
        BoardTiles.Clear();

        // Do not synchronously join the decoder task here: it can be waiting for the
        // Avalonia UI dispatcher while Dispose is called on that same UI thread. Cancel and
        // terminate immediately, then let the task's finally block finish asynchronously.
        var cts = Interlocked.Exchange(ref _liveVideoCts, null);
        cts?.Cancel();
        var process = Interlocked.Exchange(ref _liveVideoProcess, null);
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "Live video process was already stopped during dispose");
            }
        }

        var task = Interlocked.Exchange(ref _liveVideoTask, null);
        if (task is not null || cts is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (task is not null) await task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    _logger.LogDebug(ex, "Live video task cleanup completed asynchronously");
                }
                finally
                {
                    cts?.Dispose();
                    process?.Dispose();
                }
            });
        }
        else
        {
            process?.Dispose();
        }

        _api.Dispose();
    }
}
