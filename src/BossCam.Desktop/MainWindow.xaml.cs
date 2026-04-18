using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private string _diagnosticsText = "Load store to begin.";
    private string _healthBadgeText = "Health: unknown";
    private string _transcriptPreview = string.Empty;
    private ProbeStageMode _selectedProbeMode = ProbeStageMode.SafeReadOnly;
    private TypedFieldRow? _selectedTypedField;

    public ObservableCollection<DeviceIdentity> Devices { get; } = [];
    public ObservableCollection<TypedFieldRow> VideoFields { get; } = [];
    public ObservableCollection<TypedFieldRow> NetworkFields { get; } = [];
    public ObservableCollection<TypedFieldRow> UserFields { get; } = [];
    public ObservableCollection<EndpointValidationResult> ValidationMatrix { get; } = [];
    public ObservableCollection<ProbeStageMode> ProbeModes { get; } = new(Enum.GetValues<ProbeStageMode>());

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

    public ProbeStageMode SelectedProbeMode
    {
        get => _selectedProbeMode;
        set
        {
            if (_selectedProbeMode != value)
            {
                _selectedProbeMode = value;
                OnPropertyChanged();
            }
        }
    }

    public TypedFieldRow? SelectedTypedField
    {
        get => _selectedTypedField;
        set
        {
            if (!Equals(_selectedTypedField, value))
            {
                _selectedTypedField = value;
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

    public string HealthBadgeText
    {
        get => _healthBadgeText;
        set
        {
            if (_healthBadgeText != value)
            {
                _healthBadgeText = value;
                OnPropertyChanged();
            }
        }
    }

    public string TranscriptPreview
    {
        get => _transcriptPreview;
        set
        {
            if (_transcriptPreview != value)
            {
                _transcriptPreview = value;
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

    private async void LoadStore_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadDevicesAsync);
    private async void Discover_Click(object sender, RoutedEventArgs e) => await RunAsync(DiscoverAsync);
    private async void RefreshTyped_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshTypedAsync);
    private async void ProbeSelected_Click(object sender, RoutedEventArgs e) => await RunAsync(() => RunProbeAsync(SelectedDevice?.IpAddress));
    private async void ProbeKnownIps_Click(object sender, RoutedEventArgs e) => await RunAsync(ProbeKnownIpsAsync);
    private async void LoadSessions_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadSessionsAsync);
    private async void LoadTranscripts_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadTranscriptsAsync);
    private async void ApplyValidated_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyFieldAsync(expertOverride: false));
    private async void ApplyExpert_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyFieldAsync(expertOverride: true));
    private async void VerifyPersistence_Click(object sender, RoutedEventArgs e) => await RunAsync(VerifyPersistenceAsync);

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

    private async Task RefreshTypedAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        _ = await PostAsync<List<TypedSettingGroupSnapshot>>($"/api/devices/{SelectedDevice.Id}/settings/typed/refresh", null);
        await LoadTypedAsync();
    }

    private async Task RunProbeAsync(string? ip)
    {
        if (SelectedDevice is null && string.IsNullOrWhiteSpace(ip))
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new ProbeSessionRequest
        {
            DeviceId = SelectedDevice?.Id,
            DeviceIp = SelectedDevice is null ? ip : null,
            Mode = SelectedProbeMode,
            ResumeIfExists = true,
            IncludePersistenceChecks = false,
            IncludeRollbackChecks = true,
            TranscriptExportDirectory = "C:\\Users\\ceide\\Documents\\BossCamSuite\\artifacts"
        };
        var result = await PostAsync<ProbeSession>("/api/probe/sessions/start", request);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadTypedAsync();
        await LoadValidationAsync();
    }

    private async Task ProbeKnownIpsAsync()
    {
        foreach (var ip in new[] { "10.0.0.4", "10.0.0.29", "10.0.0.227" })
        {
            await RunProbeAsync(ip);
        }
    }

    private async Task LoadSessionsAsync()
    {
        var query = SelectedDevice is null ? string.Empty : $"?deviceId={SelectedDevice.Id}";
        var sessions = await GetAsync<List<ProbeSession>>($"/api/probe/sessions{query}") ?? [];
        DiagnosticsText = JsonSerializer.Serialize(sessions, SerializerOptions);
    }

    private async Task LoadTypedAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var groups = await GetAsync<List<TypedSettingGroupSnapshot>>($"/api/devices/{SelectedDevice.Id}/settings/typed") ?? [];
        var fields = groups.SelectMany(static group => group.Fields).ToList();

        ReplaceCollection(VideoFields, fields.Where(field => field.GroupKind == TypedSettingGroupKind.VideoImage).Select(ToRow));
        ReplaceCollection(NetworkFields, fields.Where(field => field.GroupKind == TypedSettingGroupKind.NetworkWireless).Select(ToRow));
        ReplaceCollection(UserFields, fields.Where(field => field.GroupKind == TypedSettingGroupKind.UsersMaintenance).Select(ToRow));

        var proven = fields.Count(field => field.ReadVerified && field.WriteVerified);
        var inferred = fields.Count(field => field.ReadVerified && !field.WriteVerified);
        var unverified = fields.Count - proven - inferred;
        HealthBadgeText = $"Health: proven={proven} inferred={inferred} unverified={unverified}";

        if (DiagnosticsText.Length < 10 || DiagnosticsText.Contains("Select a device", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsText = JsonSerializer.Serialize(groups, SerializerOptions);
        }
    }

    private async Task LoadValidationAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var validations = await GetAsync<List<EndpointValidationResult>>($"/api/devices/{SelectedDevice.Id}/validation") ?? [];
        ReplaceCollection(ValidationMatrix, validations);
    }

    private async Task LoadTranscriptsAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var transcripts = await GetAsync<List<EndpointTranscript>>($"/api/devices/{SelectedDevice.Id}/validation/transcripts?limit=150") ?? [];
        TranscriptPreview = JsonSerializer.Serialize(transcripts.Take(20), SerializerOptions);
        DiagnosticsText = $"Loaded {transcripts.Count} transcripts.";
    }

    private async Task ApplyFieldAsync(bool expertOverride)
    {
        if (SelectedDevice is null || SelectedTypedField is null)
        {
            DiagnosticsText = "Select a field first.";
            return;
        }

        if (!expertOverride && !SelectedTypedField.WriteVerified)
        {
            DiagnosticsText = "Field is not write-verified. Use Apply Expert for separated override path.";
            return;
        }

        var payload = new TypedSettingApplyRequest(SelectedTypedField.FieldKey, ParseNode(SelectedTypedField.EditableValue), expertOverride);
        var result = await PostAsync<WriteResult>($"/api/devices/{SelectedDevice.Id}/settings/typed/apply", payload);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadTypedAsync();
        await LoadValidationAsync();
    }

    private async Task VerifyPersistenceAsync()
    {
        if (SelectedDevice is null || SelectedTypedField is null)
        {
            DiagnosticsText = "Select a field first.";
            return;
        }

        var payload = new PersistenceVerificationRequest
        {
            DeviceId = SelectedDevice.Id,
            AdapterName = SelectedTypedField.AdapterName,
            Endpoint = SelectedTypedField.SourceEndpoint,
            Method = "PUT",
            Payload = new JsonObject { [SelectedTypedField.FieldKey] = ParseNode(SelectedTypedField.EditableValue) },
            RebootForVerification = SelectedTypedField.DisruptionClass == DisruptionClass.Reboot.ToString()
        };

        var result = await PostAsync<PersistenceVerificationResult>($"/api/devices/{SelectedDevice.Id}/persistence/verify", payload);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadValidationAsync();
        await LoadTypedAsync();
    }

    private static JsonNode? ParseNode(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return JsonValue.Create(string.Empty);
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed is "true" or "false")
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch
            {
            }
        }

        if (decimal.TryParse(trimmed, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(raw);
    }

    private static TypedFieldRow ToRow(NormalizedSettingField field)
        => new()
        {
            FieldKey = field.FieldKey,
            DisplayName = field.DisplayName,
            EditableValue = field.TypedValue?.ToJsonString() ?? string.Empty,
            AdapterName = field.AdapterName,
            SourceEndpoint = field.SourceEndpoint,
            ReadVerified = field.ReadVerified,
            WriteVerified = field.WriteVerified,
            PersistsAfterReboot = field.PersistsAfterReboot,
            DisruptionClass = field.DisruptionClass.ToString(),
            FirmwareFingerprint = field.FirmwareFingerprint ?? string.Empty
        };

    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = RunAsync(async () =>
        {
            if (SelectedDevice is not null)
            {
                await LoadTypedAsync();
                await LoadValidationAsync();
                await LoadTranscriptsAsync();
            }
        });
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

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
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

public sealed class TypedFieldRow : INotifyPropertyChanged
{
    private string _editableValue = string.Empty;

    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public bool ReadVerified { get; init; }
    public bool WriteVerified { get; init; }
    public bool PersistsAfterReboot { get; init; }
    public string DisruptionClass { get; init; } = string.Empty;
    public string FirmwareFingerprint { get; init; } = string.Empty;

    public string EditableValue
    {
        get => _editableValue;
        set
        {
            if (_editableValue != value)
            {
                _editableValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditableValue)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record TypedSettingApplyRequest(string FieldKey, JsonNode? Value, bool ExpertOverride);
