using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BossCam.Contracts;

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
            Background = new SolidColorBrush(Color.FromRgb(5, 10, 15)),
            BorderThickness = new Thickness(tile.IsSelected ? 3 : 1),
            BorderBrush = new SolidColorBrush(tile.IsSelected ? Color.FromRgb(255, 210, 122) : Color.FromRgb(57, 75, 91)),
            DataContext = tile
        };
        border.MouseLeftButtonDown += (_, _) => SelectedNvrTile = tile;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(NvrTileViewModel.Title)));
        grid.Children.Add(title);

        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(NvrTileViewModel.FrameSource)));
        Grid.SetRow(image, 1);
        grid.Children.Add(image);

        var status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(196, 212, 229)), TextWrapping = TextWrapping.Wrap };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(NvrTileViewModel.StatusText)));
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
        if (sender is Button { Tag: string raw } && int.TryParse(raw, out var layout))
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
        var source = sources.OrderBy(static item => item.Rank).FirstOrDefault();
        if (source is null || string.IsNullOrWhiteSpace(source.Url))
        {
            tile.Status = NvrStreamStatus.Failed;
            tile.Message = "No live source available.";
            return;
        }

        await StartTileDecodeAsync(tile, source.Url, NvrStreamMode.Live);
        NvrDiagnostics = $"Live tile {tile.TileId + 1}: {source.Kind} {source.Url}";
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
        var segment = SelectedNvrRecordingSegment ?? await ResolvePlaybackSegmentAsync(tile.Device.Id);
        if (segment is null || !File.Exists(segment.FilePath))
        {
            await FindNvrPlaybackAsync();
            tile.Status = NvrStreamStatus.Failed;
            tile.Message = "No indexed local segment found for selected time.";
            return;
        }

        SelectedNvrRecordingSegment = segment;
        await StartTileDecodeAsync(tile, segment.FilePath, NvrStreamMode.Playback);
        NvrDiagnostics = $"Playback tile {tile.TileId + 1}: {segment.StartTime:yyyy-MM-dd HH:mm:ss} {segment.FilePath}";
    }

    private async Task StartTileDecodeAsync(NvrTileViewModel tile, string source, NvrStreamMode mode)
    {
        tile.Stop();
        tile.Mode = mode;
        tile.Source = source;
        tile.Status = NvrStreamStatus.Starting;
        tile.Message = "Starting FFmpeg decode.";

        var session = new NvrFrameDecodeSession(tile.FrameSource);
        tile.Session = session;
        await session.StartAsync(source, _nvrShutdown.Token);
        tile.Status = NvrStreamStatus.Running;
        tile.Message = "Rendering latest decoded frame.";
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
        NvrDiagnostics = JsonSerializer.Serialize(new { playback = result, indexedSegments = NvrRecordingSegments.Count }, SerializerOptions);
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
}
