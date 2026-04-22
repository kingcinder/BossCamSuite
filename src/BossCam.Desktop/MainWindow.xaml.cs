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
using System.Windows.Threading;
using BossCam.Contracts;
using Microsoft.Win32;

namespace BossCam.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly IReadOnlyDictionary<string, ControlPointWidgetKind> CurrentWidgetKinds = new Dictionary<string, ControlPointWidgetKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["codec"] = ControlPointWidgetKind.Dropdown,
        ["profile"] = ControlPointWidgetKind.Dropdown,
        ["dayNight"] = ControlPointWidgetKind.Dropdown,
        ["irMode"] = ControlPointWidgetKind.Dropdown,
        ["irCut"] = ControlPointWidgetKind.Dropdown,
        ["irCutMethod"] = ControlPointWidgetKind.Dropdown,
        ["sceneMode"] = ControlPointWidgetKind.Dropdown,
        ["exposure"] = ControlPointWidgetKind.Dropdown,
        ["awb"] = ControlPointWidgetKind.Dropdown,
        ["lowlight"] = ControlPointWidgetKind.Dropdown,
        ["resolution"] = ControlPointWidgetKind.Dropdown,
        ["bitrateMode"] = ControlPointWidgetKind.Dropdown,
        ["definition"] = ControlPointWidgetKind.Dropdown,
        ["wirelessMode"] = ControlPointWidgetKind.Dropdown,
        ["apMode"] = ControlPointWidgetKind.Dropdown,
        ["motionType"] = ControlPointWidgetKind.Dropdown,
        ["osdDateFormat"] = ControlPointWidgetKind.Dropdown,
        ["osdTimeFormat"] = ControlPointWidgetKind.Dropdown,
        ["brightness"] = ControlPointWidgetKind.Slider,
        ["contrast"] = ControlPointWidgetKind.Slider,
        ["saturation"] = ControlPointWidgetKind.Slider,
        ["sharpness"] = ControlPointWidgetKind.Slider,
        ["hue"] = ControlPointWidgetKind.Slider,
        ["gamma"] = ControlPointWidgetKind.Slider,
        ["manualSharpness"] = ControlPointWidgetKind.Slider,
        ["denoise"] = ControlPointWidgetKind.Slider,
        ["wdrStrength"] = ControlPointWidgetKind.Slider,
        ["whiteLight"] = ControlPointWidgetKind.Slider,
        ["infrared"] = ControlPointWidgetKind.Slider,
        ["motionSensitivity"] = ControlPointWidgetKind.Slider,
        ["mirror"] = ControlPointWidgetKind.Toggle,
        ["flip"] = ControlPointWidgetKind.Toggle,
        ["audioEnabled"] = ControlPointWidgetKind.Toggle,
        ["osdChannelNameEnabled"] = ControlPointWidgetKind.Toggle,
        ["osdDateTimeEnabled"] = ControlPointWidgetKind.Toggle,
        ["osdDisplayWeek"] = ControlPointWidgetKind.Toggle,
        ["motionEnabled"] = ControlPointWidgetKind.Toggle,
        ["alarmEnabled"] = ControlPointWidgetKind.Toggle,
        ["alarmBuzzer"] = ControlPointWidgetKind.Toggle,
        ["eseeEnabled"] = ControlPointWidgetKind.Toggle,
        ["ntpEnabled"] = ControlPointWidgetKind.Toggle,
        ["dhcpMode"] = ControlPointWidgetKind.Toggle,
        ["bitrate"] = ControlPointWidgetKind.NumericInput,
        ["frameRate"] = ControlPointWidgetKind.NumericInput,
        ["keyframeInterval"] = ControlPointWidgetKind.NumericInput,
        ["audioBitRate"] = ControlPointWidgetKind.NumericInput,
        ["audioSampleRate"] = ControlPointWidgetKind.NumericInput,
        ["osd"] = ControlPointWidgetKind.TextInput,
        ["osdChannelNameText"] = ControlPointWidgetKind.TextInput,
        ["ip"] = ControlPointWidgetKind.TextInput,
        ["netmask"] = ControlPointWidgetKind.TextInput,
        ["gateway"] = ControlPointWidgetKind.TextInput,
        ["dns"] = ControlPointWidgetKind.TextInput,
        ["ports"] = ControlPointWidgetKind.TextInput,
        ["ntpServerDomain"] = ControlPointWidgetKind.TextInput,
        ["eseeId"] = ControlPointWidgetKind.TextInput,
        ["apSsid"] = ControlPointWidgetKind.TextInput,
        ["apPsk"] = ControlPointWidgetKind.TextInput,
        ["apChannel"] = ControlPointWidgetKind.TextInput,
        ["alarmInputActiveState"] = ControlPointWidgetKind.TextInput,
        ["alarmOutputActiveState"] = ControlPointWidgetKind.TextInput,
        ["alarmPulseDuration"] = ControlPointWidgetKind.TextInput,
        ["privacyMaskEnabled"] = ControlPointWidgetKind.Toggle,
        ["privacyMaskX"] = ControlPointWidgetKind.TextInput,
        ["privacyMaskY"] = ControlPointWidgetKind.TextInput,
        ["privacyMaskWidth"] = ControlPointWidgetKind.TextInput,
        ["privacyMaskHeight"] = ControlPointWidgetKind.TextInput
    };
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
    private string _storageSessionId = "0";
    private string _storageChannelId = "1";
    private string _storageMediaType = "all";
    private string _storageBeginTime = DateTimeOffset.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss");
    private string _storageEndTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
    private string _storageCursor = string.Empty;
    private string _storageFileName = string.Empty;
    private string _storageSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BossCam_Playback_Export.mp4");
    private string _storageHandleId = "0";
    private string _firmwareTruthBadge = "Truth: unknown";
    private string _imageTruthSummary = "Image truth not loaded.";
    private string _passwordChangeUsername = string.Empty;
    private string _passwordChangeValue = string.Empty;
    private string _wirelessApPsk = string.Empty;
    private string? _selectedPersistenceField;
    private string _lastReachableUrl = string.Empty;
    private string _lastSyncText = "Last sync: never";
    private string _groupedApplyIndicator = "Grouped apply: unknown";
    private string _toastMessage = string.Empty;
    private string _toastBackground = "#2B3C4B";
    private Visibility _toastVisibility = Visibility.Collapsed;
    private string _firmwareUploadPath = string.Empty;
    private EndpointSurfaceRow? _selectedEndpointSurface;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly List<FieldDependencyRule> _dependencyRules = [];
    private readonly Dictionary<string, ImageFieldBehaviorMap> _imageBehaviorByField = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupedUnsupportedRetestResult> _groupedRetestByField = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DeviceIdentity> Devices { get; } = [];
    public ObservableCollection<EndpointValidationResult> ValidationMatrix { get; } = [];
    public ObservableCollection<FieldProvenanceRow> FieldProvenanceRows { get; } = [];
    public ObservableCollection<SemanticWriteObservation> SemanticHistoryRows { get; } = [];
    public ObservableCollection<FieldConstraintRow> ConstraintRows { get; } = [];
    public ObservableCollection<FieldDependencyRuleRow> DependencyRows { get; } = [];
    public ObservableCollection<ImageControlInventoryItem> ImageInventoryRows { get; } = [];
    public ObservableCollection<ControlPointInventoryItem> ControlPointInventoryRows { get; } = [];
    public ObservableCollection<EndpointSurfaceRow> EndpointSurfaceRows { get; } = [];
    public ObservableCollection<ImageBehaviorRow> ImageBehaviorRows { get; } = [];
    public ObservableCollection<PromotedImageRow> PromotedImageRows { get; } = [];
    public ObservableCollection<ProbeStageMode> ProbeModes { get; } = new(Enum.GetValues<ProbeStageMode>());
    public ObservableCollection<string> UserList { get; } = [];
    public ObservableCollection<string> PersistenceFieldOptions { get; } = [];
    public ObservableCollection<string> VideoCodecOptions { get; } = [];
    public ObservableCollection<string> VideoProfileOptions { get; } = [];
    public ObservableCollection<string> VideoDayNightOptions { get; } = [];
    public ObservableCollection<string> VideoIrModeOptions { get; } = [];
    public ObservableCollection<string> VideoWdrOptions { get; } = [];
    public ObservableCollection<string> VideoIrCutOptions { get; } = [];
    public ObservableCollection<string> VideoIrCutMethodOptions { get; } = [];
    public ObservableCollection<string> VideoSceneModeOptions { get; } = [];
    public ObservableCollection<string> VideoExposureOptions { get; } = [];
    public ObservableCollection<string> VideoAwbOptions { get; } = [];
    public ObservableCollection<string> VideoLowlightOptions { get; } = [];
    public ObservableCollection<string> VideoOsdDateFormatOptions { get; } = [];
    public ObservableCollection<string> VideoOsdTimeFormatOptions { get; } = [];
    public ObservableCollection<string> VideoBitrateModeOptions { get; } = [];
    public ObservableCollection<string> VideoDefinitionOptions { get; } = [];
    public ObservableCollection<string> WirelessModeOptions { get; } = [];
    public ObservableCollection<string> WirelessApModeOptions { get; } = [];
    public ObservableCollection<string> MotionTypeOptions { get; } = [];
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

    public EndpointSurfaceRow? SelectedEndpointSurface
    {
        get => _selectedEndpointSurface;
        set
        {
            if (!Equals(_selectedEndpointSurface, value))
            {
                _selectedEndpointSurface = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndpointCurrentPayloadText));
                OnPropertyChanged(nameof(EndpointEditablePayloadText));
                OnPropertyChanged(nameof(CanExecuteSelectedEndpoint));
                OnPropertyChanged(nameof(SelectedEndpointExecutionLabel));
                OnPropertyChanged(nameof(SelectedEndpointHint));
            }
        }
    }

    public string StorageSessionId
    {
        get => _storageSessionId;
        set
        {
            if (_storageSessionId != value)
            {
                _storageSessionId = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageChannelId
    {
        get => _storageChannelId;
        set
        {
            if (_storageChannelId != value)
            {
                _storageChannelId = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageMediaType
    {
        get => _storageMediaType;
        set
        {
            if (_storageMediaType != value)
            {
                _storageMediaType = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageBeginTime
    {
        get => _storageBeginTime;
        set
        {
            if (_storageBeginTime != value)
            {
                _storageBeginTime = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageEndTime
    {
        get => _storageEndTime;
        set
        {
            if (_storageEndTime != value)
            {
                _storageEndTime = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageCursor
    {
        get => _storageCursor;
        set
        {
            if (_storageCursor != value)
            {
                _storageCursor = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageFileName
    {
        get => _storageFileName;
        set
        {
            if (_storageFileName != value)
            {
                _storageFileName = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageSavePath
    {
        get => _storageSavePath;
        set
        {
            if (_storageSavePath != value)
            {
                _storageSavePath = value;
                OnPropertyChanged();
            }
        }
    }

    public string StorageHandleId
    {
        get => _storageHandleId;
        set
        {
            if (_storageHandleId != value)
            {
                _storageHandleId = value;
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

    public string LastSyncText
    {
        get => _lastSyncText;
        set
        {
            if (_lastSyncText != value)
            {
                _lastSyncText = value;
                OnPropertyChanged();
            }
        }
    }

    public string GroupedApplyIndicator
    {
        get => _groupedApplyIndicator;
        set
        {
            if (_groupedApplyIndicator != value)
            {
                _groupedApplyIndicator = value;
                OnPropertyChanged();
            }
        }
    }

    public string ToastMessage
    {
        get => _toastMessage;
        set
        {
            if (_toastMessage != value)
            {
                _toastMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string ToastBackground
    {
        get => _toastBackground;
        set
        {
            if (_toastBackground != value)
            {
                _toastBackground = value;
                OnPropertyChanged();
            }
        }
    }

    public Visibility ToastVisibility
    {
        get => _toastVisibility;
        set
        {
            if (_toastVisibility != value)
            {
                _toastVisibility = value;
                OnPropertyChanged();
            }
        }
    }

    public string FirmwareUploadPath
    {
        get => _firmwareUploadPath;
        set
        {
            if (_firmwareUploadPath != value)
            {
                _firmwareUploadPath = value;
                OnPropertyChanged();
            }
        }
    }

    public string EndpointCurrentPayloadText => SelectedEndpointSurface?.CurrentPayload ?? "{}";
    public string EndpointEditablePayloadText
    {
        get => SelectedEndpointSurface?.EditablePayload ?? "{}";
        set
        {
            if (SelectedEndpointSurface is not null && SelectedEndpointSurface.EditablePayload != value)
            {
                SelectedEndpointSurface.EditablePayload = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanExecuteSelectedEndpoint => SelectedEndpointSurface?.SupportsExecution == true && SelectedDevice is not null;
    public string SelectedEndpointExecutionLabel => SelectedEndpointSurface?.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) == true ? "Refresh Endpoint" : "Execute Endpoint";
    public string SelectedEndpointHint => SelectedEndpointSurface is null
        ? "Select an endpoint family to inspect or execute."
        : $"{SelectedEndpointSurface.Method} {SelectedEndpointSurface.Endpoint} | {SelectedEndpointSurface.DisruptionClass} | {(SelectedEndpointSurface.RequiresConfirmation ? "confirmation required" : "safe path")}";

    public bool HasPendingChanges => _fieldByKey.Values.Any(field => !string.Equals(field.EditableValue, field.OriginalValue, StringComparison.Ordinal));
    public string DirtyStateText => HasPendingChanges ? "Unsaved changes" : "All changes saved";

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
    public string VideoIrMode { get => GetValue("irMode"); set => SetValue("irMode", value); }
    public string VideoWdr { get => GetValue("wdr"); set => SetValue("wdr", value); }
    public string VideoIrCutMethod { get => GetValue("irCutMethod"); set => SetValue("irCutMethod", value); }
    public string VideoSceneMode { get => GetValue("sceneMode"); set => SetValue("sceneMode", value); }
    public string VideoExposure { get => GetValue("exposure"); set => SetValue("exposure", value); }
    public string VideoAwb { get => GetValue("awb"); set => SetValue("awb", value); }
    public string VideoLowlight { get => GetValue("lowlight"); set => SetValue("lowlight", value); }
    public string VideoIrCut { get => GetValue("irCut"); set => SetValue("irCut", value); }
    public string VideoResolution { get => GetValue("resolution"); set => SetValue("resolution", value); }
    public string VideoBitrateMode { get => GetValue("bitrateMode"); set => SetValue("bitrateMode", value); }
    public string VideoDefinition { get => GetValue("definition"); set => SetValue("definition", value); }
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
    public string VideoKeyframeInterval { get => GetValue("keyframeInterval"); set => SetValue("keyframeInterval", value); }
    public bool StreamAudioEnabled
    {
        get => ParseBool(GetValue("audioEnabled"));
        set => SetValue("audioEnabled", value ? "true" : "false");
    }
    public string StreamAudioBitrate { get => GetValue("audioBitRate"); set => SetValue("audioBitRate", value); }
    public string StreamAudioSampleRate { get => GetValue("audioSampleRate"); set => SetValue("audioSampleRate", value); }
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
    public double ImageContrastSlider
    {
        get => ParseDouble(ImageContrast, 50);
        set
        {
            ImageContrast = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageSaturation { get => GetValue("saturation"); set => SetValue("saturation", value); }
    public double ImageSaturationSlider
    {
        get => ParseDouble(ImageSaturation, 50);
        set
        {
            ImageSaturation = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageHue { get => GetValue("hue"); set => SetValue("hue", value); }
    public double ImageHueSlider
    {
        get => ParseDouble(ImageHue, 50);
        set
        {
            ImageHue = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageSharpness { get => GetValue("sharpness"); set => SetValue("sharpness", value); }
    public double ImageSharpnessSlider
    {
        get => ParseDouble(ImageSharpness, 50);
        set
        {
            ImageSharpness = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageManualSharpness { get => GetValue("manualSharpness"); set => SetValue("manualSharpness", value); }
    public double ImageManualSharpnessSlider
    {
        get => ParseDouble(ImageManualSharpness, 170);
        set
        {
            ImageManualSharpness = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageDenoise { get => GetValue("denoise"); set => SetValue("denoise", value); }
    public double ImageDenoiseSlider
    {
        get => ParseDouble(ImageDenoise, 1);
        set
        {
            ImageDenoise = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageWdrStrength { get => GetValue("wdrStrength"); set => SetValue("wdrStrength", value); }
    public double ImageWdrStrengthSlider
    {
        get => ParseDouble(ImageWdrStrength, 3);
        set
        {
            ImageWdrStrength = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public bool ImageMirror
    {
        get => ParseBool(GetValue("mirror"));
        set => SetValue("mirror", value ? "true" : "false");
    }
    public bool ImageFlip
    {
        get => ParseBool(GetValue("flip"));
        set => SetValue("flip", value ? "true" : "false");
    }
    public string ImageGamma { get => GetValue("gamma"); set => SetValue("gamma", value); }
    public double ImageGammaSlider
    {
        get => ParseDouble(ImageGamma, 50);
        set
        {
            ImageGamma = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageWhiteLight { get => GetValue("whiteLight"); set => SetValue("whiteLight", value); }
    public double ImageWhiteLightSlider
    {
        get => ParseDouble(ImageWhiteLight, 40);
        set
        {
            ImageWhiteLight = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageInfrared { get => GetValue("infrared"); set => SetValue("infrared", value); }
    public double ImageInfraredSlider
    {
        get => ParseDouble(ImageInfrared, 40);
        set
        {
            ImageInfrared = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string ImageOsd { get => GetValue("osd"); set => SetValue("osd", value); }
    public bool ImageOsdChannelNameEnabled
    {
        get => ParseBool(GetValue("osdChannelNameEnabled"));
        set => SetValue("osdChannelNameEnabled", value ? "true" : "false");
    }
    public string ImageOsdChannelNameText { get => GetValue("osdChannelNameText"); set => SetValue("osdChannelNameText", value); }
    public bool ImageOsdDateTimeEnabled
    {
        get => ParseBool(GetValue("osdDateTimeEnabled"));
        set => SetValue("osdDateTimeEnabled", value ? "true" : "false");
    }
    public string ImageOsdDateFormat { get => GetValue("osdDateFormat"); set => SetValue("osdDateFormat", value); }
    public string ImageOsdTimeFormat { get => GetValue("osdTimeFormat"); set => SetValue("osdTimeFormat", value); }
    public bool ImageOsdDisplayWeek
    {
        get => ParseBool(GetValue("osdDisplayWeek"));
        set => SetValue("osdDisplayWeek", value ? "true" : "false");
    }

    // Network/wireless typed bindings
    public string NetworkIp { get => GetValue("ip"); set => SetValue("ip", value); }
    public string NetworkNetmask { get => GetValue("netmask"); set => SetValue("netmask", value); }
    public string NetworkGateway { get => GetValue("gateway"); set => SetValue("gateway", value); }
    public string NetworkDns { get => GetValue("dns"); set => SetValue("dns", value); }
    public string NetworkPort { get => GetValue("ports"); set => SetValue("ports", value); }
    public string WirelessApSsid { get => GetValue("apSsid"); set => SetValue("apSsid", value); }
    public string WirelessApChannel { get => GetValue("apChannel"); set => SetValue("apChannel", value); }
    public string WirelessMode { get => GetValue("wirelessMode"); set => SetValue("wirelessMode", value); }
    public string WirelessApMode { get => GetValue("apMode"); set => SetValue("apMode", value); }
    public bool NetworkDhcpMode
    {
        get => ParseBool(GetValue("dhcpMode"));
        set => SetValue("dhcpMode", value ? "true" : "false");
    }
    public bool NetworkEseeEnabled
    {
        get => ParseBool(GetValue("eseeEnabled"));
        set => SetValue("eseeEnabled", value ? "true" : "false");
    }
    public bool NetworkNtpEnabled
    {
        get => ParseBool(GetValue("ntpEnabled"));
        set => SetValue("ntpEnabled", value ? "true" : "false");
    }
    public string NetworkNtpServer { get => GetValue("ntpServerDomain"); set => SetValue("ntpServerDomain", value); }
    public string NetworkEseeId => GetValue("eseeId");
    public string CameraSerial => GetValue("serial");
    public string CameraMac => GetValue("mac");
    public bool MotionEnabled
    {
        get => ParseBool(GetValue("motionEnabled"));
        set => SetValue("motionEnabled", value ? "true" : "false");
    }
    public string MotionType { get => GetValue("motionType"); set => SetValue("motionType", value); }
    public string MotionSensitivity { get => GetValue("motionSensitivity"); set => SetValue("motionSensitivity", value); }
    public double MotionSensitivitySlider
    {
        get => ParseDouble(MotionSensitivity, 50);
        set
        {
            MotionSensitivity = ((int)value).ToString();
            OnPropertyChanged();
        }
    }
    public string MotionAlarmDuration { get => GetValue("motionAlarmDuration"); set => SetValue("motionAlarmDuration", value); }
    public bool MotionAlarm
    {
        get => ParseBool(GetValue("motionAlarm"));
        set => SetValue("motionAlarm", value ? "true" : "false");
    }
    public bool MotionBuzzer
    {
        get => ParseBool(GetValue("motionBuzzer"));
        set => SetValue("motionBuzzer", value ? "true" : "false");
    }
    public string VideoLossAlarmDuration { get => GetValue("videoLossAlarmDuration"); set => SetValue("videoLossAlarmDuration", value); }
    public bool VideoLossAlarm
    {
        get => ParseBool(GetValue("videoLossAlarm"));
        set => SetValue("videoLossAlarm", value ? "true" : "false");
    }
    public bool VideoLossBuzzer
    {
        get => ParseBool(GetValue("videoLossBuzzer"));
        set => SetValue("videoLossBuzzer", value ? "true" : "false");
    }
    public bool PrivacyMaskEnabled
    {
        get => ParseBool(GetValue("privacyMaskEnabled"));
        set => SetValue("privacyMaskEnabled", value ? "true" : "false");
    }
    public string PrivacyMaskX { get => GetValue("privacyMaskX"); set => SetValue("privacyMaskX", value); }
    public string PrivacyMaskY { get => GetValue("privacyMaskY"); set => SetValue("privacyMaskY", value); }
    public string PrivacyMaskWidth { get => GetValue("privacyMaskWidth"); set => SetValue("privacyMaskWidth", value); }
    public string PrivacyMaskHeight { get => GetValue("privacyMaskHeight"); set => SetValue("privacyMaskHeight", value); }
    public string AlarmInputState { get => GetValue("alarmInputActiveState"); set => SetValue("alarmInputActiveState", value); }
    public string AlarmOutputState { get => GetValue("alarmOutputActiveState"); set => SetValue("alarmOutputActiveState", value); }
    public string AlarmPulseDuration { get => GetValue("alarmPulseDuration"); set => SetValue("alarmPulseDuration", value); }
    public string AlarmDuration { get => GetValue("alarmDuration"); set => SetValue("alarmDuration", value); }
    public bool AlarmEnabled
    {
        get => ParseBool(GetValue("alarmEnabled"));
        set => SetValue("alarmEnabled", value ? "true" : "false");
    }
    public bool AlarmBuzzer
    {
        get => ParseBool(GetValue("alarmBuzzer"));
        set => SetValue("alarmBuzzer", value ? "true" : "false");
    }
    public string SdStatus => GetValue("sdStatus");
    public string SdMediaType => GetValue("sdMediaType");
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
    public string ImageSharpnessBehaviorBadge => BehaviorBadge("sharpness");
    public string ImageWdrBehaviorBadge => BehaviorBadge("wdr");

    // enable flags capability-driven
    public bool CanEditVideoCodec => CanEdit("codec");
    public bool CanEditVideoProfile => CanEdit("profile");
    public bool CanEditVideoResolution => CanEdit("resolution");
    public bool CanEditVideoDayNight => CanEdit("dayNight");
    public bool CanEditVideoIrMode => CanEdit("irMode");
    public bool CanEditVideoWdr => CanEdit("wdr");
    public bool CanEditVideoIrCutMethod => CanEdit("irCutMethod");
    public bool CanEditVideoSceneMode => CanEdit("sceneMode");
    public bool CanEditVideoExposure => CanEdit("exposure");
    public bool CanEditVideoAwb => CanEdit("awb");
    public bool CanEditVideoLowlight => CanEdit("lowlight");
    public bool CanEditVideoIrCut => CanEdit("irCut");
    public bool CanEditVideoBitrateMode => CanEdit("bitrateMode");
    public bool CanEditVideoDefinition => CanEdit("definition");
    public bool CanEditImageBrightness => CanEdit("brightness");
    public bool CanEditImageContrast => CanEdit("contrast");
    public bool CanEditImageSaturation => CanEdit("saturation");
    public bool CanEditImageSharpness => CanEdit("sharpness");
    public bool CanEditImageManualSharpness => CanEdit("manualSharpness");
    public bool CanEditImageDenoise => CanEdit("denoise");
    public bool CanEditImageWdrStrength => CanEdit("wdrStrength");
    public bool CanEditImageHue => CanEdit("hue");
    public bool CanEditImageGamma => CanEdit("gamma");
    public bool CanEditImageMirror => CanEdit("mirror");
    public bool CanEditImageFlip => CanEdit("flip");
    public bool CanEditImageWhiteLight => CanEdit("whiteLight");
    public bool CanEditImageInfrared => CanEdit("infrared");
    public bool CanEditImageOsd => CanEdit("osd");
    public bool CanEditImageOsdChannelNameEnabled => CanEdit("osdChannelNameEnabled");
    public bool CanEditImageOsdChannelNameText => CanEdit("osdChannelNameText");
    public bool CanEditImageOsdDateTimeEnabled => CanEdit("osdDateTimeEnabled");
    public bool CanEditImageOsdDateFormat => CanEdit("osdDateFormat");
    public bool CanEditImageOsdTimeFormat => CanEdit("osdTimeFormat");
    public bool CanEditImageOsdDisplayWeek => CanEdit("osdDisplayWeek");
    public bool CanEditImageDayNight => CanEdit("dayNight");
    public bool CanEditImageIrMode => CanEdit("irMode");
    public bool CanEditImageIrCut => CanEdit("irCut");
    public bool CanEditImageIrCutMethod => CanEdit("irCutMethod");
    public bool CanEditImageSceneMode => CanEdit("sceneMode");
    public bool CanEditImageExposure => CanEdit("exposure");
    public bool CanEditImageAwb => CanEdit("awb");
    public bool CanEditImageLowlight => CanEdit("lowlight");
    public bool CanEditStreamBitrate => CanEdit("bitrate");
    public bool CanEditStreamFrameRate => CanEdit("frameRate");
    public bool CanEditStreamKeyframe => CanEdit("keyframeInterval");
    public bool CanEditStreamAudioEnabled => CanEdit("audioEnabled");
    public bool CanEditStreamAudioBitrate => CanEdit("audioBitRate");
    public bool CanEditStreamAudioSampleRate => CanEdit("audioSampleRate");
    public bool CanEditNetworkIp => CanEdit("ip");
    public bool CanEditNetworkNetmask => CanEdit("netmask");
    public bool CanEditNetworkGateway => CanEdit("gateway");
    public bool CanEditNetworkDns => CanEdit("dns");
    public bool CanEditNetworkPort => CanEdit("ports");
    public bool CanEditNetworkDhcpMode => CanEdit("dhcpMode");
    public bool CanEditNetworkEseeEnabled => CanEdit("eseeEnabled");
    public bool CanEditNetworkNtpEnabled => CanEdit("ntpEnabled");
    public bool CanEditNetworkNtpServer => CanEdit("ntpServerDomain");
    public bool CanEditWirelessMode => CanEdit("wirelessMode");
    public bool CanEditWirelessApMode => CanEdit("apMode");
    public bool CanEditWirelessApSsid => CanEdit("apSsid");
    public bool CanEditWirelessApChannel => CanEdit("apChannel");
    public bool CanEditMotionEnabled => CanEdit("motionEnabled");
    public bool CanEditMotionType => CanEdit("motionType");
    public bool CanEditMotionSensitivity => CanEdit("motionSensitivity");
    public bool CanEditMotionAlarmDuration => CanEdit("motionAlarmDuration");
    public bool CanEditMotionAlarm => CanEdit("motionAlarm");
    public bool CanEditMotionBuzzer => CanEdit("motionBuzzer");
    public bool CanEditVideoLossAlarmDuration => CanEdit("videoLossAlarmDuration");
    public bool CanEditVideoLossAlarm => CanEdit("videoLossAlarm");
    public bool CanEditVideoLossBuzzer => CanEdit("videoLossBuzzer");
    public bool CanEditPrivacyMaskEnabled => CanEdit("privacyMaskEnabled");
    public bool CanEditPrivacyMaskX => CanEdit("privacyMaskX");
    public bool CanEditPrivacyMaskY => CanEdit("privacyMaskY");
    public bool CanEditPrivacyMaskWidth => CanEdit("privacyMaskWidth");
    public bool CanEditPrivacyMaskHeight => CanEdit("privacyMaskHeight");
    public bool CanEditAlarmInputState => CanEdit("alarmInputActiveState");
    public bool CanEditAlarmOutputState => CanEdit("alarmOutputActiveState");
    public bool CanEditAlarmPulseDuration => CanEdit("alarmPulseDuration");
    public bool CanEditAlarmDuration => CanEdit("alarmDuration");
    public bool CanEditAlarmEnabled => CanEdit("alarmEnabled");
    public bool CanEditAlarmBuzzer => CanEdit("alarmBuzzer");
    public Visibility VideoCodecVisibility => FieldVisibility("codec");
    public Visibility VideoProfileVisibility => FieldVisibility("profile");
    public Visibility VideoDayNightVisibility => FieldVisibility("dayNight");
    public Visibility VideoWdrVisibility => FieldVisibility("wdr");
    public Visibility VideoIrCutMethodVisibility => FieldVisibility("irCutMethod");
    public Visibility VideoSceneModeVisibility => FieldVisibility("sceneMode");
    public Visibility VideoExposureVisibility => FieldVisibility("exposure");
    public Visibility VideoAwbVisibility => FieldVisibility("awb");
    public Visibility VideoLowlightVisibility => FieldVisibility("lowlight");
    public Visibility VideoIrCutVisibility => FieldVisibility("irCut");
    public Visibility VideoBitrateVisibility => FieldVisibility("bitrate");
    public Visibility VideoFrameRateVisibility => FieldVisibility("frameRate");
    public Visibility VideoResolutionVisibility => FieldVisibility("resolution");
    public Visibility StreamResolutionVisibility => FieldVisibility("resolution");
    public Visibility StreamCodecVisibility => FieldVisibility("codec");
    public Visibility StreamProfileVisibility => FieldVisibility("profile");
    public Visibility StreamBitrateModeVisibility => FieldVisibility("bitrateMode");
    public Visibility StreamDefinitionVisibility => FieldVisibility("definition");
    public Visibility StreamBitrateVisibility => FieldVisibility("bitrate");
    public Visibility StreamFpsVisibility => FieldVisibility("frameRate");
    public Visibility StreamKeyframeVisibility => FieldVisibility("keyframeInterval");
    public Visibility StreamAudioEnabledVisibility => FieldVisibility("audioEnabled");
    public Visibility StreamAudioBitrateVisibility => FieldVisibility("audioBitRate");
    public Visibility StreamAudioSampleRateVisibility => FieldVisibility("audioSampleRate");
    public Visibility ImageBrightnessVisibility => OperatorFieldVisibility("brightness");
    public Visibility ImageContrastVisibility => OperatorFieldVisibility("contrast");
    public Visibility ImageSaturationVisibility => OperatorFieldVisibility("saturation");
    public Visibility ImageManualSharpnessVisibility => OperatorFieldVisibility("manualSharpness");
    public Visibility ImageDenoiseVisibility => OperatorFieldVisibility("denoise");
    public Visibility ImageWdrStrengthVisibility => OperatorFieldVisibility("wdrStrength");
    public Visibility ImageHueVisibility => OperatorFieldVisibility("hue");
    public Visibility ImageGammaVisibility => OperatorFieldVisibility("gamma");
    public Visibility ImageSharpnessVisibility => OperatorFieldVisibility("sharpness");
    public Visibility ImageWhiteLightVisibility => OperatorFieldVisibility("whiteLight");
    public Visibility ImageInfraredVisibility => OperatorFieldVisibility("infrared");
    public Visibility ImageOsdVisibility => OperatorFieldVisibility("osd");
    public Visibility ImageOsdChannelNameEnabledVisibility => OperatorFieldVisibility("osdChannelNameEnabled");
    public Visibility ImageOsdChannelNameTextVisibility => OperatorFieldVisibility("osdChannelNameText");
    public Visibility ImageOsdDateTimeEnabledVisibility => OperatorFieldVisibility("osdDateTimeEnabled");
    public Visibility ImageOsdDateFormatVisibility => OperatorFieldVisibility("osdDateFormat");
    public Visibility ImageOsdTimeFormatVisibility => OperatorFieldVisibility("osdTimeFormat");
    public Visibility ImageOsdDisplayWeekVisibility => OperatorFieldVisibility("osdDisplayWeek");
    public Visibility ImageMirrorVisibility => OperatorFieldVisibility("mirror");
    public Visibility ImageFlipVisibility => OperatorFieldVisibility("flip");
    public Visibility ImageDayNightVisibility => OperatorFieldVisibility("dayNight");
    public Visibility ImageIrModeVisibility => OperatorFieldVisibility("irMode");
    public Visibility ImageIrCutVisibility => OperatorFieldVisibility("irCut");
    public Visibility ImageIrCutMethodVisibility => OperatorFieldVisibility("irCutMethod");
    public Visibility ImageSceneModeVisibility => OperatorFieldVisibility("sceneMode");
    public Visibility ImageExposureVisibility => OperatorFieldVisibility("exposure");
    public Visibility ImageAwbVisibility => OperatorFieldVisibility("awb");
    public Visibility ImageLowlightVisibility => OperatorFieldVisibility("lowlight");
    public Visibility NetworkIpVisibility => FieldVisibility("ip");
    public Visibility NetworkNetmaskVisibility => FieldVisibility("netmask");
    public Visibility NetworkGatewayVisibility => FieldVisibility("gateway", requireStaticNetwork: true);
    public Visibility NetworkDnsVisibility => FieldVisibility("dns", requireStaticNetwork: true);
    public Visibility NetworkPortVisibility => FieldVisibility("ports");
    public Visibility NetworkDhcpModeVisibility => FieldVisibility("dhcpMode");
    public Visibility NetworkEseeVisibility => FieldVisibility("eseeEnabled");
    public Visibility NetworkNtpEnabledVisibility => FieldVisibility("ntpEnabled");
    public Visibility NetworkNtpServerVisibility => FieldVisibility("ntpServerDomain");
    public Visibility NetworkEseeIdVisibility => FieldVisibility("eseeId");
    public Visibility WirelessModeVisibility => FieldVisibility("wirelessMode");
    public Visibility WirelessApModeVisibility => FieldVisibility("apMode");
    public Visibility WirelessApSsidVisibility => FieldVisibility("apSsid");
    public Visibility WirelessApPskVisibility => FieldVisibility("apPsk");
    public Visibility WirelessApChannelVisibility => FieldVisibility("apChannel");
    public Visibility MotionEnabledVisibility => FieldVisibility("motionEnabled");
    public Visibility MotionTypeVisibility => FieldVisibility("motionType");
    public Visibility MotionSensitivityVisibility => FieldVisibility("motionSensitivity");
    public Visibility MotionAlarmDurationVisibility => FieldVisibility("motionAlarmDuration");
    public Visibility MotionAlarmVisibility => FieldVisibility("motionAlarm");
    public Visibility MotionBuzzerVisibility => FieldVisibility("motionBuzzer");
    public Visibility VideoLossAlarmDurationVisibility => FieldVisibility("videoLossAlarmDuration");
    public Visibility VideoLossAlarmVisibility => FieldVisibility("videoLossAlarm");
    public Visibility VideoLossBuzzerVisibility => FieldVisibility("videoLossBuzzer");
    public Visibility PrivacyMaskEnabledVisibility => FieldVisibility("privacyMaskEnabled");
    public Visibility PrivacyMaskXVisibility => FieldVisibility("privacyMaskX");
    public Visibility PrivacyMaskYVisibility => FieldVisibility("privacyMaskY");
    public Visibility PrivacyMaskWidthVisibility => FieldVisibility("privacyMaskWidth");
    public Visibility PrivacyMaskHeightVisibility => FieldVisibility("privacyMaskHeight");
    public Visibility AlarmInputStateVisibility => FieldVisibility("alarmInputActiveState");
    public Visibility AlarmOutputStateVisibility => FieldVisibility("alarmOutputActiveState");
    public Visibility AlarmPulseDurationVisibility => FieldVisibility("alarmPulseDuration");
    public Visibility AlarmDurationVisibility => FieldVisibility("alarmDuration");
    public Visibility AlarmEnabledVisibility => FieldVisibility("alarmEnabled");
    public Visibility AlarmBuzzerVisibility => FieldVisibility("alarmBuzzer");
    public Visibility SdStatusVisibility => FieldVisibility("sdStatus");
    public Visibility SdMediaTypeVisibility => FieldVisibility("sdMediaType");
    public Visibility CameraSerialVisibility => FieldVisibility("serial");
    public Visibility CameraMacVisibility => FieldVisibility("mac");
    public string ImageBrightnessReadOnlyTooltip => ReadOnlyTooltip("brightness");
    public string ImageContrastReadOnlyTooltip => ReadOnlyTooltip("contrast");
    public string ImageSaturationReadOnlyTooltip => ReadOnlyTooltip("saturation");
    public string ImageSharpnessReadOnlyTooltip => ReadOnlyTooltip("sharpness");
    public string ImageManualSharpnessReadOnlyTooltip => ReadOnlyTooltip("manualSharpness");
    public string ImageHueReadOnlyTooltip => ReadOnlyTooltip("hue");
    public string ImageMirrorReadOnlyTooltip => ReadOnlyTooltip("mirror");
    public string ImageFlipReadOnlyTooltip => ReadOnlyTooltip("flip");
    public string ImageDayNightReadOnlyTooltip => ReadOnlyTooltip("dayNight");
    public string ImageIrModeReadOnlyTooltip => ReadOnlyTooltip("irMode");
    public string ImageIrCutReadOnlyTooltip => ReadOnlyTooltip("irCut");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastVisibility = Visibility.Collapsed;
        };
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
    private async void ProbePipelineOwnership_Click(object sender, RoutedEventArgs e) => await RunAsync(ProbePipelineOwnershipAsync);
    private async void ProbeGroupedFamilies_Click(object sender, RoutedEventArgs e) => await RunAsync(ProbeGroupedFamiliesAsync);
    private async void RetestUnsupportedGrouped_Click(object sender, RoutedEventArgs e) => await RunAsync(RetestUnsupportedGroupedAsync);
    private async void ForceEnumerateSdkFields_Click(object sender, RoutedEventArgs e) => await RunAsync(ForceEnumerateSdkFieldsAsync);
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
    private async void FindFile_Click(object sender, RoutedEventArgs e) => await RunAsync(FindFileAsync);
    private async void FindNextFile_Click(object sender, RoutedEventArgs e) => await RunAsync(FindNextFileAsync);
    private async void GetFileByTime_Click(object sender, RoutedEventArgs e) => await RunAsync(GetFileByTimeAsync);
    private async void PlaybackByTime_Click(object sender, RoutedEventArgs e) => await RunAsync(PlaybackByTimeAsync);
    private async void FindClose_Click(object sender, RoutedEventArgs e) => await RunAsync(FindCloseAsync);
    private async void PlaybackByName_Click(object sender, RoutedEventArgs e) => await RunAsync(PlaybackByNameAsync);
    private async void GetFileByName_Click(object sender, RoutedEventArgs e) => await RunAsync(GetFileByNameAsync);
    private async void StopGetFile_Click(object sender, RoutedEventArgs e) => await RunAsync(StopGetFileAsync);
    private async void PlaybackSaveData_Click(object sender, RoutedEventArgs e) => await RunAsync(PlaybackSaveDataAsync);
    private async void StopPlaybackSave_Click(object sender, RoutedEventArgs e) => await RunAsync(StopPlaybackSaveAsync);
    private async void RunNetworkRecovery_Click(object sender, RoutedEventArgs e) => await RunAsync(RunNetworkRecoveryAsync);
    private async void RefreshSelected_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshSelectedAsync);
    private async void RefreshEndpointSurface_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadEndpointSurfaceAsync);
    private void ResetEndpointPayload_Click(object sender, RoutedEventArgs e) => ResetSelectedEndpointPayload();
    private async void ExecuteEndpointSurface_Click(object sender, RoutedEventArgs e) => await RunAsync(ExecuteSelectedEndpointAsync);
    private void BrowseFirmware_Click(object sender, RoutedEventArgs e) => SelectFirmwareFile();
    private async void UploadFirmware_Click(object sender, RoutedEventArgs e) => await RunAsync(UploadFirmwareAsync);

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DiagnosticsText = ex.ToString();
            ShowToast("Action failed. Check Advanced > Raw JSON.", success: false);
        }
    }

    private async Task RefreshSelectedAsync()
    {
        if (SelectedDevice is null)
        {
            await LoadDevicesAsync();
            return;
        }

        await LoadTypedAsync();
        await LoadValidationAsync();
        await LoadTranscriptsAsync();
        ShowToast("Camera refreshed.", success: true);
    }

    private void SelectFirmwareFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Firmware File",
            Filter = "Firmware files (*.bin;*.img;*.zip)|*.bin;*.img;*.zip|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            FirmwareUploadPath = dialog.FileName;
        }
    }

    private async Task UploadFirmwareAsync()
    {
        if (SelectedDevice is null)
        {
            MaintenanceState = "Select a device first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FirmwareUploadPath) || !File.Exists(FirmwareUploadPath))
        {
            MaintenanceState = "Select a valid firmware file first.";
            ShowToast("Firmware file is missing.", success: false);
            return;
        }

        var payload = new JsonObject
        {
            ["filePath"] = FirmwareUploadPath
        };
        await ExecuteMaintenanceAsync("FirmwareUpload", payload);
    }

    private async Task LoadDevicesAsync()
    {
        var devices = await GetAsync<List<DeviceIdentity>>("/api/devices") ?? [];
        ReplaceCollection(Devices, devices);
        DiagnosticsText = JsonSerializer.Serialize(devices, SerializerOptions);
        ShowToast($"Loaded {devices.Count} camera(s).", success: true);
    }

    private async Task DiscoverAsync()
    {
        var devices = await PostAsync<List<DeviceIdentity>>("/api/devices/discover", null) ?? [];
        ReplaceCollection(Devices, devices);
        DiagnosticsText = JsonSerializer.Serialize(devices, SerializerOptions);
        ShowToast($"Discovery found {devices.Count} camera(s).", success: true);
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

    private async Task RetestUnsupportedGroupedAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new GroupedRetestRequest
        {
            RefreshFromDevice = true,
            IncludeDangerous = false,
            ExpertOverride = true
        };
        var result = await PostAsync<List<GroupedUnsupportedRetestResult>>($"/api/devices/{SelectedDevice.Id}/grouped-config/retest-unsupported", request) ?? [];
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadTypedAsync();
        await LoadImageTruthAsync();
        ShowToast($"Grouped retest completed for {result.Count} field(s).", success: true);
    }

    private async Task ForceEnumerateSdkFieldsAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new ForcedEnumerationRequest
        {
            RefreshFromDevice = true,
            IncludeDangerous = false,
            ExpertOverride = true
        };
        var result = await PostAsync<List<GroupedUnsupportedRetestResult>>($"/api/devices/{SelectedDevice.Id}/grouped-config/force-enumerate-sdk-fields", request) ?? [];
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadTypedAsync();
        await LoadImageTruthAsync();

        var summary = string.Join(", ", result
            .GroupBy(static item => item.Classification)
            .OrderBy(static group => group.Key.ToString())
            .Select(group => $"{group.Key}={group.Count()}"));
        ShowToast($"SDK enumeration: {summary}", success: true);
    }

    private async Task ProbeGroupedFamiliesAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new GroupedFamilyProbeRequest
        {
            RefreshFromDevice = true,
            IncludePrivacyMasks = true,
            ExpertOverride = true
        };
        var result = await PostAsync<List<GroupedUnsupportedRetestResult>>($"/api/devices/{SelectedDevice.Id}/grouped-config/probe-families", request) ?? [];
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadTypedAsync();
        await LoadImageTruthAsync();

        var summary = string.Join(", ", result
            .GroupBy(static item => item.Behavior)
            .OrderBy(static group => group.Key.ToString())
            .Select(group => $"{group.Key}={group.Count()}"));
        ShowToast($"Grouped family probe: {summary}", success: true);
    }

    private async Task ProbePipelineOwnershipAsync()
    {
        if (SelectedDevice is null)
        {
            DiagnosticsText = "Select a device first.";
            return;
        }

        var request = new PipelineOwnershipProbeRequest
        {
            RefreshFromDevice = true,
            ExpertOverride = true
        };
        var result = await PostAsync<PipelineOwnershipProbeReport>($"/api/devices/{SelectedDevice.Id}/grouped-config/probe-pipeline-ownership", request);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        await LoadTypedAsync();
        await LoadImageTruthAsync();

        var summary = string.Join(", ", result?.Fields
            .GroupBy(static item => item.Classification)
            .OrderBy(static group => group.Key.ToString())
            .Select(group => $"{group.Key}={group.Count()}") ?? []);
        ShowToast($"Pipeline ownership: {summary}", success: result is not null);
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
        await LoadControlPointInventoryAsync();
        await LoadEndpointSurfaceAsync();
        await LoadTruthBadgeAsync();
        LastSyncText = $"Last sync: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        NotifyAllEditorProperties();
    }

    private async Task LoadControlPointInventoryAsync()
    {
        if (SelectedDevice is null)
        {
            ControlPointInventoryRows.Clear();
            return;
        }

        var report = await GetAsync<ControlPointInventoryReport>($"/api/devices/{SelectedDevice.Id}/control-points");
        ReplaceCollection(ControlPointInventoryRows, report?.Families
            .SelectMany(static family => family.Controls)
            .OrderBy(static item => item.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? []);
    }

    private async Task LoadEndpointSurfaceAsync()
    {
        if (SelectedDevice is null)
        {
            EndpointSurfaceRows.Clear();
            SelectedEndpointSurface = null;
            return;
        }

        var report = await GetAsync<EndpointSurfaceReport>($"/api/devices/{SelectedDevice.Id}/endpoint-surface");
        ReplaceCollection(EndpointSurfaceRows, report?.Endpoints
            .Select(EndpointSurfaceRow.FromItem)
            .OrderBy(static item => item.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ContractKey, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? []);
        SelectedEndpointSurface = EndpointSurfaceRows.FirstOrDefault();
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
        SetEnumOptions(VideoIrModeOptions, "irMode", new[] { "auto", "daylight", "night", "smart" });
        SetEnumOptions(VideoWdrOptions, "wdr", new[] { "Off", "On", "Auto" });
        SetEnumOptions(VideoIrCutOptions, "irCut", new[] { "Auto", "Day", "Night" });
        SetEnumOptions(VideoIrCutMethodOptions, "irCutMethod", new[] { "software", "hardware" });
        SetEnumOptions(VideoSceneModeOptions, "sceneMode", new[] { "auto", "indoor", "outdoor" });
        SetEnumOptions(VideoExposureOptions, "exposure", new[] { "auto", "bright", "dark" });
        SetEnumOptions(VideoAwbOptions, "awb", new[] { "auto", "indoor", "outdoor" });
        SetEnumOptions(VideoLowlightOptions, "lowlight", new[] { "close", "only night", "day-night", "auto" });
        SetEnumOptions(VideoOsdDateFormatOptions, "osdDateFormat", new[] { "YYYY-MM-DD", "MM-DD-YYYY", "DD-MM-YYYY" });
        SetEnumOptions(VideoOsdTimeFormatOptions, "osdTimeFormat", new[] { "24h", "12h" });
        SetEnumOptions(VideoBitrateModeOptions, "bitrateMode", new[] { "CBR", "VBR" });
        SetEnumOptions(VideoDefinitionOptions, "definition", new[] { "auto", "fluency", "HD", "BD" });
        SetEnumOptions(WirelessModeOptions, "wirelessMode", new[] { "Station", "AP", "Disabled" });
        SetEnumOptions(WirelessApModeOptions, "apMode", new[] { "Off", "On" });
        SetEnumOptions(MotionTypeOptions, "motionType", new[] { "grid", "region" });
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
        var groupedProfiles = await GetAsync<List<GroupedApplyProfile>>($"/api/devices/{SelectedDevice.Id}/grouped-config/profiles") ?? [];
        var groupedResults = await GetAsync<List<GroupedUnsupportedRetestResult>>($"/api/devices/{SelectedDevice.Id}/grouped-config/retest-results?limit=400") ?? [];
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
        _groupedRetestByField.Clear();
        foreach (var result in groupedResults
            .GroupBy(static item => item.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static item => item.CapturedAt).First()))
        {
            _groupedRetestByField[result.FieldKey] = result;
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
        if (groupedProfiles.Count > 0)
        {
            GroupedApplyIndicator = "Grouped apply: " + string.Join(" | ", groupedProfiles
                .OrderBy(static profile => profile.GroupKind)
                .Select(profile => $"{profile.GroupKind}:{profile.DominantBehavior}"));
        }
        else
        {
            GroupedApplyIndicator = "Grouped apply: no profiles";
        }
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
            ShowToast("No changes to save.", success: false);
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
        ShowToast(applied.All(result => result.Success) ? "Changes saved." : "Some changes failed to apply.", success: applied.All(result => result.Success));
        await LoadTypedAsync();
        await LoadValidationAsync();
        await LoadSemanticTrustAsync();
    }

    private void ResetSelectedEndpointPayload()
    {
        if (SelectedEndpointSurface is null)
        {
            return;
        }

        SelectedEndpointSurface.EditablePayload = SelectedEndpointSurface.CurrentPayloadAvailable
            ? SelectedEndpointSurface.CurrentPayload
            : SelectedEndpointSurface.SuggestedPayload;
        OnPropertyChanged(nameof(EndpointEditablePayloadText));
    }

    private async Task ExecuteSelectedEndpointAsync()
    {
        if (SelectedDevice is null || SelectedEndpointSurface is null)
        {
            DiagnosticsText = "Select a device endpoint first.";
            return;
        }

        if (SelectedEndpointSurface.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await LoadTypedAsync();
            ShowToast("Endpoint surface refreshed.", success: true);
            return;
        }

        if (SelectedEndpointSurface.RequiresConfirmation)
        {
            var prompt = $"{SelectedEndpointSurface.Method} {SelectedEndpointSurface.Endpoint} may impact the device. Continue?";
            if (MessageBox.Show(prompt, "Confirm Endpoint Execute", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var payload = ParseObjectNode(SelectedEndpointSurface.EditablePayload) ?? new JsonObject();
        var plan = new WritePlan
        {
            GroupName = SelectedEndpointSurface.GroupName,
            Endpoint = SelectedEndpointSurface.Endpoint,
            Method = SelectedEndpointSurface.Method,
            Payload = payload,
            SnapshotBeforeWrite = true,
            RequireWriteVerification = !SelectedEndpointSurface.RequiresConfirmation,
            AllowRollback = !SelectedEndpointSurface.RequiresConfirmation,
            ContractKey = SelectedEndpointSurface.ContractKey
        };
        var result = await PostAsync<WriteResult>($"/api/devices/{SelectedDevice.Id}/settings/write", plan);
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        ShowToast(result?.Success == true ? $"{SelectedEndpointSurface.ContractKey} executed." : $"{SelectedEndpointSurface.ContractKey} failed.", success: result?.Success == true);

        await LoadTypedAsync();
        await LoadValidationAsync();
        await LoadTranscriptsAsync();
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

    private async Task ExecuteMaintenanceAsync(string operation, JsonObject? payload = null)
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var prompt = operation.Equals("FactoryReset", StringComparison.OrdinalIgnoreCase)
            ? "Factory default is destructive. Continue?"
            : operation.Equals("FirmwareUpload", StringComparison.OrdinalIgnoreCase)
                ? "Upload firmware to this camera now?"
                : "Reboot camera now?";
        if (MessageBox.Show(prompt, "Confirm Maintenance", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await PostAsync<MaintenanceResult>($"/api/devices/{SelectedDevice.Id}/maintenance/{operation}", payload ?? new JsonObject());
        MaintenanceState = result is null ? $"Failed {operation}" : $"{operation}: {(result.Success ? "ok" : "failed")}";
        DiagnosticsText = JsonSerializer.Serialize(result, SerializerOptions);
        ShowToast(result?.Success == true ? $"{operation} completed." : $"{operation} failed.", success: result?.Success == true);
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

    private async Task FindFileAsync()
    {
        await RunPlaybackOperationAsync("FindFile", $"/api/devices/{SelectedDevice?.Id}/playback/find-file");
    }

    private async Task FindNextFileAsync()
    {
        await RunPlaybackOperationAsync("FindNextFile", $"/api/devices/{SelectedDevice?.Id}/playback/find-next-file");
    }

    private async Task GetFileByTimeAsync()
    {
        await RunPlaybackOperationAsync("GetFileByTime", $"/api/devices/{SelectedDevice?.Id}/playback/get-file-by-time");
    }

    private async Task PlaybackByTimeAsync()
    {
        await RunPlaybackOperationAsync("PlayBackByTimeEx", $"/api/devices/{SelectedDevice?.Id}/playback/playback-by-time");
    }

    private async Task FindCloseAsync()
    {
        await RunPlaybackOperationAsync("FindClose", $"/api/devices/{SelectedDevice?.Id}/playback/find-close");
    }

    private async Task PlaybackByNameAsync()
    {
        await RunPlaybackOperationAsync("PlayBackByName", $"/api/devices/{SelectedDevice?.Id}/playback/playback-by-name");
    }

    private async Task GetFileByNameAsync()
    {
        await RunPlaybackOperationAsync("GetFileByName", $"/api/devices/{SelectedDevice?.Id}/playback/get-file-by-name");
    }

    private async Task StopGetFileAsync()
    {
        await RunPlaybackOperationAsync("StopGetFile", $"/api/devices/{SelectedDevice?.Id}/playback/stop-get-file");
    }

    private async Task PlaybackSaveDataAsync()
    {
        await RunPlaybackOperationAsync("PlayBackSaveData", $"/api/devices/{SelectedDevice?.Id}/playback/playback-save-data");
    }

    private async Task StopPlaybackSaveAsync()
    {
        await RunPlaybackOperationAsync("StopPlayBackSave", $"/api/devices/{SelectedDevice?.Id}/playback/stop-playback-save");
    }

    private async Task RunPlaybackOperationAsync(string operation, string endpoint)
    {
        if (SelectedDevice is null)
        {
            RecordingState = "Select a device first.";
            return;
        }

        var request = BuildPlaybackRequest();
        var result = await PostAsync<NvrPlaybackCallResult>(endpoint, request);
        if (result is null)
        {
            RecordingState = $"{operation} failed.";
            return;
        }

        RecordingState = $"{operation}: {(result.Success ? "ok" : "failed")} ({result.Endpoint})";
        RecordingDiagnostics = JsonSerializer.Serialize(result, SerializerOptions);
    }

    private NvrPlaybackRequest BuildPlaybackRequest()
    {
        if (!DateTimeOffset.TryParse(StorageBeginTime, out var begin))
        {
            begin = DateTimeOffset.Now.AddMinutes(-10);
        }

        if (!DateTimeOffset.TryParse(StorageEndTime, out var end))
        {
            end = DateTimeOffset.Now;
        }

        if (end < begin)
        {
            (begin, end) = (end, begin);
        }

        if (!int.TryParse(StorageSessionId, out var sessionId))
        {
            sessionId = 0;
        }

        if (!int.TryParse(StorageChannelId, out var channelId))
        {
            channelId = 1;
        }

        if (!int.TryParse(StorageHandleId, out var handleId))
        {
            handleId = 0;
        }

        return new NvrPlaybackRequest
        {
            SessionId = Math.Max(0, sessionId),
            ChannelId = Math.Max(1, channelId),
            BeginTime = begin,
            EndTime = end,
            Type = string.IsNullOrWhiteSpace(StorageMediaType) ? "all" : StorageMediaType.Trim(),
            Cursor = string.IsNullOrWhiteSpace(StorageCursor) ? null : StorageCursor.Trim(),
            FileName = string.IsNullOrWhiteSpace(StorageFileName) ? null : StorageFileName.Trim(),
            SavePath = string.IsNullOrWhiteSpace(StorageSavePath) ? null : StorageSavePath.Trim(),
            HandleId = handleId
        };
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

            OnPropertyChanged(nameof(HasPendingChanges));
            OnPropertyChanged(nameof(DirtyStateText));
        }
    }

    private bool CanEdit(string key)
    {
        if (!HasWidgetTruth(key))
        {
            return false;
        }

        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return _groupedRetestByField.TryGetValue(key, out var grouped)
                && grouped.Classification is ForcedFieldClassification.Writable
                    or ForcedFieldClassification.WritableNeedsCommitTrigger
                    or ForcedFieldClassification.DelayedApply;
        }

        if (_groupedRetestByField.TryGetValue(key, out var groupedResult))
        {
            return groupedResult.Classification is ForcedFieldClassification.Writable
                or ForcedFieldClassification.WritableNeedsCommitTrigger
                or ForcedFieldClassification.DelayedApply;
        }

        return field.WriteVerified && field.SupportState.Equals(nameof(ContractSupportState.Supported), StringComparison.OrdinalIgnoreCase) && !field.ExpertOnly;
    }

    private Visibility FieldVisibility(string key, bool requireStaticNetwork = false)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return Visibility.Collapsed;
        }

        if (field.ExpertOnly)
        {
            return Visibility.Collapsed;
        }

        if (IsExplicitlyUnsupported(key))
        {
            return Visibility.Collapsed;
        }

        if (!HasWidgetTruth(key))
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

    private Visibility OperatorFieldVisibility(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return Visibility.Collapsed;
        }

        if (field.ExpertOnly)
        {
            return Visibility.Collapsed;
        }

        if (IsExplicitlyUnsupported(key))
        {
            return Visibility.Collapsed;
        }

        if (!HasWidgetTruth(key))
        {
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    private bool HasWidgetTruth(string key)
    {
        if (!_editorHintByKey.TryGetValue(key, out var hint))
        {
            return false;
        }

        if (!hint.NormalUiEligible || hint.ControlType is null)
        {
            return false;
        }

        if (!CurrentWidgetKinds.TryGetValue(key, out var currentWidget))
        {
            return true;
        }

        return currentWidget == hint.RecommendedWidget;
    }

    private string ReadOnlyTooltip(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return _groupedRetestByField.TryGetValue(key, out var groupedFallback)
                && groupedFallback.Classification == ForcedFieldClassification.ReadableOnly
                ? "Read-only: this control is proven readable but not writable on this camera."
                : string.Empty;
        }

        if (_groupedRetestByField.TryGetValue(key, out var groupedResult))
        {
            return groupedResult.Classification switch
            {
                ForcedFieldClassification.ReadableOnly => "Read-only: this control is proven readable but not writable on this camera.",
                ForcedFieldClassification.WritableNeedsCommitTrigger => "This control is writable, but the camera needs an additional commit/apply trigger.",
                ForcedFieldClassification.Uncertain => "This control has been tested on the correct grouped family, but commit/apply semantics are still uncertain.",
                _ => string.Empty
            };
        }

        return !CanEdit(key) && field.ReadVerified
            ? "Read-only: this control is proven readable but not writable on this camera."
            : string.Empty;
    }

    private string FieldState(string key)
    {
        if (!_fieldByKey.TryGetValue(key, out var field))
        {
            return _groupedRetestByField.TryGetValue(key, out var groupedFallback)
                ? GroupedState(groupedFallback)
                : "unverified";
        }

        if (_groupedRetestByField.TryGetValue(key, out var groupedResult))
        {
            return GroupedState(groupedResult);
        }

        if (field.SupportState.Equals(nameof(ContractSupportState.Unsupported), StringComparison.OrdinalIgnoreCase)
            || field.SupportState.Equals(nameof(ContractSupportState.Uncertain), StringComparison.OrdinalIgnoreCase))
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

    private static string GroupedState(GroupedUnsupportedRetestResult result)
        => result.Classification switch
        {
            ForcedFieldClassification.Writable => result.Behavior == GroupedApplyBehavior.DelayedApplied ? "delayed-apply" : "proven-working",
            ForcedFieldClassification.WritableNeedsCommitTrigger => result.Behavior == GroupedApplyBehavior.DelayedApplied ? "delayed-apply" : "needs-commit-trigger",
            ForcedFieldClassification.ReadableOnly => "read-only",
            ForcedFieldClassification.Uncertain => "uncertain",
            ForcedFieldClassification.Ignored => "ignored",
            _ => "unsupported"
        };

    private bool IsExplicitlyUnsupported(string key)
        => _groupedRetestByField.TryGetValue(key, out var groupedResult)
            && groupedResult.Classification == ForcedFieldClassification.Unsupported;

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
        if (_groupedRetestByField.TryGetValue(fieldKey, out var grouped))
        {
            var mechanism = grouped.Behavior switch
            {
                GroupedApplyBehavior.ImmediateApplied => "Proven Working",
                GroupedApplyBehavior.DelayedApplied => "Delayed Apply",
                GroupedApplyBehavior.RequiresSecondWrite => "Needs Commit Trigger",
                GroupedApplyBehavior.RequiresRelatedFieldWrite => "Needs Commit Trigger",
                GroupedApplyBehavior.RequiresCommitTrigger => "Needs Commit Trigger",
                GroupedApplyBehavior.StoredButNotOperational => "Uncertain",
                GroupedApplyBehavior.Uncertain => "Uncertain",
                _ => grouped.Classification == ForcedFieldClassification.ReadableOnly ? "Readable Only" : "Unmapped"
            };

            var suffix = grouped.Behavior switch
            {
                GroupedApplyBehavior.RequiresSecondWrite => " second-write",
                GroupedApplyBehavior.RequiresRelatedFieldWrite => " related-write",
                GroupedApplyBehavior.RequiresCommitTrigger => " commit-trigger",
                GroupedApplyBehavior.DelayedApplied => " delayed",
                _ => string.Empty
            };
            return mechanism + suffix;
        }

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

    private static bool ParseBool(string raw)
        => bool.TryParse(raw, out var parsed) && parsed;

    private void ShowToast(string message, bool success)
    {
        ToastMessage = message;
        ToastBackground = success ? "#245F3A" : "#7E3030";
        ToastVisibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

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
            nameof(VideoCodec), nameof(VideoProfile), nameof(VideoDayNight), nameof(VideoIrMode), nameof(VideoWdr), nameof(VideoIrCut), nameof(VideoIrCutMethod), nameof(VideoSceneMode), nameof(VideoExposure), nameof(VideoAwb), nameof(VideoLowlight), nameof(VideoBitrateMode), nameof(VideoDefinition),
            nameof(VideoResolution), nameof(VideoResolutionOptions),
            nameof(VideoBitrate), nameof(VideoBitrateSlider), nameof(VideoBitrateMin), nameof(VideoBitrateMax),
            nameof(VideoFpsHint),
            nameof(VideoFrameRate), nameof(VideoKeyframeInterval), nameof(StreamAudioEnabled), nameof(StreamAudioBitrate), nameof(StreamAudioSampleRate), nameof(ImageBrightness), nameof(ImageBrightnessSlider), nameof(ImageBrightnessMin), nameof(ImageBrightnessMax),
            nameof(ImageContrast), nameof(ImageContrastSlider), nameof(ImageSaturation), nameof(ImageSaturationSlider), nameof(ImageHue), nameof(ImageHueSlider), nameof(ImageGamma), nameof(ImageGammaSlider), nameof(ImageSharpness), nameof(ImageSharpnessSlider), nameof(ImageManualSharpness), nameof(ImageManualSharpnessSlider), nameof(ImageDenoise), nameof(ImageDenoiseSlider), nameof(ImageWdrStrength), nameof(ImageWdrStrengthSlider),
            nameof(ImageWhiteLight), nameof(ImageWhiteLightSlider), nameof(ImageInfrared), nameof(ImageInfraredSlider), nameof(ImageOsd), nameof(ImageOsdChannelNameEnabled), nameof(ImageOsdChannelNameText), nameof(ImageOsdDateTimeEnabled), nameof(ImageOsdDateFormat), nameof(ImageOsdTimeFormat), nameof(ImageOsdDisplayWeek), nameof(ImageMirror), nameof(ImageFlip),
            nameof(ImageBrightnessBehaviorBadge), nameof(ImageContrastBehaviorBadge), nameof(ImageSaturationBehaviorBadge), nameof(ImageSharpnessBehaviorBadge), nameof(ImageWdrBehaviorBadge), nameof(ImageTruthSummary),
            nameof(NetworkIp), nameof(NetworkNetmask), nameof(NetworkGateway), nameof(NetworkDns), nameof(NetworkPort), nameof(NetworkDhcpMode), nameof(NetworkEseeEnabled), nameof(NetworkNtpEnabled), nameof(NetworkNtpServer), nameof(NetworkEseeId), nameof(WirelessMode), nameof(WirelessApMode), nameof(MotionEnabled), nameof(MotionType), nameof(MotionSensitivity), nameof(MotionSensitivitySlider), nameof(MotionAlarmDuration), nameof(MotionAlarm), nameof(MotionBuzzer), nameof(VideoLossAlarmDuration), nameof(VideoLossAlarm), nameof(VideoLossBuzzer),
            nameof(PrivacyMaskEnabled), nameof(PrivacyMaskX), nameof(PrivacyMaskY), nameof(PrivacyMaskWidth), nameof(PrivacyMaskHeight), nameof(AlarmInputState), nameof(AlarmOutputState), nameof(AlarmPulseDuration), nameof(SdStatus), nameof(SdMediaType), nameof(CameraSerial), nameof(CameraMac),
            nameof(AlarmDuration), nameof(AlarmEnabled), nameof(AlarmBuzzer),
            nameof(CurrentControlUrl), nameof(PredictedControlUrl),
            nameof(WirelessApSsid), nameof(WirelessApChannel),
            nameof(VideoCodecState), nameof(VideoProfileState), nameof(NetworkIpState), nameof(WirelessApPskState),
            nameof(CanEditVideoCodec), nameof(CanEditVideoProfile), nameof(CanEditVideoResolution), nameof(CanEditVideoDayNight), nameof(CanEditVideoIrMode), nameof(CanEditVideoWdr), nameof(CanEditVideoIrCut), nameof(CanEditVideoIrCutMethod), nameof(CanEditVideoSceneMode), nameof(CanEditVideoExposure), nameof(CanEditVideoAwb), nameof(CanEditVideoLowlight), nameof(CanEditVideoBitrateMode), nameof(CanEditVideoDefinition),
            nameof(CanEditImageBrightness), nameof(CanEditImageContrast), nameof(CanEditImageSaturation), nameof(CanEditImageSharpness), nameof(CanEditImageManualSharpness), nameof(CanEditImageDenoise), nameof(CanEditImageWdrStrength), nameof(CanEditImageHue), nameof(CanEditImageGamma), nameof(CanEditImageMirror), nameof(CanEditImageFlip),
            nameof(CanEditImageWhiteLight), nameof(CanEditImageInfrared), nameof(CanEditImageOsd), nameof(CanEditImageOsdChannelNameEnabled), nameof(CanEditImageOsdChannelNameText), nameof(CanEditImageOsdDateTimeEnabled), nameof(CanEditImageOsdDateFormat), nameof(CanEditImageOsdTimeFormat), nameof(CanEditImageOsdDisplayWeek), nameof(CanEditImageDayNight), nameof(CanEditImageIrMode), nameof(CanEditImageIrCut), nameof(CanEditImageIrCutMethod), nameof(CanEditImageSceneMode), nameof(CanEditImageExposure), nameof(CanEditImageAwb), nameof(CanEditImageLowlight), nameof(CanEditMotionEnabled), nameof(CanEditMotionType), nameof(CanEditMotionSensitivity), nameof(CanEditMotionAlarmDuration), nameof(CanEditMotionAlarm), nameof(CanEditMotionBuzzer), nameof(CanEditVideoLossAlarmDuration), nameof(CanEditVideoLossAlarm), nameof(CanEditVideoLossBuzzer),
            nameof(CanEditPrivacyMaskEnabled), nameof(CanEditPrivacyMaskX), nameof(CanEditPrivacyMaskY), nameof(CanEditPrivacyMaskWidth), nameof(CanEditPrivacyMaskHeight), nameof(CanEditAlarmInputState), nameof(CanEditAlarmOutputState), nameof(CanEditAlarmPulseDuration), nameof(CanEditAlarmDuration), nameof(CanEditAlarmEnabled), nameof(CanEditAlarmBuzzer),
            nameof(CanEditStreamBitrate), nameof(CanEditStreamFrameRate), nameof(CanEditStreamKeyframe), nameof(CanEditStreamAudioEnabled), nameof(CanEditStreamAudioBitrate), nameof(CanEditStreamAudioSampleRate),
            nameof(CanEditNetworkIp), nameof(CanEditNetworkNetmask), nameof(CanEditNetworkGateway), nameof(CanEditNetworkDns), nameof(CanEditNetworkPort), nameof(CanEditNetworkDhcpMode), nameof(CanEditNetworkEseeEnabled), nameof(CanEditNetworkNtpEnabled), nameof(CanEditNetworkNtpServer), nameof(CanEditWirelessMode), nameof(CanEditWirelessApMode),
            nameof(CanEditWirelessApSsid), nameof(CanEditWirelessApChannel),
            nameof(VideoCodecVisibility), nameof(VideoProfileVisibility), nameof(VideoDayNightVisibility), nameof(VideoWdrVisibility), nameof(VideoIrCutVisibility), nameof(VideoIrCutMethodVisibility), nameof(VideoSceneModeVisibility), nameof(VideoExposureVisibility), nameof(VideoAwbVisibility), nameof(VideoLowlightVisibility), nameof(VideoResolutionVisibility),
            nameof(VideoBitrateVisibility), nameof(VideoFrameRateVisibility), nameof(StreamResolutionVisibility), nameof(StreamCodecVisibility), nameof(StreamProfileVisibility), nameof(StreamBitrateModeVisibility), nameof(StreamDefinitionVisibility), nameof(StreamBitrateVisibility), nameof(StreamFpsVisibility), nameof(StreamKeyframeVisibility), nameof(StreamAudioEnabledVisibility), nameof(StreamAudioBitrateVisibility), nameof(StreamAudioSampleRateVisibility),
            nameof(ImageBrightnessVisibility), nameof(ImageContrastVisibility), nameof(ImageSaturationVisibility), nameof(ImageManualSharpnessVisibility), nameof(ImageDenoiseVisibility), nameof(ImageWdrStrengthVisibility), nameof(ImageMirrorVisibility), nameof(ImageFlipVisibility), nameof(ImageDayNightVisibility), nameof(ImageIrModeVisibility), nameof(ImageIrCutVisibility), nameof(ImageIrCutMethodVisibility), nameof(ImageSceneModeVisibility), nameof(ImageExposureVisibility), nameof(ImageAwbVisibility), nameof(ImageLowlightVisibility),
            nameof(ImageHueVisibility), nameof(ImageGammaVisibility), nameof(ImageSharpnessVisibility), nameof(ImageWhiteLightVisibility), nameof(ImageInfraredVisibility), nameof(ImageOsdVisibility), nameof(ImageOsdChannelNameEnabledVisibility), nameof(ImageOsdChannelNameTextVisibility), nameof(ImageOsdDateTimeEnabledVisibility), nameof(ImageOsdDateFormatVisibility), nameof(ImageOsdTimeFormatVisibility), nameof(ImageOsdDisplayWeekVisibility), nameof(NetworkIpVisibility), nameof(NetworkNetmaskVisibility), nameof(NetworkGatewayVisibility),
            nameof(NetworkDnsVisibility), nameof(NetworkPortVisibility), nameof(NetworkDhcpModeVisibility), nameof(NetworkEseeVisibility), nameof(NetworkNtpEnabledVisibility), nameof(NetworkNtpServerVisibility), nameof(NetworkEseeIdVisibility), nameof(WirelessModeVisibility), nameof(WirelessApModeVisibility), nameof(WirelessApSsidVisibility), nameof(WirelessApPskVisibility), nameof(WirelessApChannelVisibility), nameof(MotionEnabledVisibility), nameof(MotionTypeVisibility), nameof(MotionSensitivityVisibility), nameof(MotionAlarmDurationVisibility), nameof(MotionAlarmVisibility), nameof(MotionBuzzerVisibility), nameof(VideoLossAlarmDurationVisibility), nameof(VideoLossAlarmVisibility), nameof(VideoLossBuzzerVisibility), nameof(PrivacyMaskEnabledVisibility), nameof(PrivacyMaskXVisibility), nameof(PrivacyMaskYVisibility), nameof(PrivacyMaskWidthVisibility), nameof(PrivacyMaskHeightVisibility), nameof(AlarmInputStateVisibility), nameof(AlarmOutputStateVisibility), nameof(AlarmPulseDurationVisibility), nameof(AlarmDurationVisibility), nameof(AlarmEnabledVisibility), nameof(AlarmBuzzerVisibility), nameof(SdStatusVisibility), nameof(SdMediaTypeVisibility), nameof(CameraSerialVisibility), nameof(CameraMacVisibility),
            nameof(ImageBrightnessReadOnlyTooltip), nameof(ImageContrastReadOnlyTooltip), nameof(ImageSaturationReadOnlyTooltip), nameof(ImageSharpnessReadOnlyTooltip), nameof(ImageManualSharpnessReadOnlyTooltip), nameof(ImageHueReadOnlyTooltip),
            nameof(ImageMirrorReadOnlyTooltip), nameof(ImageFlipReadOnlyTooltip), nameof(ImageDayNightReadOnlyTooltip), nameof(ImageIrModeReadOnlyTooltip), nameof(ImageIrCutReadOnlyTooltip),
            nameof(HasPendingChanges), nameof(DirtyStateText), nameof(LastSyncText), nameof(GroupedApplyIndicator), nameof(SelectedPersistenceField), nameof(LastReachableUrl)
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

    private static JsonObject? ParseObjectNode(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new JsonObject();
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                return JsonNode.Parse(trimmed) as JsonObject;
            }
            catch
            {
                return null;
            }
        }

        return null;
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
            ShowToast("Request failed. See Advanced > Raw JSON.", success: false);
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
            ShowToast("Request failed. See Advanced > Raw JSON.", success: false);
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

public sealed class EndpointSurfaceRow
{
    public string Family { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string ContractKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Surface { get; init; } = string.Empty;
    public string WrapperObjectName { get; init; } = string.Empty;
    public string DisruptionClass { get; init; } = string.Empty;
    public string TruthState { get; init; } = string.Empty;
    public bool Writable { get; init; }
    public bool ExpertOnly { get; init; }
    public bool RequiresConfirmation { get; init; }
    public bool CurrentPayloadAvailable { get; init; }
    public bool SupportsExecution { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string CurrentPayload { get; init; } = "{}";
    public string SuggestedPayload { get; init; } = "{}";
    public string EditablePayload { get; set; } = "{}";

    public static EndpointSurfaceRow FromItem(EndpointSurfaceItem item)
    {
        var suggested = item.SuggestedPayload?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) ?? "{}";
        var current = item.CurrentPayload?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) ?? "{}";
        return new EndpointSurfaceRow
        {
            Family = item.Family,
            GroupName = item.GroupName,
            ContractKey = item.ContractKey,
            Endpoint = item.Endpoint,
            Method = item.Method,
            Surface = item.Surface,
            WrapperObjectName = item.WrapperObjectName,
            DisruptionClass = item.DisruptionClass,
            TruthState = item.TruthState,
            Writable = item.Writable,
            ExpertOnly = item.ExpertOnly,
            RequiresConfirmation = item.RequiresConfirmation,
            CurrentPayloadAvailable = item.CurrentPayloadAvailable,
            SupportsExecution = item.SupportsExecution,
            Notes = item.Notes,
            CurrentPayload = current,
            SuggestedPayload = suggested,
            EditablePayload = item.CurrentPayloadAvailable ? current : suggested
        };
    }
}

public sealed record TypedSettingApplyRequest(string FieldKey, JsonNode? Value, bool ExpertOverride);
public sealed record TypedSettingBatchApplyRequest(IReadOnlyCollection<TypedFieldChange> Changes, bool ExpertOverride);
public sealed record PersistenceFieldVerifyRequest(string FieldKey, JsonNode? Value, bool RebootForVerification, bool ExpertOverride);
