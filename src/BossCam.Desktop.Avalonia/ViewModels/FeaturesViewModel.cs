using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Features: discover, classify and safely APPLY camera feature toggles
/// (image/video/detection). Mirrors the FeaturesPanel of the SPA: it loads the
/// control-point inventory, shows the eligible Toggle/Slider/Enum widgets,
/// gates writes behind <c>Writable</c> read-write state, and routes every apply
/// through the typed settings backend. Editors are seeded from the camera's
/// current typed values (not defaults). Expert-only controls stay disabled
/// until the per-section expert override is switched on.
/// </summary>
public sealed partial class FeaturesViewModel : SectionViewModelBase
{
    public FeaturesViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Features";
    public override string Glyph => "\U0001F39B";
    public override string Explain =>
        "Features is the safe way to flip camera settings. It lists the toggles, " +
        "sliders and dropdowns proven write-safe for the selected camera, seeded " +
        "from the camera's current values. Only write-verified controls are enabled; " +
        "expert-only controls require the Expert override switch. Run Quick Probe " +
        "to discover what this camera can do.";

    [ObservableProperty]
    private ObservableCollection<FeatureControlRow> _controls = [];

    [ObservableProperty]
    private bool _expertOverride;

    [ObservableProperty]
    private string _detailText = "Select a camera, then press Quick Probe to discover its controls.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasDevice;

    partial void OnExpertOverrideChanged(bool value)
    {
        foreach (var control in Controls)
        {
            control.RaiseEligibilityChanged();
        }
    }

    public override async Task ActivateAsync()
    {
        if (Shell.SelectedDevice is null)
        {
            HasDevice = false;
            DetailText = "Select a camera in the list on the left to manage its features.";
            return;
        }

        HasDevice = true;
        if (Controls.Count == 0)
        {
            await LoadControlPointsAsync();
        }
    }

    /// <summary>Re-checks selection whenever the shell's device changes.</summary>
    public async Task DeviceChangedAsync()
    {
        if (Shell.SelectedDevice is null)
        {
            HasDevice = false;
            DetailText = "Select a camera in the list on the left to manage its features.";
            Controls.Clear();
            return;
        }

        HasDevice = true;
        Controls.Clear();
        await LoadControlPointsAsync();
    }

