using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using BossCam.Contracts;

namespace BossCam.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:5317") };
    private DeviceIdentity? _selectedDevice;
    private string _diagnosticsText = "Press Load Store or Discover to begin.";

    public ObservableCollection<DeviceIdentity> Devices { get; } = [];
    public ObservableCollection<VideoSourceDescriptor> Sources { get; } = [];

    public DeviceIdentity? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Equals(_selectedDevice, value))
            {
                _selectedDevice = value;
                OnPropertyChanged();
            }
        }
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        set
        {
            if (_diagnosticsText != value)
            {
                _diagnosticsText = value;
                OnPropertyChanged();
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadDevicesAsync);
    private async void DiscoverDevices_Click(object sender, RoutedEventArgs e) => await RunAsync(DiscoverAsync);
    private async void ProbeSelected_Click(object sender, RoutedEventArgs e) => await RunAsync(ProbeAsync);
    private async void LoadSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadSettingsAsync);
    private async void LoadSources_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadSourcesAsync);
    private async void LoadAudit_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadAuditAsync);
    private async void LoadFirmware_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadFirmwareAsync);
    private async void LoadProtocols_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadProtocolsAsync);

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DiagnosticsText = ex.ToString();
        }
    }

    private async Task LoadDevicesAsync()
    {
        var devices = await GetAsync<List<DeviceIdentity>>("/api/devices") ?? [];
        ReplaceCollection(Devices, devices);
        DiagnosticsText = JsonSerializer.Serialize(devices, SerializerOptions);
    }

    private async Task DiscoverAsync()
    {
        var devices = await PostAsync<List<DeviceIdentity>>("/api/devices/discover", null) ?? [];
        ReplaceCollection(Devices, devices);
        DiagnosticsText = JsonSerializer.Serialize(devices, SerializerOptions);
    }

    private async Task ProbeAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var result = await PostAsync<CapabilityMap>($"/api/devices/{SelectedDevice.Id}/probe", null);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task LoadSettingsAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var result = await GetAsync<SettingsSnapshot>($"/api/devices/{SelectedDevice.Id}/settings");
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task LoadSourcesAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var result = await GetAsync<List<VideoSourceDescriptor>>($"/api/devices/{SelectedDevice.Id}/sources") ?? [];
        ReplaceCollection(Sources, result);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task LoadAuditAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var result = await GetAsync<List<WriteAuditEntry>>($"/api/diagnostics/audit?deviceId={SelectedDevice.Id}&limit=100");
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task LoadFirmwareAsync()
    {
        var result = await GetAsync<List<FirmwareArtifact>>("/api/firmware");
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task LoadProtocolsAsync()
    {
        var result = await GetAsync<List<ProtocolManifest>>("/api/protocols");
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is not null)
        {
            DiagnosticsText = JsonSerializer.Serialize(SelectedDevice, SerializerOptions);
        }
    }

    private async Task<T?> GetAsync<T>(string path)
    {
        using var response = await _httpClient.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            DiagnosticsText = raw;
            return default;
        }
        return JsonSerializer.Deserialize<T>(raw, SerializerOptions);
    }

    private async Task<T?> PostAsync<T>(string path, object? payload)
    {
        using var response = payload is null
            ? await _httpClient.PostAsync(path, null)
            : await _httpClient.PostAsJsonAsync(path, payload, SerializerOptions);
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            DiagnosticsText = raw;
            return default;
        }
        return JsonSerializer.Deserialize<T>(raw, SerializerOptions);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
