using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using BossCam.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Asserts that the catches promoted from bare-swallow to logged in the silent-catch
/// logging pass actually emit their entries through a captured <see cref="ILogger"/>
/// fake:
///  - corrupt media-storage.json → LogWarning (ApiStorageEndpoints)
///  - ffprobe duration probe failure → LogDebug (RecordingService)
/// </summary>
public sealed class LoggedCatchTests : IDisposable
{
    private readonly List<string> _cleanupPaths = [];

    [Fact]
    public void LoadMediaStoragePaths_Corrupt_Config_Logs_Warning_And_Falls_Back_To_Defaults()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"bosscam-storage-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, "{ this is not valid json !!!");
        var logger = new ListLogger();
        try
        {
            var paths = ApiStorageEndpoints.LoadMediaStoragePaths(logger, configPath);

            // Falls back to defaults (non-null), and the warning fired with the corrupt path.
            Assert.NotNull(paths.ContinuousRecordings);
            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("Failed to read media storage config", warning.Message, StringComparison.Ordinal);
            Assert.Contains(configPath, warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void LoadMediaStoragePaths_Valid_Config_Does_Not_Log_Warning()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"bosscam-storage-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """{"ContinuousRecordings":"/tmp/c","Highlights":"/tmp/h","Snapshots":"/tmp/s"}""");
        var logger = new ListLogger();
        try
        {
            _ = ApiStorageEndpoints.LoadMediaStoragePaths(logger, configPath);

            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task ProbeDurationAsync_Ffprobe_Start_Failure_Logs_Debug()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // sibling-probe fallback uses the POSIX "ffprobe" name; Windows not covered
        }

        var dir = Path.Combine(Path.GetTempPath(), $"bosscam-ffprobe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A real file named ffprobe that is NOT executable → Process.Start throws →
            // the LogDebug catch fires (this is the silent-degradation path being locked down).
            var fakeProbe = Path.Combine(dir, "ffprobe");
            await File.WriteAllTextAsync(fakeProbe, "not an executable");
            var fakeSegment = Path.Combine(dir, "segment.ts");
            await File.WriteAllTextAsync(fakeSegment, "not a real segment");

            var logger = new ListLogger<RecordingService>();
            var service = BuildRecordingService(logger);

            var duration = await service.ProbeDurationAsync(
                fakeSegment,
                Path.Combine(dir, "ffmpeg"), // sibling-dir lookup finds the fake ffprobe
                CancellationToken.None);

            Assert.Null(duration);
            var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
            Assert.Contains("ffprobe duration probe failed", debug.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private RecordingService BuildRecordingService(ILogger<RecordingService> logger)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-logged-{Guid.NewGuid():N}.db");
        _cleanupPaths.Add(dbPath);
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new RecordingService(
            store,
            new TransportBroker([], store, null, Microsoft.Extensions.Logging.Abstractions.NullLogger<TransportBroker>.Instance),
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            new HttpClientFactoryMock(),
            logger);
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
