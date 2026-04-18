using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using BossCam.Contracts;

namespace BossCam.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:5317") };
    private readonly Dictionary<string, TypedFieldRow> _fieldByKey = new(StringComparer.OrdinalIgnoreCase);

    private DeviceIdentity? _selectedDevice;
    private string _diagnosticsText = "Load store to begin.";
    private string _healthBadgeText = "Health: unknown";
    private string _capabilityBadgeText = "Capabilities: unknown";
    private string _transcriptPreview = string.Empty;
    private ProbeStageMode _selectedProbeMode = ProbeStageMode.SafeReadOnly;
    private TypedFieldRow? _selectedTypedField;
    private string _videoEditorNotice = "Video editor follows endpoint-aware parser mappings. Unsupported fields remain disabled.";
    private string _networkRecoveryHint = "Recovery hint: if IP/port changes, update control URL to the new endpoint and rerun probe.";
    private string _maintenanceState = "No maintenance action executed.";
    private string _passwordChangeUsername = string.Empty;
    private string _passwordChangeValue = string.Empty;
    private string _wirelessApPsk = string.Empty;

    public ObservableCollection<DeviceIdentity> Devices { get; } = [];
    public ObservableCollection<EndpointValidationResult> ValidationMatrix { get; } = [];
    public ObservableCollection<ProbeStageMode> ProbeModes { get; } = new(Enum.GetValues<ProbeStageMode>());
    public ObservableCollection<string> UserList { get; } = [];

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

    public string CapabilityBadgeText
    {
        get => _capabilityBadgeText;
        set
        {
            if (_capabilityBadgeText != value)
            {
                _capabilityBadgeText = value;
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

    public string VideoEditorNotice
    {
        get => _videoEditorNotice;
        set
        {
            if (_videoEditorNotice != value)
            {
                _videoEditorNotice = value;
                OnPropertyChanged();
            }
        }
    }

    public string NetworkRecoveryHint
    {
        get => _networkRecoveryHint;
        set
        {
            if (_networkRecoveryHint != value)
            {
                _networkRecoveryHint = value;
                OnPropertyChanged();
            }
        }
    }

    public string MaintenanceState
    {
        get => _maintenanceState;
        set
        {
            if (_maintenanceState != value)
            {
                _maintenanceState = value;
                OnPropertyChanged();
            }
        }
    }

    public string PasswordChangeUsername
    {
        get => _passwordChangeUsername;
        set
        {
            if (_passwordChangeUsername != value)
            {
                _passwordChangeUsername = value;
                OnPropertyChanged();
            }
        }
    }

    // Video/image typed bindings
    public string VideoCodec { get => GetValue("codec"); set => SetValue("codec", value); }
    public string VideoProfile { get => GetValue("profile"); set => SetValue("profile", value); }
    public string VideoDayNight { get => GetValue("dayNight"); set => SetValue("dayNight", value); }
    public string VideoWdr { get => GetValue("wdr"); set => SetValue("wdr", value); }
    public string VideoIrCut { get => GetValue("irCut"); set => SetValue("irCut", value); }
    public string VideoBitrate { get => GetValue("bitrate"); set => SetValue("bitrate", value); }
    public double VideoBitrateSlider
    {
        get => ParseDouble(VideoBitrate, 512);
        set
        {
            VideoBitrate = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string VideoFrameRate { get => GetValue("frameRate"); set => SetValue("frameRate", value); }
    public string ImageBrightness { get => GetValue("brightness"); set => SetValue("brightness", value); }
    public double ImageBrightnessSlider
    {
        get => ParseDouble(ImageBrightness, 50);
        set
        {
            ImageBrightness = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageContrast { get => GetValue("contrast"); set => SetValue("contrast", value); }
    public string ImageSaturation { get => GetValue("saturation"); set => SetValue("saturation", value); }
    public string ImageHue { get => GetValue("hue"); set => SetValue("hue", value); }
    public string ImageSharpness { get => GetValue("sharpness"); set => SetValue("sharpness", value); }

    // Network/wireless typed bindings
    public string NetworkIp { get => GetValue("ip"); set => SetValue("ip", value); }
    public string NetworkNetmask { get => GetValue("netmask"); set => SetValue("netmask", value); }
    public string NetworkGateway { get => GetValue("gateway"); set => SetValue("gateway", value); }
    public string NetworkDns { get => GetValue("dns"); set => SetValue("dns", value); }
    public string NetworkPort { get => GetValue("ports"); set => SetValue("ports", value); }
    public string WirelessApSsid { get => GetValue("apSsid"); set => SetValue("apSsid", value); }
    public string WirelessApChannel { get => GetValue("apChannel"); set => SetValue("apChannel", value); }

    // state labels
    public string VideoCodecState => FieldState("codec");
    public string VideoProfileState => FieldState("profile");
    public string NetworkIpState => FieldState("ip");
    public string WirelessApPskState => FieldState("apPsk");

    // enable flags capability-driven
    public bool CanEditVideoCodec => CanEdit("codec");
    public bool CanEditVideoProfile => CanEdit("profile");
    public bool CanEditVideoDayNight => CanEdit("dayNight");
    public bool CanEditVideoWdr => CanEdit("wdr");
    public bool CanEditVideoIrCut => CanEdit("irCut");
    public bool CanEditNetworkIp => CanEdit("ip");
    public bool CanEditNetworkNetmask => CanEdit("netmask");
    public bool CanEditNetworkGateway => CanEdit("gateway");
    public bool CanEditNetworkDns => CanEdit("dns");
    public bool CanEditNetworkPort => CanEdit("ports");
    public bool CanEditWirelessApSsid => CanEdit("apSsid");
    public bool CanEditWirelessApChannel => CanEdit("apChannel");

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
    private async void ApplyValidated_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyPendingEditsAsync(expertOverride: false));
    private async void ApplyExpert_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyPendingEditsAsync(expertOverride: true));
    private async void VerifyPersistence_Click(object sender, RoutedEventArgs e) => await RunAsync(VerifyPersistenceAsync);
    private async void RebootCamera_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ExecuteMaintenanceAsync("Reboot"));
    private async void FactoryDefault_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ExecuteMaintenanceAsync("FactoryReset"));
    private async void ApplyPasswordChange_Click(object sender, RoutedEventArgs e) => await RunAsync(ApplyPasswordChangeAsync);

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

        _fieldByKey.Clear();
        var groups = await GetAsync<List<TypedSettingGroupSnapshot>>($"/api/devices/{SelectedDevice.Id}/settings/typed") ?? [];
        foreach (var field in groups.SelectMany(static group => group.Fields))
        {
            _fieldByKey[field.FieldKey] = TypedFieldRow.FromField(field);
        }

        var proven = _fieldByKey.Values.Count(field => field.ReadVerified && field.WriteVerified);
        var inferred = _fieldByKey.Values.Count(field => field.ReadVerified && !field.WriteVerified);
        var unverified = _fieldByKey.Count - proven - inferred;
        HealthBadgeText = $"Health: proven={proven} inferred={inferred} unverified={unverified}";
        await LoadCapabilityProfileAsync(groups.Select(static group => group.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)));

        PopulateUserList();
        NotifyAllEditorProperties();
    }

    private async Task LoadCapabilityProfileAsync(string? firmwareFingerprint)
    {
        var profiles = await GetAsync<List<FirmwareCapabilityProfile>>("/api/firmware/capabilities") ?? [];
        var profile = string.IsNullOrWhiteSpace(firmwareFingerprint)
            ? profiles.FirstOrDefault()
            : profiles.FirstOrDefault(item => item.FirmwareFingerprint.Equals(firmwareFingerprint, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            CapabilityBadgeText = "Capabilities: no firmware profile";
            return;
        }

        CapabilityBadgeText = $"Capabilities: {profile.FirmwareFingerprint} groups={profile.SupportedSettingGroups.Count} writable={profile.VerifiedWritableFields.Count}";
        NetworkRecoveryHint = profile.DangerousFields.Any(field => field.Equals("ip", StringComparison.OrdinalIgnoreCase))
            ? "Recovery hint: IP changes are dangerous; if control is lost, reconnect using last known and new candidate URL."
            : "Recovery hint: rerun probe after network writes.";
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

        var transcripts = await GetAsync<List<EndpointTranscript>>($"/api/devices/{SelectedDevice.Id}/validation/transcripts?limit=200") ?? [];
        TranscriptPreview = JsonSerializer.Serialize(transcripts.Take(25), SerializerOptions);
        DiagnosticsText = $"Loaded {transcripts.Count} transcripts.";
    }

    private async Task ApplyPendingEditsAsync(bool expertOverride)
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var targets = new[]
        {
            ("codec", VideoCodec),
            ("profile", VideoProfile),
            ("dayNight", VideoDayNight),
            ("wdr", VideoWdr),
            ("irCut", VideoIrCut),
            ("bitrate", VideoBitrate),
            ("frameRate", VideoFrameRate),
            ("brightness", ImageBrightness),
            ("contrast", ImageContrast),
            ("saturation", ImageSaturation),
            ("hue", ImageHue),
            ("sharpness", ImageSharpness),
            ("ip", NetworkIp),
            ("netmask", NetworkNetmask),
            ("gateway", NetworkGateway),
            ("dns", NetworkDns),
            ("ports", NetworkPort),
            ("apSsid", WirelessApSsid),
            ("apPsk", _wirelessApPsk),
            ("apChannel", WirelessApChannel)
        };

        var applied = new List<WriteResult>();
        foreach (var target in targets)
        {
            if (!_fieldByKey.TryGetValue(target.Item1, out var field))
            {
                continue;
            }

            if (!expertOverride && !field.WriteVerified)
            {
                continue;
            }

            if (field.EditableValue == target.Item2)
            {
                continue;
            }

            var result = await PostAsync<WriteResult>($"/api/devices/{SelectedDevice.Id}/settings/typed/apply",
                new TypedSettingApplyRequest(field.FieldKey, ParseNode(target.Item2), expertOverride));
            if (result is not null)
            {
                applied.Add(result);
            }
        }

        DiagnosticsText = JsonSerializer.Serialize(applied, SerializerOptions);
        await LoadTypedAsync();
        await LoadValidationAsync();
    }

    private async Task VerifyPersistenceAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        if (!_fieldByKey.TryGetValue("bitrate", out var field))
        {
            DiagnosticsText = "No endpoint-aware bitrate field available for persistence check.";
            return;
        }

        var payload = new PersistenceVerificationRequest
        {
            DeviceId = SelectedDevice.Id,
            AdapterName = field.AdapterName,
            Endpoint = field.SourceEndpoint,
            Method = "PUT",
            Payload = new JsonObject { ["bitrate"] = ParseNode(VideoBitrate) },
            RebootForVerification = false
        };
        var result = await PostAsync<PersistenceVerificationResult>($"/api/devices/{SelectedDevice.Id}/persistence/verify", payload);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadValidationAsync();
    }

    private async Task ExecuteMaintenanceAsync(string operation)
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var prompt = operation.Equals("FactoryReset", StringComparison.OrdinalIgnoreCase)
            ? "Factory default is destructive. Continue?"
            : "Reboot camera now?";
        if (MessageBox.Show(prompt, "Confirm Maintenance", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await PostAsync<MaintenanceResult>($"/api/devices/{SelectedDevice.Id}/maintenance/{operation}", new JsonObject());
        MaintenanceState = result is null ? $"Failed {operation}" : $"{operation}: {(result.Success ? "ok" : "failed")}";
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private async Task ApplyPasswordChangeAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PasswordChangeUsername) || string.IsNullOrWhiteSpace(_passwordChangeValue))
        {
            MaintenanceState = "Username and new password are required.";
            return;
        }

        var payload = new JsonObject
        {
            ["username"] = PasswordChangeUsername,
            ["newPassword"] = _passwordChangeValue
        };
        var result = await PostAsync<MaintenanceResult>($"/api/devices/{SelectedDevice.Id}/maintenance/PasswordReset", payload);
        MaintenanceState = result is null ? "Password change failed." : $"Password change: {(result.Success ? "ok" : "failed")}";
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private void PopulateUserList()
    {
        UserList.Clear();
        if (_fieldByKey.TryGetValue("userList", out var users))
        {
            if (users.EditableValue.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var node = JsonNode.Parse(users.EditableValue);
                    if (node is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            UserList.Add(item?.ToJsonString() ?? string.Empty);
                        }
                    }
                }
                catch
                {
                    UserList.Add(users.EditableValue);
                }
            }
            else
            {
                UserList.Add(users.EditableValue);
            }
        }
    }

    private string GetValue(string key)
        => _fieldByKey.TryGetValue(key, out var field) ? field.EditableValue : string.Empty;

    private void SetValue(string key, string value)
    {
        if (_fieldByKey.TryGetValue(key, out var field))
        {
            field.EditableValue = value;
        }
    }

    private bool CanEdit(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return false;
        }

        // Proven behavior: normal editor only enabled for verified writes and non-expert fields.
        return field.WriteVerified && !field.ExpertOnly;
    }

    private string FieldState(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return "unsupported";
        }

        if (field.WriteVerified && field.PersistsAfterReboot)
        {
            return "proven";
        }

        if (field.WriteVerified)
        {
            return "verified";
        }

        return field.ReadVerified ? "read-only" : "unverified";
    }

    private static double ParseDouble(string raw, double fallback)
        => double.TryParse(raw, out var parsed) ? parsed : fallback;

    private void NotifyAllEditorProperties()
    {
        foreach (var name in new[]
        {
            nameof(VideoCodec), nameof(VideoProfile), nameof(VideoDayNight), nameof(VideoWdr), nameof(VideoIrCut),
            nameof(VideoBitrate), nameof(VideoBitrateSlider), nameof(VideoFrameRate), nameof(ImageBrightness), nameof(ImageBrightnessSlider),
            nameof(ImageContrast), nameof(ImageSaturation), nameof(ImageHue), nameof(ImageSharpness),
            nameof(NetworkIp), nameof(NetworkNetmask), nameof(NetworkGateway), nameof(NetworkDns), nameof(NetworkPort),
            nameof(WirelessApSsid), nameof(WirelessApChannel),
            nameof(VideoCodecState), nameof(VideoProfileState), nameof(NetworkIpState), nameof(WirelessApPskState),
            nameof(CanEditVideoCodec), nameof(CanEditVideoProfile), nameof(CanEditVideoDayNight), nameof(CanEditVideoWdr), nameof(CanEditVideoIrCut),
            nameof(CanEditNetworkIp), nameof(CanEditNetworkNetmask), nameof(CanEditNetworkGateway), nameof(CanEditNetworkDns), nameof(CanEditNetworkPort),
            nameof(CanEditWirelessApSsid), nameof(CanEditWirelessApChannel)
        })
        {
            OnPropertyChanged(name);
        }
    }

    private static JsonNode? ParseNode(string? raw)
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

    private void Devices_SelectionChanged(object sender, RoutedEventArgs e)
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

    private void WirelessPskPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
        {
            _wirelessApPsk = box.Password;
        }
    }

    private void PasswordChangePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
        {
            _passwordChangeValue = box.Password;
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

public sealed class TypedFieldRow
{
    public string FieldKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string RawSourcePath { get; init; } = string.Empty;
    public bool ReadVerified { get; init; }
    public bool WriteVerified { get; init; }
    public bool PersistsAfterReboot { get; init; }
    public bool ExpertOnly { get; init; }
    public string DisruptionClass { get; init; } = string.Empty;
    public string FirmwareFingerprint { get; init; } = string.Empty;
    public string Validity { get; init; } = string.Empty;
    public string EditableValue { get; set; } = string.Empty;

    public static TypedFieldRow FromField(NormalizedSettingField field)
        => new()
        {
            FieldKey = field.FieldKey,
            DisplayName = field.DisplayName,
            AdapterName = field.AdapterName,
            SourceEndpoint = field.SourceEndpoint,
            RawSourcePath = field.RawSourcePath,
            ReadVerified = field.ReadVerified,
            WriteVerified = field.WriteVerified,
            PersistsAfterReboot = field.PersistsAfterReboot,
            ExpertOnly = field.ExpertOnly,
            DisruptionClass = field.DisruptionClass.ToString(),
            FirmwareFingerprint = field.FirmwareFingerprint ?? string.Empty,
            Validity = field.Validity.ToString(),
            EditableValue = field.TypedValue?.ToJsonString() ?? string.Empty
        };
}

public sealed record TypedSettingApplyRequest(string FieldKey, JsonNode? Value, bool ExpertOverride);
