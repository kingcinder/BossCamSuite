using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

public sealed class ControlPointInventoryServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-control-types-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public ControlPointInventoryServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    [Fact]
    public async Task Inventory_Classifies_Representative_Control_Types()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.29",
            Name = "5523-W",
            DeviceType = "IPC",
            HardwareModel = "5523-w",
            FirmwareVersion = "1.0.0"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var inventory = BuildInventoryService(store);
        var report = await inventory.GetReportAsync(device.Id, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Contains(report!.Families, family => family.Family == "VideoInput");
        Assert.Contains(report.Families, family => family.Family == "VideoEncode");
        Assert.Contains(report.Families, family => family.Family == "Image");
        Assert.Contains(report.Families, family => family.Family == "IrCutFilter");
        Assert.Contains(report.Families, family => family.Family == "Network");
        Assert.Contains(report.Families, family => family.Family == "Wifi");
        Assert.Contains(report.Families, family => family.Family == "User");
        Assert.Contains(report.Families, family => family.Family == "Alarm");
        Assert.Contains(report.Families, family => family.Family == "Storage");
        Assert.Contains(report.Families, family => family.Family == "Overlay / OSD");
        Assert.Contains(report.Families, family => family.Family == "Audio");
        Assert.Contains(report.Families, family => family.Family == "Snapshot");

        var brightness = report.Families.SelectMany(static family => family.Controls).First(item => item.FieldKey == "brightness");
        Assert.Equal(ControlPointValueType.ScalarOrCodeValue, brightness.ControlType);
        Assert.Equal(ControlPointWidgetKind.Slider, brightness.RecommendedWidget);

        var mirror = report.Families.SelectMany(static family => family.Controls).First(item => item.FieldKey == "mirror");
        Assert.Equal(ControlPointValueType.BooleanToggle, mirror.ControlType);
        Assert.Equal(ControlPointWidgetKind.Toggle, mirror.RecommendedWidget);

        var osdDateFormat = report.Families.SelectMany(static family => family.Controls).First(item => item.FieldKey == "osdDateFormat");
        Assert.Equal(ControlPointValueType.SingleSelectSet, osdDateFormat.ControlType);
        Assert.Equal(ControlPointWidgetKind.Dropdown, osdDateFormat.RecommendedWidget);
        Assert.False(osdDateFormat.ExistingWidgetMismatch);

        var userList = report.Families.SelectMany(static family => family.Controls).First(item => item.FieldKey == "userList");
        Assert.Equal(ControlPointValueType.HigherOrderComposite, userList.ControlType);
        Assert.Equal(ControlPointWidgetKind.DependencyPanel, userList.RecommendedWidget);

        var privacyMaskX = report.Families.SelectMany(static family => family.Controls).First(item => item.FieldKey == "privacyMaskX");
        Assert.Equal(ControlPointValueType.CompositeControl, privacyMaskX.ControlType);
        Assert.True(privacyMaskX.ExistingWidgetMismatch);
    }

    [Fact]
    public async Task Typed_Settings_Hints_Expose_Control_Type_Metadata()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.4",
            Name = "cam1",
            DeviceType = "IPC",
            HardwareModel = "5523-w",
            FirmwareVersion = "1.0.0"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);
        await store.SaveEndpointValidationResultsAsync(
        [
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Video/input/channel/1",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = true,
                DisruptionClass = DisruptionClass.Safe
            },
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Video/encode/channel/101/properties",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = true,
                DisruptionClass = DisruptionClass.Safe
            },
            new EndpointValidationResult
            {
                DeviceId = device.Id,
                AdapterName = "Fake",
                Endpoint = "/NetSDK/Video/encode/channel/101/dateTimeOverlay",
                Method = "PUT",
                ReadVerified = true,
                WriteVerified = false,
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
                            Value = JsonNode.Parse("{\"id\":1,\"enabled\":true,\"brightnessLevel\":50,\"mirrorEnabled\":false}")
                        },
                        ["/NetSDK/Video/encode/channel/101/properties"] = new()
                        {
                            Key = "/NetSDK/Video/encode/channel/101/properties",
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/properties",
                            Value = JsonNode.Parse("{\"codecType\":\"H.264\",\"resolution\":\"1920x1080\",\"constantBitRate\":1024}")
                        },
                        ["/NetSDK/Video/encode/channel/101/dateTimeOverlay"] = new()
                        {
                            Key = "/NetSDK/Video/encode/channel/101/dateTimeOverlay",
                            SourceEndpoint = "/NetSDK/Video/encode/channel/101/dateTimeOverlay",
                            Value = JsonNode.Parse("{\"enabled\":true,\"dateFormat\":\"YYYY-MM-DD\",\"timeFormat\":\"24\",\"displayWeek\":true}")
                        }
                    }
                }
            ]
        };
        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var typed = BuildTypedSettingsService(store);
        var groups = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);
        var hints = groups.SelectMany(static group => group.EditorHints).ToDictionary(static hint => hint.FieldKey, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(ControlPointValueType.ScalarOrCodeValue, hints["brightness"].ControlType);
        Assert.Equal(ControlPointWidgetKind.Slider, hints["brightness"].RecommendedWidget);
        Assert.Equal(ControlPointValueType.BooleanToggle, hints["mirror"].ControlType);
        Assert.Equal(ControlPointValueType.ScalarOrCodeValue, hints["resolution"].ControlType);
        Assert.Equal(ControlPointWidgetKind.TextInput, hints["resolution"].RecommendedWidget);
        Assert.Equal(ControlPointValueType.SingleSelectSet, hints["osdDateFormat"].ControlType);
        Assert.Equal(ControlPointWidgetKind.Dropdown, hints["osdDateFormat"].RecommendedWidget);
        Assert.True(hints["osdDateFormat"].NormalUiEligible);
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

    private ControlPointInventoryService BuildInventoryService(SqliteApplicationStore store)
    {
        var contractCatalog = BuildContractCatalog(store);
        var settingsService = BuildSettingsService(store, contractCatalog);
        var typedSettingsService = BuildTypedSettingsService(store, settingsService, contractCatalog);
        var groupedConfigService = new GroupedConfigService(
            store,
            settingsService,
            typedSettingsService,
            contractCatalog,
            NullLogger<GroupedConfigService>.Instance);
        return new ControlPointInventoryService(store, contractCatalog, groupedConfigService);
    }

    private TypedSettingsService BuildTypedSettingsService(SqliteApplicationStore store)
    {
        var contractCatalog = BuildContractCatalog(store);
        var settingsService = BuildSettingsService(store, contractCatalog);
        return BuildTypedSettingsService(store, settingsService, contractCatalog);
    }

    private static TypedSettingsService BuildTypedSettingsService(SqliteApplicationStore store, SettingsService settingsService, IEndpointContractCatalog contractCatalog)
    {
        var persistence = new PersistenceVerificationService([new NoopControlAdapter()], store, NullLogger<PersistenceVerificationService>.Instance);
        var semantic = new SemanticTrustService(store, contractCatalog, settingsService, NullLogger<SemanticTrustService>.Instance);
        return new TypedSettingsService(store, settingsService, persistence, semantic, contractCatalog, NullLogger<TypedSettingsService>.Instance);
    }

    private static SettingsService BuildSettingsService(SqliteApplicationStore store, IEndpointContractCatalog contractCatalog)
    {
        var adapters = new IControlAdapter[] { new NoopControlAdapter() };
        var validation = new ProtocolValidationService(adapters, contractCatalog, store, NullLogger<ProtocolValidationService>.Instance);
        return new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
    }

    private static IEndpointContractCatalog BuildContractCatalog(SqliteApplicationStore store)
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
}
