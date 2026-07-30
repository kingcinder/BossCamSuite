using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;

namespace BossCam.Desktop.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IBossCamApiClient _api;
    private Timer? _liveTimer;

    /// <summary>
    /// Default constructor — creates a real <see cref="HttpBossCamApiClient"/>
    /// pointing at http://127.0.0.1:5317. Used by App.axaml.cs in production.
    /// </summary>
    public MainWindowViewModel()
        : this(new HttpBossCamApiClient())
    {
    }

    /// <summary>
    /// DI-friendly constructor. Accepts any <see cref="IBossCamApiClient"/>
    /// implementation (real, wrapped, or test double).
    /// </summary>
    public MainWindowViewModel(IBossCamApiClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        _api = apiClient;
    }

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
        catch { /* non-critical */ }

        // Start polling the snapshot endpoint every 2s for live preview
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
