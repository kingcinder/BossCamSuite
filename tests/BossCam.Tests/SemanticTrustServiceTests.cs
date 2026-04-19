using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class SemanticTrustServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-semantic-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public SemanticTrustServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Classifies_Clamped_Write()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var settings = BuildSettingsService(store, [new NoopAdapter()]);
        var catalog = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, catalog, settings, NullLogger<SemanticTrustService>.Instance);
        var contracts = await catalog.GetContractsAsync(CancellationToken.None);
        var videoContract = contracts.First(contract => contract.ContractKey == "video.input.channel.0");
        var bitrate = videoContract.Fields.First(field => field.Key == "bitrate");

        var status = trust.Classify(
            new WriteResult { Success = true, PostReadVerified = true },
            JsonValue.Create(4096),
            JsonValue.Create(1024),
            JsonValue.Create(3072),
            null,
            null,
            bitrate);

        Assert.Equal(SemanticWriteStatus.AcceptedClamped, status);
    }

    [Fact]
    public async Task Captures_Observation_And_Constraint_Profile()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveNormalizedSettingFieldsAsync(
        [
            new NormalizedSettingField
            {
                DeviceId = device.Id,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                FieldKey = "bitrate",
                DisplayName = "Bitrate",
                AdapterName = "Fake",
                SourceEndpoint = "/NetSDK/Video/input/channel/0",
                ContractKey = "video.input.channel.0",
                TypedValue = JsonValue.Create(1024),
                FirmwareFingerprint = "5523|1.0.0|5523-w"
            }
        ], CancellationToken.None);

        var settings = BuildSettingsService(store, [new NoopAdapter()]);
        var catalog = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, catalog, settings, NullLogger<SemanticTrustService>.Instance);
        var contract = (await catalog.GetContractsForDeviceAsync(device, CancellationToken.None)).First(item => item.ContractKey == "video.input.channel.0");
        var field = contract.Fields.First(item => item.Key == "bitrate");

        var observation = await trust.CaptureObservationAsync(
            device.Id,
            contract,
            field,
            new WriteResult { Success = true, PostReadVerified = true },
            JsonValue.Create(2048),
            JsonValue.Create(1024),
            JsonValue.Create(2048),
            JsonValue.Create(2048),
            null,
            new JsonObject { ["codec"] = "H264", ["resolution"] = "1920x1080" },
            CancellationToken.None);

        Assert.Equal(SemanticWriteStatus.AcceptedPersistedAfterDelay, observation.Status);
        var constraints = await store.GetFieldConstraintProfilesAsync("5523|1.0.0|5523-w", CancellationToken.None);
        Assert.Contains(constraints, item => item.FieldKey == "bitrate" && item.SupportedValues.Contains("2048"));
    }

    [Fact]
    public async Task Recovers_Network_When_Predicted_Url_Is_Reachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var settings = BuildSettingsService(store, [new NoopAdapter()]);
        var trust = new SemanticTrustService(store, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), settings, NullLogger<SemanticTrustService>.Instance);

        var result = await trust.RecoverNetworkAsync(new NetworkRecoveryContext
        {
            DeviceId = Guid.NewGuid(),
            PredictedControlUrl = $"http://127.0.0.1:{port}"
        }, CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.Equal($"http://127.0.0.1:{port}", result.ReachableUrl);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_tempDirectory, true);
        }
        catch
        {
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static SettingsService BuildSettingsService(IApplicationStore store, IEnumerable<IControlAdapter> adapters)
    {
        var validation = new ProtocolValidationService(adapters, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), store, NullLogger<ProtocolValidationService>.Instance);
        return new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
    }

    private sealed class NoopAdapter : IControlAdapter
    {
        public string Name => "Fake";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;
        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });
        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name });
        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken) => ReadAsync(device, cancellationToken);
        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken) => Task.FromResult(new WriteResult { Success = true, AdapterName = Name, Response = plan.Payload });
        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken) => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }
}
