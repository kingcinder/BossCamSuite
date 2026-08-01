using BossCam.Contracts;
using BossCam.Desktop.Avalonia.Services;
using BossCam.Desktop.Avalonia.ViewModels;
using Microsoft.Extensions.Logging;

namespace BossCam.Desktop.Avalonia.Tests;

/// <summary>
/// Asserts the Avalonia logging wiring actually emits entries on failures instead of
/// silently returning null/false: HttpBossCamApiClient's silent catches and
/// MainWindowViewModel's live-info enrichment catch now log at Debug.
/// </summary>
public sealed class LoggingWiringTests
{
    [Fact]
    public async Task HttpBossCamApiClient_GetSnapshotAsync_Failure_Logs_Debug()
    {
        var logger = new CapturedLogger<HttpBossCamApiClient>();
        using var client = new HttpBossCamApiClient("http://127.0.0.1:1", logger); // closed port -> refused

        var bytes = await client.GetSnapshotAsync(Guid.NewGuid());

        Assert.Null(bytes);
        var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.Contains("Snapshot fetch failed", debug.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpBossCamApiClient_GetLiveInfoAsync_Failure_Logs_Debug()
    {
        var logger = new CapturedLogger<HttpBossCamApiClient>();
        using var client = new HttpBossCamApiClient("http://127.0.0.1:1", logger);

        var info = await client.GetLiveInfoAsync(Guid.NewGuid());

        Assert.Null(info);
        var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.Contains("Live-info fetch failed", debug.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainWindowViewModel_LiveInfo_Enrichment_Failure_Logs_Debug()
    {
        var api = new TestBossCamApiClient { ThrowOnLiveInfo = true };
        var logger = new CapturedLogger<MainWindowViewModel>();
        var vm = new MainWindowViewModel(api, logger);
        vm.SelectedDevice = new DeviceIdentity { Id = Guid.NewGuid(), Name = "TestCam", IpAddress = "192.168.1.10" };

        await vm.RefreshDeviceCommand.ExecuteAsync(null);

        // Setting SelectedDevice already fired OnSelectedDeviceChanged -> RefreshDeviceAsync,
        // and the faulted task completes synchronously, so GetLiveInfoAsync may be hit twice.
        Assert.True(api.GetLiveInfoCallCount >= 1);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Debug && entry.Message.Contains("Live-info enrichment failed", StringComparison.Ordinal));
    }
}
