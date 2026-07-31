using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Connectivity: per-device health snapshots with diagnostic and reconnect
/// actions. Wraps the ConnectivityWatchdog / failover surface of the service.
/// </summary>
public sealed partial class ConnectivityViewModel : SectionViewModelBase
{
    public ConnectivityViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Connectivity";
    public override string Glyph => "\U0001F50C";
    public override string Explain =>
        "Connectivity shows the live health of every camera transport (HTTP API, " +
        "RTSP, snapshot). Use Diagnose to run a full connection diagnostic on the " +
        "selected camera, or Reconnect to force the failover logic to recover it.";

    [ObservableProperty]
    private ObservableCollection<DeviceConnectivitySnapshot> _snapshots = [];

    [ObservableProperty]
    private DeviceConnectivitySnapshot? _selectedSnapshot;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public override async Task ActivateAsync()
    {
        if (Snapshots.Count == 0)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var snaps = await Api.GetConnectivityAllAsync();
            Snapshots = new ObservableCollection<DeviceConnectivitySnapshot>(snaps ?? []);
            ResultText = $"Loaded {Snapshots.Count} snapshot(s).";
        }
        catch (Exception ex)
        {
            ResultText = $"Connectivity load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DiagnoseAsync()
    {
        var target = SelectedSnapshot
            ?? (SelectedDevice is { } d
                ? new DeviceConnectivitySnapshot { DeviceId = d.Id }
                : null);
        if (target is null)
        {
            ResultText = "Select a camera snapshot (or a device on the left) first.";
            return;
        }

        try
        {
            var result = await Api.DiagnoseConnectivityAsync(target.DeviceId);
            // Refresh first so the action outcome (not the refresh summary) is
            // what the operator sees in ResultText.
            await RefreshAsync();
            ResultText = $"Diagnose returned: {result}";
        }
        catch (Exception ex)
        {
            ResultText = $"Diagnose failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        var target = SelectedSnapshot
            ?? (SelectedDevice is { } d
                ? new DeviceConnectivitySnapshot { DeviceId = d.Id }
                : null);
        if (target is null)
        {
            ResultText = "Select a camera snapshot (or a device on the left) first.";
            return;
        }

        try
        {
            var result = await Api.ReconnectDeviceAsync(target.DeviceId);
            await RefreshAsync();
            ResultText = $"Reconnect returned: {result}";
        }
        catch (Exception ex)
        {
            ResultText = $"Reconnect failed: {ex.Message}";
        }
    }
}
