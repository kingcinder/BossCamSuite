using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using System.Collections.ObjectModel;

namespace BossCam.Desktop.Avalonia.ViewModels;

/// <summary>
/// Devices: register, discover and inspect cameras. Registration accepts a
/// single device or the Aegon-LAN convenience batch (typical lab installs).
/// </summary>
public sealed partial class DevicesViewModel : SectionViewModelBase
{
    public DevicesViewModel(IBossCamApiClient api, MainWindowViewModel shell)
        : base(api, shell)
    {
    }

    public override string Title => "Devices";
    public override string Glyph => "\U0001F5A5";
    public override string Explain =>
        "Devices lets you add and manage cameras. You can discover cameras on the " +
        "LAN, register a single camera by address, or bulk-register the usual Aegon " +
        "LAN set. Registered cameras appear in the shared device list on the left.";

    [ObservableProperty]
    private string _ipAddress = string.Empty;

    [ObservableProperty]
    private string _port = "80";

    [ObservableProperty]
    private string _loginName = "admin";

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _hardwareModel = "5523-W";

    [ObservableProperty]
    private string _lorexPassword = string.Empty;

    [ObservableProperty]
    private string _wvcPassword = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public override Task ActivateAsync() => Task.CompletedTask;

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        ResultText = "Discovering cameras…";
        try
        {
            var found = await Api.DiscoverAsync();
            Shell.Devices = new ObservableCollection<DeviceIdentity>(found ?? []);
            ResultText = $"Discovery returned {found?.Count ?? 0} camera(s).";
            SetStatus(ResultText);
        }
        catch (Exception ex)
        {
            ResultText = $"Discovery failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (string.IsNullOrWhiteSpace(IpAddress))
        {
            ResultText = "Enter an IP address first.";
            return;
        }

        var port = int.TryParse(Port, out var p) ? p : 80;
        try
        {
            var device = await Api.RegisterAsync(
                IpAddress.Trim(), port,
                string.IsNullOrWhiteSpace(LoginName) ? null : LoginName,
                string.IsNullOrWhiteSpace(Password) ? null : Password,
                name: null,
                string.IsNullOrWhiteSpace(HardwareModel) ? null : HardwareModel);
            ResultText = device is null
                ? "Registration completed (no device returned)."
                : $"Registered {device.DisplayName} at {device.IpAddress}.";
            await Shell.LoadDevicesCommand.ExecuteAsync(null);
            SetStatus(ResultText);
        }
        catch (Exception ex)
        {
            ResultText = $"Registration failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RegisterAegonAsync()
    {
        ResultText = "Registering Aegon LAN batch…";
        try
        {
            var devices = await Api.RegisterAegonLanAsync(
                string.IsNullOrWhiteSpace(LorexPassword) ? null : LorexPassword,
                string.IsNullOrWhiteSpace(WvcPassword) ? null : WvcPassword);
            ResultText = $"Registered {devices?.Count ?? 0} Aegon-LAN camera(s).";
            await Shell.LoadDevicesCommand.ExecuteAsync(null);
            SetStatus(ResultText);
        }
        catch (Exception ex)
        {
            ResultText = $"Aegon-LAN registration failed: {ex.Message}";
        }
    }
}