    [RelayCommand]
    private async Task LoadControlPointsAsync()
    {
        if (Shell.SelectedDevice is null)
        {
            DetailText = "Select a camera first.";
            return;
        }

        IsBusy = true;
        try
        {
            var report = await Api.GetControlPointsAsync(Shell.SelectedDevice.Id);

            // Seed editors from live camera values (typed settings), not defaults.
            Dictionary<string, JsonNode?> liveValues;
            try
            {
                var groups = await Api.GetTypedSettingsAsync(Shell.SelectedDevice.Id);
                liveValues = (groups ?? [])
                    .SelectMany(g => g.Fields ?? [])
                    .Where(f => !string.IsNullOrWhiteSpace(f.FieldKey))
                    .GroupBy(f => f.FieldKey!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last().TypedValue, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                liveValues = [];
            }

            var rows = new List<FeatureControlRow>();
            foreach (var family in report?.Families ?? [])
            {
                foreach (var item in family.Controls ?? [])
                {
                    rows.Add(CreateRow(family.Family, item, liveValues));
                }
            }
            foreach (var item in report?.AmbiguousControls ?? [])
            {
                rows.Add(CreateRow("Ambiguous", item, liveValues));
            }

            rows = rows
                .Where(r => r.IsToggle || r.IsSlider || r.IsEnum || r.IsNumeric || r.IsText)
                .ToList();

            Controls = new ObservableCollection<FeatureControlRow>(rows);
            DetailText = rows.Count == 0
                ? $"No eligible controls reported for {Shell.SelectedDevice.DisplayName}. Run Quick Probe to discover them."
                : $"{rows.Count} control(s) for {Shell.SelectedDevice.DisplayName}.";
            SetStatus(DetailText);
        }
        catch (Exception ex)
        {
            DetailText = $"Control points failed to load: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private FeatureControlRow CreateRow(string family, ControlPointInventoryItem item, Dictionary<string, JsonNode?> liveValues)
        => new(this, family, item, liveValues.TryGetValue(item.FieldKey, out var live) ? live : null);

    [RelayCommand]
    private async Task QuickProbeAsync()
    {
        if (Shell.SelectedDevice is null)
        {
            DetailText = "Select a camera first.";
            return;
        }

        IsBusy = true;
        DetailText = $"Probing {Shell.SelectedDevice.DisplayName} — normalizing typed settings…";
        try
        {
            await Api.NormalizeTypedSettingsAsync(Shell.SelectedDevice.Id);
            DetailText = "Probe stage 1 complete — running capability probe…";
            var capability = await Api.ProbeAsync(Shell.SelectedDevice.Id);
            DetailText = capability is null
                ? "Probe finished (no capability map returned)."
                : $"Probe complete: adapter={capability.PrimaryControlAdapter ?? "\u2014"}, groups={capability.SupportedSettingGroups?.Count ?? 0}.";
            await LoadControlPointsAsync();
        }
        catch (Exception ex)
        {
            DetailText = $"Probe failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ApplyAsync(FeatureControlRow row)
    {
        if (Shell.SelectedDevice is null || row is null)
        {
            return;
        }

        var deviceId = Shell.SelectedDevice.Id;
        row.IsApplying = true;
        try
        {
            var result = await Api.ApplyTypedFieldAsync(deviceId, row.FieldKey, row.BuildValue(), ExpertOverride);
            if (result.Success)
            {
                row.ReadBackSucceeded(result);
                DetailText = $"{row.DisplayName}: applied and verified (semantic={result.SemanticStatus}).";
            }
            else
            {
                row.ReadBackFailed(result);
                var violations = result.ContractViolations is { Count: > 0 }
                    ? " Contract violations: " + string.Join("; ", result.ContractViolations)
                    : string.Empty;
                DetailText = $"{row.DisplayName}: not applied. {result.Message}{violations}";
            }
            SetStatus(DetailText);
            // Reload so the UI reflects the camera's current truth.
            await LoadControlPointsAsync();
        }
        catch (Exception ex)
        {
            row.ReadBackFailed(null);
            DetailText = $"{row.DisplayName}: apply failed — {ex.Message}";
        }
        finally
        {
            row.IsApplying = false;
        }
    }
}

/// <summary>
/// One feature toggle/slider/enum row. Wraps a control-point inventory item and
/// keeps the editable value, eligibility and apply state for the UI.
/// </summary>
public sealed partial class FeatureControlRow : ObservableObject
{
    private readonly FeaturesViewModel _owner;
    private readonly ControlPointInventoryItem _item;
    private readonly JsonNode? _liveValue;

    internal FeatureControlRow(FeaturesViewModel owner, string family, ControlPointInventoryItem item, JsonNode? liveValue)
    {
        _owner = owner;
        _item = item;
        _liveValue = liveValue;
        Family = family;
        FieldKey = item.FieldKey;
        ContractKey = item.ContractKey;
        DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.FieldKey : item.DisplayName;
        ReadWriteState = item.ReadWriteState;
        Endpoint = item.Endpoint;
        Blocker = item.ExactBlocker;
        AllowedValues = item.AllowedValues?.ToList() ?? [];
        Min = item.Min;
        Max = item.Max;
        RecommendedWidget = item.RecommendedWidget;

        _currentValue = CurrentValueFromItem(item, liveValue);
        _expertOnly = string.Equals(item.Ownership, "expert", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ReadWriteState, "WritableExpertOnly", StringComparison.OrdinalIgnoreCase);
    }

    public string Family { get; }
    public string FieldKey { get; }
    public string ContractKey { get; }
    public string DisplayName { get; }
    public string ReadWriteState { get; }
    public string Endpoint { get; }
    public string Blocker { get; }
    public List<string> AllowedValues { get; }
    public decimal? Min { get; }
    public decimal? Max { get; }
    public ControlPointWidgetKind RecommendedWidget { get; }

    [ObservableProperty]
    private string _currentValue;

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private bool _expertOnly;

    partial void OnIsApplyingChanged(bool value)
    {
        // In-flight state changes eligibility-driven visibility (IsEnabled/IsDisabled).
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsDisabled));
    }

    /// <summary>
    /// Boolean editor value (two-way bound to a CheckBox). Parses the seeded
    /// text value; writes back canonical "true"/"false".
    /// </summary>
    public bool CurrentBool
    {
        get => bool.TryParse(CurrentValue, out var b) && b;
        set
        {
            CurrentValue = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Numeric editor value (two-way bound to a Slider / NumericUpDown).
    /// Defaults to the seeded value or the lower bound.
    /// </summary>
    public decimal CurrentNumber
    {
        get => decimal.TryParse(CurrentValue, out var d) ? d : (Min ?? 0);
        set
        {
            CurrentValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            OnPropertyChanged();
        }
    }

    /// <summary>True when the backend has write-verified this field.</summary>
    public bool IsWritable => ReadWriteState.StartsWith("Writable", StringComparison.OrdinalIgnoreCase);

    public bool IsToggle => RecommendedWidget == ControlPointWidgetKind.Toggle;
    public bool IsSlider => RecommendedWidget == ControlPointWidgetKind.Slider;
    public bool IsEnum => RecommendedWidget == ControlPointWidgetKind.Dropdown && AllowedValues.Count > 0;
    public bool IsNumeric => RecommendedWidget == ControlPointWidgetKind.NumericInput;
    public bool IsText => RecommendedWidget == ControlPointWidgetKind.TextInput;

    // ── Explainer texts (shown in hover popups over each widget) ──

    public string ToggleExplainer =>
        $"Toggles \"{DisplayName}\" ({FieldKey}) on the camera. " +
        "The current value shown is the camera's live value where known.";

    public string SliderExplainer =>
        $"Slides \"{DisplayName}\" ({FieldKey}) within {Min}–{Max}. " +
        "Seeded from the camera's current value; drag then press Apply.";

    public string EnumExplainer =>
        $"Chooses a value for \"{DisplayName}\" ({FieldKey}). " +
        "Only the listed options are accepted by this camera.";

    public string NumericExplainer =>
        $"Edits the numeric value of \"{DisplayName}\" ({FieldKey}) within {Min}–{Max}.";

    public string TextExplainer =>
        $"Edits the text value of \"{DisplayName}\" ({FieldKey}). " +
        "The camera may reject values outside its accepted format.";

    public bool IsBlocked => !string.IsNullOrWhiteSpace(Blocker);

    /// <summary>Eligible for the normal (non-expert) UI: writable, not blocked, not expert-only.</summary>
    public bool IsEligible => IsWritable && !IsBlocked && !ExpertOnly;

    /// <summary>
    /// Enabled in the UI (expert controls unlock only with the override switch);
    /// disabled while an apply is in flight to prevent double submission.
    /// </summary>
    public bool IsEnabled => IsWritable && !IsApplying && (IsEligible || (ExpertOnly && _owner.ExpertOverride));

    public bool IsDisabled => !IsEnabled;

    public void RaiseEligibilityChanged() => OnPropertyChanged(nameof(IsEnabled));

    [RelayCommand]
    private Task ApplyAsync() => _owner.ApplyAsync(this);

    /// <summary>Serializes the current editor value into the JSON payload shape the typed-apply endpoint expects.</summary>
    public JsonNode? BuildValue()
    {
        if (IsToggle)
        {
            return JsonValue.Create(bool.TryParse(CurrentValue, out var b) && b);
        }
        if (IsNumeric || IsSlider)
        {
            return JsonValue.Create(decimal.TryParse(CurrentValue, out var d) ? d : 0);
        }
        return JsonValue.Create(CurrentValue);
    }

    public void ReadBackSucceeded(WriteResult result)
    {
        // Use raw value text (consistent with how editors are seeded) so string
        // fields don't pick up JSON quote characters; booleans stay lower case.
        if (result.PostWriteValue is not null)
        {
            CurrentValue = JsonNodeText.Of(result.PostWriteValue);
        }
        else if (result.Response is not null)
        {
            CurrentValue = JsonNodeText.Of(result.Response);
        }
    }

    public void ReadBackFailed(WriteResult? result)
    {
        // Revert to the last known value from the inventory snapshot.
        var fallback = _item.ValueDisplay(_liveValue);
        if (!string.IsNullOrEmpty(fallback))
        {
            CurrentValue = fallback;
        }
        _ = result;
    }

    private static string CurrentValueFromItem(ControlPointInventoryItem item, JsonNode? liveValue)
        => ControlPointItemExtensions.ValueDisplay(item, liveValue) ?? string.Empty;
}

internal static class ControlPointItemExtensions
{
    /// <summary>
    /// Best-effort current value string for an inventory item, preferring a live
    /// typed value when the caller has one. Booleans are normalized to lower
    /// case (<c>true</c>/<c>false</c>) so editors and tests agree on casing.
    /// </summary>
    public static string? ValueDisplay(this ControlPointInventoryItem item, JsonNode? liveValue = null)
    {
        if (liveValue is not null)
        {
            var text = JsonNodeText.Of(liveValue);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }
        if (item.AllowedValues is { Count: > 0 })
        {
            return item.AllowedValues.First();
        }
        if (item.PrimitiveType == ControlPointPrimitiveType.Boolean)
        {
            return "false";
        }
        if (item.Min is not null)
        {
            return item.Min.ToString();
        }
        return null;
    }
}

/// <summary>Formats a JsonNode as editor text, normalizing booleans to lower case.</summary>
internal static class JsonNodeText
{
    public static string Of(JsonNode? node)
        => node is JsonValue jv && jv.TryGetValue<bool>(out var b)
            ? (b ? "true" : "false")
            : node?.ToString() ?? string.Empty;
}
