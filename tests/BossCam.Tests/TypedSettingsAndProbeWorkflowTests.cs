using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class TypedSettingsAndProbeWorkflowTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public TypedSettingsAndProbeWorkflowTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Normalizes_Typed_Settings_From_Snapshot_And_Validation()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.4", Name = "cam1", DeviceType = "5523-w", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Video/encode/channel/101/properties",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = true,
                DisruptionClass = DisruptionClass.Safe
            }
        ], CancellationToken.None);

        var snapshot = new SettingsSnapshot
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
                        ["/NetSDK/Video/input/channel/1"] = new()
                        {
                            Key = "/NetSDK/Video/input/channel/1",
                            SourceEndpoint = "/NetSDK/Video/input/channel/1",
                            Value = JsonNode.Parse("{\"id\":1,\"enabled\":true,\"brightnessLevel\":50,\"contrastLevel\":50,\"saturationLevel\":50,\"sharpnessLevel\":50,\"hueLevel\":50}")
                        },
                        ["/NetSDK/Video/encode/channel/101/properties"] = new()
                        {
                            Key = "/NetSDK/Video/encode/channel/101/properties",
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                            Value = JsonNode.Parse("{\"codecType\":\"H.264\",\"h264Profile\":\"main\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024,\"frameRate\":20,\"keyFrameInterval\":25}")
                        }
                    }
                }
            ]
        };
        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var settingsService = BuildSettingsService(store, [new NoopControlAdapter()]);
        var typed = new TypedSettingsService(store, settingsService, new PersistenceVerificationService([new NoopControlAdapter()], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, BuildContractCatalog(store), settingsService, NullLogger<SemanticTrustService>.Instance), BuildContractCatalog(store), NullLogger<TypedSettingsService>.Instance);
        var groups = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);

        Assert.Contains(groups, group => group.GroupKind == TypedSettingGroupKind.VideoImage);
        var fields = groups.SelectMany(static group => group.Fields).ToList();
        Assert.Contains(fields, field => field.FieldKey == "codec" && field.WriteVerified);
        Assert.Contains(fields, field => field.FieldKey == "bitrate");
        Assert.Contains(fields, field => field.FieldKey == "brightness");
    }

    [Fact]
    public async Task Blocks_Unverified_Typed_Field_Write_Without_Expert_Override()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.29", Name = "cam2", DeviceType = "5523-w", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveNormalizedSettingFieldsAsync(
        [
            new NormalizedSettingField
            {
                DeviceId = device.Id,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                FieldKey = "ip",
                DisplayName = "IP",
                AdapterName = "Fake",
                SourceEndpoint = "/NetSDK/Network/interfaces",
                TypedValue = JsonValue.Create("10.0.0.29"),
                ReadVerified = true,
                WriteVerified = false
            }
        ], CancellationToken.None);

        var settingsService = BuildSettingsService(store, [new NoopControlAdapter()]);
        var typed = new TypedSettingsService(store, settingsService, new PersistenceVerificationService([new NoopControlAdapter()], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, BuildContractCatalog(store), settingsService, NullLogger<SemanticTrustService>.Instance), BuildContractCatalog(store), NullLogger<TypedSettingsService>.Instance);
        var result = await typed.ApplyTypedFieldAsync(device.Id, "ip", JsonValue.Create("10.0.0.31"), expertOverride: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("blocked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allows_Grouped_Writable_Image_Field_Write_Without_Expert_Override()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.29", Name = "cam-image", DeviceType = "5523-w", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Stateful",
                Endpoint = "/NetSDK/Video/input/channel/1",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = true
            }
        ], CancellationToken.None);
        await store.SaveNormalizedSettingFieldsAsync(
        [
            new NormalizedSettingField
            {
                DeviceId = device.Id,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                FieldKey = "brightness",
                DisplayName = "Brightness",
                AdapterName = "Stateful",
                SourceEndpoint = "/NetSDK/Video/input/channel/1",
                RawSourcePath = "$.brightnessLevel",
                ContractKey = "video.input.channel.0",
                TypedValue = JsonValue.Create(50),
                ReadVerified = true,
                WriteVerified = false,
                SupportState = ContractSupportState.Supported,
                Validity = FieldValidityState.Proven
            }
        ], CancellationToken.None);
        await store.SaveGroupedRetestResultsAsync(
        [
            new GroupedUnsupportedRetestResult
            {
                DeviceId = device.Id,
                FirmwareFingerprint = "5523|1.0.0|ipc",
                IpAddress = device.IpAddress ?? string.Empty,
                GroupKind = GroupedConfigKind.ImageConfig,
                ContractKey = "video.input.channel.0",
                FieldKey = "brightness",
                SourceEndpoint = "/NetSDK/Video/input/channel/1",
                SourcePath = "$.brightnessLevel",
                Classification = ForcedFieldClassification.Writable,
                Behavior = GroupedApplyBehavior.ImmediateApplied,
                BaselineFieldPresent = true
            }
        ], CancellationToken.None);
        await store.SaveSettingsSnapshotAsync(new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "Stateful",
            Groups =
            [
                new SettingGroup
                {
                    Name = "Video",
                    DisplayName = "Video",
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["/NetSDK/Video/input/channel/1"] = new()
                        {
                            SourceEndpoint = "/NetSDK/Video/input/channel/1",
                            Value = JsonNode.Parse("{\"id\":1,\"enabled\":true,\"brightnessLevel\":50,\"contrastLevel\":50,\"saturationLevel\":50,\"sharpnessLevel\":50,\"hueLevel\":50}")
                        }
                    }
                }
            ]
        }, CancellationToken.None);

        var adapter = new StatefulTestAdapter();
        var settingsService = BuildSettingsService(store, [adapter]);
        var typed = new TypedSettingsService(store, settingsService, new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, BuildContractCatalog(store), settingsService, NullLogger<SemanticTrustService>.Instance), BuildContractCatalog(store), NullLogger<TypedSettingsService>.Instance);
        var result = await typed.ApplyTypedFieldAsync(device.Id, "brightness", JsonValue.Create(61), expertOverride: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Success, result?.Message);
    }

    [Fact]
    public async Task Persists_Persistence_Verification_Result()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { IpAddress = "10.0.0.227", Name = "cam3" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new StatefulTestAdapter();
        var service = new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance);
        var result = await service.VerifyAsync(new PersistenceVerificationRequest
        {
            DeviceId = device.Id,
            AdapterName = adapter.Name,
            Endpoint = "/NetSDK/Video/input/channel/0",
            Method = "PUT",
            Payload = new JsonObject { ["bitrate"] = 2048 },
            RebootForVerification = false
        }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.ImmediateVerifyPassed);
        var stored = await service.GetResultsAsync(device.Id, 10, CancellationToken.None);
        Assert.NotEmpty(stored);
    }

    [Fact]
    public async Task Promotes_Firmware_Capability_Profile()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var deviceId = Guid.NewGuid();
        await store.SaveNormalizedSettingFieldsAsync(
        [
            new NormalizedSettingField
            {
                DeviceId = deviceId,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image",
                FieldKey = "codec",
                DisplayName = "Codec",
                AdapterName = "Fake",
                SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                FirmwareFingerprint = "5523|1.0.0|ipc",
                ReadVerified = true,
                WriteVerified = true,
                Validity = FieldValidityState.Proven,
                SupportState = ContractSupportState.Supported
            },
            new NormalizedSettingField
            {
                DeviceId = deviceId,
                GroupKind = TypedSettingGroupKind.NetworkWireless,
                GroupName = "Network / Wireless",
                FieldKey = "ip",
                DisplayName = "IP",
                AdapterName = "Fake",
                SourceEndpoint = "/NetSDK/Network/interfaces",
                FirmwareFingerprint = "5523|1.0.0|ipc",
                DisruptionClass = DisruptionClass.NetworkChanging,
                Validity = FieldValidityState.Inferred
            }
        ], CancellationToken.None);

        var capability = new CapabilityPromotionService(store, BuildContractCatalog(store));
        var profile = await capability.PromoteForDeviceAsync(deviceId, CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Contains("codec", profile!.VerifiedWritableFields);
        Assert.Contains("ip", profile.DangerousFields);
        Assert.Contains("ip", profile.UncertainFields);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_tempDirectory, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }));

    private static SettingsService BuildSettingsService(IApplicationStore store, IEnumerable<IControlAdapter> adapters)
    {
        var validation = new ProtocolValidationService(adapters, BuildContractCatalog(store), store, NullLogger<ProtocolValidationService>.Instance);
        return new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
    }

    private static IEndpointContractCatalog BuildContractCatalog(IApplicationStore store)
        => new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);

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

    private sealed class StatefulTestAdapter : IControlAdapter
    {
        private JsonNode? _value = JsonNode.Parse("{\"bitrate\":1024}");
        public string Name => "Stateful";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;
        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });
        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name });
        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken) => ReadAsync(device, cancellationToken);

        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
        {
            if (plan.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new WriteResult { Success = true, AdapterName = Name, Response = _value?.DeepClone() });
            }

            _value = plan.Payload?.DeepClone();
            return Task.FromResult(new WriteResult { Success = true, AdapterName = Name, Response = _value?.DeepClone() });
        }

        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }
}


