using System.Net;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Persisted NetSDK probe-verdict cache: a successful deviceInfo probe stamps
/// nativeNetSdk + proven port + probed-at onto the device's store metadata so the probe
/// is NOT re-run on every stream request (sources / live-info / manifest / live.ts /
/// live.mjpeg / live.mp4 all resolve through TransportBroker.GetSourcesAsync). The
/// verdict refreshes on TTL expiry or when failed playback invalidates it.
/// </summary>
public sealed class NetSdkProbeVerdictCacheTests
{
    // ── pure TTL semantics ────────────────────────────────────────────

    [Fact]
    public void Fresh_Verdict_Within_Ttl_Returns_Probe_Port()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var device = NewDevice();
        NetSdkProbeVerdictCache.Stamp(device, probePort: 80, now);

        var fresh = NetSdkProbeVerdictCache.TryGetFreshProbePort(device, TimeSpan.FromMinutes(30), now.AddMinutes(10), out var port);

        Assert.True(fresh);
        Assert.Equal(80, port);
    }

    [Fact]
    public void Stale_Verdict_Outside_Ttl_Is_Not_Fresh()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var device = NewDevice();
        NetSdkProbeVerdictCache.Stamp(device, probePort: 80, now.AddMinutes(-60));

        var fresh = NetSdkProbeVerdictCache.TryGetFreshProbePort(device, TimeSpan.FromMinutes(30), now, out _);

        Assert.False(fresh);
    }

    [Fact]
    public void Unmarked_Device_Has_No_Verdict()
    {
        var device = NewDevice();

        Assert.False(NetSdkProbeVerdictCache.HasVerdict(device));
        Assert.False(NetSdkProbeVerdictCache.TryGetFreshProbePort(device, TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow, out _));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Malformed_Probe_Port_Is_Not_Fresh(string badPort)
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var device = NewDevice();
        NetSdkProbeVerdictCache.Stamp(device, 80, now);
        device.Metadata[NetSdkProbeVerdictCache.ProbePortKey] = badPort;

        var fresh = NetSdkProbeVerdictCache.TryGetFreshProbePort(device, TimeSpan.FromMinutes(30), now, out _);

        Assert.False(fresh);
    }

    [Fact]
    public void Corrupted_Future_Timestamp_Is_Not_Fresh()
    {
        // A bit-flip / clock-skew future timestamp must NOT pin the verdict as fresh forever —
        // a negative age would be <= ttl; the guard requires age >= TimeSpan.Zero so a
        // corrupted value forces a re-probe instead of serving stale sources indefinitely.
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var device = NewDevice();
        NetSdkProbeVerdictCache.Stamp(device, 80, now.AddYears(10));

        var fresh = NetSdkProbeVerdictCache.TryGetFreshProbePort(device, TimeSpan.FromMinutes(30), now, out _);

        Assert.False(fresh);
    }

    [Fact]
    public void Clear_Removes_Verdict_Keys_Only()
    {
        var device = NewDevice();
        NetSdkProbeVerdictCache.Stamp(device, 80, DateTimeOffset.UtcNow);
        device.Metadata["other"] = "keep";

        var cleared = NetSdkProbeVerdictCache.Clear(device);

        Assert.False(NetSdkProbeVerdictCache.HasVerdict(cleared));
        Assert.Equal("keep", cleared.Metadata["other"]);
    }

    // ── store persistence ─────────────────────────────────────────────

    [Fact]
    public async Task SaveVerdict_Persists_Keys_To_Store()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, probePort: 80, DateTimeOffset.UtcNow, CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.True(NetSdkProbeVerdictCache.HasVerdict(reloaded!));
        Assert.Equal("80", reloaded!.Metadata[NetSdkProbeVerdictCache.ProbePortKey]);
        Assert.True(reloaded.Metadata.ContainsKey(NetSdkProbeVerdictCache.ProbedAtKey));
    }

    [Fact]
    public async Task Invalidate_Clears_Persisted_Verdict_From_Store()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, 80, DateTimeOffset.UtcNow, CancellationToken.None);

        await NetSdkProbeVerdictCache.InvalidateAsync(store, device.Id, CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.False(NetSdkProbeVerdictCache.HasVerdict(reloaded!));
    }

    // ── adapter integration ───────────────────────────────────────────

    [Fact]
    public async Task Adapter_Skips_Network_Probe_When_Verdict_Fresh()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, 80, DateTimeOffset.UtcNow, CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        // A fresh persisted verdict must mean ZERO network probes — the responder throws if called.
        var adapter = NewStreamAdapter(store, _ => throw new InvalidOperationException("probe must not run when verdict is fresh"));
        var sources = await adapter.GetSourcesAsync(reloaded!, CancellationToken.None);

        Assert.NotEmpty(sources);
        Assert.Contains(sources, source => source.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adapter_Probes_Again_After_Ttl_Expiry_And_Persists_Refresh()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        // Stale verdict (older than the 30-minute default TTL).
        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, 80, DateTimeOffset.UtcNow.AddMinutes(-60), CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        var probes = 0;
        var adapter = NewStreamAdapter(store, _ => { probes++; return OkJson(DeviceInfoFixtureBody); });
        var sources = await adapter.GetSourcesAsync(reloaded!, CancellationToken.None);

        Assert.NotEmpty(sources);
        Assert.Equal(1, probes);
        // The successful re-probe must refresh the persisted verdict timestamp.
        var after = await store.GetDeviceAsync(device.Id, CancellationToken.None);
        Assert.True(NetSdkProbeVerdictCache.HasVerdict(after!));
        Assert.True(NetSdkProbeVerdictCache.TryGetFreshProbePort(after!, TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public async Task Adapter_Probe_Failure_Invalidates_Stale_Verdict_In_Store()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        // STALE verdict (older than the 30-minute default TTL) so the probe actually runs — a
        // fresh verdict would take the cache-hit path and never probe, which is the other test.
        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, 80, DateTimeOffset.UtcNow.AddMinutes(-60), CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        var adapter = NewStreamAdapter(store, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var sources = await adapter.GetSourcesAsync(reloaded!, CancellationToken.None);

        Assert.Empty(sources);
        // A stale verdict must not keep MultiBrand's generic fallback suppressed after the
        // probe just failed — clear it so the fallback tiers run and the next resolution re-probes.
        var after = await store.GetDeviceAsync(device.Id, CancellationToken.None);
        Assert.False(NetSdkProbeVerdictCache.HasVerdict(after!));
    }

    [Fact]
    public async Task Adapter_ReProbes_After_Failed_Playback_Invalidation()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, 80, DateTimeOffset.UtcNow, CancellationToken.None);

        // Failed playback path (LiveStreamService) invalidates the persisted verdict.
        await NetSdkProbeVerdictCache.InvalidateAsync(store, device.Id, CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        var probes = 0;
        var adapter = NewStreamAdapter(store, _ => { probes++; return OkJson(DeviceInfoFixtureBody); });
        var sources = await adapter.GetSourcesAsync(reloaded!, CancellationToken.None);

        Assert.NotEmpty(sources);
        Assert.Equal(1, probes); // verdict was cleared → probe runs again
    }

    [Fact]
    public void Rtsp_Verdict_Keys_Stamp_Parse_And_Survive_Clear()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var device = NewDevice();
        NetSdkProbeVerdictCache.Stamp(device, 80, now);
        NetSdkProbeVerdictCache.StampRtspVerdict(device, "main", true);
        NetSdkProbeVerdictCache.StampRtspVerdict(device, "sub", false);

        Assert.True(NetSdkProbeVerdictCache.TryGetRtspVerdict(device, "main", out var mainOk));
        Assert.True(mainOk);
        Assert.True(NetSdkProbeVerdictCache.TryGetRtspVerdict(device, "sub", out var subOk));
        Assert.False(subOk);
        // Absent keys are "not recorded" (known=false → adapter backfills), and a malformed
        // value never verifies (known=true, verified=false → the path is gated out, the safe
        // direction for a credential probe).
        Assert.False(NetSdkProbeVerdictCache.TryGetRtspVerdict(device, "audio", out _));
        device.Metadata[NetSdkProbeVerdictCache.RtspMainVerifiedKey] = "garbage";
        Assert.True(NetSdkProbeVerdictCache.TryGetRtspVerdict(device, "main", out var corrupted));
        Assert.False(corrupted);

        var cleared = NetSdkProbeVerdictCache.Clear(device);
        Assert.False(NetSdkProbeVerdictCache.TryGetRtspVerdict(cleared, "main", out _));
        Assert.False(NetSdkProbeVerdictCache.TryGetRtspVerdict(cleared, "sub", out _));
    }

    [Fact]
    public async Task Adapter_Persists_Rtsp_Handshake_Results_And_Reuses_On_Cache_Hit()
    {
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        var firstDevice = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        var probes = 0;
        var handshakes = 0;
        var adapter = NewStreamAdapter(
            store,
            _ => { probes++; return OkJson(DeviceInfoFixtureBody); },
            (host, port, path, user, password, ct) =>
            {
                handshakes++;
                return Task.FromResult(path == "ch0_0.264"); // main verified, sub rejected
            });
        var sources = await adapter.GetSourcesAsync(firstDevice!, CancellationToken.None);

        Assert.Equal(1, probes);
        Assert.Equal(2, handshakes); // both paths confirmed once during the probe
        Assert.Contains(sources, s => s.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Url.EndsWith("/ch0_1.264", StringComparison.Ordinal));

        // The rtsp flags round-trip through the store alongside the verdict.
        var persisted = await store.GetDeviceAsync(device.Id, CancellationToken.None);
        Assert.True(NetSdkProbeVerdictCache.HasVerdict(persisted!));
        Assert.True(NetSdkProbeVerdictCache.TryGetRtspVerdict(persisted!, "main", out var mainOk));
        Assert.True(mainOk);
        Assert.True(NetSdkProbeVerdictCache.TryGetRtspVerdict(persisted!, "sub", out var subOk));
        Assert.False(subOk);

        // Fresh load → pure cache hit: NO probe, NO handshake, identical gate.
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);
        var cachedSources = await adapter.GetSourcesAsync(reloaded!, CancellationToken.None);
        Assert.Equal(1, probes);
        Assert.Equal(2, handshakes);
        Assert.Contains(cachedSources, s => s.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));
        Assert.DoesNotContain(cachedSources, s => s.Url.EndsWith("/ch0_1.264", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_Verdict_Without_Rtsp_Flags_Backfills_Handshake_Once()
    {
        // A verdict persisted by the previous deployment (marker + port + timestamp, no rtsp
        // confirmation keys) must NOT emit unconfirmed sources, but must also NOT re-run the
        // expensive REST probe: the cache-hit path backfills the two bounded RTSP handshakes
        // once, persists the flags, and the next resolution is a pure cache hit.
        var store = await CreateStoreAsync();
        var device = NewDevice(ip: "10.0.0.169");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        // Stamp WITHOUT rtsp flags — exactly what the pre-handshake build wrote.
        await NetSdkProbeVerdictCache.SaveVerdictAsync(store, device, 80, DateTimeOffset.UtcNow, CancellationToken.None);
        var reloaded = await store.GetDeviceAsync(device.Id, CancellationToken.None);

        var probes = 0;
        var handshakes = 0;
        var adapter = NewStreamAdapter(
            store,
            _ => { probes++; return OkJson(DeviceInfoFixtureBody); },
            (host, port, path, user, password, ct) =>
            {
                handshakes++;
                return Task.FromResult(true);
            });
        var sources = await adapter.GetSourcesAsync(reloaded!, CancellationToken.None);

        Assert.Equal(0, probes); // REST plane still trusted from the fresh verdict
        Assert.Equal(2, handshakes); // but both paths get their credential confirmation
        Assert.Contains(sources, s => s.Url.EndsWith("/ch0_0.264", StringComparison.Ordinal));

        // Flags now persisted → the next resolution is a pure cache hit (no backfill handshake).
        var after = await store.GetDeviceAsync(device.Id, CancellationToken.None);
        Assert.True(NetSdkProbeVerdictCache.TryGetRtspVerdict(after!, "main", out var mainOk));
        Assert.True(mainOk);
        var cachedSources = await adapter.GetSourcesAsync(after!, CancellationToken.None);
        Assert.Equal(0, probes);
        Assert.Equal(2, handshakes);
        Assert.NotEmpty(cachedSources);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private const string DeviceInfoFixtureBody = "{\"serial\":\"SN123456\",\"model\":\"5523-w\",\"firmware\":\"v1.0.0\",\"mac\":\"AA:BB:CC:DD:EE:FF\",\"eseeId\":\"ESEE1234\"}";

    private static DeviceIdentity NewDevice(string ip = "10.0.0.169")
        => new()
        {
            IpAddress = ip,
            Port = 80,
            LoginName = "admin",
            Password = string.Empty,
            DeviceType = "ONVIF",
            Name = "5523-W"
        };

    private static async Task<IApplicationStore> CreateStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-verdict-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static NativeNetSdkStreamAdapter NewStreamAdapter(
        IApplicationStore store,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        Func<string, int, string, string, string, CancellationToken, Task<bool>>? rtspHandshake = null)
        => new(
            Options.Create(new BossCamRuntimeOptions()),
            new StubHttpClientFactory(responder),
            NullLogger<NativeNetSdkStreamAdapter>.Instance,
            store,
            rtspHandshake: rtspHandshake ?? ((_, _, _, _, _, _) => Task.FromResult(true)));

    private static HttpResponseMessage OkJson(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(responder)) { Timeout = TimeSpan.FromSeconds(5) };

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(responder(request));
        }
    }
}
