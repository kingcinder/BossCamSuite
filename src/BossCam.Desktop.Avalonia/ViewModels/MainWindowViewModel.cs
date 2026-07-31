using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Main window shell: owns navigation between sections, the shared device
/// selection, and the live-preview section (this VM is itself the first
/// section in the list). All other sections are hosted as child ViewModels.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, ISectionViewModel, IDisposable
{
    private readonly IBossCamApiClient _api;
    private Timer? _liveTimer;

    /// <summary>Default constructor — real HTTP client at http://127.0.0.1:5317.</summary>
    public MainWindowViewModel()
        : this(new HttpBossCamApiClient())
    {
    }

    /// <summary>DI-friendly constructor for production wiring and tests.</summary>
    public MainWindowViewModel(IBossCamApiClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        _api = apiClient;

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

    // ── Shared device list / selection ────────────────────────────

    [ObservableProperty]
    private ObservableCollection<DeviceIdentity> _devices = [];

    [ObservableProperty]
    private DeviceIdentity? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "Connect to BossCamService at http://127.0.0.1:5317";

    [ObservableProperty]
    private string _deviceInfoText = string.Empty;

    [ObservableProperty]
    private Bitmap? _liveFrame;

    [ObservableProperty]
    private bool _isLive;

    partial void OnSelectedDeviceChanged(DeviceIdentity? value)
    {
        if (value is not null)
        {
            _ = RefreshDeviceAsync();
        }

        // Keep the Features section in sync with the camera being managed.
        _ = FeaturesSection.DeviceChangedAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _api.GetDevicesAsync();
            Devices = new ObservableCollection<DeviceIdentity>(devices);
            StatusText = $"Loaded {devices.Count} device(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load devices: {ex.Message}";
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
        catch
        {
            // Optional enrichment only — GetLiveInfoAsync already returns null on failure,
            // so the live view must not break if live-info is temporarily unavailable.
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

    public void Dispose()
    {
        _liveTimer?.Dispose();
        _api.Dispose();
    }
}
