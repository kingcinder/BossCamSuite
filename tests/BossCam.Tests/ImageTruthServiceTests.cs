using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class ImageTruthServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-image-truth-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public ImageTruthServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Runs_Image_Truth_Sweep_And_Persists_Inventory_And_Behavior()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Name = "cam",
            DeviceType = "5523-w",
            IpAddress = "10.0.0.4",
            HardwareModel = "5523",
            FirmwareVersion = "1.0.0",
            LoginName = "admin",
            Password = "admin"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Stateful",
                Endpoint = "/NetSDK/Video/input/channel/0",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = true,
                PersistsAfterReboot = true,
                FirmwareFingerprint = "5523|1.0.0|5523-w"
            }
        ], CancellationToken.None);

        var adapter = new StatefulImageAdapter();
        var settings = BuildSettingsService(store, [adapter]);
        var contracts = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, contracts, settings, NullLogger<SemanticTrustService>.Instance);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance), trust, contracts, NullLogger<TypedSettingsService>.Instance);
        var imageTruth = new ImageTruthService(store, typed, contracts, NullLogger<ImageTruthService>.Instance);

        var result = await imageTruth.RunImageTruthSweepAsync(device.Id, includeBehaviorMapping: true, refreshFromDevice: true, _tempDirectory, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result!.Inventory, item => item.FieldKey == "brightness");
        Assert.Contains(result.WritableTestSet, item => item.FieldKey == "brightness");
        Assert.Contains(result.BehaviorMaps, item => item.FieldKey == "brightness");

        var persistedInventory = await store.GetImageControlInventoryAsync(device.Id, CancellationToken.None);
        var persistedMaps = await store.GetImageBehaviorMapsAsync(device.Id, CancellationToken.None);
        var persistedTestSet = await store.GetImageWritableTestSetAsync(device.Id, CancellationToken.None);
        Assert.NotEmpty(persistedInventory);
        Assert.NotEmpty(persistedMaps);
        Assert.NotNull(persistedTestSet);
    }

    [Fact]
    public async Task Does_Not_Label_Unsupported_When_LiveAuth_And_Semantic_Proof_Are_Missing()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Name = "cam-no-auth-proof",
            DeviceType = "5523-w",
            IpAddress = "10.0.0.29",
            HardwareModel = "5523",
            FirmwareVersion = "1.0.0",
            LoginName = "admin",
            Password = "admin"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new StatefulImageAdapter();
        var settings = BuildSettingsService(store, [adapter]);
        var contracts = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, contracts, settings, NullLogger<SemanticTrustService>.Instance);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance), trust, contracts, NullLogger<TypedSettingsService>.Instance);
        var imageTruth = new ImageTruthService(store, typed, contracts, NullLogger<ImageTruthService>.Instance);

        var inventory = await imageTruth.DiscoverInventoryAsync(device.Id, refreshFromDevice: true, CancellationToken.None);

        Assert.NotEmpty(inventory);
        Assert.DoesNotContain(inventory, item => item.CandidateClassification == HiddenCandidateClassification.UnsupportedOnFirmware);
        Assert.Contains(inventory, item => item.CandidateClassification == HiddenCandidateClassification.NoSemanticProof
                                            || item.CandidateClassification == HiddenCandidateClassification.HiddenAdjacentCandidate
                                            || item.CandidateClassification == HiddenCandidateClassification.PrivatePathCandidate);
    }

    [Fact]
    public async Task Uses_LikelyUnsupported_For_Rejections_With_Incomplete_Evidence()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity
        {
            Name = "cam-likely-unsupported",
            DeviceType = "5523-w",
            IpAddress = "10.0.0.227",
            HardwareModel = "5523",
            FirmwareVersion = "1.0.0"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveSemanticWriteObservationsAsync(
        [
            new SemanticWriteObservation
            {
                DeviceId = device.Id,
                FieldKey = "gamma",
                Endpoint = "/NetSDK/Image/0",
                ContractKey = "video-image.image",
                Status = SemanticWriteStatus.Rejected,
                Notes = "400 bad request"
            },
            new SemanticWriteObservation
            {
                DeviceId = device.Id,
                FieldKey = "gamma",
                Endpoint = "/NetSDK/Image/0",
                ContractKey = "video-image.image",
                Status = SemanticWriteStatus.TransportFailed,
                Notes = "timeout"
            }
        ], CancellationToken.None);

        var adapter = new StatefulImageAdapter();
        var settings = BuildSettingsService(store, [adapter]);
        var contracts = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var trust = new SemanticTrustService(store, contracts, settings, NullLogger<SemanticTrustService>.Instance);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance), trust, contracts, NullLogger<TypedSettingsService>.Instance);
        var imageTruth = new ImageTruthService(store, typed, contracts, NullLogger<ImageTruthService>.Instance);

        var inventory = await imageTruth.DiscoverInventoryAsync(device.Id, refreshFromDevice: true, CancellationToken.None);
        var gamma = inventory.FirstOrDefault(item => item.FieldKey.Equals("gamma", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(gamma);
        Assert.Equal(HiddenCandidateClassification.LikelyUnsupported, gamma!.CandidateClassification);
        Assert.DoesNotContain("no_semantic_proof", gamma.ReasonCodes, StringComparer.OrdinalIgnoreCase);
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

    private sealed class StatefulImageAdapter : IControlAdapter
    {
        private readonly JsonObject _video = JsonNode.Parse("{\"codec\":\"H264\",\"profile\":\"Main\",\"resolution\":\"1920x1080\",\"bitrate\":1024,\"frameRate\":20,\"gop\":25,\"brightnessLevel\":50,\"contrastLevel\":50,\"saturationLevel\":50,\"sharpnessLevel\":50}")!.AsObject();
        private readonly JsonObject _image = JsonNode.Parse("{\"brightness\":50,\"contrast\":50,\"saturation\":50,\"hue\":50,\"sharpness\":50,\"denoise\":20,\"wdr\":\"Off\",\"dayNight\":\"Auto\",\"irCut\":\"Auto\"}")!.AsObject();

        public string Name => "Stateful";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;

        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(new CapabilityMap { DeviceId = device.Id });

        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(Snapshot(device.Id));

        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(Snapshot(device.Id));

        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
        {
            if (plan.Endpoint.Contains("/Image/", StringComparison.OrdinalIgnoreCase) && plan.Payload is not null)
            {
                MergeInto(_image, plan.Payload);
            }
            if (plan.Endpoint.Contains("/Video/input/channel", StringComparison.OrdinalIgnoreCase) && plan.Payload is not null)
            {
                MergeInto(_video, plan.Payload);
            }

            return Task.FromResult(new WriteResult { Success = true, AdapterName = Name, Response = plan.Payload?.DeepClone() });
        }

        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });

        private SettingsSnapshot Snapshot(Guid deviceId)
            => new()
            {
                DeviceId = deviceId,
                AdapterName = Name,
                Groups =
                [
                    new SettingGroup
                    {
                        Name = "VideoImage",
                        DisplayName = "Video / Image",
                        Values = new Dictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["/NetSDK/Video/input/channel/0"] = new() { Key = "/NetSDK/Video/input/channel/0", SourceEndpoint = "/NetSDK/Video/input/channel/0", Value = _video.DeepClone() },
                            ["/NetSDK/Image/0"] = new() { Key = "/NetSDK/Image/0", SourceEndpoint = "/NetSDK/Image/0", Value = _image.DeepClone() }
                        }
                    }
                ]
            };

        private static void MergeInto(JsonObject target, JsonObject update)
        {
            foreach (var kvp in update)
            {
                target[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
    }
}
