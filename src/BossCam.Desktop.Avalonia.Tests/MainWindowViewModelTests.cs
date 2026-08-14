using BossCam.Contracts;
using BossCam.Desktop.Avalonia.ViewModels;
using System.Text.Json;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="MainWindowViewModel"/>.
/// Uses <see cref="TestBossCamApiClient"/> so no live server needed.
///
/// Run with:
///   dotnet test src/BossCam.Desktop.Avalonia.Tests/BossCam.Desktop.Avalonia.Tests.csproj
///
/// Note: requires Avalonia NuGet packages to be restored first.
/// See scripts/restore-avalonia-packages.sh if restore times out.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private static MainWindowViewModel CreateVm(
        TestBossCamApiClient? api = null,
        FakeServiceStarter? starter = null)
    {
        api ??= new TestBossCamApiClient();
        starter ??= new FakeServiceStarter();
        return new MainWindowViewModel(api, serviceStarter: starter);
    }

    // ── Initial state ────────────────────────────────────────────

    [Fact]
    public void Constructor_Sets_Default_StatusText()
    {
        var vm = CreateVm();
        Assert.Contains("Connecting to BossCamService", vm.StatusText);
        Assert.Empty(vm.Devices);
        Assert.Null(vm.SelectedDevice);
        Assert.False(vm.IsLive);
        Assert.Null(vm.LiveFrame);
        Assert.Empty(vm.DeviceInfoText);
    }

    // ── Persistent connection indicator ───────────────────────────

    [Fact]
    public void Constructor_ConnectionIndicator_Defaults_To_Starting()
    {
        var vm = CreateVm();
        Assert.Equal(ServiceConnectionStatus.Starting, vm.ConnectionStatus);
        Assert.Equal("Starting\u2026", vm.ConnectionStatusText);
    }

    [Fact]
    public async Task InitializeAsync_Healthy_Sets_Connection_Online()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = []
        };
        var vm = CreateVm(api);

        await vm.InitializeAsync();

        Assert.Equal(ServiceConnectionStatus.Online, vm.ConnectionStatus);
        Assert.Equal("Service online", vm.ConnectionStatusText);
    }

    [Fact]
    public async Task InitializeAsync_Offline_Starter_Fails_Sets_Connection_Offline()
    {
        var api = new TestBossCamApiClient();
        var starter = new FakeServiceStarter { StartResult = false };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();

        Assert.Equal(ServiceConnectionStatus.Offline, vm.ConnectionStatus);
        Assert.Equal("Service offline", vm.ConnectionStatusText);
    }

    [Fact]
    public async Task InitializeAsync_Offline_Starter_Starts_Sets_Connection_Online()
    {
        var api = new TestBossCamApiClient
        {
            DevicesResult = []
        };
        var starter = new FakeServiceStarter { StartResult = true };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();

        Assert.Equal(ServiceConnectionStatus.Online, vm.ConnectionStatus);
        Assert.Equal("Service online", vm.ConnectionStatusText);
    }

    [Fact]
    public async Task RetryConnection_After_Service_Comes_Back_Reconnects()
    {
        var api = new TestBossCamApiClient(); // health null => offline first
        var starter = new FakeServiceStarter { StartResult = false };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();
        Assert.Equal(ServiceConnectionStatus.Offline, vm.ConnectionStatus);

        // Service comes back; Retry should now connect without needing the starter.
        api.HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}""");
        api.DevicesResult = [new() { Id = Guid.NewGuid(), Name = "Cam1" }];

        await vm.RetryConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ServiceConnectionStatus.Online, vm.ConnectionStatus);
        Assert.Equal("Service online", vm.ConnectionStatusText);
        Assert.Contains("Connected to BossCamService", vm.StatusText);
        Assert.Single(vm.Devices);
    }

    [Fact]
    public async Task RetryConnection_Starts_Service_When_Still_Down()
    {
        var api = new TestBossCamApiClient();
        var starter = new FakeServiceStarter { StartResult = false };
        var vm = CreateVm(api, starter);

        // First handshake: service down and the starter cannot start it -> offline.
        await vm.InitializeAsync();
        Assert.Equal(ServiceConnectionStatus.Offline, vm.ConnectionStatus);

        // Still offline, but the starter can now bring it up on Retry.
        starter.StartResult = true;
        api.DevicesResult = [];

        await vm.RetryConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ServiceConnectionStatus.Online, vm.ConnectionStatus);
        Assert.Equal(2, starter.StartCallCount);
    }

    [Fact]
    public async Task PollHealth_Reflects_Service_Going_Offline_Then_Online()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = []
        };
        var vm = CreateVm(api);
        await vm.InitializeAsync();
        Assert.Equal(ServiceConnectionStatus.Online, vm.ConnectionStatus);

        // Service dies -> the periodic poll flips the indicator red.
        api.HealthResult = null;
        await vm.PollHealthAsync();
        Assert.Equal(ServiceConnectionStatus.Offline, vm.ConnectionStatus);

        // Service recovers -> the poll flips it back green without a Retry.
        api.HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}""");
        await vm.PollHealthAsync();
        Assert.Equal(ServiceConnectionStatus.Online, vm.ConnectionStatus);
    }

    [Fact]
    public async Task PollHealth_Throwing_Probe_Marks_Offline()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = []
        };
        var vm = CreateVm(api);
        await vm.InitializeAsync();

        api.ThrowOnHealth = true;
        await vm.PollHealthAsync();

        Assert.Equal(ServiceConnectionStatus.Offline, vm.ConnectionStatus);
    }

    [Fact]
    public async Task PollHealth_Does_Not_Clobber_InFlight_Handshake_State()
    {
        var api = new TestBossCamApiClient();
        // The handshake suspends on this pending start task, keeping it genuinely
        // in-flight (the plain StartResult path would complete synchronously).
        var pendingStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starter = new FakeServiceStarter { PendingTask = pendingStart.Task };
        var vm = CreateVm(api, starter);

        var handshake = vm.InitializeAsync();
        // The handshake has set Starting and is now awaiting the pending start task.
        Assert.Equal(ServiceConnectionStatus.Starting, vm.ConnectionStatus);

        // A poll racing the handshake must not overwrite the amber Starting state.
        await vm.PollHealthAsync();
        Assert.Equal(ServiceConnectionStatus.Starting, vm.ConnectionStatus);

        pendingStart.SetResult(false);
        await handshake;
        Assert.Equal(ServiceConnectionStatus.Offline, vm.ConnectionStatus);
    }

    // ── Startup handshake (InitializeAsync) ───────────────────────

    [Fact]
    public async Task InitializeAsync_Healthy_Loads_Devices_And_Shows_Connected()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult =
            [
                new() { Id = Guid.NewGuid(), Name = "Cam1" },
                new() { Id = Guid.NewGuid(), Name = "Cam2" }
            ]
        };
        var vm = CreateVm(api);

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Devices.Count);
        Assert.Contains("Connected to BossCamService", vm.StatusText);
        Assert.Contains("2", vm.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_Unhealthy_Health_Shows_Offline()
    {
        // HealthResult is null by default => treated as unreachable. The fake
        // starter fails, so the handshake must surface a clear offline state.
        var api = new TestBossCamApiClient();
        var starter = new FakeServiceStarter { StartResult = false };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();

        Assert.Equal(1, starter.StartCallCount);
        Assert.Empty(vm.Devices);
        Assert.Contains("BossCamService offline", vm.StatusText);
        Assert.Contains("could not be started automatically", vm.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_Health_Throws_Shows_Offline()
    {
        var api = new TestBossCamApiClient { ThrowOnHealth = true };
        var vm = CreateVm(api);

        await vm.InitializeAsync();

        Assert.Empty(vm.Devices);
        Assert.Contains("BossCamService offline", vm.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_Offline_Starts_Service_Then_Loads_Devices()
    {
        var api = new TestBossCamApiClient
        {
            DevicesResult =
            [
                new() { Id = Guid.NewGuid(), Name = "Cam1" },
                new() { Id = Guid.NewGuid(), Name = "Cam2" }
            ]
        };
        var starter = new FakeServiceStarter { StartResult = true };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();

        Assert.Equal(1, starter.StartCallCount);
        Assert.Equal(2, vm.Devices.Count);
        Assert.Contains("Connected to BossCamService", vm.StatusText);
        Assert.Contains("2", vm.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_Healthy_Does_Not_Attempt_Service_Start()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = []
        };
        var starter = new FakeServiceStarter { StartResult = true };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();

        Assert.Equal(0, starter.StartCallCount);
        Assert.Contains("Connected to BossCamService", vm.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_Health_Ok_But_Devices_Fail_Shows_Failed_To_Load()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            // DevicesResult null => GetDevicesAsync throws inside LoadDevicesAsync.
        };
        var vm = CreateVm(api);

        await vm.InitializeAsync();

        Assert.Empty(vm.Devices);
        Assert.Contains("Failed to load", vm.StatusText);
    }

    [Fact]
    public void IsHealthy_Returns_True_Only_For_Status_Ok()
    {
        Assert.True(MainWindowViewModel.IsHealthy(
            JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}""")));
        Assert.False(MainWindowViewModel.IsHealthy(
            JsonSerializer.Deserialize<JsonElement>("""{"status":"degraded"}""")));
        Assert.False(MainWindowViewModel.IsHealthy(null));
    }


    [Fact]
    public void Constructor_Sets_Empty_Devices_Collection()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.Devices);
        Assert.Empty(vm.Devices);
    }

    // ── Device selection ─────────────────────────────────────────

    [Fact]
    public void SelectDevice_Updates_SelectedDevice()
    {
        var vm = CreateVm();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "TestCam",
            IpAddress = "192.168.1.100",
            HardwareModel = "5523-W",
            FirmwareVersion = "V4.00.R02",
            DeviceType = "IPC",
            Port = 80
        };

        vm.SelectedDevice = device;

        Assert.Same(device, vm.SelectedDevice);
        Assert.True(vm.IsLive);
    }

    // ── Device info formatting ───────────────────────────────────

    [Fact]
    public void DeviceInfoText_Formats_Correctly()
    {
        var vm = CreateVm();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "Front Door",
            IpAddress = "10.0.0.50",
            HardwareModel = "IPC-5523W",
            FirmwareVersion = "V4.30.R01",
            DeviceType = "IPC",
            Port = 80,
            LoginName = "admin"
        };

        vm.SelectedDevice = device;

        Assert.Contains("IPC-5523W", vm.DeviceInfoText);
        Assert.Contains("10.0.0.50", vm.DeviceInfoText);
        Assert.Contains("V4.30.R01", vm.DeviceInfoText);
        Assert.Contains("IPC", vm.DeviceInfoText);
    }

    [Fact]
    public void DeviceInfoText_Handles_Null_Fields()
    {
        var vm = CreateVm();
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            Name = "NullCam",
            IpAddress = null,
            HardwareModel = null,
            FirmwareVersion = null,
            DeviceType = null
        };

        vm.SelectedDevice = device;

        Assert.Contains("\u2014", vm.DeviceInfoText);
    }

    // ── Status text updates ─────────────────────────────────────

    [Fact]
    public void StatusText_Can_Be_Updated()
    {
        var vm = CreateVm();
        vm.StatusText = "Custom status update";
        Assert.Equal("Custom status update", vm.StatusText);
    }

    // ── Property change notifications ─────────────────────────────

    [Fact]
    public void Setting_SelectedDevice_Raises_PropertyChanged()
    {
        var vm = CreateVm();
        var propertyNames = new List<string?>();
        vm.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        vm.SelectedDevice = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Test" };

        Assert.Contains(nameof(MainWindowViewModel.SelectedDevice), propertyNames);
        Assert.Contains(nameof(MainWindowViewModel.IsLive), propertyNames);
    }

    // ── API integration ──────────────────────────────────────────

    [Fact]
    public async Task LoadDevicesAsync_Populates_Devices_From_Api()
    {
        var api = new TestBossCamApiClient
        {
            DevicesResult =
            [
                new() { Id = Guid.NewGuid(), Name = "Cam1" },
                new() { Id = Guid.NewGuid(), Name = "Cam2" }
            ]
        };
        var vm = CreateVm(api);

        await vm.LoadDevicesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Devices.Count);
        Assert.Contains("Loaded 2", vm.StatusText);
    }

    [Fact]
    public async Task LoadDevicesAsync_Handles_Api_Error()
    {
        // DevicesResult stays null => TestBossCamApiClient throws
        var vm = CreateVm(new TestBossCamApiClient());

        await vm.LoadDevicesCommand.ExecuteAsync(null);

        Assert.Empty(vm.Devices);
        Assert.Contains("Failed to load", vm.StatusText);
    }

    [Fact]
    public async Task LoadDevicesAsync_Empty_Result_Is_Handled()
    {
        var api = new TestBossCamApiClient { DevicesResult = [] };
        var vm = CreateVm(api);

        await vm.LoadDevicesCommand.ExecuteAsync(null);

        Assert.Empty(vm.Devices);
        Assert.Contains("Loaded 0", vm.StatusText);
    }

    [Fact]
    public async Task SnapshotAsync_Calls_SaveEndpoint()
    {
        var api = new TestBossCamApiClient { SaveSnapshotResult = true };
        var vm = CreateVm(api);
        vm.SelectedDevice = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Test" };

        await vm.SnapshotCommand.ExecuteAsync(null);

        Assert.Equal(1, api.SaveSnapshotCallCount);
        Assert.Equal("Snapshot saved", vm.StatusText);
    }

    [Fact]
    public async Task SnapshotAsync_Handles_Failure()
    {
        var api = new TestBossCamApiClient { SaveSnapshotResult = false };
        var vm = CreateVm(api);
        vm.SelectedDevice = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Test" };

        await vm.SnapshotCommand.ExecuteAsync(null);

        Assert.Equal("Snapshot failed", vm.StatusText);
    }

    // ── Edge cases ───────────────────────────────────────────────

    [Fact]
    public void SelectDevice_Null_Does_Not_Throw()
    {
        var vm = CreateVm();
        var exception = Record.Exception(() => vm.SelectedDevice = null);
        Assert.Null(exception);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public void Devices_Can_Be_Cleared_And_Refilled()
    {
        var vm = CreateVm();
        vm.Devices = new System.Collections.ObjectModel.ObservableCollection<DeviceIdentity>
        {
            new() { Id = Guid.NewGuid(), Name = "Cam1" },
            new() { Id = Guid.NewGuid(), Name = "Cam2" }
        };
        Assert.Equal(2, vm.Devices.Count);

        vm.Devices.Clear();
        Assert.Empty(vm.Devices);
    }

    // ── Constructor edge cases ───────────────────────────────────

    [Fact]
    public void Constructor_Null_Api_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MainWindowViewModel(null!));
    }

    // ── Live playback command construction ───────────────────────

    [Fact]
    public void DesktopPlayback_Prefers_Shared_H264_Fmp4_Manifest_Output()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.H264Fmp4,
            FallbackModes = [LiveMediaModeContract.H264Fmp4, LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4",
            MpegTsUrl = "http://127.0.0.1:5317/api/devices/device/live.ts"
        };

        Assert.Equal(manifest.H264Fmp4Url, MainWindowViewModel.SelectDesktopStreamUrl(manifest));
    }

    [Fact]
    public void DesktopPlayback_Includes_Preferred_Mode_Before_Stale_Fallback_List()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.H264Fmp4,
            // Older services could omit PreferredMode from FallbackModes.
            FallbackModes = [LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4",
            MjpegUrl = "http://127.0.0.1:5317/api/devices/device/live.mjpeg",
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        Assert.Equal(
            [manifest.H264Fmp4Url, manifest.MjpegUrl, manifest.SnapshotUrl],
            MainWindowViewModel.SelectDesktopStreamUrls(manifest));
    }

    [Fact]
    public void DesktopPlayback_Uses_Mjpeg_When_Backend_Negotiates_Mjpeg_First()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.Mjpeg,
            FallbackModes = [LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4",
            MjpegUrl = "http://127.0.0.1:5317/api/devices/device/live.mjpeg",
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        Assert.Equal(manifest.MjpegUrl, MainWindowViewModel.SelectDesktopStreamUrl(manifest));
    }

    [Fact]
    public void DesktopPlayback_Uses_Snapshot_When_Only_Snapshot_Is_Negotiated()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.Snapshot,
            FallbackModes = [LiveMediaModeContract.Snapshot],
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        Assert.Equal(manifest.SnapshotUrl, MainWindowViewModel.SelectDesktopStreamUrl(manifest));
    }

    [Fact]
    public void DesktopPlayback_Exposes_Ordered_Fallback_Urls()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.H264Fmp4,
            FallbackModes = [LiveMediaModeContract.H264Fmp4, LiveMediaModeContract.H264MpegTs, LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            H264Fmp4Url = "http://127.0.0.1:5317/live.h264.mp4",
            MpegTsUrl = "http://127.0.0.1:5317/live.ts",
            MjpegUrl = "http://127.0.0.1:5317/live.mjpeg",
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/snapshot"
        };

        Assert.Equal(
            [manifest.H264Fmp4Url, manifest.MpegTsUrl, manifest.MjpegUrl, manifest.SnapshotUrl],
            MainWindowViewModel.SelectDesktopStreamUrls(manifest));
    }

    [Fact]
    public void DesktopPlayback_Advances_To_Next_Fallback_After_Stream_Failure()
    {
        Assert.Equal(1, MainWindowViewModel.NextDesktopStreamIndex(0, 3));
        Assert.Equal(2, MainWindowViewModel.NextDesktopStreamIndex(1, 3));
        Assert.Equal(0, MainWindowViewModel.NextDesktopStreamIndex(2, 3));
        Assert.Equal(0, MainWindowViewModel.NextDesktopStreamIndex(0, 0));
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Add_Lan_Token_Without_Embedding_It_In_The_Url()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.ts",
            "secret-token");

        Assert.Contains("-headers", args);
        var headerIndex = args.ToList().IndexOf("-headers");
        Assert.Equal("X-LAN-Token: secret-token\r\n", args[headerIndex + 1]);
        Assert.DoesNotContain("secret-token", args[args.ToList().IndexOf("-i") + 1]);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Omit_Empty_Lan_Token()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.ts",
            null);

        Assert.DoesNotContain("-headers", args);
    }

    [Fact]
    public void DesktopLanToken_Uses_Environment_Then_Configured_Fallback()
    {
        Assert.Equal("from-env", App.ResolveConfiguredLanToken("from-env", "from-config"));
        Assert.Equal("from-config", App.ResolveConfiguredLanToken(null, "from-config"));
        Assert.Null(App.ResolveConfiguredLanToken("   ", " "));
    }

    // ── Starred landing board (server-synced, mirrors the SPA) ───

    [Fact]
    public async Task InitializeAsync_Loads_Starred_Ids_From_Server()
    {
        var starredId = Guid.NewGuid();
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = [new() { Id = starredId, Name = "Cam1" }],
            StarredIds = [starredId]
        };
        var vm = CreateVm(api);

        await vm.InitializeAsync();

        Assert.True(vm.IsStarred(starredId));
        Assert.Equal(1, vm.StarredCount);
        // Starred-only landing: the board shows the starred camera.
        Assert.Single(vm.BoardTiles);
        Assert.Equal(starredId, vm.BoardTiles[0].Device.Id);
        // Auto-load: the starred camera becomes the selected/streaming camera.
        Assert.NotNull(vm.SelectedDevice);
        Assert.Equal(starredId, vm.SelectedDevice!.Id);
    }

    [Fact]
    public async Task StarredLoad_Failure_Keeps_Local_State()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = [new() { Id = Guid.NewGuid(), Name = "Cam1" }],
            ThrowOnStars = true
        };
        var vm = CreateVm(api);

        await vm.InitializeAsync();

        // Offline-tolerant: no stars loaded, board falls back to all cameras.
        Assert.Equal(0, vm.StarredCount);
        Assert.Single(vm.BoardTiles);
    }

    [Fact]
    public async Task ToggleStar_Persists_Server_Side()
    {
        var device = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Cam1" };
        var api = new TestBossCamApiClient { DevicesResult = [device] };
        var vm = CreateVm(api);

        await vm.ToggleStarCommand.ExecuteAsync(device);

        Assert.True(vm.IsStarred(device.Id));
        Assert.Equal(1, api.SetStarCallCount);
        Assert.Equal(device.Id, api.LastStarredDeviceId);
        Assert.True(api.LastStarredValue);
        Assert.Contains("pinned", vm.StatusText);
    }

    [Fact]
    public async Task ToggleStar_Unstars_And_Persists_Server_Side()
    {
        var device = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Cam1" };
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = [device],
            StarredIds = [device.Id]
        };
        var vm = CreateVm(api);
        await vm.InitializeAsync();

        await vm.ToggleStarCommand.ExecuteAsync(device);

        Assert.False(vm.IsStarred(device.Id));
        Assert.Equal(1, api.SetStarCallCount);
        Assert.False(api.LastStarredValue);
        Assert.Contains("unpinned", vm.StatusText);
    }

    [Fact]
    public async Task ToggleStar_Failed_Save_Keeps_Local_Pin()
    {
        var device = new DeviceIdentity { Id = Guid.NewGuid(), Name = "Cam1" };
        var api = new TestBossCamApiClient { DevicesResult = [device], ThrowOnStars = true };
        var vm = CreateVm(api);

        await vm.ToggleStarCommand.ExecuteAsync(device);

        // Offline-tolerant (mirrors the SPA): the optimistic local pin survives.
        Assert.True(vm.IsStarred(device.Id));
        Assert.Contains("offline", vm.StatusText);
    }

    [Fact]
    public async Task ToggleStarredOnly_Filters_Board()
    {
        var starredId = Guid.NewGuid();
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult =
            [
                new() { Id = starredId, Name = "Starred" },
                new() { Id = Guid.NewGuid(), Name = "Other" }
            ],
            StarredIds = [starredId]
        };
        var vm = CreateVm(api);
        await vm.InitializeAsync();

        // Default: starred-only board.
        Assert.Single(vm.BoardTiles);
        Assert.Equal("⭐ Starred (1)", vm.StarredFilterText);

        vm.ToggleStarredOnlyCommand.Execute(null);

        // All-cameras board.
        Assert.Equal(2, vm.BoardTiles.Count);
        Assert.Equal("All cameras", vm.StarredFilterText);
    }

    // ── HD-main default (mirrors the SPA's stream-quality default) ─

    [Fact]
    public async Task LivePlayback_Requests_Main_Quality()
    {
        var api = new TestBossCamApiClient
        {
            HealthResult = JsonSerializer.Deserialize<JsonElement>("""{"status":"ok"}"""),
            DevicesResult = [new() { Id = Guid.NewGuid(), Name = "Cam1" }]
        };
        var vm = CreateVm(api);
        await vm.InitializeAsync();

        // SelectedDevice is auto-selected at startup → the live loop requests main.
        Assert.NotNull(vm.SelectedDevice);
        Assert.Equal("main", api.LastManifestQuality);
    }

    // ── Dispose ──────────────────────────────────────────────────

    [Fact]
    public void Dispose_Does_Not_Throw()
    {
        var vm = CreateVm();
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_With_TestApi_Does_Not_Throw()
    {
        var api = new TestBossCamApiClient();
        var vm = new MainWindowViewModel(api);
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
        // api should not throw on double-dispose either
        var ex2 = Record.Exception(() => api.Dispose());
        Assert.Null(ex2);
    }
}
