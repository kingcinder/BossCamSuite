using System.Net;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the automatic 5523-W clock sync: TimeSync fires on device
/// registration and on every typed-settings normalization, gated to 5523-family hardware,
/// best-effort (never throws), and routed through the same ExecuteMaintenanceAsync path the
/// "Sync Camera Clock" button uses (bare-scalar RTC + timeZone PUTs proven on 5523-W firmware).
/// </summary>
public sealed class AutoTimeSyncTests
{
    // ── 1. 5523-W gate ───────────────────────────────────────────────

    [Theory]
    [InlineData("5523-W", true)]
    [InlineData("5523-w", true)]
    [InlineData("5523", true)]
    [InlineData("5523W", true)]
    [InlineData("W5C", false)]
    [InlineData("Lorex", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Is5523W_Gates_On_Hardware_Model(string? model, bool expected)
    {
        Assert.Equal(expected, SettingsService.Is5523W(new DeviceIdentity { HardwareModel = model }));
    }

    // ── 2. AutoSyncClockAsync routes through ExecuteMaintenanceAsync ──

    [Fact]
    public async Task AutoSyncClockAsync_Fires_TimeSync_For_5523W()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("5523-W");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new RecordingMaintenanceAdapter();
        var settings = BuildSettingsService(store, adapter);

        await settings.AutoSyncClockAsync(device, CancellationToken.None);

        var call = Assert.Single(adapter.MaintenanceCalls);
        Assert.Equal(MaintenanceOperation.TimeSync, call.Operation);
        // Mirrors the SPA/desktop '{}' maintenance body so the endpoint stays well-formed.
        Assert.NotNull(call.Payload);
        Assert.Empty(call.Payload!);
    }

    [Fact]
    public async Task AutoSyncClockAsync_Skips_Non_5523_Device()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("W5C");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new RecordingMaintenanceAdapter();
        var settings = BuildSettingsService(store, adapter);

        await settings.AutoSyncClockAsync(device, CancellationToken.None);

        Assert.Empty(adapter.MaintenanceCalls);
    }

    [Fact]
    public async Task AutoSyncClockAsync_Respects_Per_Device_Cooldown()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("5523-W");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new RecordingMaintenanceAdapter();
        var settings = BuildSettingsService(store, adapter);

        await settings.AutoSyncClockAsync(device, CancellationToken.None);
        await settings.AutoSyncClockAsync(device, CancellationToken.None);

