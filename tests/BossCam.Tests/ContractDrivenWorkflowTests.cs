using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using BossCam.NativeBridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class ContractDrivenWorkflowTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-contract-tests-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public ContractDrivenWorkflowTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Loads_Explicit_Endpoint_Contracts()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var catalog = new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);

        var contracts = await catalog.GetContractsAsync(CancellationToken.None);

        Assert.Contains(contracts, contract => contract.ContractKey == "video.input.channel.0");
        Assert.Contains(contracts, contract => contract.GroupKind == TypedSettingGroupKind.NetworkWireless);
        Assert.Contains(contracts, contract => contract.GroupKind == TypedSettingGroupKind.UsersMaintenance);
    }

    [Fact]
    public async Task Promotes_Transcript_Evidence_To_Fixtures()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointTranscriptsAsync(
        [
            new EndpointTranscript
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Video/input/channel/1",
                Method = "GET",
                ParsedResponse = JsonNode.Parse("{\"id\":1,\"enabled\":true,\"brightnessLevel\":50,\"contrastLevel\":50,\"saturationLevel\":50,\"sharpnessLevel\":50,\"hueLevel\":50}")!,
                FirmwareFingerprint = "5523|1.0.0|5523-w",
                Success = true
            }
        ], CancellationToken.None);

        var evidence = new ContractEvidenceService(store, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), NullLogger<ContractEvidenceService>.Instance);
        var fixtures = await evidence.PromoteFromTranscriptsAsync(device.Id, _tempDirectory, CancellationToken.None);

        Assert.NotEmpty(fixtures);
        Assert.True(File.Exists(fixtures.First().FixturePath));
    }

    [Fact]
    public async Task Contract_Driven_Normalization_Produces_Field_Contract_Metadata()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
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
                FirmwareFingerprint = "5523|1.0.0|5523-w"
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
                            SourceEndpoint = "/NetSDK/Video/input/channel/1",
                            Value = JsonNode.Parse("{\"id\":1,\"enabled\":true,\"brightnessLevel\":50,\"contrastLevel\":50,\"saturationLevel\":50,\"sharpnessLevel\":50,\"hueLevel\":50,\"mirrorEnabled\":false,\"flipEnabled\":false}")
                        },
                        ["/NetSDK/Video/encode/channel/101/properties"] = new()
                        {
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                            Value = JsonNode.Parse("{\"codecType\":\"H.264\",\"h264Profile\":\"main\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024,\"frameRate\":20,\"keyFrameInterval\":25}")
                        }
                    }
                }
            ]
        };

        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var settings = BuildSettingsService(store, [new NoopControlAdapter()]);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([new NoopControlAdapter()], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), settings, NullLogger<SemanticTrustService>.Instance), new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), NullLogger<TypedSettingsService>.Instance);
        var groups = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);

        var codec = groups.SelectMany(static group => group.Fields).First(field => field.FieldKey == "codec");
        var brightness = groups.SelectMany(static group => group.Fields).First(field => field.FieldKey == "brightness");
        Assert.Equal("video.encode.channel", codec.ContractKey);
        Assert.Equal("video.input.channel.0", brightness.ContractKey);
        Assert.Equal(ContractSupportState.Supported, codec.SupportState);
    }

    [Fact]
    public async Task Contract_Driven_Apply_Blocks_Invalid_Value()
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
                FieldKey = "brightness",
                DisplayName = "Brightness",
                AdapterName = "Fake",
                SourceEndpoint = "/NetSDK/Video/input/channel/1",
                RawSourcePath = "$.brightnessLevel",
                ContractKey = "video.input.channel.0",
                TypedValue = JsonValue.Create(50),
                WriteVerified = true,
                ReadVerified = true,
                SupportState = ContractSupportState.Supported,
                Validity = FieldValidityState.Proven
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
                        ["/NetSDK/Video/encode/channel/101/properties"] = new()
                        {
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                            Value = JsonNode.Parse("{\"codecType\":\"H.264\",\"h264Profile\":\"main\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024,\"frameRate\":20,\"keyFrameInterval\":25}")
                        }
                    }
                }
            ]
        };
        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var settings = BuildSettingsService(store, [new NoopControlAdapter()]);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([new NoopControlAdapter()], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), settings, NullLogger<SemanticTrustService>.Instance), new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), NullLogger<TypedSettingsService>.Instance);

        var result = await typed.ApplyTypedFieldAsync(device.Id, "bitrate", JsonValue.Create(999999), expertOverride: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(SemanticWriteStatus.ContractViolation, result.SemanticStatus);
    }

    [Fact]
    public async Task Contract_Driven_Apply_Blocks_When_Full_Object_Required_And_Snapshot_Missing()
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
                SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                RawSourcePath = "$.constantBitRate",
                ContractKey = "video.encode.channel",
                TypedValue = JsonValue.Create(1024),
                WriteVerified = true,
                ReadVerified = true,
                SupportState = ContractSupportState.Supported,
                Validity = FieldValidityState.Proven
            }
        ], CancellationToken.None);

        var settings = BuildSettingsService(store, [new NoopControlAdapter()]);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([new NoopControlAdapter()], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), settings, NullLogger<SemanticTrustService>.Instance), new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), NullLogger<TypedSettingsService>.Instance);
        var result = await typed.ApplyTypedFieldAsync(device.Id, "brightness", JsonValue.Create(51), expertOverride: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(SemanticWriteStatus.ContractViolation, result.SemanticStatus);
        Assert.True(
            (result.Message ?? string.Empty).Contains("unknown field", StringComparison.OrdinalIgnoreCase)
            || (result.Message ?? string.Empty).Contains("required", StringComparison.OrdinalIgnoreCase)
            || (result.Message ?? string.Empty).Contains("contract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fixture_File_Has_Realish_Top_Group_Shape()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", "video_image", "5523_w", "NetSDK_Video_input_channel_0_GET.json");
        var fallbackPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "contracts", "video_image", "5523_w", "NetSDK_Video_input_channel_0_GET.json"));
        var path = File.Exists(fixturePath) ? fixturePath : fallbackPath;

        var node = JsonNode.Parse(File.ReadAllText(path));
        Assert.NotNull(node?["responseBody"]?["brightnessLevel"]);
        Assert.NotNull(node?["responseBody"]?["contrastLevel"]);
    }

    [Fact]
    public async Task Typed_Apply_Returns_Semantic_Status_ImmediateApplied()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Stateful",
                Endpoint = "/NetSDK/Video/encode/channel/101/properties",
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
                FieldKey = "bitrate",
                DisplayName = "Bitrate",
                AdapterName = "Stateful",
                SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                RawSourcePath = "$.constantBitRate",
                ContractKey = "video.encode.channel",
                TypedValue = JsonValue.Create(1024),
                WriteVerified = true,
                ReadVerified = true,
                SupportState = ContractSupportState.Supported,
                Validity = FieldValidityState.Proven
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
                        ["/NetSDK/Video/encode/channel/101/properties"] = new()
                        {
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                            Value = JsonNode.Parse("{\"codecType\":\"H.264\",\"h264Profile\":\"main\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024,\"frameRate\":20,\"keyFrameInterval\":25}")
                        }
                    }
                }
            ]
        }, CancellationToken.None);

        var adapter = new StatefulVideoAdapter();
        var settings = BuildSettingsService(store, [adapter]);
        var typed = new TypedSettingsService(store, settings, new PersistenceVerificationService([adapter], store, NullLogger<PersistenceVerificationService>.Instance), new SemanticTrustService(store, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), settings, NullLogger<SemanticTrustService>.Instance), new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), NullLogger<TypedSettingsService>.Instance);
        var result = await typed.ApplyTypedFieldAsync(device.Id, "bitrate", JsonValue.Create(2048), expertOverride: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(SemanticWriteStatus.AcceptedChanged, result.SemanticStatus);
    }

    [Fact]
    public async Task Write_Audit_Redacts_Sensitive_Paths()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4", HardwareModel = "5523", FirmwareVersion = "1.0.0" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Stateful",
                Endpoint = "/NetSDK/Network/wireless/0",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = true
            }
        ], CancellationToken.None);

        var adapter = new StatefulVideoAdapter();
        var settings = BuildSettingsService(store, [adapter]);
        _ = await settings.WriteAsync(device.Id, new WritePlan
        {
            GroupName = "Network / Wireless",
            Endpoint = "/NetSDK/Network/wireless/0",
            Method = "PUT",
            AdapterName = "Stateful",
            Payload = new JsonObject
            {
                ["ap"] = new JsonObject
                {
                    ["ssid"] = "cam-net",
                    ["psk"] = "SuperSecret123"
                }
            },
            RequireWriteVerification = true,
            SensitivePaths = ["$.ap.psk"]
        }, CancellationToken.None);

        var entries = await store.GetAuditEntriesAsync(device.Id, 10, CancellationToken.None);
        var request = entries.First().RequestContent ?? string.Empty;
        Assert.DoesNotContain("SuperSecret123", request, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_Index_Refresh_Persists_Segments()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var recordingDir = Path.Combine(_tempDirectory, "recordings");
        Directory.CreateDirectory(recordingDir);
        var filePath = Path.Combine(recordingDir, $"{device.Id:N}_20260418_101500.mp4");
        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3, 4 }, CancellationToken.None);

        var profile = new RecordingProfile
        {
            DeviceId = device.Id,
            Name = "Default",
            OutputDirectory = recordingDir,
            SegmentSeconds = 60,
            Enabled = true
        };
        await store.SaveRecordingProfilesAsync([profile], CancellationToken.None);

        var broker = new TransportBroker([new NoSourceVideoAdapter()], store, NullLogger<TransportBroker>.Instance);
        var recording = new RecordingService(store, broker, NullLogger<RecordingService>.Instance);
        var indexed = await recording.RefreshIndexAsync(device.Id, CancellationToken.None);

        Assert.NotEmpty(indexed);
        var fromStore = await store.GetRecordingSegmentsAsync(device.Id, 10, CancellationToken.None);
        Assert.NotEmpty(fromStore);
        Assert.Equal(filePath, fromStore.First().FilePath);
    }

    [Fact]
    public async Task Recording_Housekeeping_Deletes_Old_Files_By_Retention()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var recordingDir = Path.Combine(_tempDirectory, "retention");
        Directory.CreateDirectory(recordingDir);
        var oldFile = Path.Combine(recordingDir, "old.mp4");
        var newFile = Path.Combine(recordingDir, "new.mp4");
        await File.WriteAllBytesAsync(oldFile, new byte[] { 1, 2, 3 }, CancellationToken.None);
        await File.WriteAllBytesAsync(newFile, new byte[] { 4, 5, 6 }, CancellationToken.None);
        File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(newFile, DateTime.UtcNow);

        var profile = new RecordingProfile
        {
            DeviceId = device.Id,
            Name = "Retention",
            OutputDirectory = recordingDir,
            SegmentSeconds = 60,
            Enabled = true,
            AutoStart = false,
            RetentionDays = 7
        };
        await store.SaveRecordingProfilesAsync([profile], CancellationToken.None);

        var broker = new TransportBroker([new NoSourceVideoAdapter()], store, NullLogger<TransportBroker>.Instance);
        var recording = new RecordingService(store, broker, NullLogger<RecordingService>.Instance);
        var result = await recording.RunHousekeepingAsync(device.Id, CancellationToken.None);

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public async Task Recording_Reconcile_AutoStart_Does_Not_Throw_When_Ffmpeg_Missing()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Name = "cam", DeviceType = "5523-w", IpAddress = "10.0.0.4" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var profile = new RecordingProfile
        {
            DeviceId = device.Id,
            Name = "Auto",
            OutputDirectory = Path.Combine(_tempDirectory, "auto"),
            Enabled = true,
            AutoStart = true
        };
        await store.SaveRecordingProfilesAsync([profile], CancellationToken.None);

        var broker = new TransportBroker([new RtspSourceVideoAdapter()], store, NullLogger<TransportBroker>.Instance);
        var recording = new RecordingService(store, broker, NullLogger<RecordingService>.Instance);
        var started = await recording.ReconcileAutoStartAsync(CancellationToken.None);

        Assert.NotNull(started);
    }

    [Fact]
    public void Native_Interop_Probe_Handles_Missing_Directories()
    {
        var probes = NativeInteropProbe.Probe(@"C:\definitely-missing-ipcam-dir", @"C:\definitely-missing-esee-dir");
        Assert.NotEmpty(probes);
        Assert.All(probes, probe => Assert.False(probe.Loaded));
    }

    public void Dispose()
    {
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

    private static SettingsService BuildSettingsService(IApplicationStore store, IEnumerable<IControlAdapter> adapters)
    {
        var validation = new ProtocolValidationService(adapters, new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), store, NullLogger<ProtocolValidationService>.Instance);
        return new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
    }

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

    private sealed class StatefulVideoAdapter : IControlAdapter
    {
        private JsonObject _state = JsonNode.Parse("{\"codecType\":\"H.264\",\"h264Profile\":\"main\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024,\"frameRate\":20,\"keyFrameInterval\":25}")!.AsObject();
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
                return Task.FromResult(new WriteResult { Success = true, AdapterName = Name, Response = _state.DeepClone() });
            }

            if (plan.Payload is JsonObject payload)
            {
                _state = (JsonObject)payload.DeepClone();
            }

            return Task.FromResult(new WriteResult { Success = true, AdapterName = Name, Response = _state.DeepClone() });
        }
        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken) => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }

    private sealed class NoSourceVideoAdapter : IVideoTransportAdapter
    {
        public string Name => "NoSource";
        public TransportKind TransportKind => TransportKind.Rtsp;
        public int Priority => 1;
        public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>([]);
    }

    private sealed class RtspSourceVideoAdapter : IVideoTransportAdapter
    {
        public string Name => "RtspSource";
        public TransportKind TransportKind => TransportKind.Rtsp;
        public int Priority => 1;
        public Task<IReadOnlyCollection<VideoSourceDescriptor>> GetSourcesAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<VideoSourceDescriptor>>(
            [
                new VideoSourceDescriptor { Kind = TransportKind.Rtsp, Url = "rtsp://10.0.0.4:554", Rank = 1 }
            ]);
    }
}


