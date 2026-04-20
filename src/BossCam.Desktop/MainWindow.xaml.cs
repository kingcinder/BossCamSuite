using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private readonly Dictionary<string, TypedFieldRow> _fieldByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EditorHint> _editorHintByKey = new(StringComparer.OrdinalIgnoreCase);

    private DeviceIdentity? _selectedDevice;
    private string _diagnosticsText = "Load store to begin.";
    private string _healthBadgeText = "Health: unknown";
    private string _capabilityBadgeText = "Capabilities: unknown";
    private string _transcriptPreview = string.Empty;
    private ProbeStageMode _selectedProbeMode = ProbeStageMode.SafeReadOnly;
    private TypedFieldRow? _selectedTypedField;
    private string _videoEditorNotice = "Video editor follows endpoint-aware parser mappings. Fields are only editable when proven writable; uncertain fields stay gated.";
    private string _networkRecoveryHint = "Recovery hint: if IP/port changes, update control URL to the new endpoint and rerun probe.";
    private string _maintenanceState = "No maintenance action executed.";
    private string _recordingState = "Recording idle.";
    private string _recordingDiagnostics = string.Empty;
    private string _firmwareTruthBadge = "Truth: unknown";
    private string _imageTruthSummary = "Image truth not loaded.";
    private string _passwordChangeUsername = string.Empty;
    private string _passwordChangeValue = string.Empty;
    private string _wirelessApPsk = string.Empty;
    private string? _selectedPersistenceField;
    private string _lastReachableUrl = string.Empty;
    private readonly List<FieldDependencyRule> _dependencyRules = [];
    private readonly Dictionary<string, ImageFieldBehaviorMap> _imageBehaviorByField = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DeviceIdentity> Devices { get; } = [];
    public ObservableCollection<EndpointValidationResult> ValidationMatrix { get; } = [];
    public ObservableCollection<FieldProvenanceRow> FieldProvenanceRows { get; } = [];
    public ObservableCollection<SemanticWriteObservation> SemanticHistoryRows { get; } = [];
    public ObservableCollection<FieldConstraintRow> ConstraintRows { get; } = [];
    public ObservableCollection<FieldDependencyRuleRow> DependencyRows { get; } = [];
    public ObservableCollection<ImageControlInventoryItem> ImageInventoryRows { get; } = [];
    public ObservableCollection<ImageBehaviorRow> ImageBehaviorRows { get; } = [];
    public ObservableCollection<PromotedImageRow> PromotedImageRows { get; } = [];
    public ObservableCollection<ProbeStageMode> ProbeModes { get; } = new(Enum.GetValues<ProbeStageMode>());
    public ObservableCollection<string> UserList { get; } = [];
    public ObservableCollection<string> PersistenceFieldOptions { get; } = [];
    public ObservableCollection<string> VideoCodecOptions { get; } = [];
    public ObservableCollection<string> VideoProfileOptions { get; } = [];
    public ObservableCollection<string> VideoDayNightOptions { get; } = [];
    public ObservableCollection<string> VideoWdrOptions { get; } = [];
    public ObservableCollection<string> VideoIrCutOptions { get; } = [];
    public ObservableCollection<string> VideoResolutionOptions { get; } = [];

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

    public string RecordingState
    {
        get => _recordingState;
        set
        {
            if (_recordingState != value)
            {
                _recordingState = value;
                OnPropertyChanged();
            }
        }
    }

    public string RecordingDiagnostics
    {
        get => _recordingDiagnostics;
        set
        {
            if (_recordingDiagnostics != value)
            {
                _recordingDiagnostics = value;
                OnPropertyChanged();
            }
        }
    }

    public string FirmwareTruthBadge
    {
        get => _firmwareTruthBadge;
        set
        {
            if (_firmwareTruthBadge != value)
            {
                _firmwareTruthBadge = value;
                OnPropertyChanged();
            }
        }
    }

    public string ImageTruthSummary
    {
        get => _imageTruthSummary;
        set
        {
            if (_imageTruthSummary != value)
            {
                _imageTruthSummary = value;
                OnPropertyChanged();
            }
        }
    }

    public string LastReachableUrl
    {
        get => _lastReachableUrl;
        set
        {
            if (_lastReachableUrl != value)
            {
                _lastReachableUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public string CurrentControlUrl => string.IsNullOrWhiteSpace(SelectedDevice?.IpAddress) ? string.Empty : BuildUrlFromIpPort(SelectedDevice.IpAddress, NetworkPort);
    public string PredictedControlUrl => BuildUrlFromIpPort(NetworkIp, NetworkPort);

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
    public string VideoResolution { get => GetValue("resolution"); set => SetValue("resolution", value); }
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
    public string? SelectedPersistenceField
    {
        get => _selectedPersistenceField;
        set
        {
            if (_selectedPersistenceField != value)
            {
                _selectedPersistenceField = value;
                OnPropertyChanged();
            }
        }
    }

    // state labels
    public string VideoCodecState => FieldState("codec");
    public string VideoProfileState => FieldState("profile");
    public string NetworkIpState => FieldState("ip");
    public string WirelessApPskState => FieldState("apPsk");
    public string VideoFpsHint => $"FPS max: {HintMax("frameRate", 60):0}";
    public double VideoBitrateMin => HintMin("bitrate", 64);
    public double VideoBitrateMax => HintMax("bitrate", 16384);
    public double ImageBrightnessMin => HintMin("brightness", 0);
    public double ImageBrightnessMax => HintMax("brightness", 100);
    public string ImageBrightnessBehaviorBadge => BehaviorBadge("brightness");
    public string ImageContrastBehaviorBadge => BehaviorBadge("contrast");
    public string ImageSaturationBehaviorBadge => BehaviorBadge("saturation");

    // enable flags capability-driven
    public bool CanEditVideoCodec => CanEdit("codec");
    public bool CanEditVideoProfile => CanEdit("profile");
    public bool CanEditVideoResolution => CanEdit("resolution");
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
    public Visibility VideoCodecVisibility => FieldVisibility("codec");
    public Visibility VideoProfileVisibility => FieldVisibility("profile");
    public Visibility VideoDayNightVisibility => FieldVisibility("dayNight");
    public Visibility VideoWdrVisibility => FieldVisibility("wdr");
    public Visibility VideoIrCutVisibility => FieldVisibility("irCut");
    public Visibility VideoBitrateVisibility => FieldVisibility("bitrate");
    public Visibility VideoFrameRateVisibility => FieldVisibility("frameRate");
    public Visibility VideoResolutionVisibility => FieldVisibility("resolution");
    public Visibility ImageBrightnessVisibility => FieldVisibility("brightness");
    public Visibility ImageContrastVisibility => FieldVisibility("contrast");
    public Visibility ImageSaturationVisibility => FieldVisibility("saturation");
    public Visibility ImageHueVisibility => FieldVisibility("hue");
    public Visibility ImageSharpnessVisibility => FieldVisibility("sharpness");
    public Visibility NetworkIpVisibility => FieldVisibility("ip");
    public Visibility NetworkNetmaskVisibility => FieldVisibility("netmask");
    public Visibility NetworkGatewayVisibility => FieldVisibility("gateway", requireStaticNetwork: true);
    public Visibility NetworkDnsVisibility => FieldVisibility("dns", requireStaticNetwork: true);
    public Visibility NetworkPortVisibility => FieldVisibility("ports");
    public Visibility WirelessApSsidVisibility => FieldVisibility("apSsid");
    public Visibility WirelessApPskVisibility => FieldVisibility("apPsk");
    public Visibility WirelessApChannelVisibility => FieldVisibility("apChannel");

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
    private async void DiscoverConstraints_Click(object sender, RoutedEventArgs e) => await RunAsync(DiscoverConstraintsAsync);
    private async void RunImageTruthSweep_Click(object sender, RoutedEventArgs e) => await RunAsync(RunImageTruthSweepAsync);
    private async void PromoteFixtures_Click(object sender, RoutedEventArgs e) => await RunAsync(PromoteFixturesAsync);
    private async void NativeAssessment_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadNativeAssessmentAsync);
    private async void ApplyValidated_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyPendingEditsAsync(expertOverride: false));
    private async void ApplyExpert_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyPendingEditsAsync(expertOverride: true));
    private async void VerifyPersistence_Click(object sender, RoutedEventArgs e) => await RunAsync(VerifyPersistenceAsync);
    private async void RebootCamera_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ExecuteMaintenanceAsync("Reboot"));
    private async void FactoryDefault_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ExecuteMaintenanceAsync("FactoryReset"));
    private async void ApplyPasswordChange_Click(object sender, RoutedEventArgs e) => await RunAsync(ApplyPasswordChangeAsync);
    private async void StartRecording_Click(object sender, RoutedEventArgs e) => await RunAsync(StartRecordingAsync);
    private async void StopRecording_Click(object sender, RoutedEventArgs e) => await RunAsync(StopRecordingAsync);
    private async void RefreshRecordingIndex_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshRecordingIndexAsync);
    private async void ExportRecentClip_Click(object sender, RoutedEventArgs e) => await RunAsync(ExportRecentClipAsync);
    private async void RunNetworkRecovery_Click(object sender, RoutedEventArgs e) => await RunAsync(RunNetworkRecoveryAsync);

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

    private async Task PromoteFixturesAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var payload = new { exportRoot = "C:\\Users\\ceide\\Documents\\BossCamSuite\\artifacts" };
        var promoted = await PostAsync<List<EndpointContractFixture>>($"/api/contracts/fixtures/promote/{SelectedDevice.Id}", payload) ?? [];
        DiagnosticsText = JsonSerializer.Serialize(new { promoted = promoted.Count, fixtures = promoted.Take(20) }, SerializerOptions);
        await LoadTypedAsync();
    }

    private async Task DiscoverConstraintsAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new ConstraintDiscoveryRequest
        {
            DeviceId = SelectedDevice.Id,
            FieldKeys = new[] { "codec", "profile", "resolution", "bitrate", "frameRate", "wdr", "dayNight", "dhcpMode", "wirelessMode", "apChannel" },
            IncludeNetworkChanging = false,
            ExpertOverride = false,
            DelaySeconds = 2
        };
        var result = await PostAsync<ConstraintDiscoveryResult>($"/api/devices/{SelectedDevice.Id}/constraints/discover", request);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadSemanticTrustAsync();
        await LoadTypedAsync();
    }

    private async Task RunImageTruthSweepAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new { includeBehaviorMapping = true, refreshFromDevice = true, exportRoot = "fixtures" };
        var result = await PostAsync<JsonObject>($"/api/devices/{SelectedDevice.Id}/image/truth-sweep", request);
        DiagnosticsText = result?.ToJsonString(new JsonSerializerOptions(SerializerOptions) { WriteIndented = true }) ?? "No response.";
        await LoadImageTruthAsync();
        await LoadTypedAsync();
    }

    private async Task LoadNativeAssessmentAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var assessment = await GetAsync<NativeFallbackAssessment>($"/api/devices/{SelectedDevice.Id}/native-fallback-assessment");
        DiagnosticsText = JsonSerializer.Serialize(assessment, SerializerOptions);
    }

    private async Task LoadTypedAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        _fieldByKey.Clear();
        FieldProvenanceRows.Clear();
        _editorHintByKey.Clear();
        var groups = await GetAsync<List<TypedSettingGroupSnapshot>>($"/api/devices/{SelectedDevice.Id}/settings/typed") ?? [];
        foreach (var hint in groups.SelectMany(static group => group.EditorHints))
        {
            _editorHintByKey[hint.FieldKey] = hint;
        }

        foreach (var field in groups.SelectMany(static group => group.Fields))
        {
            _fieldByKey[field.FieldKey] = TypedFieldRow.FromField(field);
            FieldProvenanceRows.Add(FieldProvenanceRow.FromField(field));
        }

        RefreshContractOptions();
        await LoadPersistenceEligibleFieldsAsync();

        var proven = _fieldByKey.Values.Count(field => field.ReadVerified && field.WriteVerified);
        var inferred = _fieldByKey.Values.Count(field => field.ReadVerified && !field.WriteVerified);
        var unverified = _fieldByKey.Count - proven - inferred;
        HealthBadgeText = $"Health: proven={proven} inferred={inferred} unverified={unverified}";
        await LoadCapabilityProfileAsync(groups.Select(static group => group.FirmwareFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)));

        PopulateUserList();
        await LoadSemanticTrustAsync();
        await LoadImageTruthAsync();
        await LoadTruthBadgeAsync();
        NotifyAllEditorProperties();
    }

    private async Task LoadTruthBadgeAsync()
    {
        if (SelectedDevice is null)
        {
            FirmwareTruthBadge = "Truth: unknown";
            return;
        }

        var report = await GetAsync<TruthSweepReport>($"/api/truth/sweep?ips={SelectedDevice.IpAddress}");
        var profile = report?.Devices.FirstOrDefault(device => device.DeviceId == SelectedDevice.Id)
            ?? report?.Devices.FirstOrDefault(device => string.Equals(device.IpAddress, SelectedDevice.IpAddress, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            FirmwareTruthBadge = "Truth: no evidence profile";
            return;
        }

        var confidence = profile.EndpointsWriteVerified > 0
            ? "proven-write"
            : profile.EndpointsReadVerified > 0
                ? "read-only"
                : "unverified";
        FirmwareTruthBadge = $"Truth: {confidence} firmware={profile.FirmwareFingerprint} read={profile.EndpointsReadVerified} write={profile.EndpointsWriteVerified}";
    }

    private async Task LoadPersistenceEligibleFieldsAsync()
    {
        PersistenceFieldOptions.Clear();
        if (SelectedDevice is null)
        {
            return;
        }

        var fields = await GetAsync<List<PersistenceEligibleField>>($"/api/devices/{SelectedDevice.Id}/persistence/eligible-fields") ?? [];
        foreach (var field in fields.Where(static item => item.Supported).OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            PersistenceFieldOptions.Add(field.FieldKey);
        }

        SelectedPersistenceField = PersistenceFieldOptions.FirstOrDefault();
    }

    private void RefreshContractOptions()
    {
        SetEnumOptions(VideoCodecOptions, "codec", new[] { "H264", "H265" });
        SetEnumOptions(VideoProfileOptions, "profile", new[] { "Baseline", "Main", "High" });
        SetEnumOptions(VideoResolutionOptions, "resolution", new[] { "1920x1080", "1280x720", "640x360" });
        SetEnumOptions(VideoDayNightOptions, "dayNight", new[] { "Auto", "Day", "Night" });
        SetEnumOptions(VideoWdrOptions, "wdr", new[] { "Off", "On", "Auto" });
        SetEnumOptions(VideoIrCutOptions, "irCut", new[] { "Auto", "Day", "Night" });
        ApplyDependencyFilters();
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
        LastReachableUrl = string.IsNullOrWhiteSpace(SelectedDevice?.IpAddress) ? string.Empty : $"http://{SelectedDevice.IpAddress}:80";
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

    private async Task LoadSemanticTrustAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var semantic = await GetAsync<List<SemanticWriteObservation>>($"/api/devices/{SelectedDevice.Id}/semantic/history?limit=200") ?? [];
        var constraints = await GetAsync<List<FieldConstraintProfile>>($"/api/devices/{SelectedDevice.Id}/constraints") ?? [];
        var dependencies = await GetAsync<List<DependencyMatrixProfile>>($"/api/devices/{SelectedDevice.Id}/dependencies") ?? [];
        ReplaceCollection(SemanticHistoryRows, semantic);
        ReplaceCollection(ConstraintRows, constraints
            .OrderBy(static row => row.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static row => new FieldConstraintRow
            {
                FieldKey = row.FieldKey,
                ContractKey = row.ContractKey,
                SupportedValues = string.Join(",", row.SupportedValues),
                Min = row.Min?.ToString() ?? string.Empty,
                Max = row.Max?.ToString() ?? string.Empty,
                Quality = row.Quality.ToString(),
                Notes = row.Notes
            }));
        _dependencyRules.Clear();
        _dependencyRules.AddRange(dependencies.SelectMany(static dep => dep.Rules));
        ReplaceCollection(DependencyRows, _dependencyRules.Select(rule => new FieldDependencyRuleRow
        {
            PrimaryFieldKey = rule.PrimaryFieldKey,
            DependsOnFieldKey = rule.DependsOnFieldKey,
            DependsOnValues = string.Join(",", rule.DependsOnValues),
            AllowedPrimaryValues = string.Join(",", rule.AllowedPrimaryValues),
            Notes = rule.Notes ?? string.Empty,
            Quality = rule.Quality.ToString()
        }));

        var latestNetwork = semantic
            .Where(static row => row.DisruptionClass == DisruptionClass.NetworkChanging)
            .OrderByDescending(static row => row.Timestamp)
            .FirstOrDefault();
        if (latestNetwork is not null)
        {
            NetworkRecoveryHint = latestNetwork.Status == SemanticWriteStatus.Uncertain
                ? "Last network write ended uncertain. Use recovery workflow and reprobe before more network changes."
                : $"Last network semantic status: {latestNetwork.Status}";
        }

        ApplyDependencyFilters();
    }

    private async Task LoadImageTruthAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var inventory = await GetAsync<List<ImageControlInventoryItem>>($"/api/devices/{SelectedDevice.Id}/image/inventory") ?? [];
        var maps = await GetAsync<List<ImageFieldBehaviorMap>>($"/api/devices/{SelectedDevice.Id}/image/behavior-maps") ?? [];
        ReplaceCollection(ImageInventoryRows, inventory.OrderBy(item => item.FieldKey, StringComparer.OrdinalIgnoreCase).ToList());
        ReplaceCollection(PromotedImageRows, inventory
            .Where(static item => item.PromotedToUi)
            .OrderBy(item => item.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PromotedImageRow
            {
                FieldKey = item.FieldKey,
                Classification = ClassificationBadge(item),
                Status = item.Status.ToString(),
                SourceEndpoint = item.SourceEndpoint,
                Notes = string.IsNullOrWhiteSpace(item.Notes)
                    ? string.Join(",", item.ReasonCodes)
                    : $"{item.Notes} ({string.Join(",", item.ReasonCodes)})"
            })
            .ToList());
        _imageBehaviorByField.Clear();
        foreach (var map in maps)
        {
            _imageBehaviorByField[map.FieldKey] = map;
        }

        ReplaceCollection(ImageBehaviorRows, maps
            .OrderBy(map => map.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static map => new ImageBehaviorRow
            {
                FieldKey = map.FieldKey,
                SafeRange = map.RecommendedRange,
                Thresholds = string.Join(", ", map.Thresholds.Select(value => value.ToString("0.##"))),
                CatastrophicValues = string.Join(", ", map.CatastrophicValues.Select(value => value.ToString("0.##"))),
                TriggerSequence = map.TriggerSequence,
                TruthState = map.TruthState.ToString()
            })
            .ToList());

        var writable = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.Writable);
        var readOnly = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.ReadableOnly);
        var hidden = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.HiddenAdjacentCandidate);
        var needsAuth = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.NoSemanticProof);
        var altShape = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.AltWriteShapeRequired);
        var likely = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.LikelyUnsupported);
        var unsupported = inventory.Count(item => item.CandidateClassification == HiddenCandidateClassification.UnsupportedOnFirmware);
        ImageTruthSummary = $"Inventory: {inventory.Count} fields | proven={writable} | read-only={readOnly} | alt-write-shape={altShape} | hidden={hidden} | needs-auth/no-proof={needsAuth} | likely-unsupported={likely} | unsupported={unsupported}. Behavior maps={maps.Count}.";
        NotifyAllEditorProperties();
    }

    private static string ClassificationBadge(ImageControlInventoryItem item)
        => item.CandidateClassification switch
        {
            HiddenCandidateClassification.Writable => "Proven",
            HiddenCandidateClassification.ReadableOnly => "Read-only",
            HiddenCandidateClassification.HiddenAdjacentCandidate => "Hidden candidate",
            HiddenCandidateClassification.PrivatePathCandidate => "Private path candidate",
            HiddenCandidateClassification.NoSemanticProof => "Needs live auth / No semantic proof",
            HiddenCandidateClassification.AltWriteShapeRequired => "Alt write shape required",
            HiddenCandidateClassification.LikelyUnsupported => "Likely unsupported",
            HiddenCandidateClassification.UnsupportedOnFirmware => "Unsupported",
            HiddenCandidateClassification.RejectedByFirmware => "Rejected by firmware",
            HiddenCandidateClassification.RequiresCommitTrigger => "Requires commit trigger",
            HiddenCandidateClassification.Dangerous => "Dangerous",
            HiddenCandidateClassification.Ignored => "Ignored",
            _ => item.CandidateClassification.ToString()
        };

    private async Task LoadTranscriptsAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var transcripts = await GetAsync<List<EndpointTranscript>>($"/api/devices/{SelectedDevice.Id}/validation/transcripts?limit=200") ?? [];
        var contracts = await GetAsync<List<EndpointContract>>($"/api/contracts/endpoints?deviceId={SelectedDevice.Id}") ?? [];
        var fixtures = await GetAsync<List<EndpointContractFixture>>($"/api/contracts/fixtures?deviceId={SelectedDevice.Id}") ?? [];
        TranscriptPreview = JsonSerializer.Serialize(transcripts.Take(25), SerializerOptions);
        DiagnosticsText = JsonSerializer.Serialize(new
        {
            transcriptCount = transcripts.Count,
            contractCount = contracts.Count,
            fixtureCount = fixtures.Count,
            contracts = contracts.Select(contract => new { contract.ContractKey, contract.Endpoint, contract.Method, contract.TruthState }),
            fixtures = fixtures.Take(20)
        }, SerializerOptions);
    }

    private async Task ApplyPendingEditsAsync(bool expertOverride)
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var comboErrors = ValidateCompatibilityEdits();
        if (comboErrors.Count > 0)
        {
            DiagnosticsText = "Compatibility block: " + string.Join(" | ", comboErrors);
            return;
        }

        var changes = _fieldByKey.Values
            .Where(field => !string.Equals(field.EditableValue, field.OriginalValue, StringComparison.Ordinal))
            .Where(field => expertOverride
                ? field.SupportState != nameof(ContractSupportState.Unsupported)
                : field.WriteVerified
                    && field.SupportState == nameof(ContractSupportState.Supported)
                    && !field.ExpertOnly)
            .Select(field => new TypedFieldChange(field.FieldKey, ParseNode(field.EditableValue)))
            .ToList();

        if (changes.Count == 0)
        {
            DiagnosticsText = "No contract-supported field changes to apply.";
            return;
        }

        var applied = await PostAsync<List<WriteResult>>($"/api/devices/{SelectedDevice.Id}/settings/typed/apply-batch",
            new TypedSettingBatchApplyRequest(changes, expertOverride)) ?? [];
        var recoveryMessage = applied
            .Select(result => result.Message ?? string.Empty)
            .FirstOrDefault(message => message.Contains("networkRecovery:recovered:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(recoveryMessage))
        {
            var marker = "networkRecovery:recovered:";
            var index = recoveryMessage.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                LastReachableUrl = recoveryMessage[(index + marker.Length)..].Split(';', StringSplitOptions.TrimEntries)[0];
            }
        }

        DiagnosticsText = JsonSerializer.Serialize(applied.Select(result => new
        {
            result.Success,
            result.SemanticStatus,
            result.Message,
            result.ContractKey,
            result.ContractViolations
        }), SerializerOptions);
        await LoadTypedAsync();
        await LoadValidationAsync();
        await LoadSemanticTrustAsync();
    }

    private List<string> ValidateCompatibilityEdits()
    {
        var errors = new List<string>();
        if (_fieldByKey.TryGetValue("resolution", out var resolutionField) && VideoResolutionOptions.Count > 0)
        {
            var resolution = resolutionField.EditableValue;
            if (!string.IsNullOrWhiteSpace(resolution) && !VideoResolutionOptions.Contains(resolution))
            {
                errors.Add($"resolution '{resolution}' is not valid for current codec/profile constraints");
            }
        }

        if (_fieldByKey.TryGetValue("frameRate", out var fpsField))
        {
            if (int.TryParse(fpsField.EditableValue, out var fps))
            {
                var max = (int)HintMax("frameRate", 60);
                if (fps > max)
                {
                    errors.Add($"frameRate '{fps}' exceeds constrained max '{max}'");
                }
            }
        }

        if (_fieldByKey.TryGetValue("bitrate", out var bitrateField))
        {
            if (int.TryParse(bitrateField.EditableValue, out var bitrate))
            {
                var min = (int)HintMin("bitrate", 64);
                var max = (int)HintMax("bitrate", 16384);
                if (bitrate < min || bitrate > max)
                {
                    errors.Add($"bitrate '{bitrate}' outside constrained range {min}-{max}");
                }
            }
        }

        return errors;
    }

    private async Task VerifyPersistenceAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedPersistenceField) || !_fieldByKey.TryGetValue(SelectedPersistenceField, out var field))
        {
            DiagnosticsText = "No eligible persistence field selected.";
            return;
        }

        var result = await PostAsync<PersistenceVerificationResult>($"/api/devices/{SelectedDevice.Id}/persistence/verify-field", new PersistenceFieldVerifyRequest(field.FieldKey, ParseNode(field.EditableValue), false, false));
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadValidationAsync();
        await LoadSemanticTrustAsync();
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

    private async Task RunNetworkRecoveryAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var context = new NetworkRecoveryContext
        {
            DeviceId = SelectedDevice.Id,
            PreviousIp = SelectedDevice.IpAddress,
            PreviousGateway = NetworkGateway,
            PreviousDns = NetworkDns,
            PreviousControlUrl = string.IsNullOrWhiteSpace(LastReachableUrl) ? CurrentControlUrl : LastReachableUrl,
            PredictedControlUrl = PredictedControlUrl
        };
        var result = await PostAsync<NetworkRecoveryResult>($"/api/devices/{SelectedDevice.Id}/network/recovery", context);
        if (result is not null && result.Recovered && !string.IsNullOrWhiteSpace(result.ReachableUrl))
        {
            LastReachableUrl = result.ReachableUrl;
        }
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

        if (_passwordChangeValue.Length < 8)
        {
            MaintenanceState = "Password must be at least 8 characters.";
            return;
        }

        if (MessageBox.Show($"Apply password change for user '{PasswordChangeUsername}'?", "Confirm Password Change", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            MaintenanceState = "Password change cancelled.";
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

    private async Task StartRecordingAsync()
    {
        if (SelectedDevice is null)
        {
            RecordingState = "Select a device first.";
            return;
        }

        var job = await PostAsync<RecordingJob>("/api/recordings/start", new RecordingStartRequest { DeviceId = SelectedDevice.Id });
        if (job is null)
        {
            RecordingState = "Start recording failed.";
            return;
        }

        RecordingState = $"Recording started. Job={job.Id} PID={job.ProcessId}";
        RecordingDiagnostics = JsonSerializer.Serialize(job, SerializerOptions);
    }

    private async Task StopRecordingAsync()
    {
        var jobs = await GetAsync<List<RecordingJob>>("/api/recordings/jobs") ?? [];
        if (SelectedDevice is not null)
        {
            jobs = jobs.Where(job => job.DeviceId == SelectedDevice.Id).ToList();
        }

        if (jobs.Count == 0)
        {
            RecordingState = "No running recording jobs.";
            return;
        }

        var stopped = new List<RecordingJob>();
        foreach (var job in jobs)
        {
            var result = await PostAsync<RecordingJob>($"/api/recordings/stop/{job.Id}", null);
            if (result is not null)
            {
                stopped.Add(result);
            }
        }

        RecordingState = $"Stopped {stopped.Count} recording job(s).";
        RecordingDiagnostics = JsonSerializer.Serialize(stopped, SerializerOptions);
    }

    private async Task RefreshRecordingIndexAsync()
    {
        if (SelectedDevice is null)
        {
            RecordingState = "Select a device first.";
            return;
        }

        var indexed = await PostAsync<List<RecordingSegment>>($"/api/recordings/index/refresh?deviceId={SelectedDevice.Id}", null) ?? [];
        RecordingState = $"Indexed {indexed.Count} segment(s).";
        RecordingDiagnostics = JsonSerializer.Serialize(indexed.Take(50), SerializerOptions);
    }

    private async Task ExportRecentClipAsync()
    {
        if (SelectedDevice is null)
        {
            RecordingState = "Select a device first.";
            return;
        }

        var now = DateTimeOffset.Now;
        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"BossCam_{SelectedDevice.Id:N}_{now:yyyyMMdd_HHmmss}.mp4");
        var request = new ClipExportRequest
        {
            DeviceId = SelectedDevice.Id,
            StartTime = now.AddMinutes(-10),
            EndTime = now,
            OutputPath = output
        };
        var result = await PostAsync<ClipExportResult>("/api/recordings/export", request);
        RecordingState = result is null ? "Clip export failed." : (result.Success ? $"Clip exported: {result.OutputPath}" : $"Export failed: {result.Message}");
        RecordingDiagnostics = JsonSerializer.Serialize(result, SerializerOptions);
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
            if (key.Equals("ip", StringComparison.OrdinalIgnoreCase) || key.Equals("ports", StringComparison.OrdinalIgnoreCase))
            {
                OnPropertyChanged(nameof(CurrentControlUrl));
                OnPropertyChanged(nameof(PredictedControlUrl));
            }
        }
    }

    private bool CanEdit(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return false;
        }

        // Contract-driven gating: normal editor only enabled for proven writable + supported + non-expert fields.
        return field.WriteVerified && field.SupportState.Equals(nameof(ContractSupportState.Supported), StringComparison.OrdinalIgnoreCase) && !field.ExpertOnly;
    }

    private Visibility FieldVisibility(string key, bool requireStaticNetwork = false)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return Visibility.Collapsed;
        }

        if (field.SupportState.Equals(nameof(ContractSupportState.Unsupported), StringComparison.OrdinalIgnoreCase))
        {
            return Visibility.Collapsed;
        }

        if (requireStaticNetwork)
        {
            var dhcpMode = GetValue("dhcpMode");
            if (bool.TryParse(dhcpMode, out var dhcpEnabled) && dhcpEnabled)
            {
                return Visibility.Collapsed;
            }
        }

        return Visibility.Visible;
    }

    private string FieldState(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return "unsupported";
        }

        if (field.SupportState.Equals(nameof(ContractSupportState.Uncertain), StringComparison.OrdinalIgnoreCase))
        {
            return "uncertain";
        }

        if (field.Validity.Equals(nameof(FieldValidityState.Invalid), StringComparison.OrdinalIgnoreCase))
        {
            return "invalid";
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

    private double HintMin(string key, double fallback)
    {
        if (_imageBehaviorByField.TryGetValue(key, out var behavior) && behavior.SafeMin is decimal safeMin)
        {
            return (double)safeMin;
        }

        if (_editorHintByKey.TryGetValue(key, out var hint) && hint.Min is decimal min)
        {
            return (double)min;
        }

        return fallback;
    }

    private double HintMax(string key, double fallback)
    {
        if (_imageBehaviorByField.TryGetValue(key, out var behavior) && behavior.SafeMax is decimal safeMax)
        {
            return (double)safeMax;
        }

        if (_editorHintByKey.TryGetValue(key, out var hint) && hint.Max is decimal max)
        {
            return (double)max;
        }

        return fallback;
    }

    private void SetEnumOptions(ObservableCollection<string> target, string fieldKey, IEnumerable<string> fallback)
    {
        target.Clear();
        if (_editorHintByKey.TryGetValue(fieldKey, out var hint) && hint.EnumValues is JsonArray arr && arr.Count > 0)
        {
            foreach (var item in arr)
            {
                if (item is null)
                {
                    continue;
                }

                target.Add(item.ToJsonString().Trim('\"'));
            }

            return;
        }

        if (!_fieldByKey.TryGetValue(fieldKey, out var field)
            || !field.TruthState.Equals(nameof(ContractTruthState.Proven), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var value in fallback)
        {
            target.Add(value);
        }
    }

    private string BehaviorBadge(string fieldKey)
    {
        if (!_imageBehaviorByField.TryGetValue(fieldKey, out var behavior))
        {
            return "unmapped";
        }

        var threshold = behavior.Thresholds.Count > 0
            ? $" thresholds:{string.Join("/", behavior.Thresholds.Select(value => value.ToString("0.##")))}"
            : string.Empty;
        var cliffs = behavior.CatastrophicValues.Count > 0
            ? $" cliffs:{string.Join("/", behavior.CatastrophicValues.Select(value => value.ToString("0.##")))}"
            : string.Empty;
        var trigger = !string.IsNullOrWhiteSpace(behavior.TriggerSequence) && !behavior.TriggerSequence.Equals("none-observed", StringComparison.OrdinalIgnoreCase)
            ? $" trigger:{behavior.TriggerSequence}"
            : string.Empty;
        return $"safe:{behavior.RecommendedRange}{threshold}{cliffs}{trigger}";
    }

    private void ApplyDependencyFilters()
    {
        if (_dependencyRules.Count == 0)
        {
            return;
        }

        var codec = GetValue("codec");
        var resolutionRules = _dependencyRules
            .Where(rule => rule.PrimaryFieldKey.Equals("resolution", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnFieldKey.Equals("codec", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnValues.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (resolutionRules.Count > 0)
        {
            var allowed = resolutionRules.SelectMany(static rule => rule.AllowedPrimaryValues).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            FilterOptions(VideoResolutionOptions, allowed);
        }

        var resolution = GetValue("resolution");
        var fpsRules = _dependencyRules
            .Where(rule => rule.PrimaryFieldKey.Equals("frameRate", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnFieldKey.Equals("resolution", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnValues.Contains(resolution, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (fpsRules.Count > 0)
        {
            var fpsNumbers = fpsRules
                .SelectMany(static rule => rule.AllowedPrimaryValues)
                .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
                .Where(static value => value > 0)
                .ToList();
            if (fpsNumbers.Count > 0)
            {
                var fpsMax = fpsNumbers.Max();
                if (_editorHintByKey.TryGetValue("frameRate", out var hint))
                {
                    _editorHintByKey["frameRate"] = hint with { Max = fpsMax };
                }
            }
        }

        var profileRules = _dependencyRules
            .Where(rule => rule.PrimaryFieldKey.Equals("profile", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnFieldKey.Equals("codec", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnValues.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (profileRules.Count > 0)
        {
            var allowed = profileRules.SelectMany(static rule => rule.AllowedPrimaryValues).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            FilterOptions(VideoProfileOptions, allowed);
        }

        var bitrateRules = _dependencyRules
            .Where(rule => rule.PrimaryFieldKey.Equals("bitrate", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnFieldKey.Equals("codec", StringComparison.OrdinalIgnoreCase)
                && rule.DependsOnValues.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (bitrateRules.Count > 0)
        {
            var values = bitrateRules
                .SelectMany(static rule => rule.AllowedPrimaryValues)
                .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
                .Where(static value => value > 0)
                .ToList();
            if (values.Count > 0 && _editorHintByKey.TryGetValue("bitrate", out var hint))
            {
                _editorHintByKey["bitrate"] = hint with { Min = values.Min(), Max = values.Max() };
            }
        }
    }

    private static void FilterOptions(ObservableCollection<string> target, IReadOnlyCollection<string> allowed)
    {
        var remove = target.Where(item => !allowed.Contains(item, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var item in remove)
        {
            target.Remove(item);
        }
    }

    private static double ParseDouble(string raw, double fallback)
        => double.TryParse(raw, out var parsed) ? parsed : fallback;

    private static string BuildUrlFromIpPort(string? ip, string? portRaw)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return string.Empty;
        }

        if (!int.TryParse(portRaw, out var port))
        {
            port = 80;
        }

        return $"http://{ip}:{port}";
    }

    private void NotifyAllEditorProperties()
    {
        foreach (var name in new[]
        {
            nameof(VideoCodec), nameof(VideoProfile), nameof(VideoDayNight), nameof(VideoWdr), nameof(VideoIrCut),
            nameof(VideoResolution), nameof(VideoResolutionOptions),
            nameof(VideoBitrate), nameof(VideoBitrateSlider), nameof(VideoBitrateMin), nameof(VideoBitrateMax),
            nameof(VideoFpsHint),
            nameof(VideoFrameRate), nameof(ImageBrightness), nameof(ImageBrightnessSlider), nameof(ImageBrightnessMin), nameof(ImageBrightnessMax),
            nameof(ImageContrast), nameof(ImageSaturation), nameof(ImageHue), nameof(ImageSharpness),
            nameof(ImageBrightnessBehaviorBadge), nameof(ImageContrastBehaviorBadge), nameof(ImageSaturationBehaviorBadge), nameof(ImageTruthSummary),
            nameof(NetworkIp), nameof(NetworkNetmask), nameof(NetworkGateway), nameof(NetworkDns), nameof(NetworkPort),
            nameof(CurrentControlUrl), nameof(PredictedControlUrl),
            nameof(WirelessApSsid), nameof(WirelessApChannel),
            nameof(VideoCodecState), nameof(VideoProfileState), nameof(NetworkIpState), nameof(WirelessApPskState),
            nameof(CanEditVideoCodec), nameof(CanEditVideoProfile), nameof(CanEditVideoResolution), nameof(CanEditVideoDayNight), nameof(CanEditVideoWdr), nameof(CanEditVideoIrCut),
            nameof(CanEditNetworkIp), nameof(CanEditNetworkNetmask), nameof(CanEditNetworkGateway), nameof(CanEditNetworkDns), nameof(CanEditNetworkPort),
            nameof(CanEditWirelessApSsid), nameof(CanEditWirelessApChannel),
            nameof(VideoCodecVisibility), nameof(VideoProfileVisibility), nameof(VideoDayNightVisibility), nameof(VideoWdrVisibility), nameof(VideoIrCutVisibility), nameof(VideoResolutionVisibility),
            nameof(VideoBitrateVisibility), nameof(VideoFrameRateVisibility), nameof(ImageBrightnessVisibility), nameof(ImageContrastVisibility), nameof(ImageSaturationVisibility),
            nameof(ImageHueVisibility), nameof(ImageSharpnessVisibility), nameof(NetworkIpVisibility), nameof(NetworkNetmaskVisibility), nameof(NetworkGatewayVisibility),
            nameof(NetworkDnsVisibility), nameof(NetworkPortVisibility), nameof(WirelessApSsidVisibility), nameof(WirelessApPskVisibility), nameof(WirelessApChannelVisibility),
            nameof(SelectedPersistenceField), nameof(LastReachableUrl)
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
            SetValue("apPsk", _wirelessApPsk);
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
    public string SupportState { get; init; } = string.Empty;
    public string TruthState { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public bool PersistenceExpectedAfterReboot { get; init; }
    public string EditableValue { get; set; } = string.Empty;
    public string OriginalValue { get; init; } = string.Empty;

    public static TypedFieldRow FromField(NormalizedSettingField field)
    {
        var editable = ToEditableString(field.TypedValue);
        return new()
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
            SupportState = field.SupportState.ToString(),
            TruthState = field.TruthState.ToString(),
            ContractKey = field.ContractKey ?? string.Empty,
            GroupName = field.GroupName,
            PersistenceExpectedAfterReboot = field.PersistenceExpectedAfterReboot,
            EditableValue = editable,
            OriginalValue = editable
        };
    }

    private static string ToEditableString(JsonNode? value)
    {
        if (value is JsonValue node && node.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value?.ToJsonString() ?? string.Empty;
    }
}

public sealed class FieldProvenanceRow
{
    public string FieldKey { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string TruthState { get; init; } = string.Empty;
    public string SupportState { get; init; } = string.Empty;
    public string Validity { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string RawSourcePath { get; init; } = string.Empty;

    public static FieldProvenanceRow FromField(NormalizedSettingField field)
        => new()
        {
            FieldKey = field.FieldKey,
            ContractKey = field.ContractKey ?? string.Empty,
            TruthState = field.TruthState.ToString(),
            SupportState = field.SupportState.ToString(),
            Validity = field.Validity.ToString(),
            SourceEndpoint = field.SourceEndpoint,
            RawSourcePath = field.RawSourcePath
        };
}

public sealed class FieldDependencyRuleRow
{
    public string PrimaryFieldKey { get; init; } = string.Empty;
    public string DependsOnFieldKey { get; init; } = string.Empty;
    public string DependsOnValues { get; init; } = string.Empty;
    public string AllowedPrimaryValues { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string Quality { get; init; } = string.Empty;
}

public sealed class FieldConstraintRow
{
    public string FieldKey { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string SupportedValues { get; init; } = string.Empty;
    public string Min { get; init; } = string.Empty;
    public string Max { get; init; } = string.Empty;
    public string Quality { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

public sealed class ImageBehaviorRow
{
    public string FieldKey { get; init; } = string.Empty;
    public string SafeRange { get; init; } = string.Empty;
    public string Thresholds { get; init; } = string.Empty;
    public string CatastrophicValues { get; init; } = string.Empty;
    public string TriggerSequence { get; init; } = string.Empty;
    public string TruthState { get; init; } = string.Empty;
}

public sealed class PromotedImageRow
{
    public string FieldKey { get; init; } = string.Empty;
    public string Classification { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

public sealed record TypedSettingApplyRequest(string FieldKey, JsonNode? Value, bool ExpertOverride);
public sealed record TypedSettingBatchApplyRequest(IReadOnlyCollection<TypedFieldChange> Changes, bool ExpertOverride);
public sealed record PersistenceFieldVerifyRequest(string FieldKey, JsonNode? Value, bool RebootForVerification, bool ExpertOverride);
