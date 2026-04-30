using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BossCam.Contracts;
using WpfBinding = System.Windows.Data.Binding;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfImage = System.Windows.Controls.Image;

namespace BossCam.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource _nvrShutdown = new();
    private int _selectedNvrLayout = 4;
    private NvrStreamMode _nvrMode = NvrStreamMode.Live;
    private NvrTileViewModel? _selectedNvrTile;
    private DateTime? _playbackDate = DateTime.Today;
    private string _playbackTime = DateTime.Now.AddMinutes(-10).ToString("HH:mm:ss");
    private int _playbackWindowMinutes = 10;
    private RecordingSegment? _selectedNvrRecordingSegment;
    private string _nvrDiagnostics = "NVR idle.";

    public ObservableCollection<NvrTileViewModel> NvrTiles { get; } = [];
    public ObservableCollection<RecordingSegment> NvrRecordingSegments { get; } = [];
    public ObservableCollection<RecordingJob> NvrRecordingJobs { get; } = [];
    public ObservableCollection<VideoSourceDescriptor> NvrSources { get; } = [];
    public IReadOnlyList<int> NvrLayouts => NvrLayoutCatalog.SupportedLayouts;

    public int SelectedNvrLayout
    {
        get => _selectedNvrLayout;
        set
        {
            if (_selectedNvrLayout != value)
            {
                var oldCount = NvrLayoutCatalog.GetSlots(_selectedNvrLayout).Count;
                _selectedNvrLayout = value;
                OnPropertyChanged(nameof(SelectedNvrLayout));
                StopRemovedNvrTiles(oldCount, NvrLayoutCatalog.GetSlots(value).Count);
                ApplyNvrLayout();
            }
        }
    }

    public NvrStreamMode NvrMode
    {
        get => _nvrMode;
        set
        {
            if (_nvrMode != value)
            {
                _nvrMode = value;
                OnPropertyChanged(nameof(NvrMode));
                OnPropertyChanged(nameof(NvrModeText));
            }
        }
    }

    public string NvrModeText => $"Mode: {NvrMode}";

    public NvrTileViewModel? SelectedNvrTile
    {
        get => _selectedNvrTile;
        set
        {
            if (!Equals(_selectedNvrTile, value))
            {
                if (_selectedNvrTile is not null)
                {
                    _selectedNvrTile.IsSelected = false;
                }

                _selectedNvrTile = value;
                if (_selectedNvrTile is not null)
                {
                    _selectedNvrTile.IsSelected = true;
                }

                OnPropertyChanged(nameof(SelectedNvrTile));
                ApplyNvrLayout();
            }
        }
    }

    public DateTime? PlaybackDate
    {
        get => _playbackDate;
        set
        {
            if (_playbackDate != value)
            {
                _playbackDate = value;
                SyncStorageTimesFromPlaybackControls();
                OnPropertyChanged(nameof(PlaybackDate));
            }
        }
    }

    public string PlaybackTime
    {
        get => _playbackTime;
        set
        {
            if (_playbackTime != value)
            {
                _playbackTime = value;
                SyncStorageTimesFromPlaybackControls();
                OnPropertyChanged(nameof(PlaybackTime));
            }
        }
    }

    public int PlaybackWindowMinutes
    {
        get => _playbackWindowMinutes;
        set
        {
            var normalized = Math.Max(1, value);
            if (_playbackWindowMinutes != normalized)
            {
                _playbackWindowMinutes = normalized;
                SyncStorageTimesFromPlaybackControls();
                OnPropertyChanged(nameof(PlaybackWindowMinutes));
            }
        }
    }

    public RecordingSegment? SelectedNvrRecordingSegment
    {
        get => _selectedNvrRecordingSegment;
        set
        {
            if (!Equals(_selectedNvrRecordingSegment, value))
            {
                _selectedNvrRecordingSegment = value;
                OnPropertyChanged(nameof(SelectedNvrRecordingSegment));
            }
        }
    }

    public string NvrDiagnostics
    {
        get => _nvrDiagnostics;
        set
        {
            if (_nvrDiagnostics != value)
            {
                _nvrDiagnostics = value;
                OnPropertyChanged(nameof(NvrDiagnostics));
            }
        }
    }

    private void InitializeNvr()
    {
        for (var i = 0; i < 9; i++)
        {
            NvrTiles.Add(new NvrTileViewModel(i));
        }

        SelectedNvrTile = NvrTiles[0];
        SyncStorageTimesFromPlaybackControls();
        ApplyNvrLayout();
    }

    private void ShutdownNvr()
    {
        _nvrShutdown.Cancel();
        foreach (var tile in NvrTiles)
        {
            tile.Stop();
        }
    }

    private void ApplyNvrLayout()
    {
        if (NvrTileGrid is null)
        {
            return;
        }

        NvrTileGrid.Children.Clear();
        NvrTileGrid.RowDefinitions.Clear();
        NvrTileGrid.ColumnDefinitions.Clear();

        var (rows, columns) = NvrLayoutCatalog.GetGridSize(SelectedNvrLayout);
        for (var row = 0; row < rows; row++)
        {
            NvrTileGrid.RowDefinitions.Add(new RowDefinition());
        }

        for (var column = 0; column < columns; column++)
        {
            NvrTileGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        foreach (var slot in NvrLayoutCatalog.GetSlots(SelectedNvrLayout))
        {
            var tile = NvrTiles[slot.TileId];
            var view = BuildNvrTileView(tile);
            Grid.SetRow(view, slot.Row);
            Grid.SetColumn(view, slot.Column);
            Grid.SetRowSpan(view, slot.RowSpan);
            Grid.SetColumnSpan(view, slot.ColumnSpan);
            NvrTileGrid.Children.Add(view);
        }
    }

    private Border BuildNvrTileView(NvrTileViewModel tile)
    {
        var border = new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(WpfColor.FromRgb(5, 10, 15)),
            BorderThickness = new Thickness(tile.IsSelected ? 3 : 1),
            BorderBrush = new SolidColorBrush(tile.IsSelected ? WpfColor.FromRgb(255, 210, 122) : WpfColor.FromRgb(57, 75, 91)),
            DataContext = tile
        };
        border.MouseLeftButtonDown += (_, _) => SelectedNvrTile = tile;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { Foreground = WpfBrushes.White, FontWeight = FontWeights.SemiBold };
        title.SetBinding(TextBlock.TextProperty, new WpfBinding(nameof(NvrTileViewModel.Title)));
        grid.Children.Add(title);

        var image = new WpfImage
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        image.SetBinding(WpfImage.SourceProperty, new WpfBinding(nameof(NvrTileViewModel.FrameSource)));
        Grid.SetRow(image, 1);
        grid.Children.Add(image);

        var status = new TextBlock { Foreground = new SolidColorBrush(WpfColor.FromRgb(196, 212, 229)), TextWrapping = TextWrapping.Wrap };
        status.SetBinding(TextBlock.TextProperty, new WpfBinding(nameof(NvrTileViewModel.StatusText)));
        Grid.SetRow(status, 2);
        grid.Children.Add(status);

        border.Child = grid;
        return border;
    }

    private void StopRemovedNvrTiles(int oldCount, int newCount)
    {
        if (newCount >= oldCount)
        {
            return;
        }

        for (var i = newCount; i < oldCount && i < NvrTiles.Count; i++)
        {
            NvrTiles[i].Stop();
        }

        if (SelectedNvrTile is not null && SelectedNvrTile.TileId >= newCount)
        {
            SelectedNvrTile = NvrTiles[0];
        }
    }

    private async void NvrLiveMode_Click(object sender, RoutedEventArgs e)
    {
        NvrMode = NvrStreamMode.Live;
        await RunAsync(StartSelectedNvrLiveAsync);
    }

    private void NvrPlaybackMode_Click(object sender, RoutedEventArgs e)
    {
        NvrMode = NvrStreamMode.Playback;
    }

    private void NvrLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string raw } && int.TryParse(raw, out var layout))
        {
            SelectedNvrLayout = layout;
        }
    }

    private void AssignSelectedCameraToNvr_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is null || SelectedNvrTile is null)
        {
            NvrDiagnostics = "Select a camera and a tile first.";
            return;
        }

        SelectedNvrTile.Stop();
        SelectedNvrTile.Device = SelectedDevice;
        SelectedNvrTile.Message = "Camera assigned.";
        SelectedNvrTile.Status = NvrStreamStatus.Stopped;
        ApplyNvrLayout();
    }

    private async void StartNvrLive_Click(object sender, RoutedEventArgs e) => await RunAsync(StartSelectedNvrLiveAsync);
    private async void StopSelectedNvrTile_Click(object sender, RoutedEventArgs e) => await RunAsync(StopSelectedNvrTileAsync);
    private async void RefreshNvrSources_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshNvrSourcesAsync);
    private async void NvrFindPlayback_Click(object sender, RoutedEventArgs e) => await RunAsync(FindNvrPlaybackAsync);
    private async void NvrPlayPlayback_Click(object sender, RoutedEventArgs e) => await RunAsync(StartSelectedNvrPlaybackAsync);
    private async void RefreshNvrIndex_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshNvrIndexAsync);
    private async void RefreshNvrJobs_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshNvrJobsAsync);
    private async void NvrHousekeeping_Click(object sender, RoutedEventArgs e) => await RunAsync(RunNvrHousekeepingAsync);
    private async void NvrExportClip_Click(object sender, RoutedEventArgs e) => await RunAsync(ExportNvrClipAsync);

    private async Task StartSelectedNvrLiveAsync()
    {
        var tile = EnsureNvrTile();
        if (tile.Device is null)
        {
            if (SelectedDevice is null)
            {
                NvrDiagnostics = "Select a camera first.";
                return;
            }

            tile.Device = SelectedDevice;
        }

        NvrMode = NvrStreamMode.Live;
        tile.Mode = NvrStreamMode.Live;
        tile.Status = NvrStreamStatus.Resolving;
        tile.Message = "Resolving live source.";
        var sources = await GetAsync<List<VideoSourceDescriptor>>($"/api/devices/{tile.Device.Id}/sources") ?? [];
        ReplaceCollection(NvrSources, sources);
        var candidates = BuildLiveSourceCandidates(tile.Device, sources).ToList();
        if (candidates.Count == 0)
        {
            tile.Status = NvrStreamStatus.Failed;
            tile.Message = "No decodable live source available.";
            NvrDiagnostics = BuildMissingLiveSourceDiagnostics(tile.Device, sources);
            return;
        }

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            var failure = await TryStartTileDecodeAsync(tile, candidate.Url, NvrStreamMode.Live, candidate.Label);
            if (failure is null)
            {
                NvrDiagnostics = $"Live tile {tile.TileId + 1} playing.{Environment.NewLine}{BuildNvrSessionDiagnostics(tile, candidate.Label)}";
                return;
            }

            failures.Add(failure);
        }

        tile.Status = NvrStreamStatus.Failed;
        tile.Message = BuildLiveFailureSummary(tile.Device, failures);
        NvrDiagnostics = string.Join($"{Environment.NewLine}{Environment.NewLine}", failures);
    }

    private async Task StartSelectedNvrPlaybackAsync()
    {
        var tile = EnsureNvrTile();
        if (tile.Device is null)
        {
            tile.Device = SelectedDevice;
        }

        if (tile.Device is null)
        {
            NvrDiagnostics = "Select a playback camera first.";
            return;
        }

        NvrMode = NvrStreamMode.Playback;
        tile.Mode = NvrStreamMode.Playback;
        tile.Status = NvrStreamStatus.Resolving;
        tile.Message = "Resolving playback source.";
        var segment = SelectedNvrRecordingSegment ?? await ResolvePlaybackSegmentAsync(tile.Device.Id);
        if (segment is null)
        {
            await FindNvrPlaybackAsync();
            tile.Status = NvrStreamStatus.Failed;
            tile.Message = "No recording found for selected time.";
            NvrDiagnostics = "Playback search completed, but no indexed local recording overlaps the selected date/time window.";
            return;
        }

        if (!File.Exists(segment.FilePath))
        {
            tile.Status = NvrStreamStatus.Failed;
            tile.Message = "Playback file is missing.";
            NvrDiagnostics = $"Playback file missing for tile {tile.TileId + 1}.{Environment.NewLine}path={segment.FilePath}";
            return;
        }

        SelectedNvrRecordingSegment = segment;
        var failure = await TryStartTileDecodeAsync(tile, segment.FilePath, NvrStreamMode.Playback, "Indexed recording");
        if (failure is null)
        {
            NvrDiagnostics = $"Playback tile {tile.TileId + 1} playing.{Environment.NewLine}segment={segment.FilePath}{Environment.NewLine}{BuildNvrSessionDiagnostics(tile, "Indexed recording")}";
            return;
        }

        tile.Status = NvrStreamStatus.Failed;
        tile.Message = "Playback decode failed.";
        NvrDiagnostics = failure;
    }

    private async Task<string?> TryStartTileDecodeAsync(NvrTileViewModel tile, string source, NvrStreamMode mode, string label)
    {
        tile.Stop();
        tile.Mode = mode;
        tile.Source = source;
        tile.Status = NvrStreamStatus.Starting;
        tile.Message = $"Starting FFmpeg ({label}).";

        var session = new NvrFrameDecodeSession(tile.FrameSource);
        tile.Session = session;
        try
        {
            await session.StartAsync(source, _nvrShutdown.Token);
            tile.Status = NvrStreamStatus.Running;
            tile.Message = "Playing";
            return null;
        }
        catch (Exception ex)
        {
            tile.Session = null;
            session.Dispose();
            tile.Status = NvrStreamStatus.Failed;
            tile.Message = BuildTileFailureMessage(ex);
            return BuildNvrFailureDiagnostics(tile, label, source, session, ex);
        }
    }

    private Task StopSelectedNvrTileAsync()
    {
        EnsureNvrTile().Stop();
        NvrDiagnostics = "Selected NVR tile stopped.";
        return Task.CompletedTask;
    }

    private async Task RefreshNvrSourcesAsync()
    {
        var device = SelectedNvrTile?.Device ?? SelectedDevice;
        if (device is null)
        {
            NvrDiagnostics = "Select a camera first.";
            return;
        }

        var sources = await GetAsync<List<VideoSourceDescriptor>>($"/api/devices/{device.Id}/sources") ?? [];
        ReplaceCollection(NvrSources, sources);
        NvrDiagnostics = $"Loaded {sources.Count} source(s) for {device.DisplayName}.";
    }

    private async Task FindNvrPlaybackAsync()
    {
        var tile = EnsureNvrTile();
        var device = tile.Device ?? SelectedDevice;
        if (device is null)
        {
            NvrDiagnostics = "Select a camera first.";
            return;
        }

        SyncStorageTimesFromPlaybackControls();
        await RefreshNvrIndexAsync();
        var request = BuildPlaybackRequest();
        var result = await PostAsync<NvrPlaybackCallResult>($"/api/devices/{device.Id}/playback/find-file", request);
        NvrDiagnostics = result is null
            ? "Playback find-file request failed."
            : JsonSerializer.Serialize(new { playback = result, indexedSegments = NvrRecordingSegments.Count }, SerializerOptions);
    }

    private async Task RefreshNvrIndexAsync()
    {
        var device = SelectedNvrTile?.Device ?? SelectedDevice;
        if (device is null)
        {
            NvrDiagnostics = "Select a camera first.";
            return;
        }

        var indexed = await PostAsync<List<RecordingSegment>>($"/api/recordings/index/refresh?deviceId={device.Id}", null) ?? [];
        var begin = BuildPlaybackStartTime();
        var end = begin.AddMinutes(PlaybackWindowMinutes);
        ReplaceCollection(NvrRecordingSegments, indexed
            .Where(segment => segment.EndTime >= begin && segment.StartTime <= end)
            .OrderByDescending(static segment => segment.StartTime));
        SelectedNvrRecordingSegment = NvrRecordingSegments.FirstOrDefault();
        NvrDiagnostics = $"Indexed {indexed.Count} segment(s); {NvrRecordingSegments.Count} overlap selected time.";
    }

    private async Task RefreshNvrJobsAsync()
    {
        var jobs = await GetAsync<List<RecordingJob>>("/api/recordings/jobs") ?? [];
        ReplaceCollection(NvrRecordingJobs, jobs.OrderByDescending(static job => job.StartedAt));
        NvrDiagnostics = $"Loaded {jobs.Count} running recording job(s).";
    }

    private async Task RunNvrHousekeepingAsync()
    {
        var device = SelectedNvrTile?.Device ?? SelectedDevice;
        var suffix = device is null ? string.Empty : $"?deviceId={device.Id}";
        var result = await PostAsync<RecordingHousekeepingResult>($"/api/recordings/housekeeping{suffix}", null);
        NvrDiagnostics = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task ExportNvrClipAsync()
    {
        var device = SelectedNvrTile?.Device ?? SelectedDevice;
        if (device is null)
        {
            NvrDiagnostics = "Select a camera first.";
            return;
        }

        var begin = BuildPlaybackStartTime();
        var end = begin.AddMinutes(PlaybackWindowMinutes);
        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"BossCam_NVR_{device.Id:N}_{begin:yyyyMMdd_HHmmss}.mp4");
        var result = await PostAsync<ClipExportResult>("/api/recordings/export", new ClipExportRequest
        {
            DeviceId = device.Id,
            StartTime = begin,
            EndTime = end,
            OutputPath = output
        });
        NvrDiagnostics = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task<RecordingSegment?> ResolvePlaybackSegmentAsync(Guid deviceId)
    {
        var begin = BuildPlaybackStartTime();
        var end = begin.AddMinutes(PlaybackWindowMinutes);
        var segments = await GetAsync<List<RecordingSegment>>($"/api/recordings/index?deviceId={deviceId}&limit=500") ?? [];
        var segment = segments
            .Where(item => item.EndTime >= begin && item.StartTime <= end && File.Exists(item.FilePath))
            .OrderBy(item => Math.Abs((item.StartTime - begin).TotalSeconds))
            .FirstOrDefault();
        ReplaceCollection(NvrRecordingSegments, segments.Where(item => item.EndTime >= begin && item.StartTime <= end).OrderByDescending(static item => item.StartTime));
        return segment;
    }

    private NvrTileViewModel EnsureNvrTile()
    {
        if (SelectedNvrTile is not null)
        {
            return SelectedNvrTile;
        }

        SelectedNvrTile = NvrTiles.First();
        return SelectedNvrTile;
    }

    private void SyncStorageTimesFromPlaybackControls()
    {
        var begin = BuildPlaybackStartTime();
        StorageBeginTime = begin.ToString("yyyy-MM-dd HH:mm:ss");
        StorageEndTime = begin.AddMinutes(PlaybackWindowMinutes).ToString("yyyy-MM-dd HH:mm:ss");
    }

    private DateTimeOffset BuildPlaybackStartTime()
    {
        var date = PlaybackDate ?? DateTime.Today;
        if (!TimeSpan.TryParse(PlaybackTime, out var time))
        {
            time = DateTime.Now.TimeOfDay;
        }

        return new DateTimeOffset(date.Date.Add(time));
    }

    private static string BuildTileFailureMessage(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Authentication failed (401). Verify camera stream credentials.";
        }

        if (message.Contains("ffmpeg not found", StringComparison.OrdinalIgnoreCase))
        {
            return "FFmpeg is not available on this machine.";
        }

        if (message.Contains("Error number -138", StringComparison.OrdinalIgnoreCase))
        {
            return "Stream endpoint rejected the request (error -138).";
        }

        if (message.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
        {
            return "Stream opened but returned invalid media data.";
        }

        if (message.Contains("No frames received", StringComparison.OrdinalIgnoreCase))
        {
            return "No frames received from the selected source.";
        }

        if (message.Contains("exited before the first frame", StringComparison.OrdinalIgnoreCase))
        {
            return "Decoder exited before first frame.";
        }

        return $"Decode error: {message}";
    }

    private string BuildNvrFailureDiagnostics(NvrTileViewModel tile, string label, string source, NvrFrameDecodeSession session, Exception ex)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"tile={tile.TileId + 1}");
        builder.AppendLine($"mode={tile.Mode}");
        builder.AppendLine($"attempt={label}");
        builder.AppendLine($"source={SensitiveValueRedactor.RedactUrl(source)}");
        builder.AppendLine($"error={ex.GetBaseException().Message}");
        builder.AppendLine(BuildNvrSessionDiagnostics(tile, label, session));
        return builder.ToString().Trim();
    }

    private string BuildNvrSessionDiagnostics(NvrTileViewModel tile, string label)
        => BuildNvrSessionDiagnostics(tile, label, tile.Session);

    private static string BuildNvrSessionDiagnostics(NvrTileViewModel tile, string label, NvrFrameDecodeSession? session)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"tile={tile.TileId + 1}");
        builder.AppendLine($"mode={tile.Mode}");
        builder.AppendLine($"attempt={label}");
        builder.AppendLine($"status={tile.Status}");
        builder.AppendLine($"message={tile.Message}");
        if (session is not null)
        {
            builder.AppendLine($"source={SensitiveValueRedactor.RedactUrl(session.Source)}");
            builder.AppendLine($"ffmpegArgs={SensitiveValueRedactor.RedactText(session.FfmpegArguments)}");
            builder.AppendLine($"previewResolution={NvrFrameDecodeSession.FrameWidth}x{NvrFrameDecodeSession.FrameHeight}");
            builder.AppendLine($"exitCode={(session.ExitCode?.ToString() ?? "running")}");
            builder.AppendLine($"framesDecoded={session.FramesDecoded}");
            builder.AppendLine($"lastFrameTimestamp={(session.LastFrameTimestamp?.ToString("O") ?? "none")}");
            builder.AppendLine($"stderrTail={session.StderrTail}");
        }

        return builder.ToString().Trim();
    }

    private static string BuildMissingLiveSourceDiagnostics(DeviceIdentity device, IReadOnlyCollection<VideoSourceDescriptor> sources)
    {
        if (sources.Count == 0)
        {
            return $"No live sources were returned for {device.DisplayName}.";
        }

        var listed = string.Join(
            Environment.NewLine,
            sources.OrderBy(static item => item.Rank).Select(static item => $"{item.Kind} rank={item.Rank} url={SensitiveValueRedactor.RedactUrl(item.Url)} expected={item.ExpectedWidth}x{item.ExpectedHeight} codec={item.ExpectedCodec} auth={item.AuthState} outcome={item.SourceTruthOutcome} lowResOnly={item.LowResOnly}"));
        return $"No FFmpeg-decodable live source was available for {device.DisplayName}.{Environment.NewLine}{listed}";
    }

    private static string BuildLiveFailureSummary(DeviceIdentity? device, IReadOnlyCollection<string> failures)
    {
        var combined = string.Join(" ", failures);
        if (combined.Contains("401", StringComparison.OrdinalIgnoreCase) || combined.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            if (device?.IpAddress == "10.0.0.227")
            {
                return "FAIL_RTSP_EMPTY_PASSWORD_AUTH_NEGOTIATION";
            }

            return "Authentication failed for RTSP source (401 Unauthorized).";
        }

        if (combined.Contains("Error number -138", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP stream endpoint rejected the request (error -138).";
        }

        if (combined.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
        {
            return "Stream returned invalid media data.";
        }

        return "No frames received from any live source.";
    }

    private static IEnumerable<NvrSourceCandidate> BuildLiveSourceCandidates(DeviceIdentity device, IEnumerable<VideoSourceDescriptor> sources)
    {
        var candidates = new List<NvrSourceCandidate>();
        foreach (var source in sources.OrderBy(static item => item.Rank))
        {
            if (string.IsNullOrWhiteSpace(source.Url) || !IsDirectDecodeCandidate(source.Url))
            {
                continue;
            }

            if (TryBuildCredentialedVariants(source.Url, ResolvePreferredCredentials(device), out var credentialedVariants))
            {
                foreach (var variant in credentialedVariants)
                {
                    candidates.Add(new NvrSourceCandidate(variant.Url, $"{source.Kind} ({variant.Label})"));
                }
            }

            candidates.Add(new NvrSourceCandidate(source.Url, source.Kind.ToString()));
        }

        return candidates
            .GroupBy(static item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First());
    }

    private static bool IsDirectDecodeCandidate(string source)
        => source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase);

    private static (string Username, string Password, string Label) ResolvePreferredCredentials(DeviceIdentity device)
    {
        if (!string.IsNullOrWhiteSpace(device.LoginName))
        {
            return (device.LoginName!, device.Password ?? string.Empty, "device credentials");
        }

        return ("admin", string.Empty, "default admin");
    }

    private static bool TryBuildCredentialedVariants(string source, (string Username, string Password, string Label) credentials, out IReadOnlyCollection<(string Url, string Label)> variants)
    {
        variants = [];
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return false;
        }

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var user = Uri.EscapeDataString(credentials.Username);
        var password = Uri.EscapeDataString(credentials.Password ?? string.Empty);
        var path = uri.PathAndQuery;
        if (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) && path == "/")
        {
            path = string.Empty;
        }

        var list = new List<(string Url, string Label)>
        {
            ($"{uri.Scheme}://{user}:{password}@{authority}{path}{uri.Fragment}", $"{credentials.Label}:user+pass")
        };

        if (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            // Do not collapse explicit empty password into user-only RTSP auth.
        }

        variants = list;
        return true;
    }

    private sealed record NvrSourceCandidate(string Url, string Label);
}
