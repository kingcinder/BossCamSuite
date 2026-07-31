using System.Net;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the single-pick consumers of the snapshot descriptors:
/// <see cref="NetSdkPortCandidates.FirstReachableSnapshotAsync"/> probes candidates in ascending
/// rank order (recorded port first, then the :80 fallback the adapters emit), and both
/// <see cref="RecordingService.ResolveSnapshotUrlAsync"/> and the highlight-board tiles consume
/// the first reachable candidate — so snapshot recording / tile thumbnails self-heal on dead
/// recorded ports instead of FirstOrDefault pinning them to the rank-25 dead URL.
/// </summary>
public sealed class SnapshotConsumerProbeTests
{
    private const int RecordedOnvifPort = 8888;

    [Fact]
    public async Task FirstReachableSnapshotAsync_Prefers_Recorded_Port_When_It_Serves_Jpeg()
    {
        var handler = new ScriptedHandler(uri => JpegOk());
        var factory = new HandlerBackedFactory(handler);

        var pick = await NetSdkPortCandidates.FirstReachableSnapshotAsync(factory, SnapshotSources(), CancellationToken.None);

        Assert.NotNull(pick);
        Assert.Contains($":{RecordedOnvifPort}/NetSDK/Video/encode/channel/101/snapShot", pick!.Url, StringComparison.Ordinal);
        // Short-circuits: only the recorded port is probed when it already answers.
        Assert.Equal(new[] { RecordedOnvifPort }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task FirstReachableSnapshotAsync_Falls_Back_To_80_When_Recorded_Port_Dead()
    {
        var handler = new ScriptedHandler(uri => uri.Port == RecordedOnvifPort
            ? throw new HttpRequestException($"connection refused on :{uri.Port}")
            : JpegOk());
        var factory = new HandlerBackedFactory(handler);

        var pick = await NetSdkPortCandidates.FirstReachableSnapshotAsync(factory, SnapshotSources(), CancellationToken.None);

        Assert.NotNull(pick);
        Assert.Contains(":80/NetSDK/Video/encode/channel/101/snapShot", pick!.Url, StringComparison.Ordinal);
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task FirstReachableSnapshotAsync_Non_Jpeg_On_Recorded_Port_Falls_Back_To_80()
    {
        // Recorded port answers 200 but with an HTML body — not a JPEG, so it must not be
        // selected; the :80 fallback that serves a real JPEG wins.
        var handler = new ScriptedHandler(uri => uri.Port == RecordedOnvifPort
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") }
            : JpegOk());
        var factory = new HandlerBackedFactory(handler);

        var pick = await NetSdkPortCandidates.FirstReachableSnapshotAsync(factory, SnapshotSources(), CancellationToken.None);

        Assert.NotNull(pick);
        Assert.Contains(":80/NetSDK/Video/encode/channel/101/snapShot", pick!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstReachableSnapshotAsync_Returns_Null_When_No_Candidate_Serves_Jpeg()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var factory = new HandlerBackedFactory(handler);

        var pick = await NetSdkPortCandidates.FirstReachableSnapshotAsync(factory, SnapshotSources(), CancellationToken.None);

        Assert.Null(pick);
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task FirstReachableSnapshotAsync_Returns_Null_Without_Snapshot_Descriptors()
    {
        var factory = new HandlerBackedFactory(new ScriptedHandler(_ => JpegOk()));
        var sources = new List<VideoSourceDescriptor>
        {
            new() { Kind = TransportKind.Rtsp, Url = "rtsp://admin@127.0.0.1:554/ch0_0.264", Rank = 0 }
        };

        var pick = await NetSdkPortCandidates.FirstReachableSnapshotAsync(factory, sources, CancellationToken.None);

        Assert.Null(pick);
        Assert.Empty(factory.Handler.RequestedUris);
    }

    [Fact]
    public async Task RecordingService_ResolveSnapshotUrlAsync_Falls_Back_To_80_For_Snapshot_Pipeline()
    {
        using var harness = await CreateHarnessAsync(RecordedOnvifPort);
        var service = new RecordingService(
            harness.Store,
            harness.Broker,
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            harness.Factory,
            NullLogger<RecordingService>.Instance);

        var url = await service.ResolveSnapshotUrlAsync(harness.Device, SnapshotSources(), CancellationToken.None);

        Assert.Contains(":80/NetSDK/Video/encode/channel/101/snapShot", url, StringComparison.Ordinal);
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, harness.Handler.RequestedUris.Select(uri => uri.Port));
    }

    [Fact]
    public async Task RecordingService_ResolveSnapshotUrlAsync_Falls_Back_To_BuildSnapshotUrl_When_No_Candidate_Works()
    {
        using var harness = await CreateHarnessAsync(RecordedOnvifPort);
        var service = new RecordingService(
            harness.Store,
            harness.Broker,
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            harness.Factory,
            NullLogger<RecordingService>.Instance);

        // A device with no snapshot descriptors at all → BuildSnapshotUrl (recorded port kept).
        var url = await service.ResolveSnapshotUrlAsync(harness.Device, [], CancellationToken.None);

        Assert.Contains($":{RecordedOnvifPort}/NetSDK/Video/encode/channel/101/snapShot", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HighlightBoard_Tile_SnapshotUrl_Falls_Back_To_80()
    {
        using var harness = await CreateHarnessAsync(RecordedOnvifPort);
        var recording = new RecordingService(
            harness.Store,
            harness.Broker,
            new TestRecordingPipelineResolver(),
            NullBossCamEventBroadcaster.Instance,
            harness.Factory,
            NullLogger<RecordingService>.Instance);
        var board = new HighlightBoardService(
            harness.Store,
            harness.Broker,
            recording,
            harness.Factory,
            NullBossCamEventBroadcaster.Instance,
            NullLogger<HighlightBoardService>.Instance);

        var state = await board.GetStateAsync(CancellationToken.None);

        var tile = Assert.Single(state.Tiles);
        Assert.NotNull(tile.SnapshotUrl);
        Assert.Contains(":80/NetSDK/Video/encode/channel/101/snapShot", tile.SnapshotUrl, StringComparison.Ordinal);
        // The recorded-port-first contract still holds: 8888 was tried before :80.
        Assert.Equal(new[] { RecordedOnvifPort, 80 }, harness.Handler.RequestedUris.Select(uri => uri.Port));
    }

    private static IReadOnlyCollection<VideoSourceDescriptor> SnapshotSources()
        => new[]
        {
            SnapshotDescriptor(RecordedOnvifPort, rank: 25, isFallback: false),
            SnapshotDescriptor(80, rank: 26, isFallback: true)
        };

    private static VideoSourceDescriptor SnapshotDescriptor(int port, int rank, bool isFallback)
        => new()
        {
            Kind = TransportKind.LanRest,
            Url = $"http://admin:secret@127.0.0.1:{port}/NetSDK/Video/encode/channel/101/snapShot",
            Rank = rank,
            DisplayName = isFallback ? "JPEG snapshot (:80 fallback)" : "JPEG snapshot (NetSDK)",
            Metadata = new Dictionary<string, string> { ["kind"] = "snapshot", ["port"] = port.ToString() }
        };

    private static async Task<ProbeHarness> CreateHarnessAsync(int recordedPort)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-snapshot-consumer-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            IpAddress = "127.0.0.1",
            Port = recordedPort,
            LoginName = "admin",
            Password = "secret",
            Name = "snapshot-consumer-test",
            HardwareModel = "5523-W",
            DeviceType = "IPC"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var handler = new ScriptedHandler(uri => uri.Port == recordedPort
            ? throw new HttpRequestException($"connection refused on :{uri.Port}")
            : JpegOk());
        var factory = new HandlerBackedFactory(handler);
        var broker = new TransportBroker([new SnapshotOnlyVideoAdapter(recordedPort)], store, null, NullLogger<TransportBroker>.Instance);

        return new ProbeHarness(dbPath, store, broker, device, handler, factory);
    }

    private sealed record ProbeHarness(
        string DbPath,
        SqliteApplicationStore Store,
        TransportBroker Broker,
        DeviceIdentity Device,
        ScriptedHandler Handler,
        HandlerBackedFactory Factory) : IDisposable
    {
        public void Dispose()
        {
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    private static HttpResponseMessage JpegOk()
    {
        var payload = new byte[600];
        payload[0] = 0xFF;
        payload[1] = 0xD8;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
    }

    /// <summary>Emits the same snapshot descriptors as the real adapters for a recorded port.</summary>
    private sealed class SnapshotOnlyVideoAdapter(int recordedPort) : IVideoTransportAdapter
    {
        public string Name => "SnapshotOnly";
        public TransportKind TransportKind => TransportKind.LanRest;
        public int Priority => 10;

        public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
        {
            IReadOnlyCollection<VideoSourceDescriptor> sources = new[]
            {
                new VideoSourceDescriptor
                {
                    Kind = TransportKind.LanRest,
                    Url = $"http://admin:secret@127.0.0.1:{recordedPort}/NetSDK/Video/encode/channel/101/snapShot",
                    Rank = 25,
                    DisplayName = "JPEG snapshot (NetSDK)",
                    Metadata = new Dictionary<string, string> { ["kind"] = "snapshot", ["port"] = recordedPort.ToString() }
                },
                new VideoSourceDescriptor
                {
                    Kind = TransportKind.LanRest,
                    Url = "http://admin:secret@127.0.0.1:80/NetSDK/Video/encode/channel/101/snapShot",
                    Rank = 26,
                    DisplayName = "JPEG snapshot (:80 fallback)",
                    Metadata = new Dictionary<string, string> { ["kind"] = "snapshot", ["port"] = "80" }
                }
            };
            return Task.FromResult(sources);
        }
    }

    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(responder(request.RequestUri!));
        }
    }

    private sealed class HandlerBackedFactory(ScriptedHandler handler) : IHttpClientFactory
    {
        public ScriptedHandler Handler => handler;

        public HttpClient CreateClient(string name) => new(handler);
    }
}
