using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class ImageTruthClassificationTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-image-truth-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _originalCwd;

    public ImageTruthClassificationTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(_tempDirectory);
        Directory.SetCurrentDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Live_Evidence_Prefers_Private_And_LikelyUnsupported_Over_ReadableOnly()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity
        {
            Name = "cam",
            DeviceType = "5523-w",
            IpAddress = "10.0.0.4",
            HardwareModel = "5523",
            FirmwareVersion = "1.0.0"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveSettingsSnapshotAsync(new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "Fake",
            Groups =
            [
                new SettingGroup
                {
                    Name = "Video",
                    DisplayName = "Video",
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["/NetSDK/Image"] = new()
                        {
                            SourceEndpoint = "/NetSDK/Image",
                            Value = JsonNode.Parse("{\"irCutFilter\":{\"irCutMode\":\"light\"},\"awbMode\":\"indoor\",\"exposureMode\":\"auto\",\"denoise3d\":{\"denoise3dStrength\":3},\"WDR\":{\"enabled\":true}}")
                        },
                        ["/NetSDK/Video/encode/channel/101/properties"] = new()
                        {
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                            Value = JsonNode.Parse("{\"codecType\":\"H.264\",\"h264Profile\":\"main\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024,\"frameRate\":20,\"keyFrameInterval\":25}")
                        }
                    }
                }
            ]
        }, CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Image",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = false
            },
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Video/encode/channel/101/properties",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = false
            }
        ], CancellationToken.None);

        var evidenceDir = Path.Combine(_tempDirectory, "artifacts", "5523w");
        Directory.CreateDirectory(evidenceDir);
        var evidenceRows = new JsonArray
        {
            new JsonObject
            {
                ["ip"] = "10.0.0.4",
                ["field"] = "whiteLight",
                ["phase"] = "read",
                ["readable"] = true,
                ["status"] = "ReadProbe",
                ["endpoint"] = "/NetSDK/Factory?cmd=WhiteLightCtrl",
                ["method"] = "GET",
                ["classification"] = "ReadableOnly",
                ["reasonCode"] = "live_read_probe"
            },
            new JsonObject
            {
                ["ip"] = "10.0.0.4",
                ["field"] = "whiteLight",
                ["phase"] = "write",
                ["readable"] = true,
                ["status"] = "AcceptedNoSemanticChange",
                ["endpoint"] = "/NetSDK/Factory?cmd=WhiteLightCtrl",
                ["method"] = "PUT",
                ["classification"] = "PrivatePathCandidate",
                ["reasonCode"] = "private_path_candidate"
            },
            new JsonObject
            {
                ["ip"] = "10.0.0.4",
                ["field"] = "codec",
                ["phase"] = "read",
                ["readable"] = true,
                ["status"] = "ReadProbe",
                ["endpoint"] = "/NetSDK/Video/encode/channel/101/properties",
                ["method"] = "GET",
                ["classification"] = "ReadableOnly",
                ["reasonCode"] = "live_read_probe"
            },
            new JsonObject
            {
                ["ip"] = "10.0.0.4",
                ["field"] = "codec",
                ["phase"] = "write",
                ["readable"] = true,
                ["status"] = "Rejected",
                ["endpoint"] = "/NetSDK/Video/encode/channel/101/properties",
                ["method"] = "PUT",
                ["classification"] = "LikelyUnsupported",
                ["reasonCode"] = "repeated_live_failure_incomplete_evidence"
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDir, "live-image-targeted-semantic-test.json"),
            evidenceRows.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        var adapters = new IControlAdapter[] { new NoopControlAdapter() };
        var catalog = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);
        var validation = new ProtocolValidationService(adapters, catalog, store, NullLogger<ProtocolValidationService>.Instance);
        var settings = new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
        var persistence = new PersistenceVerificationService(adapters, store, NullLogger<PersistenceVerificationService>.Instance);
        var semantic = new SemanticTrustService(store, catalog, settings, NullLogger<SemanticTrustService>.Instance);
        var typed = new TypedSettingsService(store, settings, persistence, semantic, catalog, NullLogger<TypedSettingsService>.Instance);
        var imageTruth = new ImageTruthService(store, typed, catalog, NullLogger<ImageTruthService>.Instance);

        var inventory = await imageTruth.DiscoverInventoryAsync(device.Id, refreshFromDevice: false, CancellationToken.None);
        var whiteLight = inventory.First(item => item.FieldKey.Equals("whiteLight", StringComparison.OrdinalIgnoreCase));
        var codec = inventory.First(item => item.FieldKey.Equals("codec", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(HiddenCandidateClassification.PrivatePathCandidate, whiteLight.CandidateClassification);
        Assert.False(whiteLight.PromotedToUi);
        Assert.Equal(HiddenCandidateClassification.LikelyUnsupported, codec.CandidateClassification);
        Assert.False(codec.PromotedToUi);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
            }
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private sealed class NoopControlAdapter : IControlAdapter
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
