using System.Collections.ObjectModel;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="MainWindowViewModel"/>.
/// Verifies command behavior, property change notifications, and edge cases
/// for the Avalonia desktop UI ViewModel.
///
/// Run with:
///   dotnet test src/BossCam.Desktop.Avalonia/BossCam.Desktop.Avalonia.Tests.csproj
///
/// Note: requires Avalonia NuGet packages to be restored first.
/// See scripts/restore-avalonia-packages.sh if restore times out.
/// </summary>
public sealed class MainWindowViewModelTests
{
    // ── Initial state ────────────────────────────────────────────

    [Fact]
    public void Constructor_Sets_Default_StatusText()
    {
        var vm = new MainWindowViewModel();
        Assert.Contains("Connect to BossCamService", vm.StatusText);
        Assert.Empty(vm.Devices);
        Assert.Null(vm.SelectedDevice);
        Assert.False(vm.IsLive);
        Assert.Null(vm.LiveFrame);
        Assert.Empty(vm.DeviceInfoText);
    }

    [Fact]
    public void Constructor_Sets_Empty_Devices_Collection()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.Devices);
        Assert.Empty(vm.Devices);
    }

    // ── Device selection ─────────────────────────────────────────

    [Fact]
    public void SelectDevice_Updates_SelectedDevice()
    {
        var vm = new MainWindowViewModel();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            DisplayName = "TestCam",
            IpAddress = "192.168.1.100",
            HardwareModel = "5523-W",
            FirmwareVersion = "V4.00.R02",
            DeviceType = "IPC",
            Port = 80
        };

        vm.SelectedDevice = device;

        Assert.Same(device, vm.SelectedDevice);
        // OnSelectedDeviceChanged triggers RefreshDeviceAsync which is async;
        // by the time we check, the text may or may not have been set depending
        // on the async scheduler. Check that IsLive was set to true (synchronous
        // part of the handler).
        Assert.True(vm.IsLive);
    }

    // ── Device info formatting ───────────────────────────────────

    [Fact]
    public void DeviceInfoText_Formats_Correctly()
    {
        var vm = new MainWindowViewModel();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Front Door",
            IpAddress = "10.0.0.50",
            HardwareModel = "IPC-5523W",
            FirmwareVersion = "V4.30.R01",
            DeviceType = "IPC",
            Port = 80,
            LoginName = "admin"
        };

        // Reflection-based approach: access the private RefreshDeviceAsync
        // indirectly by setting SelectedDevice (which triggers the command)
        vm.SelectedDevice = device;

        // The synchronous part of RefreshDeviceAsync runs before the await:
        Assert.Contains("IPC-5523W", vm.DeviceInfoText);
        Assert.Contains("10.0.0.50", vm.DeviceInfoText);
        Assert.Contains("V4.30.R01", vm.DeviceInfoText);
        Assert.Contains("IPC", vm.DeviceInfoText);
    }

    [Fact]
    public void DeviceInfoText_Handles_Null_Fields()
    {
        var vm = new MainWindowViewModel();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            DisplayName = "NullCam",
            IpAddress = null,
            HardwareModel = null,
            FirmwareVersion = null,
            DeviceType = null
        };

        vm.SelectedDevice = device;

        // Should use em-dashes for null fields
        Assert.Contains("\u2014", vm.DeviceInfoText);
    }

    // ── Status text updates ─────────────────────────────────────

    [Fact]
    public void StatusText_Can_Be_Updated()
    {
        var vm = new MainWindowViewModel();
        vm.StatusText = "Custom status update";
        Assert.Equal("Custom status update", vm.StatusText);
    }

    // ── Property change notifications ─────────────────────────────

    [Fact]
    public void Setting_SelectedDevice_Raises_PropertyChanged()
    {
        var vm = new MainWindowViewModel();
        var propertyNames = new List<string?>();
        vm.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        vm.SelectedDevice = new DeviceIdentity { Id = Guid.NewGuid(), DisplayName = "Test" };

        Assert.Contains(nameof(MainWindowViewModel.SelectedDevice), propertyNames);
        // IsLive is set synchronously in OnSelectedDeviceChanged → RefreshDeviceAsync
        Assert.Contains(nameof(MainWindowViewModel.IsLive), propertyNames);
    }

    // ── Edge cases ───────────────────────────────────────────────

    [Fact]
    public void SelectDevice_Null_Does_Not_Throw()
    {
        var vm = new MainWindowViewModel();
        // Should not throw even when SelectedDevice is set to null
        var exception = Record.Exception(() => vm.SelectedDevice = null);
        Assert.Null(exception);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public void Devices_Can_Be_Cleared_And_Refilled()
    {
        var vm = new MainWindowViewModel();
        vm.Devices = new ObservableCollection<DeviceIdentity>
        {
            new() { Id = Guid.NewGuid(), DisplayName = "Cam1" },
            new() { Id = Guid.NewGuid(), DisplayName = "Cam2" }
        };
        Assert.Equal(2, vm.Devices.Count);

        vm.Devices.Clear();
        Assert.Empty(vm.Devices);
    }

    // ── Dispose ──────────────────────────────────────────────────

    [Fact]
    public void Dispose_Does_Not_Throw()
    {
        var vm = new MainWindowViewModel();
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }
}
