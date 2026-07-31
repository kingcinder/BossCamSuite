using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Dashboard: service health, per-device connectivity, and storage paths —
/// the "at a glance" landing section.
/// </summary>
public sealed partial class DashboardViewModel : SectionViewModelBase
{
    public DashboardViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Dashboard";
    public override string Glyph => "\U0001F3E0";
    public override string Explain =>
        "Dashboard gives you an at-a-glance view of the whole BossCamSuite install: " +
        "service health, the connectivity status of every registered camera, and the " +
        "media storage folders in use. Use Refresh to pull the latest values.";

    [ObservableProperty]
    private string _healthText = "Not checked yet.";

    [ObservableProperty]
    private string _storageSummary = "Storage paths not loaded.";

    [ObservableProperty]
    private string _lastRefreshed = string.Empty;

    [ObservableProperty]
    private ObservableCollection<DeviceConnectivitySnapshot> _connectivity = [];

    public override async Task ActivateAsync()
    {
        if (Connectivity.Count == 0 && string.IsNullOrEmpty(LastRefreshed))
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var health = await Api.GetHealthAsync();
            HealthText = DescribeHealth(health);
        }
        catch (Exception ex)
        {
            HealthText = $"Health check failed: {ex.Message}";
        }

        try
        {
            var snaps = await Api.GetConnectivityAllAsync();
            Connectivity = new ObservableCollection<DeviceConnectivitySnapshot>(snaps ?? []);
        }
        catch (Exception ex)
        {
            SetStatus($"Connectivity load failed: {ex.Message}");
        }

        try
        {
            var paths = await Api.GetStoragePathsAsync();
            StorageSummary = paths is null
                ? "Storage paths unavailable."
                : $"Continuous: {Fmt(paths.ContinuousRecordings)}\nHighlights: {Fmt(paths.Highlights)}\nSnapshots: {Fmt(paths.Snapshots)}";
        }
        catch (Exception ex)
        {
            StorageSummary = $"Storage paths failed: {ex.Message}";
        }

        LastRefreshed = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SetStatus("Dashboard refreshed.");
    }

    private static string DescribeHealth(JsonElement? health)
    {
        if (health is not { } h || h.ValueKind != JsonValueKind.Object)
        {
            return "Service reachable (health payload not structured).";
        }

        var parts = new List<string>();
        foreach (var prop in h.EnumerateObject())
        {
            parts.Add($"{prop.Name}: {prop.Value}");
        }
        return parts.Count == 0 ? "Service is healthy." : string.Join(" · ", parts);
    }
}
