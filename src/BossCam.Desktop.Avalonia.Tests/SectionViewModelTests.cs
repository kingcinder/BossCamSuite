using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Desktop.Avalonia.ViewModels;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Unit tests for the section ViewModels (Features, Recordings, Devices,
/// Storage, Highlights, Playback, Diagnostics, Connectivity, Dashboard).
/// Uses <see cref="TestBossCamApiClient"/> so no live server is needed.
/// </summary>
public sealed class SectionViewModelTests
{
    private static (MainWindowViewModel Shell, TestBossCamApiClient Api) Create()
    {
        var api = new TestBossCamApiClient();
        return (new MainWindowViewModel(api), api);
    }

    private static DeviceIdentity Device()
        => new() { Id = Guid.NewGuid(), Name = "TestCam", IpAddress = "192.168.1.100" };

    private static ControlPointInventoryReport ReportWith(
        ControlPointWidgetKind widget,
        string fieldKey = "flip",
        string readWriteState = "Writable",
        string? blocker = null,
        string ownership = "operator")
    {
        var item = new ControlPointInventoryItem
        {
            FieldKey = fieldKey,
            ContractKey = "image.flip",
            DisplayName = "Flip",
            Endpoint = "/cgi-bin/image",
            RecommendedWidget = widget,
            ReadWriteState = readWriteState,
            ExactBlocker = blocker ?? string.Empty,
            Ownership = ownership,
            AllowedValues = widget == ControlPointWidgetKind.Dropdown ? ["off", "on"] : [],
            PrimitiveType = widget == ControlPointWidgetKind.Toggle ? ControlPointPrimitiveType.Boolean : ControlPointPrimitiveType.Integer
        };
        return new ControlPointInventoryReport
        {
            DeviceId = Device().Id,
            Families = [new ControlPointInventoryFamily { Family = "image", Controls = [item] }]
        };
    }

    // ── Features: control-point loading + typed apply ─────────────

    [Fact]
    public async Task Features_Loads_Eligible_Toggles()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Toggle);

        await shell.FeaturesSection.ActivateAsync();

        Assert.Single(shell.FeaturesSection.Controls);
        Assert.True(shell.FeaturesSection.Controls[0].IsToggle);
        Assert.True(shell.FeaturesSection.Controls[0].IsEnabled);
        shell.Dispose();
    }

    [Fact]
    public async Task Features_Seeds_Editor_From_Live_Typed_Value()
    {
        var (shell, api) = Create();
        var device = Device();
        shell.SelectedDevice = device;
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Toggle);
        api.TypedSettingsResult =
        [
            new TypedSettingGroupSnapshot
            {
                DeviceId = device.Id,
                GroupName = "image",
                Fields =
                [
                    new NormalizedSettingField { FieldKey = "flip", TypedValue = JsonValue.Create(true) }
                ]
            }
        ];

        await shell.FeaturesSection.ActivateAsync();

        Assert.Equal("true", shell.FeaturesSection.Controls[0].CurrentValue);
        Assert.True(shell.FeaturesSection.Controls[0].CurrentBool);
        shell.Dispose();
    }

    [Fact]
    public async Task Features_Apply_Sends_Typed_Field_Shape()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Toggle);

        await shell.FeaturesSection.ActivateAsync();
        await shell.FeaturesSection.Controls[0].ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, api.ApplyCallCount);
        Assert.Equal("flip", api.LastAppliedFieldKey);
        Assert.False(api.LastExpertOverride);
        var applied = Assert.IsAssignableFrom<JsonValue>(api.LastAppliedValue);
        Assert.False(applied.GetValue<bool>());
        shell.Dispose();
    }

    [Fact]
    public async Task Features_Apply_With_ExpertOverride_Flagged()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Dropdown, readWriteState: "WritableExpertOnly", ownership: "expert");

        await shell.FeaturesSection.ActivateAsync();
        shell.FeaturesSection.ExpertOverride = true;
        var row = shell.FeaturesSection.Controls[0];
        Assert.True(row.IsEnabled);

        await row.ApplyCommand.ExecuteAsync(null);

        Assert.True(api.LastExpertOverride);
        Assert.Equal("flip", api.LastAppliedFieldKey);
        shell.Dispose();
    }

    [Fact]
    public async Task Features_Blocked_Control_Is_Disabled()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Toggle, blocker: "missing-contract");

        await shell.FeaturesSection.ActivateAsync();

        Assert.False(shell.FeaturesSection.Controls[0].IsEnabled);
        shell.Dispose();
    }

    [Fact]
    public async Task Features_QuickProbe_Normalizes_Then_Probes()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Toggle);

        await shell.FeaturesSection.QuickProbeCommand.ExecuteAsync(null);

        Assert.Equal(1, api.NormalizeCallCount);
        Assert.Equal(1, api.ProbeCallCount);
        shell.Dispose();
    }

    // ── Recordings ────────────────────────────────────────────────

    [Fact]
    public async Task Recordings_StartSelected_Uses_Selected_Device()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();

        await shell.RecordingsSection.StartSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, api.StartRecordingCallCount);
        Assert.Contains("Recording started", shell.RecordingsSection.ResultText);
        shell.Dispose();
    }

    [Fact]
    public async Task Recordings_StartSelected_Requires_Device()
    {
        var (shell, _) = Create();

        await shell.RecordingsSection.StartSelectedCommand.ExecuteAsync(null);

        Assert.Contains("Select a camera", shell.RecordingsSection.ResultText);
    }

    [Fact]
    public async Task Recordings_Export_Uses_Selected_Device_And_Times()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ExportResult = new ClipExportResult { Success = true, OutputPath = "/tmp/clip.mp4", Bytes = 1234, ReEncoded = false };

        await shell.RecordingsSection.ExportClipCommand.ExecuteAsync(null);

        Assert.Equal(1, api.ExportCallCount);
        Assert.Equal(shell.SelectedDevice.Id, api.LastExportDeviceId);
        Assert.Contains("Exported", shell.RecordingsSection.ResultText);
        shell.Dispose();
    }

    [Fact]
    public async Task Recordings_Refresh_Loads_Jobs_And_Segments()
    {
        var (shell, api) = Create();
        api.JobsResult = [new RecordingJob { Id = Guid.NewGuid(), IsRunning = true }];
        api.SegmentsResult = [new RecordingSegment { FilePath = "/s/seg.ts", DurationSec = 300 }];

        await shell.RecordingsSection.RefreshCommand.ExecuteAsync(null);

        Assert.Single(shell.RecordingsSection.Jobs);
        Assert.Single(shell.RecordingsSection.Segments);
    }

    [Fact]
    public async Task Recordings_Housekeeping_Reports_Deletions()
    {
        var (shell, api) = Create();
        api.HousekeepingResult = new RecordingHousekeepingResult { FilesDeleted = 7, BytesDeleted = 99999 };

        await shell.RecordingsSection.RunHousekeepingCommand.ExecuteAsync(null);

        Assert.Contains("7 file(s)", shell.RecordingsSection.HousekeepingResult);
    }

    // ── Devices ───────────────────────────────────────────────────

    [Fact]
    public async Task Devices_Register_Calls_Api_And_Reloads()
    {
        var (shell, api) = Create();
        api.RegisterResult = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.9", Name = "NewCam" };

        shell.DevicesSection.IpAddress = "10.0.0.9";
        shell.DevicesSection.Port = "80";
        shell.DevicesSection.LoginName = "admin";
        shell.DevicesSection.Password = "secret";

        await shell.DevicesSection.RegisterCommand.ExecuteAsync(null);

        Assert.Contains("Registered", shell.DevicesSection.ResultText);
        Assert.Equal(1, api.GetDevicesCallCount); // reload after registration
    }

    [Fact]
    public async Task Devices_Register_Requires_Ip()
    {
        var (shell, _) = Create();

        await shell.DevicesSection.RegisterCommand.ExecuteAsync(null);

        Assert.Contains("Enter an IP", shell.DevicesSection.ResultText);
    }

    // ── Storage ───────────────────────────────────────────────────

    [Fact]
    public async Task Storage_Load_Populates_Paths()
    {
        var (shell, api) = Create();
        api.StoragePathsResult = new MediaStoragePaths
        {
            ContinuousRecordings = "/media/rec",
            Highlights = "/media/hi",
            Snapshots = "/media/snap"
        };

        await shell.StorageSection.LoadCommand.ExecuteAsync(null);

        Assert.Equal("/media/rec", shell.StorageSection.ContinuousRecordings);
        Assert.Equal("/media/hi", shell.StorageSection.Highlights);
        Assert.Equal("/media/snap", shell.StorageSection.Snapshots);
    }

    [Fact]
    public async Task Storage_Save_Persists_Paths()
    {
        var (shell, api) = Create();
        shell.StorageSection.ContinuousRecordings = "/new/rec";
        shell.StorageSection.Highlights = "/new/hi";
        shell.StorageSection.Snapshots = "/new/snap";

        await shell.StorageSection.SaveCommand.ExecuteAsync(null);

        Assert.Equal("/new/rec", api.SavedStoragePathsResult?.ContinuousRecordings);
        Assert.Contains("saved", shell.StorageSection.ResultText);
    }

    // ── Highlights ────────────────────────────────────────────────

    [Fact]
    public async Task Highlights_Select_Requires_Device()
    {
        var (shell, _) = Create();

        await shell.HighlightsSection.SelectDeviceCommand.ExecuteAsync(null);

        Assert.Contains("Select a camera", shell.HighlightsSection.ResultText);
    }

    [Fact]
    public async Task Highlights_Stream_Applies_Mode()
    {
        var (shell, api) = Create();
        api.HighlightsResult = new BossCam.Desktop.Avalonia.Models.HighlightBoardSnapshot { PreferredStream = "sub" };
        shell.HighlightsSection.StreamMode = "sub";

        await shell.HighlightsSection.StreamCommand.ExecuteAsync(null);

        Assert.Contains("sub", shell.HighlightsSection.ResultText);
    }

    // ── Playback ──────────────────────────────────────────────────

    [Fact]
    public async Task Playback_Requires_Device()
    {
        var (shell, _) = Create();

        await shell.PlaybackSection.FindFileCommand.ExecuteAsync(null);

        Assert.Contains("Select a camera", shell.PlaybackSection.ResultText);
    }

    [Fact]
    public async Task Playback_FindFile_Runs_And_Reports()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.PlaybackResult = new NvrPlaybackCallResult { Success = true, Operation = "find-file", Message = "1 file" };

        await shell.PlaybackSection.FindFileCommand.ExecuteAsync(null);

        Assert.Contains("find-file", shell.PlaybackSection.ResultText);
        shell.Dispose();
    }

    // ── Diagnostics ───────────────────────────────────────────────

    [Fact]
    public async Task Diagnostics_Refresh_Counts()
    {
        var (shell, api) = Create();
        api.AuditResult = [new WriteAuditEntry { Operation = "write", Success = true }];
        api.SessionsResult = [new ProbeSession { Status = ProbeSessionStatus.Completed }];

        await shell.DiagnosticsSection.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("1 audit", shell.DiagnosticsSection.ResultText);
        Assert.Contains("1 session", shell.DiagnosticsSection.ResultText);
    }

    // ── Connectivity ──────────────────────────────────────────────

    [Fact]
    public async Task Connectivity_Refresh_Loads_Snapshots()
    {
        var (shell, api) = Create();
        api.ConnectivityResult = [new DeviceConnectivitySnapshot { DeviceId = Guid.NewGuid(), Status = ConnectivityStatus.Healthy }];

        await shell.ConnectivitySection.RefreshCommand.ExecuteAsync(null);

        Assert.Single(shell.ConnectivitySection.Snapshots);
    }

    [Fact]
    public async Task Connectivity_Diagnose_Uses_Selected_Device()
    {
        var (shell, api) = Create();
        shell.SelectedDevice = Device();
        api.ConnectivityActionResult = System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true });

        await shell.ConnectivitySection.DiagnoseCommand.ExecuteAsync(null);

        Assert.Contains("Diagnose", shell.ConnectivitySection.ResultText);
        shell.Dispose();
    }

    // ── Dashboard ─────────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_Refresh_Summarizes()
    {
        var (shell, api) = Create();
        api.ConnectivityResult = [new DeviceConnectivitySnapshot { Status = ConnectivityStatus.Healthy }];
        api.StoragePathsResult = new MediaStoragePaths { ContinuousRecordings = "/rec" };

        await shell.DashboardSection.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("Continuous", shell.DashboardSection.StorageSummary);
        Assert.NotNull(shell.DashboardSection.LastRefreshed);
    }

    // ── Fullscreen menu sheet (per-tile content) ─────────────────

    [Fact]
    public void Fullscreen_MenuContent_Opens_The_Tiles_Own_Panel()
    {
        var (shell, api) = Create();
        var device = Device();
        var vm = new FullscreenCameraViewModel(api, device, lanToken: null, shell: shell);

        // Network → the shell's real connectivity section (never Features).
        vm.ActiveMenu = "Network";
        Assert.Same(shell.ConnectivitySection, vm.MenuContent);

        // Record → the shell's recordings section.
        vm.ActiveMenu = "Record";
        Assert.Same(shell.RecordingsSection, vm.MenuContent);

        // Advanced → diagnostics; Firmware → firmware.
        vm.ActiveMenu = "Advanced";
        Assert.Same(shell.DiagnosticsSection, vm.MenuContent);
        vm.ActiveMenu = "Firmware";
        Assert.Same(shell.FirmwareSection, vm.MenuContent);

        // Features / Settings → the typed control-point surface.
        vm.ActiveMenu = "Features";
        Assert.Same(vm.Features, vm.MenuContent);
        vm.ActiveMenu = "Settings";
        Assert.Same(vm.Features, vm.MenuContent);

        // Display / Audio / Hotspot / Recovery → dedicated panels off the VM itself,
        // never the Features surface (this was the "every tile opens Features" bug).
        vm.ActiveMenu = "Display";
        Assert.Same(vm, vm.MenuContent);
        Assert.True(vm.IsDisplayMenu);
        vm.ActiveMenu = "Audio";
        Assert.Same(vm, vm.MenuContent);
        Assert.True(vm.IsAudioMenu);
        vm.ActiveMenu = "Hotspot";
        Assert.Same(vm, vm.MenuContent);
        Assert.True(vm.IsHotspotMenu);
        vm.ActiveMenu = "Recovery";
        Assert.Same(vm, vm.MenuContent);
        Assert.True(vm.IsRecoveryMenu);

        vm.Dispose();
        shell.Dispose();
    }

    [Fact]
    public void Fullscreen_Quality_Switches_Manifest_Quality_And_Restarts()
    {
        var (shell, api) = Create();
        var vm = new FullscreenCameraViewModel(api, Device(), lanToken: null, shell: shell);

        // HD main is the always-preferred default.
        Assert.Equal("main", vm.Quality);
        Assert.True(vm.IsMainQuality);
        Assert.False(vm.IsSubQuality);

        // Switching to sub updates the manifest-requested quality.
        vm.SelectQualityCommand.Execute("sub");
        Assert.Equal("sub", vm.Quality);
        Assert.True(vm.IsSubQuality);

        vm.Dispose();
        shell.Dispose();
    }

    // ── Navigation shell ──────────────────────────────────────────

    [Fact]
    public void Shell_Hosts_All_Sections()
    {
        var (shell, _) = Create();

        Assert.Equal(11, shell.Sections.Count);
        Assert.Equal("Live View", shell.Title);
        Assert.Same(shell, shell.Sections[0]);
    }

    [Fact]
    public async Task Selecting_Device_Syncs_Features()
    {
        var (shell, api) = Create();
        api.ControlPointsResult = ReportWith(ControlPointWidgetKind.Toggle);

        // The shell fires DeviceChangedAsync on selection; await the public path
        // directly so the test is deterministic (no Task.Delay races).
        shell.SelectedDevice = Device();
        await shell.FeaturesSection.DeviceChangedAsync();

        Assert.True(shell.FeaturesSection.HasDevice);
        Assert.Single(shell.FeaturesSection.Controls);
        shell.Dispose();
    }
}
