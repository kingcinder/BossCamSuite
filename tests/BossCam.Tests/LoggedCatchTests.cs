using System.Diagnostics;
using System.Net.Http;
using BossCam.Contracts;
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
///  - housekeeping TryDelete failure → LogDebug (RecordingService)
///  - ffmpeg stderr drain failure → LogDebug (RecordingService)
///  - transport reachability / probe failure → LogDebug, credentials truncated
///    (TransportFailoverService)
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

    [Fact]
    public void TryDelete_Directory_Failure_Logs_Debug_And_Returns_False()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bosscam-trydelete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var logger = new ListLogger<RecordingService>();
        var service = BuildRecordingService(logger);
        try
        {
            // FileInfo.Delete() on a directory path throws → the housekeeping catch fires.
            var info = new FileInfo(dir);
            var deletedFiles = 0;
            long deletedBytes = 0;

            var deleted = service.TryDelete(info, ref deletedFiles, ref deletedBytes);

            Assert.False(deleted);
            Assert.Equal(0, deletedFiles);
            var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
            Assert.Contains("Housekeeping could not delete", debug.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DrainProcessOutputAsync_NonRedirected_Stderr_Logs_Debug()
    {
        var logger = new ListLogger<RecordingService>();
        var service = BuildRecordingService(logger);

        // A Process whose stderr was never redirected → StandardError getter throws
        // InvalidOperationException → the drain catch fires.
        using var process = new Process();
        await service.DrainProcessOutputAsync(process, processId: 4242);

        var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.Contains("Failed to drain ffmpeg stderr", debug.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsTransportReachableAsync_Probe_Failure_Logs_Debug_Without_Credentials()
    {
        var logger = new ListLogger<TransportFailoverService>();
        var service = BuildFailoverService(logger);
        var device = NewProbeDevice();
        var source = NewProbeSource();

        var reachable = await service.IsTransportReachableAsync(device, source, CancellationToken.None);

        Assert.False(reachable);
        var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("Transport reachability probe failed", StringComparison.Ordinal));
        Assert.DoesNotContain("sekrit", debug.Message, StringComparison.Ordinal); // TruncateCredentials applied
    }

    [Fact]
    public async Task ProbeTransportAsync_Probe_Failure_Logs_Debug_Without_Credentials()
    {
        var logger = new ListLogger<TransportFailoverService>();
        var service = BuildFailoverService(logger);
        var device = NewProbeDevice();
        var source = NewProbeSource();

        var result = await service.ProbeTransportAsync(device, source, CancellationToken.None);

        Assert.Null(result);
        var debug = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("Transport probe failed", StringComparison.Ordinal));
        Assert.DoesNotContain("sekrit", debug.Message, StringComparison.Ordinal); // TruncateCredentials applied
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
            logger,
            new ApplicationStoreRecordingStore(store),
            new RecordingProcessSupervisor());
    }

    private TransportFailoverService BuildFailoverService(ILogger<TransportFailoverService> logger)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-failover-{Guid.NewGuid():N}.db");
        _cleanupPaths.Add(dbPath);
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new TransportFailoverService(
            store,
            new TransportBroker([], store, null, Microsoft.Extensions.Logging.Abstractions.NullLogger<TransportBroker>.Instance),
            new ThrowingHttpClientFactory(),
            logger);
    }

    private static DeviceIdentity NewProbeDevice() => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = "10.0.0.1",
        Port = 8888,
        LoginName = "admin",
        Password = "sekrit"
    };

    private static VideoSourceDescriptor NewProbeSource() => new()
    {
        Kind = TransportKind.LanRest,
        Url = "http://admin:sekrit@10.0.0.1:8888/NetSDK/Video/encode/channel/101/snapShot",
        Rank = 25
    };

    /// <summary>Client factory whose probes always fail at the transport level (connection refused).</summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