        // NormalizeDeviceAsync is a hot path (image-truth sweeps re-normalize per field read); the
        // cooldown must collapse repeated attempts into a single TimeSync write.
        Assert.Single(adapter.MaintenanceCalls);
    }

    [Fact]
    public async Task AutoSyncClockAsync_Never_Throws_When_Adapter_Fails()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("5523-W");
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var settings = BuildSettingsService(store, new ThrowingMaintenanceAdapter());

        // The camera/relay being down must never bubble out of the auto-sync hook.
        await settings.AutoSyncClockAsync(device, CancellationToken.None);
    }

    // ── 3. Registration hook ──────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_Triggers_Auto_TimeSync_For_5523W()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var recording = new RecordingMaintenanceAdapter();
        var handler = new ScriptedHandler(uri => uri.AbsolutePath.Contains("deviceInfo", StringComparison.Ordinal)
            ? DeviceInfoOk("5523-W")
            : MaintenanceOk());
        var registration = new DeviceRegistrationService(
            store,
            new HandlerBackedFactory(handler),
            new CapabilityProbeService([], store, NullBossCamEventBroadcaster.Instance, NullLogger<CapabilityProbeService>.Instance),
            BuildSettingsService(store, recording),
            Options.Create(new BossCamRuntimeOptions()),
            NullLogger<DeviceRegistrationService>.Instance);

        var registered = await registration.RegisterAsync(
            new DeviceRegisterRequest { IpAddress = "10.0.0.29", Port = 80, LoginName = "admin", Password = "" },
            CancellationToken.None);

        Assert.Equal("5523-W", registered.HardwareModel);
        var call = Assert.Single(recording.MaintenanceCalls);
        Assert.Equal(MaintenanceOperation.TimeSync, call.Operation);
    }

    [Fact]
    public async Task RegisterAsync_Does_Not_TimeSync_Non_5523_Device()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var recording = new RecordingMaintenanceAdapter();
        var handler = new ScriptedHandler(uri => uri.AbsolutePath.Contains("deviceInfo", StringComparison.Ordinal)
            ? DeviceInfoOk("W5C")
            : MaintenanceOk());
        var registration = new DeviceRegistrationService(
            store,
            new HandlerBackedFactory(handler),
            new CapabilityProbeService([], store, NullBossCamEventBroadcaster.Instance, NullLogger<CapabilityProbeService>.Instance),
            BuildSettingsService(store, recording),
            Options.Create(new BossCamRuntimeOptions()),
            NullLogger<DeviceRegistrationService>.Instance);

        var registered = await registration.RegisterAsync(
            new DeviceRegisterRequest { IpAddress = "10.0.0.30", Port = 80, LoginName = "admin", Password = "" },
            CancellationToken.None);

        Assert.Equal("W5C", registered.HardwareModel);
        Assert.Empty(recording.MaintenanceCalls);
    }

    // ── 4. Normalize hook ─────────────────────────────────────────────

    [Fact]
    public async Task NormalizeDeviceAsync_Triggers_Auto_TimeSync_For_5523W()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("5523-W");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveSettingsSnapshotAsync(new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "Fake",
            Groups = []
        }, CancellationToken.None);

        var adapter = new RecordingMaintenanceAdapter();
        var settings = BuildSettingsService(store, adapter);
        var contracts = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, contracts, settings, NullLogger<SemanticTrustService>.Instance);
        var typed = new TypedSettingsService(
            settings,
            new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance),
            trust,
            contracts,
            new CapabilityPromotionService(store, contracts),
            NullLogger<TypedSettingsService>.Instance,
            new ApplicationStoreTypedControlStore(store));

        _ = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);

        var call = Assert.Single(adapter.MaintenanceCalls);
        Assert.Equal(MaintenanceOperation.TimeSync, call.Operation);
    }

    [Fact]
    public async Task NormalizeDeviceAsync_Does_Not_TimeSync_Non_5523_Device()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = NewDevice("W5C");
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveSettingsSnapshotAsync(new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "Fake",
            Groups = []
        }, CancellationToken.None);

        var adapter = new RecordingMaintenanceAdapter();
        var settings = BuildSettingsService(store, adapter);
        var contracts = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, contracts, settings, NullLogger<SemanticTrustService>.Instance);
        var typed = new TypedSettingsService(
            settings,
            new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance),
            trust,
            contracts,
            new CapabilityPromotionService(store, contracts),
            NullLogger<TypedSettingsService>.Instance,
            new ApplicationStoreTypedControlStore(store));

        _ = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);

        Assert.Empty(adapter.MaintenanceCalls);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"bosscam-autosync-{Guid.NewGuid():N}.db")
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

    private static DeviceIdentity NewDevice(string hardwareModel) => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = "10.0.0.29",
        Port = 80,
        LoginName = "admin",
        Password = string.Empty,
        Name = "auto-sync-test",
        HardwareModel = hardwareModel,
        DeviceType = "IPC"
    };

    private static HttpResponseMessage DeviceInfoOk(string model) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""
            {"deviceName":"cam","model":"{{model}}","serialNumber":"SN-{{model}}","macAddress":"AA:BB:CC:DD:EE:FF","firmwareVersion":"3.6.60"}
            """, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage MaintenanceOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"statusCode\":0}", System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class RecordingMaintenanceAdapter : IControlAdapter
    {
        public List<(MaintenanceOperation Operation, JsonObject? Payload)> MaintenanceCalls { get; } = [];
        public string Name => "Recording";
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
            MaintenanceCalls.Add((operation, payload?.DeepClone() as JsonObject));
            return Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
        }
    }

    /// <summary>Adapter whose maintenance path throws — proves the auto-sync hook swallows it.</summary>
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

    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request.RequestUri!));
    }

    private sealed class HandlerBackedFactory(ScriptedHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
