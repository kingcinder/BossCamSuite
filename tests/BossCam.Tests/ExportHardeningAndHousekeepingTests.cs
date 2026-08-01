using System.Diagnostics;
using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using BossCam.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Re-audit punchlist coverage (P0–P2):
///  - P0: ExportClipAsync rejects OutputPath outside the configured export roots (including the
///    sibling-prefix case) before touching the filesystem, and both ffmpeg invocations pass the
///    path as a single ArgumentList element (no `"`-breakout argument injection).
///  - P1: the recordings download containment check is segment-aware (sibling root rejected).
///  - P2: housekeeping that purges a physical segment file also removes the recording_segments
///    row and evicts the in-memory index cache, and ExportClipAsync skips already-purged
///    segments and reports the partial range honestly instead of failing the concat opaquely.
/// </summary>
public sealed class ExportHardeningAndHousekeepingTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-export-hardening-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public ExportHardeningAndHousekeepingTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    // ── P0: export write-side allow-list ──────────────────────────────

    [Fact]
    public async Task ExportClipAsync_Rejects_OutputPath_Outside_AllowedRoot()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var exportRoot = Path.Combine(_tempDirectory, "exports");
        var outside = Path.Combine(Path.GetTempPath(), $"bosscam-outside-{Guid.NewGuid():N}", "clip.mp4");

        var service = BuildRecordingService(store, exportRoot);
        var result = await service.ExportClipAsync(new ClipExportRequest
        {
            DeviceId = Guid.NewGuid(),
            StartTime = DateTimeOffset.UtcNow.AddHours(-1),
            EndTime = DateTimeOffset.UtcNow,
            OutputPath = outside
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("export directory", result.Message, StringComparison.OrdinalIgnoreCase);
        // Rejected before touching the filesystem — no directory must be created.
        Assert.False(Directory.Exists(Path.GetDirectoryName(outside)));
    }

    [Fact]
    public async Task ExportClipAsync_Rejects_OutputPath_With_Sibling_Prefix()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var exportRoot = Path.Combine(_tempDirectory, "exports");
        // /tmp/.../exports-evil must NOT be accepted when the root is /tmp/.../exports.
        var sibling = exportRoot + "-evil";

        var service = BuildRecordingService(store, exportRoot);
        var result = await service.ExportClipAsync(new ClipExportRequest
        {
            DeviceId = Guid.NewGuid(),
            StartTime = DateTimeOffset.UtcNow.AddHours(-1),
            EndTime = DateTimeOffset.UtcNow,
            OutputPath = Path.Combine(sibling, "clip.mp4")
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(sibling));
    }

    [Fact]
    public async Task ExportClipAsync_Rejects_When_No_Export_Root_Configured()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        // Default BossCamRuntimeOptions has ExportAllowedDirectories empty → exports disabled.
        var service = BuildRecordingService(store, exportRoot: null);
        var result = await service.ExportClipAsync(new ClipExportRequest
        {
            DeviceId = Guid.NewGuid(),
            StartTime = DateTimeOffset.UtcNow.AddHours(-1),
            EndTime = DateTimeOffset.UtcNow,
            OutputPath = Path.Combine(_tempDirectory, "exports", "clip.mp4")
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ExportAllowedDirectories", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportClipAsync_OutputPath_With_DoubleQuote_Does_Not_Inject_Ffmpeg_Args()
    {
        var evil = Path.Combine(Path.GetTempPath(), "clip\"; rm -rf / ;\".mp4");

        var copyInfo = RecordingService.BuildExportFfmpegStartInfo("/usr/bin/ffmpeg", "/tmp/list.txt", evil, reEncode: false);
        var reencodeInfo = RecordingService.BuildExportFfmpegStartInfo("/usr/bin/ffmpeg", "/tmp/list.txt", evil, reEncode: true);

        // The path survives as ONE literal ArgumentList element on both invocations — a quote
        // can never break it into extra argv entries (e.g. "-i", "rm", "-rf"). The injection
        // payload only ever appears inside that single element.
        foreach (var info in new[] { copyInfo, reencodeInfo })
        {
            // The path is the final argv element, verbatim and intact — a `"` can never break it
            // into extra ffmpeg flags (e.g. "-i", "rm", "-rf"). The injection payload only ever
            // appears inside that single element.
            Assert.Equal(evil, info.ArgumentList[^1]);
            Assert.Single(info.ArgumentList, arg => arg.Contains("rm -rf", StringComparison.Ordinal));
        }

        // Fast path carries -c copy; fallback carries the re-encode flag set.
        Assert.Contains("copy", copyInfo.ArgumentList, StringComparer.Ordinal);
        Assert.Contains("libx264", reencodeInfo.ArgumentList, StringComparer.Ordinal);
    }

    // ── P3: brand detection distinguishes Wansview / Netvue / Netview ─

    [Fact]
    public void DetectBrand_Recognizes_Wansview_And_Netvue_And_Netview()
    {
        Assert.Equal(CameraBrand.Wansview, MultiBrandHighResTransportAdapter.DetectBrand(new DeviceIdentity { HardwareModel = "Wansview W6", Name = "cam" }));
        Assert.Equal(CameraBrand.Wansview, MultiBrandHighResTransportAdapter.DetectBrand(new DeviceIdentity { HardwareModel = "W6", Name = "Wansview cam" }));
        Assert.Equal(CameraBrand.Netvue, MultiBrandHighResTransportAdapter.DetectBrand(new DeviceIdentity { HardwareModel = "Netvue Cam", Name = "cam" }));
        Assert.Equal(CameraBrand.Netvue, MultiBrandHighResTransportAdapter.DetectBrand(new DeviceIdentity { HardwareModel = "X1", Name = "Netview cam" }));
        // A truly-unknown model stays Unknown (not folded into a brand tier).
        Assert.Equal(CameraBrand.Unknown, MultiBrandHighResTransportAdapter.DetectBrand(new DeviceIdentity { HardwareModel = "Temu PTZ Pro", Name = "cam" }));
    }

    [Fact]
    public async Task Wansview_Brand_Does_Not_Receive_Juan_Guess_Tier()
    {
        // Previously a Wansview (Unknown brand) got Juan's rank-0/3 + Dahua rank-2 guess tiers
        // ahead of its real generic/ONVIF candidates; with a dedicated brand it must not. The
        // generic RTSP fallback itself re-lists /ch0_0.264 and /cam/realmonitor at ranks 25/24,
        // so "no Juan tier" must be asserted by RANK BAND (<20 absent), not by path.
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.6",
            HardwareModel = "Wansview W6",
            DeviceType = "IPC",
            LoginName = "admin",
            Password = "pw"
        };
        var adapter = new MultiBrandHighResTransportAdapter(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(),
            NullLogger<MultiBrandHighResTransportAdapter>.Instance);

        var sources = await adapter.GetSourcesAsync(device, CancellationToken.None);

        // No Juan/Dahua guess tier (ranks 0-3 / 50-52) is emitted for a Wansview-branded unit.
        Assert.DoesNotContain(sources, source => source.Rank <= 3);
        Assert.DoesNotContain(sources, source => source.Rank is >= 50 and <= 52);
        // Generic tier is present and is the ONLY main band (20-27).
        var mains = sources.Where(source => source.Metadata.TryGetValue("stream", out var stream) && stream == "main").ToList();
        Assert.NotEmpty(mains);
        Assert.All(mains, source => Assert.InRange(source.Rank, 20, 27));
        Assert.Contains(sources, source => source.Url.Contains("/stream1", StringComparison.Ordinal));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticHandler());

        private sealed class StaticHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    // ── P1: download containment is segment-aware ─────────────────────

    [Fact]
    public void Download_Rejects_Path_With_Sibling_Directory_Prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bosscam-download-root-{Guid.NewGuid():N}");
        var sibling = root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.True(ApiRecordingsEndpoints.IsPathUnderStorageRoot(Path.Combine(root, "clip.mp4"), root));
            // /root-evil/x.mp4 must NOT be treated as inside /root (raw StartsWith would accept it).
            Assert.False(ApiRecordingsEndpoints.IsPathUnderStorageRoot(Path.Combine(sibling, "clip.mp4"), root));
            // Parent traversal must not escape either.
            Assert.False(ApiRecordingsEndpoints.IsPathUnderStorageRoot(Path.Combine(root, "..", "escape.mp4"), root));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    // ── P2: housekeeping reconciles the index + cache ─────────────────

    [Fact]
    public async Task Housekeeping_Deletes_File_Also_Removes_RecordingSegment_Row()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice();
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var recDir = Path.Combine(_tempDirectory, "retention");
        Directory.CreateDirectory(recDir);
        var oldFile = Path.Combine(recDir, "old.ts");
        await File.WriteAllBytesAsync(oldFile, new byte[] { 1, 2, 3 }, CancellationToken.None);
        File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));

        var profile = NewRetentionProfile(device.Id, recDir);
        await store.SaveRecordingProfilesAsync([profile], CancellationToken.None);
        // Seed an index row pointing at the file retention will purge.
        await store.SaveRecordingSegmentsAsync(
        [
            new RecordingSegment
            {
                DeviceId = device.Id,
                ProfileId = profile.Id,
                FilePath = oldFile,
                StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                EndTime = DateTimeOffset.UtcNow.AddDays(-29),
                DurationSec = 30
            }
        ], CancellationToken.None);

        var recording = BuildRecordingService(store);
        var result = await recording.RunHousekeepingAsync(device.Id, CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(oldFile));
        Assert.Empty(await store.GetRecordingSegmentsAsync(device.Id, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Housekeeping_Deletes_File_Also_Evicts_IndexedCache_Entry()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice();
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var recDir = Path.Combine(_tempDirectory, "retention-cache");
        Directory.CreateDirectory(recDir);
        var oldFile = Path.Combine(recDir, "old.ts");
        // >= 8 bytes so RefreshIndexAsync actually indexes it.
        await File.WriteAllBytesAsync(oldFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, CancellationToken.None);
        File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));

        var profile = NewRetentionProfile(device.Id, recDir);
        await store.SaveRecordingProfilesAsync([profile], CancellationToken.None);

        var recording = BuildRecordingService(store);
        _ = await recording.RefreshIndexAsync(device.Id, CancellationToken.None);
        Assert.True(recording.IsFileIndexed(oldFile));

        var result = await recording.RunHousekeepingAsync(device.Id, CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(oldFile));
        Assert.False(recording.IsFileIndexed(oldFile));
    }

    // ── P2: export skips purged segments and reports the partial range ─

    [Fact]
    public async Task ExportClipAsync_Skips_Missing_Segments_And_Reports_Partial_Range()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var exportRoot = Path.Combine(_tempDirectory, "exports");
        var segDir = Path.Combine(_tempDirectory, "segments");
        Directory.CreateDirectory(segDir);
        Directory.CreateDirectory(exportRoot);

        var deviceId = Guid.NewGuid();
        var existingFile = Path.Combine(segDir, "keep.ts");
        await File.WriteAllBytesAsync(existingFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, CancellationToken.None);
        var missingFile = Path.Combine(segDir, "purged.ts");
        var now = DateTimeOffset.UtcNow;
        await store.SaveRecordingSegmentsAsync(
        [
            new RecordingSegment { DeviceId = deviceId, FilePath = existingFile, StartTime = now.AddMinutes(-2), EndTime = now.AddMinutes(-1), DurationSec = 30 },
            new RecordingSegment { DeviceId = deviceId, FilePath = missingFile, StartTime = now.AddMinutes(-1), EndTime = now, DurationSec = 30 }
        ], CancellationToken.None);

        var fakeFfmpeg = WriteFakeFfmpeg();
        var previous = Environment.GetEnvironmentVariable("BOSSCAM_FFMPEG_PATH");
        Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", fakeFfmpeg);
        try
        {
            var recording = BuildRecordingService(store, exportRoot);
            var result = await recording.ExportClipAsync(new ClipExportRequest
            {
                DeviceId = deviceId,
                StartTime = now.AddMinutes(-3),
                EndTime = now,
                OutputPath = Path.Combine(exportRoot, "clip.mp4")
            }, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("skipped 1 purged", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 segment", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.OutputPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOSSCAM_FFMPEG_PATH", previous);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static DeviceIdentity NewDevice() => new() { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4" };

    private static RecordingProfile NewRetentionProfile(Guid deviceId, string outputDirectory) => new()
    {
        DeviceId = deviceId,
        Name = "Retention",
        OutputDirectory = outputDirectory,
        SegmentSeconds = 60,
        Enabled = true,
        AutoStart = false,
        RetentionDays = 7
    };

    private RecordingService BuildRecordingService(SqliteApplicationStore store, string? exportRoot = null)
    {
        var options = Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = _dbPath,
            ExportAllowedDirectories = exportRoot is null ? [] : [exportRoot]
        });
        return new RecordingService(
            store,
            new TransportBroker([], store, null, NullLogger<TransportBroker>.Instance),
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            new HttpClientFactoryMock(),
            NullLogger<RecordingService>.Instance,
            new ApplicationStoreRecordingStore(store),
            new RecordingProcessSupervisor(),
            options);
    }

    /// <summary>Hermetic fake ffmpeg: writes its last argv element (the output path) and exits 0,
    /// so the export success path runs without a real media toolchain.</summary>
    private string WriteFakeFfmpeg()
    {
        var path = Path.Combine(_tempDirectory, "fake-ffmpeg.sh");
        File.WriteAllText(path, "#!/usr/bin/env bash\nprintf 'x' > \"${@: -1}\"\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            using var chmod = Process.Start("chmod", $"+x {path}");
            chmod?.WaitForExit(2000);
        }
        return path;
    }
}
