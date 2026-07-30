using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;

namespace BossCam.Desktop.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:5317"), Timeout = TimeSpan.FromSeconds(10) };
    private Timer? _liveTimer;

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
            var devices = await _http.GetFromJsonAsync<List<DeviceIdentity>>("/api/devices");
            if (devices is not null)
            {
                Devices = new ObservableCollection<DeviceIdentity>(devices);
                StatusText = $"Loaded {devices.Count} device(s)";
            }
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
            var info = await _http.GetFromJsonAsync<JsonElement>($"/api/devices/{id}/live-info");
            if (info.TryGetProperty("mainRtsp", out var main))
            {
                DeviceInfoText += $"\nMain RTSP: {main}";
            }
        }
        catch { /* non-critical */ }

        // Start polling the snapshot endpoint every 2s for live preview
        _liveTimer?.Dispose();
        _liveTimer = new Timer(async _ =>
        {
            try
            {
                using var res = await _http.GetAsync($"/api/devices/{id}/snapshot?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                if (res.IsSuccessStatusCode)
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync();
                    if (bytes.Length > 100)
                    {
                        using var ms = new MemoryStream(bytes);
                        LiveFrame = new Bitmap(ms);
                    }
                }
            }
            catch { /* polling failures are expected */ }
        }, null, 0, 2000);
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (SelectedDevice is null) return;
        try
        {
            var res = await _http.PostAsJsonAsync($"/api/storage/save-snapshot/{SelectedDevice.Id}", "{ }");
            if (res.IsSuccessStatusCode)
            {
                StatusText = "Snapshot saved";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Snapshot failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _liveTimer?.Dispose();
        _http.Dispose();
    }
}
