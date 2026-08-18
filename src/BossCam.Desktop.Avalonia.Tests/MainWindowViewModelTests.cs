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
        Assert.Equal("Not started", vm.LivePlaybackStatus);
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
    public async Task InitializeAsync_Health_Throws_Attempts_Service_Start_Then_Shows_Offline()
    {
        var api = new TestBossCamApiClient { ThrowOnHealth = true };
        var starter = new FakeServiceStarter { StartResult = false };
        var vm = CreateVm(api, starter);

        await vm.InitializeAsync();

        Assert.Equal(1, starter.StartCallCount);
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
    public async Task ReadExact_Uses_One_Deadline_For_The_Whole_Frame()
    {
        await using var stream = new DelayedByteStream(
            [1, 2],
            TimeSpan.FromMilliseconds(80));
        var buffer = new byte[2];

        var completed = await MainWindowViewModel.ReadExactAsync(
            stream,
            buffer,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(40));

        Assert.False(completed);
    }

    [Fact]
    public void HardwareDecoder_Failure_Retries_Same_Source_In_Software()
    {
        Assert.True(MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
            hardwareAttempt: true,
            processExited: true,
            renderedFrame: false,
            attemptDuration: TimeSpan.FromSeconds(1)));
        Assert.False(MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
            hardwareAttempt: false,
            processExited: true,
            renderedFrame: false,
            attemptDuration: TimeSpan.FromSeconds(1)));
        Assert.False(MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
            hardwareAttempt: true,
            processExited: false,
            renderedFrame: false,
            attemptDuration: TimeSpan.FromSeconds(1)));
        Assert.False(MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
            hardwareAttempt: true,
            processExited: true,
            renderedFrame: false,
            attemptDuration: TimeSpan.FromSeconds(5)));
        Assert.False(MainWindowViewModel.ShouldRetryWithSoftwareDecoder(
            hardwareAttempt: true,
            processExited: true,
            renderedFrame: true,
            attemptDuration: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LiveManifestRetry_Stops_After_Three_Attempts_Or_Dispose()
    {
        Assert.True(MainWindowViewModel.ShouldRetryLiveManifest(attempt: 1, disposed: false));
        Assert.True(MainWindowViewModel.ShouldRetryLiveManifest(attempt: 2, disposed: false));
        Assert.False(MainWindowViewModel.ShouldRetryLiveManifest(attempt: 3, disposed: false));
        Assert.False(MainWindowViewModel.ShouldRetryLiveManifest(attempt: 1, disposed: true));
    }

    [Fact]
    public void ReconnectDelay_Uses_Capped_Exponential_Backoff()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), MainWindowViewModel.GetReconnectDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), MainWindowViewModel.GetReconnectDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), MainWindowViewModel.GetReconnectDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(5000), MainWindowViewModel.GetReconnectDelay(20));
        Assert.Equal(TimeSpan.FromMilliseconds(250), MainWindowViewModel.GetReconnectDelay(-1));
    }

    [Fact]
    public void FirstFrameWatchdog_Reports_Only_An_Active_NoFrame_Stall()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-6);
        Assert.True(MainWindowViewModel.ShouldReportNoFirstFrame(
            processActive: true,
            renderedFrame: false,
            startedAt: started,
            now: DateTimeOffset.UtcNow,
            timeout: TimeSpan.FromSeconds(5)));
        Assert.False(MainWindowViewModel.ShouldReportNoFirstFrame(
            processActive: false,
            renderedFrame: false,
            startedAt: started,
            now: DateTimeOffset.UtcNow,
            timeout: TimeSpan.FromSeconds(5)));
        Assert.False(MainWindowViewModel.ShouldReportNoFirstFrame(
            processActive: true,
            renderedFrame: true,
            startedAt: started,
            now: DateTimeOffset.UtcNow,
            timeout: TimeSpan.FromSeconds(5)));
    }

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
    public void CopyBgraFrameToFramebuffer_Respects_Padded_Destination_Stride()
    {
        const int width = 2;
        const int height = 2;
        const int sourceRowBytes = width * 4;
        const int destinationRowBytes = 12;
        var frame = new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8,
            9, 10, 11, 12, 13, 14, 15, 16
        };
        var destination = System.Runtime.InteropServices.Marshal.AllocHGlobal(destinationRowBytes * height);
        try
        {
            for (var i = 0; i < destinationRowBytes * height; i++)
            {
                System.Runtime.InteropServices.Marshal.WriteByte(destination, i, 0xCC);
            }

            MainWindowViewModel.CopyBgraFrameToFramebuffer(
                frame, destination, destinationRowBytes, width, height);

            var copied = new byte[destinationRowBytes * height];
            System.Runtime.InteropServices.Marshal.Copy(destination, copied, 0, copied.Length);
            Assert.Equal(frame[..sourceRowBytes], copied[..sourceRowBytes]);
            Assert.Equal(frame[sourceRowBytes..], copied[destinationRowBytes..(destinationRowBytes + sourceRowBytes)]);
            Assert.All(copied[sourceRowBytes..destinationRowBytes], b => Assert.Equal(0xCC, b));
            Assert.All(copied[(destinationRowBytes + sourceRowBytes)..], b => Assert.Equal(0xCC, b));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void Decoder_Drops_Frame_When_No_Render_Slot_Is_Free()
    {
        Assert.False(MainWindowViewModel.ShouldDiscardDecodedFrame(hasFreeRenderSlot: true));
        Assert.True(MainWindowViewModel.ShouldDiscardDecodedFrame(hasFreeRenderSlot: false));
    }

    [Fact]
    public void Queued_Frame_From_Older_Decode_Generation_Is_Not_Presented()
    {
        Assert.True(MainWindowViewModel.ShouldRenderFrame(
            disposed: false,
            frameGeneration: 7,
            currentGeneration: 7));
        Assert.False(MainWindowViewModel.ShouldRenderFrame(
            disposed: false,
            frameGeneration: 6,
            currentGeneration: 7));
        Assert.False(MainWindowViewModel.ShouldRenderFrame(
            disposed: true,
            frameGeneration: 7,
            currentGeneration: 7));
    }

    [Fact]
    public void Fullscreen_Queued_Frame_From_Older_Decode_Generation_Is_Not_Presented()
    {
        // Fullscreen uses the same shared generation predicate as board tiles.
        Assert.True(MainWindowViewModel.ShouldRenderFrame(
            disposed: false,
            frameGeneration: 3,
            currentGeneration: 3));
        Assert.False(MainWindowViewModel.ShouldRenderFrame(
            disposed: false,
            frameGeneration: 2,
            currentGeneration: 3));
        Assert.False(MainWindowViewModel.ShouldRenderFrame(
            disposed: true,
            frameGeneration: 3,
            currentGeneration: 3));
    }


    private sealed class DelayedByteStream(byte[] bytes, TimeSpan delay) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            // First byte arrives promptly; the second is deliberately delayed. The old
            // per-read timeout reset after byte one, while the fixed whole-frame deadline
            // rejects the incomplete frame.
            if (_position > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }
            if (_position >= bytes.Length)
            {
                return 0;
            }

            destination.Span[0] = bytes[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    [Fact]
    public void Snapshot_Fallback_Is_Rejected_When_A_New_Video_Generation_Started()
    {
        Assert.True(BoardTileViewModel.ShouldApplySnapshot(
            videoProcessActive: false,
            snapshotGeneration: 4,
            currentVideoGeneration: 4));
        Assert.False(BoardTileViewModel.ShouldApplySnapshot(
            videoProcessActive: false,
            snapshotGeneration: 4,
            currentVideoGeneration: 5));
        Assert.False(BoardTileViewModel.ShouldApplySnapshot(
            videoProcessActive: true,
            snapshotGeneration: 4,
            currentVideoGeneration: 4));
    }

    [Fact]
    public void Snapshot_Fallback_Activates_When_Active_Decoder_Has_Stalled()
    {
        var now = TimeSpan.FromSeconds(6).Ticks;
        var started = TimeSpan.FromSeconds(1).Ticks;
        var lastFrame = TimeSpan.FromSeconds(2).Ticks;

        Assert.False(BoardTileViewModel.ShouldUseSnapshotFallback(
            videoProcessActive: true,
            lastFrameUtcTicks: lastFrame,
            videoStartedUtcTicks: started,
            nowUtcTicks: now,
            maxFrameAge: TimeSpan.FromSeconds(5)));
        Assert.True(BoardTileViewModel.ShouldUseSnapshotFallback(
            videoProcessActive: true,
            lastFrameUtcTicks: lastFrame,
            videoStartedUtcTicks: started,
            nowUtcTicks: now + TimeSpan.FromSeconds(2).Ticks,
            maxFrameAge: TimeSpan.FromSeconds(5)));
        Assert.True(BoardTileViewModel.ShouldUseSnapshotFallback(
            videoProcessActive: true,
            lastFrameUtcTicks: 0,
            videoStartedUtcTicks: started,
            nowUtcTicks: now + TimeSpan.FromSeconds(1).Ticks,
            maxFrameAge: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Snapshot_Fallback_Does_Not_Replace_Fresh_Live_Video()
    {
        var now = TimeSpan.FromSeconds(10).Ticks;
        Assert.False(BoardTileViewModel.ShouldUseSnapshotFallback(
            videoProcessActive: true,
            lastFrameUtcTicks: TimeSpan.FromSeconds(8).Ticks,
            videoStartedUtcTicks: TimeSpan.FromSeconds(1).Ticks,
            nowUtcTicks: now,
            maxFrameAge: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DesktopPlayback_Tolerates_Manifest_With_Null_FallbackModes()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.H264Fmp4,
            FallbackModes = null!,
            H264Fmp4Url = "http://127.0.0.1:5317/live.h264.mp4"
        };

        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest);

        Assert.Equal([manifest.H264Fmp4Url], urls);
    }

    [Fact]
    public void DesktopPlayback_Prefers_Direct_Hevc_Copy_For_Native_Manifest()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes =
            [
                LiveMediaModeContract.HevcFmp4,
                LiveMediaModeContract.H264Fmp4,
                LiveMediaModeContract.Mjpeg,
                LiveMediaModeContract.Snapshot
            ],
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4",
            MpegTsUrl = "http://127.0.0.1:5317/api/devices/device/live.ts",
            MjpegUrl = "http://127.0.0.1:5317/api/devices/device/live.mjpeg",
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        // Native desktop: the zero-transcode direct-HEVC URL wins over the H.264 browser
        // transcode. The compatibility fallback loop appends the TS URL because it is set
        // on the manifest; the HEVC copy stays first in the ladder either way.
        Assert.Equal(manifest.HevcFmp4Url, MainWindowViewModel.SelectDesktopStreamUrl(manifest));
        Assert.Equal(
            [manifest.HevcFmp4Url, manifest.H264Fmp4Url],
            MainWindowViewModel.SelectDesktopStreamUrls(manifest));
    }

    [Fact]
    public void DesktopPlayback_Prefers_Shared_Server_Stream_Over_Extra_Direct_Rtsp()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes =
            [
                LiveMediaModeContract.HevcFmp4,
                LiveMediaModeContract.H264Fmp4,
                LiveMediaModeContract.Mjpeg,
                LiveMediaModeContract.Snapshot
            ],
            RtspUrl = "rtsp://admin:p%40ss@10.0.0.169:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4",
            MjpegUrl = "http://127.0.0.1:5317/api/devices/device/live.mjpeg",
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        // The shared native HEVC session owns one RTSP connection per camera and fans out
        // to every desktop viewer. This avoids starving a camera that is already recording.
        Assert.Equal(manifest.HevcFmp4Url, MainWindowViewModel.SelectDesktopStreamUrl(manifest));
        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest);
        Assert.Equal([manifest.HevcFmp4Url, manifest.H264Fmp4Url], urls);
        Assert.DoesNotContain(manifest.RtspUrl, urls);
    }

    [Fact]
    public void Native_BoardTiles_Keep_Shared_Stream_First_For_Freshest_Reliable_Cadence()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4],
            RtspUrl = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4"
        };

        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest, preferDirectRtsp: true);

        // The opt-in flag is retained only for an emergency last resort. Shared service
        // output must remain first so each landing tile does not open another camera socket.
        Assert.Equal(manifest.HevcFmp4Url, urls[0]);
        Assert.Equal([manifest.HevcFmp4Url, manifest.H264Fmp4Url, manifest.RtspUrl], urls);
    }

    [Fact]
    public void BoardTiles_Prefer_Shared_HighQuality_Stream_Then_Direct_Rtsp_Fallback()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4],
            RtspUrl = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4"
        };

        var urls = BoardTileViewModel.SelectBoardStreamUrls(manifest);

        // Landing tiles use the shared native HEVC representation first. It preserves native
        // resolution/cadence while keeping one RTSP session per camera for all viewers.
        Assert.Equal(manifest.HevcFmp4Url, urls[0]);
        Assert.DoesNotContain(manifest.RtspUrl, urls);
    }

    [Fact]
    public void DesktopPlayback_CanPrefer_ServerShared_Hevc_ForBoardTiles()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4],
            RtspUrl = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4"
        };

        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest, preferDirectRtsp: false);

        Assert.Equal(manifest.HevcFmp4Url, urls[0]);
        Assert.DoesNotContain(manifest.RtspUrl, urls);
    }

    [Fact]
    public void DirectRtspFirst_Refresh_Puts_Direct_Rtsp_Before_Shared_Ladder()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4],
            RtspUrl = "rtsp://admin:@10.0.0.29:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4"
        };

        // After the shared-session ladder failed repeatedly, the refresh path re-points
        // the camera's direct RTSP first — the recorder proves these cameras stream
        // direct RTSP reliably, and a stalled service session must not strand the tile.
        var urls = BoardTileViewModel.SelectBoardStreamUrls(manifest, directRtspFirst: true);

        Assert.Equal(manifest.RtspUrl, urls[0]);
        Assert.Contains(manifest.HevcFmp4Url, urls);
        Assert.Contains(manifest.H264Fmp4Url, urls);
    }

    [Fact]
    public void DirectRtspFirst_Is_Opt_In_And_Default_Ladder_Is_Unchanged()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4],
            RtspUrl = "rtsp://admin:@10.0.0.29:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4"
        };

        // The ordinary landing-tile ladder must NOT open a direct camera socket first;
        // direct-RTSP-first is reserved for the post-failure refresh path only.
        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest);

        Assert.Equal(manifest.HevcFmp4Url, urls[0]);
        Assert.DoesNotContain(manifest.RtspUrl, urls);
    }

    [Fact]
    public void BoardTiles_Put_Direct_Rtsp_Last_As_Emergency_Source()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4],
            RtspUrl = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/api/devices/device/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4"
        };

        var urls = BoardTileViewModel.SelectBoardStreamUrls(manifest);

        Assert.Equal(
            [manifest.HevcFmp4Url, manifest.H264Fmp4Url],
            urls);
        Assert.DoesNotContain(manifest.RtspUrl, urls);
    }

    [Fact]
    public void BoardTiles_Never_Open_Extra_Direct_Rtsp_Session()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.Mjpeg,
            FallbackModes = [LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            RtspUrl = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            MjpegUrl = "http://127.0.0.1:5317/api/devices/device/live.mjpeg"
        };

        // The service owns the camera RTSP session and provides the retrying HTTP ladder.
        // Desktop tiles must not open a second camera session after the shared modes fail.
        Assert.Empty(BoardTileViewModel.SelectBoardStreamUrls(manifest));
        Assert.DoesNotContain(manifest.RtspUrl, BoardTileViewModel.SelectBoardStreamUrls(manifest));
    }

    [Fact]
    public void DesktopPlayback_Omits_Direct_Rtsp_When_Manifest_Does_Not_Advertise_It()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.H264Fmp4,
            FallbackModes = [LiveMediaModeContract.H264Fmp4, LiveMediaModeContract.Mjpeg],
            H264Fmp4Url = "http://127.0.0.1:5317/api/devices/device/live.h264.mp4",
            MjpegUrl = "http://127.0.0.1:5317/api/devices/device/live.mjpeg"
        };

        // RtspUrl empty (browser manifest or RTSP probe failed): ladder starts at the
        // negotiated HTTP mode — no rtsp:// entry is injected.
        Assert.DoesNotContain("rtsp://", MainWindowViewModel.SelectDesktopStreamUrl(manifest)!);
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
            [manifest.H264Fmp4Url],
            MainWindowViewModel.SelectDesktopStreamUrls(manifest));
    }

    [Fact]
    public void DesktopPlayback_Skips_Mjpeg_When_Backend_Negotiates_Mjpeg_First()
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

        // MJPEG is intentionally not fed into the rawvideo decoder, and no continuous
        // fallback was negotiated. Snapshot remains on the separate watchdog path.
        Assert.Null(MainWindowViewModel.SelectDesktopStreamUrl(manifest));
    }

    [Fact]
    public void DesktopPlayback_Does_Not_Feed_Snapshot_To_Continuous_Decoder()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.H264Fmp4,
            FallbackModes = [LiveMediaModeContract.H264Fmp4, LiveMediaModeContract.Mjpeg, LiveMediaModeContract.Snapshot],
            H264Fmp4Url = "http://127.0.0.1:5317/live.h264.mp4",
            MjpegUrl = "http://127.0.0.1:5317/live.mjpeg",
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/snapshot"
        };

        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest);

        Assert.DoesNotContain(manifest.SnapshotUrl, urls);
        Assert.Equal([manifest.H264Fmp4Url], urls);
    }

    [Fact]
    public void DesktopPlayback_Does_Not_Fall_Back_To_Mjpeg_Slideshow()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.HevcFmp4,
            FallbackModes = [LiveMediaModeContract.HevcFmp4, LiveMediaModeContract.H264Fmp4, LiveMediaModeContract.Mjpeg],
            RtspUrl = "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            HevcFmp4Url = "http://127.0.0.1:5317/live.mp4",
            H264Fmp4Url = "http://127.0.0.1:5317/live.h264.mp4",
            MjpegUrl = "http://127.0.0.1:5317/live.mjpeg"
        };

        var urls = MainWindowViewModel.SelectDesktopStreamUrls(manifest);

        Assert.Equal([manifest.HevcFmp4Url, manifest.H264Fmp4Url], urls);
        Assert.DoesNotContain(manifest.RtspUrl, urls);
        Assert.DoesNotContain(manifest.MjpegUrl, urls);
    }

    [Fact]
    public void DesktopPlayback_Uses_No_Continuous_Url_When_Only_Snapshot_Is_Negotiated()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.Snapshot,
            FallbackModes = [LiveMediaModeContract.Snapshot],
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        Assert.Empty(MainWindowViewModel.SelectDesktopStreamUrls(manifest));
    }

    [Fact]
    public void DesktopPlayback_Does_Not_Select_Snapshot_For_Continuous_Decoder()
    {
        var manifest = new LiveMediaManifest
        {
            PreferredMode = LiveMediaModeContract.Snapshot,
            FallbackModes = [LiveMediaModeContract.Snapshot],
            SnapshotAvailable = true,
            SnapshotUrl = "http://127.0.0.1:5317/api/devices/device/snapshot"
        };

        Assert.Empty(MainWindowViewModel.SelectDesktopStreamUrls(manifest));
        Assert.Null(MainWindowViewModel.SelectDesktopStreamUrl(manifest));
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
            [manifest.H264Fmp4Url, manifest.MpegTsUrl],
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
    public void LiveVideoFfmpegArguments_Default_To_CPU_BGRA_For_Stable_Frame_Size()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            null);
        var argList = args.ToList();

        // The renderer consumes a fixed-size CPU BGRA frame. Automatic hardware decode
        // can leave frames in an accelerator surface or fail the scale/pad download path,
        // so the stable default is software decode; operators can explicitly opt into
        // hardware with useHardwareAcceleration=true after validating their driver.
        Assert.DoesNotContain("-hwaccel", args);
        Assert.True(argList.IndexOf("-i") > 0);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Can_Force_Software_Decode_For_Driver_Recovery()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.mp4",
            null,
            useHardwareAcceleration: false);

        Assert.DoesNotContain("-hwaccel", args);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Accept_Target_Resolution_And_MultiThreaded_Decode()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.mp4",
            null,
            width: 1920,
            height: 1080);

        // -threads 0 lets ffmpeg use every core for HEVC software decode (native path).
        Assert.Contains("-threads", args);
        Assert.Contains("0", args);
        // Keep the input low-latency without dropping HEVC reference frames: the
        // 5523-W's ordered TCP stream needs the normal demuxer queue for 15 fps decode.
        Assert.Contains("-fflags", args);
        var argList = args.ToList();
        var fflags = args[argList.IndexOf("-fflags") + 1];
        Assert.DoesNotContain("nobuffer", fflags);
        Assert.Contains("discardcorrupt", fflags);
        // Do not disable decoder reordering: the 5523-W HEVC reference chain needs
        // normal frame reordering for smooth playback.
        Assert.DoesNotContain("low_delay", args);
        Assert.Equal("2000000", argList[argList.IndexOf("-probesize") + 1]);
        Assert.Equal("2000000", argList[argList.IndexOf("-analyzeduration") + 1]);
        // Passthrough frame timing preserves the camera's native timestamps without
        // CFR duplication/drops.
        Assert.Equal("passthrough", argList[argList.IndexOf("-fps_mode") + 1]);
        // The requested surface resolution is encoded into the scale/pad filter chain.
        Assert.Contains(
            "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2",
            args);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Default_To_Board_Tile_Resolution()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.mp4",
            null);

        Assert.Contains(
            "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2",
            args);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Direct_Rtsp_Preserves_Reference_Frame_Pacing()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            null);
        var argList = args.ToList();
        var fflags = argList[argList.IndexOf("-fflags") + 1];

        // The 5523-W sends HEVC reference frames over ordered TCP. Forcing ffmpeg's
        // demuxer into nobuffer/reorder-queue-zero mode drops reference frames and
        // measured output fell to 1-3 fps instead of the camera's 15 fps.
        Assert.DoesNotContain("nobuffer", fflags);
        Assert.DoesNotContain("-reorder_queue_size", args);
        Assert.Equal("passthrough", argList[argList.IndexOf("-fps_mode") + 1]);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Add_Rtsp_Transport_Flags_For_Direct_Rtsp_Url()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "rtsp://admin:p%40ss@10.0.0.169:554/ch0_0.264",
            null);

        // Direct RTSP uses TCP transport and a bounded socket timeout. Keep the normal
        // packet reorder queue because the camera's HEVC reference chain depends on it.
        Assert.Contains("-rtsp_transport", args);
        Assert.Contains("tcp", args);
        Assert.Contains("-rtsp_flags", args);
        Assert.Contains("prefer_tcp", args);
        // Assert flag→value pairings: a bare Contains("0") would be vacuous here because
        // -threads 0 already emits a standalone "0" element in the same arg list.
        // discardcorrupt belongs inside -fflags; it is not a standalone ffmpeg option.
        var argList = args.ToList();
        Assert.Equal("500000", argList[argList.IndexOf("-max_delay") + 1]);
        Assert.DoesNotContain("-reorder_queue_size", args);
        Assert.Contains("discardcorrupt", argList[argList.IndexOf("-fflags") + 1]);
        Assert.DoesNotContain("-discardcorrupt", args);
        Assert.Equal("10000000", argList[argList.IndexOf("-timeout") + 1]);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Http_Url_Omits_Rtsp_Transport_Flags()
    {
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.mp4",
            null);

        Assert.DoesNotContain("-rtsp_transport", args);
        Assert.DoesNotContain("-rtsp_flags", args);
        Assert.DoesNotContain("-max_delay", args);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Http_Url_Adds_Rw_Timeout_To_Fail_Fast_On_Stalls()
    {
        // The service's shared HTTP fMP4 session can stall mid-stream (half-open connection
        // after a camera Wi-Fi blip). ffmpeg's HTTP demuxer has no built-in read deadline,
        // so rw_timeout bounds the socket read/write and lets the frame watchdog advance the
        // reconnect ladder instead of freezing the tile on its last frame.
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "http://127.0.0.1:5317/api/devices/1/live.mp4",
            null);
        var argList = args.ToList();

        Assert.Equal("10000000", argList[argList.IndexOf("-rw_timeout") + 1]);
    }

    [Fact]
    public void LiveVideoFfmpegArguments_Direct_Rtsp_Does_Not_Add_Http_Rw_Timeout()
    {
        // rw_timeout is an HTTP-protocol option; direct RTSP uses -timeout instead.
        var args = MainWindowViewModel.BuildLiveVideoFfmpegArguments(
            "rtsp://admin:@10.0.0.169:554/ch0_0.264",
            null);
        var argList = args.ToList();

        Assert.DoesNotContain("-rw_timeout", args);
        Assert.Equal("10000000", argList[argList.IndexOf("-timeout") + 1]);
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
    }    // ── Dispose ─────────────────────────────────────────────

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
        var ex2 = Record.Exception(() => api.Dispose());
        Assert.Null(ex2);
    }
}
