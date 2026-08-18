using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
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
    private Timer? _healthPollTimer;
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
        "Live View is the landing board: every starred camera auto-loads and streams here " +
        "at the camera's native rate, each tile the same size. Double-click a tile for " +
        "fullscreen; click ☆ to pin or unpin. The footer shows the negotiated stream mode " +
        "and the selected camera's identity.";

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
            // Connection refusal, timeout, and malformed health responses are all the same
            // startup condition: the service is not usable yet. Do not let an exception
            // bypass the auto-start path and strand the GUI in a false offline state.
            var healthy = false;
            try
            {
                healthy = IsHealthy(await _api.GetHealthAsync());
            }
            catch (Exception ex) when (!startupToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Initial BossCamService health probe failed; attempting startup");
            }

            if (!healthy)
            {
                ConnectionStatus = ServiceConnectionStatus.Starting;
                StatusText = "BossCamService offline at http://127.0.0.1:5317 \u2014 starting it\u2026";
                var started = await _serviceStarter.TryStartAsync(
                    async () =>
                    {
                        try
                        {
                            return IsHealthy(await _api.GetHealthAsync());
                        }
                        catch (Exception ex) when (!startupToken.IsCancellationRequested)
                        {
                            _logger.LogDebug(ex, "BossCamService health probe while starting failed");
                            return false;
                        }
                    },
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
    /// Opens the immersive fullscreen camera view for the given camera. The fullscreen
    /// view owns its own HD-main decode loop, independent of the shell's selected-camera
    /// loop, and embeds the real camera-options surface (Features control points) so its
    /// banner tiles open in-window menus instead of navigating away or closing the video.
    /// </summary>
    [RelayCommand]
    private void OpenFullscreen(DeviceIdentity device)
    {
        if (device is null) return;
        SelectedDevice = device;
        var fullscreen = new FullscreenCameraViewModel(_api, device, _api.LanToken, shell: this);
        var window = new Views.FullscreenCameraWindow
        {
            DataContext = fullscreen
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
    private bool _isLive;

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
            // so the live view must not break if live-info is temporarily unavailable.
            _logger.LogDebug(ex, "Live-info enrichment failed for device {DeviceId}", id);
        }

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
        // HD main is the always-preferred stream (mirrors the SPA's default). The landing
        // board tiles each run their own decode loop at 960x540, so the shell must NOT
        // launch a second hidden 1280x720 decode — it would only steal CPU from the feeds
        // actually on screen. The shell negotiates the manifest so the footer reports the
        // live mode without burning a decoder.
        var manifest = await _api.GetLiveManifestAsync(deviceId, "main");
        if (manifest is null || SelectDesktopStreamUrls(manifest).Count == 0)
        {
            LivePlaybackStatus = "Live manifest unavailable — board tiles keep streaming.";
            return;
        }

        LivePlaybackStatus =
            $"Negotiated {manifest.PreferredMode} — board tiles stream at the camera's native rate";
    }

    internal static IReadOnlyList<string> SelectDesktopStreamUrls(
        LiveMediaManifest manifest,
        bool preferDirectRtsp = false,
        bool directRtspFirst = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Follow the backend's ordered negotiation result. The old "first non-empty URL"
        // behavior could launch fMP4 even when RTSP had already degraded to MJPEG/snapshot.
        // A non-null FallbackModes collection is authoritative. Only manifests from
        // older services that omitted the field entirely may use the legacy URL sweep
        // below; otherwise an explicitly unsupported URL must not be resurrected after
        // the direct source fails.
        var hasAdvertisedFallbacks = manifest.FallbackModes is not null;
        // A non-null fallback list is authoritative. Only older manifests that omitted
        // the field entirely may use the legacy URL sweep below.
        var modes = new[] { manifest.PreferredMode }
            .Concat(manifest.FallbackModes ?? [])
            .Distinct()
            .ToList();
        var urls = new List<string>();
        // The shared native HTTP representation is always the primary desktop path. The
        // service owns one RTSP session per camera and fans it out to all desktop/browser
        // viewers; opening direct RTSP from every tile/fullscreen window can exhaust the
        // 5523-W session budget when recording is active and was confirmed to starve
        // 10.0.0.169. Direct RTSP is retained only as an emergency fallback, appended
        // after every negotiated server-owned continuous representation.
        foreach (var mode in modes)
        {
            var url = mode switch
            {
                // Direct HEVC copy is the native desktop path (local ffmpeg decodes HEVC);
                // H.264 fMP4/TS are the browser-compatibility transcodes. The backend only
                // advertises HevcFmp4 for client=native manifests.
                LiveMediaModeContract.HevcFmp4 => manifest.HevcFmp4Url,
                LiveMediaModeContract.H264Fmp4 => manifest.H264Fmp4Url,
                LiveMediaModeContract.H264MpegTs => manifest.MpegTsUrl,
                // The desktop decoder must not use the MJPEG compatibility endpoint. On
                // 5523-W this endpoint is backed by a snapshot pump, so treating it as a
                // continuous source produces exactly the one-frame-every-few-seconds
                // slideshow reported in the incident. It remains available to browser
                // clients through their own negotiated player path.
                LiveMediaModeContract.Mjpeg => null,
                // Snapshot is a single JPEG, not a continuous media source. It is handled
                // by BoardTileViewModel's snapshot watchdog and must never be handed to
                // ffmpeg's continuous rawvideo loop (which would yield one frame per restart).
                LiveMediaModeContract.Snapshot => null,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        // Compatibility with older manifests that omitted FallbackModes. Only continuous
        // media URLs belong in this decoder ladder; snapshot remains a separate watchdog
        // path so it cannot produce the observed one-frame-every-few-seconds slideshow.
        if (!hasAdvertisedFallbacks)
        {
            foreach (var url in new[] { manifest.HevcFmp4Url, manifest.H264Fmp4Url, manifest.MpegTsUrl })
            {
                if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                {
                    urls.Add(url);
                }
            }
        }

        // Direct RTSP: as an opt-in emergency fallback (appended last) while the
        // service-owned representations are merely degraded, or — after the shared
        // session ladder has failed repeatedly — as the FIRST rung, so a stalled
        // service session cannot strand a camera that still answers direct RTSP
        // (the recorder demonstrably streams these cameras direct RTSP fine).
        if (!string.IsNullOrWhiteSpace(manifest.RtspUrl)
            && !urls.Contains(manifest.RtspUrl, StringComparer.OrdinalIgnoreCase))
        {
            if (directRtspFirst)
            {
                urls.Insert(0, manifest.RtspUrl);
            }
            else if (preferDirectRtsp)
            {
                urls.Add(manifest.RtspUrl);
            }
        }
        return urls;
    }

    internal static string? SelectDesktopStreamUrl(LiveMediaManifest manifest, bool preferDirectRtsp = false)
        => SelectDesktopStreamUrls(manifest, preferDirectRtsp).FirstOrDefault();

    internal static int NextDesktopStreamIndex(int currentIndex, int streamCount)
        => streamCount <= 0 ? 0 : (currentIndex + 1) % streamCount;

    /// <summary>
    /// A hardware decoder that exits before delivering a frame is commonly a driver/
    /// hwdownload negotiation failure, not a dead camera. Retry the same representation
    /// once with software decoding before advancing the transport ladder. A stream that
    /// remains alive but produces no bytes is treated as a transport stall instead, so
    /// it advances normally rather than being misclassified as a hardware failure.
    /// </summary>
    internal static bool ShouldRetryWithSoftwareDecoder(
        bool hardwareAttempt,
        bool processExited,
        bool renderedFrame,
        TimeSpan attemptDuration)
        => hardwareAttempt
           && processExited
           && !renderedFrame
           && attemptDuration < TimeSpan.FromSeconds(5);

    /// <summary>Limits manifest acquisition retries so a dead service cannot create a request storm.</summary>
    internal static bool ShouldRetryLiveManifest(int attempt, bool disposed)
        => !disposed && attempt < 3;

    /// <summary>
    /// Capped exponential reconnect delay. A dead camera/service must not create an
    /// unbounded ffmpeg/HTTP retry storm, while a transient drop still recovers quickly.
    /// </summary>
    internal static TimeSpan GetReconnectDelay(int failureIndex)
    {
        var exponent = Math.Clamp(failureIndex, 0, 5);
        var milliseconds = 250L * (1L << exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, 5000L));
    }

    /// <summary>
    /// Identifies an ffmpeg process that is alive but has not delivered its first complete
    /// frame within the bounded startup window. The caller tears down that process and lets
    /// the normal transport ladder retry; this prevents a blank fullscreen window from
    /// remaining in a misleading "Playing" state forever.
    /// </summary>
    internal static bool ShouldReportNoFirstFrame(
        bool processActive,
        bool renderedFrame,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        TimeSpan timeout)
        => processActive
           && !renderedFrame
           && startedAt != default
           && now - startedAt > timeout;

    /// <summary>
    /// Reads a full rawvideo frame, bounding each read with a stall timeout. A silent RTSP
    /// stall (5523-W drops media but keeps the socket open) otherwise blocks ReadAsync forever
    /// and freezes the last frame on screen for minutes. A timed-out read returns false so the
    /// caller advances to the next negotiated representation and reconnects.
    /// </summary>
    /// <summary>
    /// Copies packed BGRA decoder output into an Avalonia framebuffer without assuming
    /// that the destination rows are tightly packed. Avalonia may pad RowBytes for the
    /// platform framebuffer; copying one row at a time preserves image alignment and
    /// prevents writes from crossing row boundaries.
    /// </summary>
    internal static void CopyBgraFrameToFramebuffer(
        byte[] frame,
        IntPtr destination,
        int destinationRowBytes,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var sourceRowBytes = checked(width * 4);
        var requiredBytes = checked(sourceRowBytes * height);
        if (frame.Length < requiredBytes)
        {
            throw new ArgumentException("The BGRA frame is smaller than the requested surface.", nameof(frame));
        }
        if (destinationRowBytes < sourceRowBytes)
        {
            throw new ArgumentException("The framebuffer row stride is smaller than the packed BGRA row.", nameof(destinationRowBytes));
        }

        for (var row = 0; row < height; row++)
        {
            global::System.Runtime.InteropServices.Marshal.Copy(
                frame,
                row * sourceRowBytes,
                IntPtr.Add(destination, row * destinationRowBytes),
                sourceRowBytes);
        }
    }

    /// <summary>Returns whether a decoded frame should be discarded when the UI queue is full.</summary>
    internal static bool ShouldDiscardDecodedFrame(bool hasFreeRenderSlot) => !hasFreeRenderSlot;

    /// <summary>
    /// Rejects a UI callback queued by an older decoder generation. A reconnect can leave one
    /// render-priority callback in Avalonia's dispatcher after its ffmpeg process is gone;
    /// comparing generations prevents that stale frame from replacing the first fresh frame
    /// from the new source.
    /// </summary>
    internal static bool ShouldRenderFrame(bool disposed, int frameGeneration, int currentGeneration)
        => !disposed && frameGeneration == currentGeneration;

    internal static async Task<bool> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0)
        {
            return true;
        }

        // The deadline belongs to the complete frame, not each partial read. A decoder
        // emitting one byte periodically must not keep a tile alive forever while no
        // complete frame can reach the renderer.
        var frameTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (frameTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The frame timeout must be positive.");
        }

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(frameTimeout);
        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), readTimeout.Token);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // stalled — fail over to the next stream representation
        }
    }

    /// <summary>
    /// Builds the ffmpeg args for a desktop rawvideo decode. Each surface decodes at its
    /// own target resolution (board tiles 960x540, fullscreen 1920x1080) so CPU stays
    /// proportional to what is actually displayed. -threads 0 lets ffmpeg use every core
    /// for HEVC software decode (the direct native path), which is the single biggest
    /// decoder bottleneck on this machine.
    /// </summary>
    internal static IReadOnlyList<string> BuildLiveVideoFfmpegArguments(
        string streamUrl,
        string? lanToken,
        int width = 960,
        int height = 540,
        bool? useHardwareAcceleration = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error"
        };
        // ffmpeg's HTTP input accepts custom headers. Keep the token out of the URL and
        // reject line breaks so a malformed token cannot create additional HTTP headers.
        // X-LAN-Token is an HTTP header. Passing -headers to the native RTSP
        // demuxer makes ffmpeg reject the direct camera path before decoding starts,
        // which then falls through to the slower fragment path.
        if (streamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || streamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(lanToken)
                && !lanToken.Contains('\r', StringComparison.Ordinal)
                && !lanToken.Contains('\n', StringComparison.Ordinal))
            {
                args.Add("-headers");
                args.Add($"X-LAN-Token: {lanToken}\r\n");
            }
            // The service's shared HTTP fMP4 session can stall mid-stream (a half-open
            // connection after the camera Wi-Fi blips). ffmpeg's HTTP demuxer has no
            // built-in read deadline, so a stalled session would hold the decode loop
            // open forever. rw_timeout bounds the socket read/write so the 5-second
            // frame watchdog gets a prompt EOF/error and the reconnect ladder advances
            // instead of freezing the tile on its last frame.
            args.Add("-rw_timeout");
            args.Add("10000000"); // 10s, in microseconds
        }
        // Keep the HEVC decoder's normal reference-frame reordering. The 5523-W's
        // ordered TCP stream needs those references; low-delay decoder flags can cause
        // POC errors and collapse a 15 fps source into a slideshow.
        // Do not use nobuffer or a zero reorder queue here. The 5523-W sends HEVC
        // reference frames over ordered TCP; those latency shortcuts make ffmpeg emit
        // POC/reference errors and collapse a 15 fps source into a 1–3 fps slideshow.
        // Keep timestamp generation/corrupt-frame dropping, but let the demuxer retain
        // enough ordered packets to decode the reference chain.
        args.AddRange([
            "-fflags", "genpts+discardcorrupt",
            "-probesize", "2000000",
            "-analyzeduration", "2000000"
        ]);
        // Direct-RTSP input uses TCP transport, a bounded delay, and a socket timeout.
        // Keep ffmpeg's packet reorder queue enabled: ordered HEVC reference frames need it;
        // a silent media stall still aborts through the socket timeout instead of freezing
        // the last frame on screen.
        if (streamUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange([
                "-rtsp_transport", "tcp",
                "-rtsp_flags", "prefer_tcp",
                "-max_delay", "500000",
                "-timeout", "10000000"
            ]);
        }
        // The renderer consumes a fixed-size CPU BGRA frame. Automatic hardware decode is
        // intentionally opt-in: on Linux, ffmpeg can select a decoder surface that is not
        // downloaded cleanly through scale/pad, leaving the raw pipe short or stalled and
        // freezing every tile. Operators can opt in with BOSSCAM_ENABLE_HWACCEL=true after
        // validating the host driver; a failed explicit attempt still falls back to software.
        // This is deliberately before -i so the input decoder can use the selected path;
        // the raw BGRA output remains a stable CPU-readable surface for Avalonia.
        var hardwareAcceleration = useHardwareAcceleration ?? IsHardwareAccelerationEnabled();
        if (hardwareAcceleration)
        {
            args.AddRange(["-hwaccel", "auto"]);
        }
        // Configure the video decoder before the input is opened. Keeping these as decoder
        // options (rather than output options after -i) is important for HEVC: the camera's
        // reference-frame chain must be decoded with the available workers.
        args.AddRange([
            "-threads", "0",
            "-i", streamUrl,
            "-an",
            // Passthrough frame timing: rawvideo is emitted at the decoder's native cadence —
            // no CFR duplication or drops, so motion stays as smooth as the camera's rate.
            // Preserve the camera's timestamps without CFR duplication/drop decisions.
            // fps_mode is the current ffmpeg spelling of the old -vsync 0 behavior.
            "-fps_mode", "passthrough",
            "-vf",
            $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ]);
        return args;
    }

    internal static bool IsHardwareAccelerationDisabled()
    {
        var value = Environment.GetEnvironmentVariable("BOSSCAM_DISABLE_HWACCEL");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHardwareAccelerationEnabled()
    {
        var value = Environment.GetEnvironmentVariable("BOSSCAM_ENABLE_HWACCEL");
        return !IsHardwareAccelerationDisabled()
            && (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? ResolveFfmpegPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null;
    }

    internal static async Task StopProcessSafelyAsync(Process? process)
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
        _startupCts.Cancel();
        _serviceStarter.Dispose();
        _startupCts.Dispose();
        _healthPollTimer?.Dispose();

        foreach (var tile in BoardTiles)
        {
            tile.Dispose();
        }
        BoardTiles.Clear();
        _api.Dispose();
    }
}
