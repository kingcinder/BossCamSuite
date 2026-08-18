using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Control;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for <c>MaintenanceOperation.ClockVerify</c>: the pass probes each
/// 5523-W's /NetSDK/System/time/rtc (bare unix-seconds int) + /timeZone (bare GMT string),
/// runs TimeSync (bare-scalar PUTs), re-reads both, and confirms the OSD epoch is within
/// BossCam:ClockVerifyToleranceSeconds of the host epoch. Covers the adapter wire sequence,
/// drift/tolerance semantics, timezone-match enforcement, and the service-level fleet sweep.
/// </summary>
public sealed class ClockVerificationTests
{
    private static string HostTzString => HttpControlAdapterBase.BuildGmtOffsetString(
        TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow));

    // ── 1. Adapter-level wire sequence ───────────────────────────────

    [Fact]
    public async Task ClockVerify_Probes_Syncs_And_Confirms_Epoch_Within_Tolerance()
    {
        var hostEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hostTz = HostTzString;
        var rtcGetCount = 0;
        var handler = new BodyRecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var isGet = request.Method == HttpMethod.Get;
            var isRtc = path.EndsWith("/time/rtc", StringComparison.Ordinal);

            if (isGet && isRtc)
            {
                // First GET = BEFORE probe (stale, a day in the past); second GET = AFTER probe
                // (synced to the host epoch — within the 30s tolerance).
                var stale = rtcGetCount == 0;
                rtcGetCount++;
                return BareInt(stale ? hostEpoch - 86_400 : hostEpoch + 1);
            }
            if (isGet)
            {
                return BareString(hostTz);
            }
            return StatusCodeZero(); // PUT rtc / PUT timeZone
        });

        var adapter = NewAdapter(handler);
        var device = NewDevice(port: 80);

        var result = await adapter.ExecuteMaintenanceAsync(
            device, MaintenanceOperation.ClockVerify, payload: null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(MaintenanceOperation.ClockVerify, result.Operation);

        // Exactly six requests in order: GET rtc, GET timeZone, PUT rtc, PUT timeZone,
        // GET rtc, GET timeZone.
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(
            new[]
            {
                "/NetSDK/System/time/rtc", "/NetSDK/System/time/timeZone",
                "/NetSDK/System/time/rtc", "/NetSDK/System/time/timeZone",
                "/NetSDK/System/time/rtc", "/NetSDK/System/time/timeZone"
            },
            handler.Requests.Select(r => r.Uri.AbsolutePath));

        // The structured report must carry the before/after epochs and the computed drift.
        var report = Assert.IsType<JsonObject>(result.Response);
        var drift = report["driftSeconds"]!.GetValue<long>();
        Assert.Equal(hostEpoch - 86_400, report["rtcBefore"]!.GetValue<long>());
        Assert.Equal(hostEpoch + 1, report["rtcAfter"]!.GetValue<long>());
        Assert.InRange(drift, 0, 2); // after-read is hostEpoch+1, hostEpoch captured milliseconds earlier
        Assert.Equal(30, report["toleranceSeconds"]!.GetValue<long>());
        Assert.Equal(hostTz, report["timeZoneAfter"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClockVerify_Fails_When_Osd_Epoch_Stays_Day_Into_Future()
    {
        var hostEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hostTz = HostTzString;
        var handler = new BodyRecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var isGet = request.Method == HttpMethod.Get;
            if (isGet && path.EndsWith("/time/rtc", StringComparison.Ordinal))
            {
                // The exact symptom reported: the OSD clock stays ~a day into the future even
                // after the sync claims success. Drift must blow past the tolerance.
                return BareInt(hostEpoch + 86_400);
            }
            if (isGet)
            {
                return BareString(hostTz);
            }
            return StatusCodeZero();
        });

        var adapter = NewAdapter(handler);
        var result = await adapter.ExecuteMaintenanceAsync(
            NewDevice(port: 80), MaintenanceOperation.ClockVerify, payload: null, CancellationToken.None);

        Assert.False(result.Success);
        var report = (JsonObject)result.Response!;
        Assert.False(report["inSync"]!.GetValue<bool>());
        Assert.True(report["driftSeconds"]!.GetValue<long>() > 30);
        Assert.Contains("drift", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClockVerify_Reports_Timezone_Mismatch_As_Diagnostic_Not_Failure()
    {
        var hostEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // A zone guaranteed different from the host's so the test cannot flip on a machine
        // whose local timezone happens to be GMT+13:45 (Tonga).
        var otherTz = HostTzString == "GMT+13:45" ? "GMT-13:45" : "GMT+13:45";
        var handler = new BodyRecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var isGet = request.Method == HttpMethod.Get;
            if (isGet && path.EndsWith("/time/rtc", StringComparison.Ordinal))
            {
                return BareInt(hostEpoch + 1); // epoch is fine…
            }
            if (isGet)
            {
                return BareString(otherTz); // …but the camera zone does not match the host.
            }
            return StatusCodeZero();
        });

        var adapter = NewAdapter(handler);
        var result = await adapter.ExecuteMaintenanceAsync(
            NewDevice(port: 80), MaintenanceOperation.ClockVerify, payload: null, CancellationToken.None);

        // The OSD epoch is the confirmation criterion — it is in sync, so the pass SUCCEEDS.
        // The zone mismatch is surfaced as a diagnostic in the report and message, not a failure
        // (a camera may legitimately normalize "GMT+8" vs "GMT+08:00").
        Assert.True(result.Success, result.Message);
        var report = (JsonObject)result.Response!;
        Assert.False(report["tzMatchesHost"]!.GetValue<bool>());
        Assert.Contains("mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClockVerify_Fails_When_ReRead_Is_Gated_Or_Empty()
    {
        // The camera answers the BEFORE probe but its AFTER rtc read returns an empty body — the
        // exact gated/blank-GET failure mode on this firmware. A null re-read means the drift is
        // unknown, so the pass must report failure rather than assume the sync landed.
        var hostEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hostTz = HostTzString;
        var rtcGetCount = 0;
        var handler = new BodyRecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var isGet = request.Method == HttpMethod.Get;
            if (isGet && path.EndsWith("/time/rtc", StringComparison.Ordinal))
            {
                // First GET (before) returns a stale epoch; second GET (after) returns empty.
                var stale = rtcGetCount == 0;
                rtcGetCount++;
                return stale ? BareInt(hostEpoch - 86_400) : EmptyOk();
            }
            if (isGet)
            {
                return BareString(hostTz);
            }
            return StatusCodeZero();
        });

        var adapter = NewAdapter(handler);
        var result = await adapter.ExecuteMaintenanceAsync(
            NewDevice(port: 80), MaintenanceOperation.ClockVerify, payload: null, CancellationToken.None);

        Assert.False(result.Success);
        var report = (JsonObject)result.Response!;
        Assert.False(report["inSync"]!.GetValue<bool>());
        Assert.Null(report["rtcAfter"]);
    }

    [Fact]
    public async Task ClockVerify_Fails_When_Sync_Write_Is_Rejected()
    {
        // The camera rejects the rtc PUT with statusCode 6 (Invalid Document) — the sync
        // reports failure, so verification must NOT claim the OSD clock is confirmed.
        var hostEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var handler = new BodyRecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/time/rtc", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"statusCode\":6,\"message\":\"Invalid Document\"}", Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/time/rtc", StringComparison.Ordinal))
            {
                return BareInt(hostEpoch + 1);
            }
            if (request.Method == HttpMethod.Get)
            {
                return BareString(HostTzString);
            }
            return StatusCodeZero();
        });

        var adapter = NewAdapter(handler);
        var result = await adapter.ExecuteMaintenanceAsync(
            NewDevice(port: 80), MaintenanceOperation.ClockVerify, payload: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        var report = (JsonObject)result.Response!;
        Assert.False(report["syncSucceeded"]!.GetValue<bool>());
        Assert.False(report["inSync"]!.GetValue<bool>());
    }

    // ── 2. Service-level ─────────────────────────────────────────────

    [Fact]
    public async Task VerifyClockAsync_Returns_Structured_Result_For_5523W()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("5523-W");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new ReportMaintenanceAdapter();
        var settings = BuildSettingsService(store, adapter);

        var result = await settings.VerifyClockAsync(device.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(device.Id, result.DeviceId);
        Assert.Equal("5523-W", result.DeviceName);
        Assert.Equal(2, result.DriftSeconds);
        Assert.Equal(30, result.ToleranceSeconds);
        Assert.Equal(1_700_000_000, result.RtcBefore);
        Assert.Equal(1_700_000_012, result.RtcAfter);
        Assert.True(result.TimeZoneMatchesHost);
    }

    [Fact]
    public async Task VerifyClockAsync_Returns_Null_For_Unknown_Device()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var settings = BuildSettingsService(store, new ReportMaintenanceAdapter());

        var result = await settings.VerifyClockAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyClockAsync_Never_Throws_When_Adapter_Fails()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("5523-W");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var settings = BuildSettingsService(store, new ThrowingMaintenanceAdapter());

        var result = await settings.VerifyClockAsync(device.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task VerifyAll5523ClocksAsync_Checks_Only_5523_W_Devices()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        // Distinct IPs: the store merges identities by MAC/IP key, so seeding three rows on the
        // same 127.0.0.1 would collapse them (and the W5C last-write would win).
        var cam1 = NewDevice("5523-W", ip: "10.0.0.31");
        var cam2 = NewDevice("5523-W", ip: "10.0.0.32");
        var cam3 = NewDevice("W5C", ip: "10.0.0.33");
        await store.UpsertDevicesAsync([cam1, cam2, cam3], CancellationToken.None);

        var settings = BuildSettingsService(store, new ReportMaintenanceAdapter());

        var report = await settings.VerifyAll5523ClocksAsync(CancellationToken.None);

        Assert.Equal(2, report.DevicesChecked); // only the 5523-W pair
        Assert.Equal(2, report.DevicesVerified);
        Assert.Equal(0, report.DevicesFailed);
        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, result => Assert.True(result.Success));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"bosscam-clockverify-{Guid.NewGuid():N}.db")
        }));

    private static SettingsService BuildSettingsService(SqliteApplicationStore store, IControlAdapter adapter)
    {
        var adapters = new IControlAdapter[] { adapter };
        var validation = new ProtocolValidationService(
            adapters,
            new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance),
            store,
            NullLogger<ProtocolValidationService>.Instance);
        return new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
    }

    private static LanDirectNetSdkRestAdapter NewAdapter(BodyRecordingHandler handler)
        => new(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 8, ClockVerifyToleranceSeconds = 30 }),
            new HandlerBackedFactory(handler),
            store: null!, // ClockVerify → SendRawAsync only; the store is never consulted
            NullLogger<LanDirectNetSdkRestAdapter>.Instance);

    private static DeviceIdentity NewDevice(string hardwareModel = "5523-W", int port = 80, string ip = "127.0.0.1") => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = ip,
        Port = port,
        LoginName = "admin",
        Password = "secret",
        Name = hardwareModel,
        HardwareModel = hardwareModel,
        DeviceType = "IPC"
    };

    private static HttpResponseMessage BareInt(long value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BareString(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"\"{value}\"", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage StatusCodeZero() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"statusCode\":0}", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage EmptyOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
    };

    private sealed record RecordedRequest(Uri Uri, string Body);

    private sealed class BodyRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, body));
            return responder(request);
        }
    }

    private sealed class HandlerBackedFactory(BodyRecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    /// <summary>Adapter whose ClockVerify returns a structured in-sync report (drift 2s ≤ 30s).</summary>
    private sealed class ReportMaintenanceAdapter : IControlAdapter
    {
        public string Name => "Report";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;
        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });
        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => SnapshotAsync(device, cancellationToken);
        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name });
        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
            => Task.FromResult(new WriteResult { Success = true, AdapterName = Name, StatusCode = 200 });
        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
        {
            var report = new JsonObject
            {
                ["rtcBefore"] = JsonValue.Create(1_700_000_000L),
                ["rtcAfter"] = JsonValue.Create(1_700_000_012L),
                ["hostEpoch"] = JsonValue.Create(1_700_000_010L),
                ["driftSeconds"] = JsonValue.Create(2L),
                ["toleranceSeconds"] = JsonValue.Create(30L),
                ["timeZoneBefore"] = JsonValue.Create("GMT+08:00"),
                ["timeZoneAfter"] = JsonValue.Create("GMT+08:00"),
                ["hostTimeZone"] = JsonValue.Create("GMT+08:00"),
                ["syncSucceeded"] = JsonValue.Create(true),
                ["tzMatchesHost"] = JsonValue.Create(true),
                ["inSync"] = JsonValue.Create(true)
            };
            return Task.FromResult(new MaintenanceResult
            {
                Success = true,
                AdapterName = Name,
                Operation = operation,
                Response = report,
                Message = "rtc in sync (drift 2s ≤ 30s)."
            });
        }
    }

    /// <summary>Adapter whose maintenance path throws — proves the verify pass swallows it.</summary>
    private sealed class ThrowingMaintenanceAdapter : IControlAdapter
    {
        public string Name => "Throwing";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;
        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });
        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => SnapshotAsync(device, cancellationToken);
        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name });
        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
            => Task.FromResult(new WriteResult { Success = true, AdapterName = Name });
        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("camera unreachable");
    }
}
