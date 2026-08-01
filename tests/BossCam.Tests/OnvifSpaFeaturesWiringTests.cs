using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the ONVIF → SPA Features-panel wiring pass:
/// 1. The ONVIF imaging contracts (image.onvif.brightness etc.) scope to DeviceType "ONVIF"
///    (GenericOnvif / WVC) and never leak onto NetSDK "IPC" devices.
/// 2. An object-shaped onvif:GetImagingSettings snapshot normalizes into a VideoImage group
///    with real sliders (brightness/contrast/...) and dropdowns (exposure/AWB/WDR/day-night)
///    instead of "unmapped:onvif:*" diagnostics.
/// 3. ApplyTypedChangesAsync routes an ONVIF imaging field to the OnvifImagingControlAdapter
///    with the correct contract key / endpoint / payload shape.
/// 4. The control-point inventory surfaces the ONVIF imaging controls with Slider/Dropdown
///    widgets (what FeaturesPanel.svelte renders).
/// </summary>
public sealed class OnvifSpaFeaturesWiringTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"bosscam-onvif-spa-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public OnvifSpaFeaturesWiringTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test.db");
    }

    // ── 1. Contract scoping ────────────────────────────────────────

    [Fact]
    public async Task Onvif_Imaging_Contracts_Scope_To_Onvif_DeviceType_Not_Ipc()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var catalog = BuildContractCatalog(store);

        var onvifDevice = new DeviceIdentity { IpAddress = "10.0.0.7", DeviceType = "ONVIF", Name = "WVC W5C" };
        var ipcDevice = new DeviceIdentity { IpAddress = "10.0.0.4", DeviceType = "IPC", Name = "5523-W" };

        var onvifContracts = await catalog.GetContractsForDeviceAsync(onvifDevice, CancellationToken.None);
        Assert.Contains(onvifContracts, c => c.ContractKey == "image.onvif.brightness");
        Assert.Contains(onvifContracts, c => c.ContractKey == "image.onvif.exposure");
        Assert.Contains(onvifContracts, c => c.ContractKey == "image.onvif.awb");
        Assert.Contains(onvifContracts, c => c.ContractKey == "video.onvif.profiles");
        Assert.Contains(onvifContracts, c => c.ContractKey == "device.onvif.info");

        var ipcContracts = await catalog.GetContractsForDeviceAsync(ipcDevice, CancellationToken.None);
        Assert.DoesNotContain(ipcContracts, c => c.ContractKey.StartsWith("image.onvif", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ipcContracts, c => c.ContractKey == "video.onvif.profiles");
        Assert.DoesNotContain(ipcContracts, c => c.ContractKey == "device.onvif.info");
    }

    // ── 2. Normalization ───────────────────────────────────────────

    [Fact]
    public async Task Onvif_Imaging_Snapshot_Normalizes_Into_VideoImage_Sliders_And_Dropdowns()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.7", DeviceType = "ONVIF", Name = "WVC W5C" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var snapshot = new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "OnvifImagingControlAdapter",
            Groups =
            [
                new SettingGroup
                {
                    Name = "Image",
                    DisplayName = "ONVIF Imaging",
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["imagingSettings"] = new()
                        {
                            Key = "imagingSettings",
                            DisplayName = "Imaging Settings",
                            Value = JsonNode.Parse("""
                            {
                              "brightness": 60, "contrast": 40, "saturation": 50, "sharpness": 30, "gamma": 10,
                              "exposure": "MANUAL", "awb": "AUTO", "wdr": "OFF", "daynight": "AUTO"
                            }
                            """),
                            ValueKind = SettingValueKind.Object,
                            SourceEndpoint = "onvif:GetImagingSettings"
                        }
                    }
                }
            ]
        };
        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var typed = BuildTypedSettingsService(store);
        var groups = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);

        var image = Assert.Single(groups, g => g.GroupKind == TypedSettingGroupKind.VideoImage);
        var hints = image.EditorHints.ToDictionary(h => h.FieldKey, StringComparer.OrdinalIgnoreCase);

        // No unmapped diagnostics — the whole point of the wiring.
        Assert.DoesNotContain(image.Fields, f => f.FieldKey.StartsWith("unmapped:", StringComparison.Ordinal));

        // Scalars → Slider with the signed ONVIF range.
        Assert.Contains("brightness", hints.Keys);
        Assert.Equal(ControlPointWidgetKind.Slider, hints["brightness"].RecommendedWidget);
        Assert.Equal(-100m, hints["brightness"].Min);
        Assert.Equal(100m, hints["brightness"].Max);
        Assert.Equal(60m, image.Fields.First(f => f.FieldKey == "brightness").TypedValue!.GetValue<decimal>());

        // Modes → Dropdown with the ONVIF vocabulary.
        Assert.Equal(ControlPointWidgetKind.Dropdown, hints["exposure"].RecommendedWidget);
        Assert.Equal(ControlPointWidgetKind.Dropdown, hints["awb"].RecommendedWidget);
        Assert.Equal(ControlPointWidgetKind.Dropdown, hints["wdr"].RecommendedWidget);
        Assert.Equal(ControlPointWidgetKind.Dropdown, hints["daynight"].RecommendedWidget);

        var exposureHint = hints["exposure"];
        Assert.NotNull(exposureHint.EnumValues);
        Assert.Contains("MANUAL", exposureHint.EnumValues!.Select(v => v?.GetValue<string>()));
        Assert.Contains("AUTO", exposureHint.EnumValues!.Select(v => v?.GetValue<string>()));
    }

    [Fact]
    public async Task Onvif_Device_Info_And_Profiles_Normalize_Without_Unmapped_Noise()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.7", DeviceType = "ONVIF", Name = "Generic ONVIF" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var snapshot = new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "OnvifImagingControlAdapter",
            Groups =
            [
                new SettingGroup
                {
                    Name = "Device",
                    DisplayName = "ONVIF Device",
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["deviceInfo"] = new()
                        {
                            Key = "deviceInfo",
                            Value = JsonNode.Parse("""{"manufacturer":"TestCam","model":"W5C","firmware":"1.0","serial":"SN1"}"""),
                            ValueKind = SettingValueKind.Object,
                            SourceEndpoint = "onvif:GetDeviceInformation"
                        }
                    }
                },
                new SettingGroup
                {
                    Name = "Video",
                    DisplayName = "ONVIF Media",
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["mediaProfiles"] = new()
                        {
                            Key = "mediaProfiles",
                            Value = JsonNode.Parse("""{"profile":"PROFILE_000","resolution":"1920x1080","frameRate":25}"""),
                            ValueKind = SettingValueKind.Object,
                            SourceEndpoint = "onvif:GetProfiles"
                        }
                    }
                }
            ]
        };
        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var typed = BuildTypedSettingsService(store);
        var groups = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);
        var allFields = groups.SelectMany(g => g.Fields).ToList();

        Assert.DoesNotContain(allFields, f => f.FieldKey.StartsWith("unmapped:", StringComparison.Ordinal));
        Assert.Contains(allFields, f => f.FieldKey == "manufacturer");
        Assert.Contains(allFields, f => f.FieldKey == "serial");
        Assert.Contains(allFields, f => f.FieldKey == "resolution");
        Assert.Contains(allFields, f => f.FieldKey == "frameRate");
    }

    // ── 3. Apply routing ───────────────────────────────────────────

    [Fact]
    public async Task ApplyTypedField_Routes_Onvif_Imaging_To_Onvif_Adapter_With_Contract_Payload()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.7", DeviceType = "ONVIF", Name = "WVC W5C" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var snapshot = new SettingsSnapshot
        {
            DeviceId = device.Id,
            AdapterName = "OnvifImagingControlAdapter",
            Groups =
            [
                new SettingGroup
                {
                    Name = "Image",
                    DisplayName = "ONVIF Imaging",
                    Values = new Dictionary<string, SettingValue>
                    {
                        ["imagingSettings"] = new()
                        {
                            Key = "imagingSettings",
                            Value = JsonNode.Parse("""{"brightness":60,"contrast":40,"exposure":"MANUAL"}"""),
                            ValueKind = SettingValueKind.Object,
                            SourceEndpoint = "onvif:GetImagingSettings"
                        }
                    }
                }
            ]
        };
        await store.SaveSettingsSnapshotAsync(snapshot, CancellationToken.None);

        var capturing = new CapturingOnvifAdapter();
        var typed = BuildTypedSettingsService(store, capturing);
        _ = await typed.NormalizeDeviceAsync(device.Id, refreshFromDevice: false, CancellationToken.None);

        var results = await typed.ApplyTypedChangesAsync(
            device.Id,
            [new TypedFieldChange("brightness", JsonValue.Create(75))],
            expertOverride: true,
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Equal("OnvifImagingControlAdapter", result.AdapterName);

        // The routed plan must carry the ONVIF contract key + endpoint so the adapter's
        // ResolveFieldKey derives "brightness" and ExtractFieldValue finds it in the payload.
        var writePlan = capturing.Plans.FirstOrDefault(p => p.Method == "PUT");
        Assert.NotNull(writePlan);
        Assert.Equal("image.onvif.brightness", writePlan!.ContractKey);
        Assert.Equal("onvif:GetImagingSettings", writePlan.Endpoint);
        Assert.Equal(75m, writePlan.Payload!["brightness"]!.GetValue<decimal>());
        Assert.Equal("OnvifImagingControlAdapter", writePlan.AdapterName);
    }

    [Fact]
    public async Task Onvif_Adapter_Field_Resolution_Matches_Contract_Key_And_Payload()
    {
        // End-to-end guard between the seed contract and the adapter's write path: the contract
        // key trailing segment must resolve to a field that OnvifImagingControlAdapter maps.
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var catalog = BuildContractCatalog(store);
        var device = new DeviceIdentity { IpAddress = "10.0.0.7", DeviceType = "ONVIF" };

        var contracts = await catalog.GetContractsForDeviceAsync(device, CancellationToken.None);
        foreach (var fieldKey in new[] { "brightness", "exposure", "awb", "wdr", "daynight" })
        {
            var contract = Assert.Single(contracts, c => c.ContractKey == $"image.onvif.{fieldKey}");
            var plan = new WritePlan
            {
                ContractKey = contract.ContractKey,
                Endpoint = contract.Endpoint,
                Method = "PUT",
                Payload = new JsonObject { [fieldKey] = fieldKey == "brightness" ? JsonValue.Create(50) : JsonValue.Create("AUTO") }
            };

            var resolved = OnvifImagingControlAdapter.ResolveFieldKey(plan);
            Assert.Equal(fieldKey, resolved);
            var value = OnvifImagingControlAdapter.ExtractFieldValue(plan.Payload, resolved);
            Assert.NotNull(value);
            var soap = OnvifImagingControlAdapter.BuildImagingSettingsElement(resolved, value);
            Assert.NotNull(soap);
        }
    }

    // ── 4. Inventory surfacing ─────────────────────────────────────

    [Fact]
    public async Task Inventory_Surfaces_Onvif_Imaging_Controls_With_Recommended_Widgets()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity { IpAddress = "10.0.0.7", DeviceType = "ONVIF", Name = "WVC W5C" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var inventory = BuildInventoryService(store);
        var report = await inventory.GetReportAsync(device.Id, CancellationToken.None);

        Assert.NotNull(report);
        var onvifControls = report!.Families
            .SelectMany(f => f.Controls)
            .Where(c => c.Endpoint.Equals("onvif:GetImagingSettings", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var brightness = Assert.Single(onvifControls, c => c.FieldKey == "brightness");
        Assert.Equal(ControlPointValueType.ScalarOrCodeValue, brightness.ControlType);
        Assert.Equal(ControlPointWidgetKind.Slider, brightness.RecommendedWidget);
        Assert.Equal(-100, brightness.Min);
        Assert.Equal(100, brightness.Max);

        foreach (var modeKey in new[] { "exposure", "awb", "wdr", "daynight" })
        {
            var mode = Assert.Single(onvifControls, c => c.FieldKey == modeKey);
            Assert.Equal(ControlPointValueType.SingleSelectSet, mode.ControlType);
            Assert.Equal(ControlPointWidgetKind.Dropdown, mode.RecommendedWidget);
        }
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

    private ControlPointInventoryService BuildInventoryService(SqliteApplicationStore store)
    {
        var contractCatalog = BuildContractCatalog(store);
        var settingsService = BuildSettingsService(store, contractCatalog, new CapturingOnvifAdapter());
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
        => BuildTypedSettingsService(store, new CapturingOnvifAdapter());

    private TypedSettingsService BuildTypedSettingsService(SqliteApplicationStore store, IControlAdapter adapter)
        => BuildTypedSettingsService(store, BuildSettingsService(store, BuildContractCatalog(store), adapter), BuildContractCatalog(store));

    private static TypedSettingsService BuildTypedSettingsService(SqliteApplicationStore store, SettingsService settingsService, IEndpointContractCatalog contractCatalog)
    {
        var adapters = new IControlAdapter[] { new CapturingOnvifAdapter() };
        var persistence = new PersistenceVerificationService(adapters, store, NullLogger<PersistenceVerificationService>.Instance);
        var semantic = new SemanticTrustService(store, contractCatalog, settingsService, NullLogger<SemanticTrustService>.Instance);
        return new TypedSettingsService(store, settingsService, persistence, semantic, contractCatalog, new CapabilityPromotionService(store, contractCatalog), NullLogger<TypedSettingsService>.Instance);
    }

    private static SettingsService BuildSettingsService(SqliteApplicationStore store, IEndpointContractCatalog contractCatalog, IControlAdapter adapter)
    {
        var adapters = new IControlAdapter[] { adapter };
        var validation = new ProtocolValidationService(adapters, contractCatalog, store, NullLogger<ProtocolValidationService>.Instance);
        return new SettingsService(adapters, store, validation, NullLogger<SettingsService>.Instance);
    }

    private static IEndpointContractCatalog BuildContractCatalog(SqliteApplicationStore store)
        => new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance);

    /// <summary>IControlAdapter that records every WritePlan so the apply test can assert the
    /// exact contract key / endpoint / payload routed to the ONVIF adapter.</summary>
    private sealed class CapturingOnvifAdapter : IControlAdapter
    {
        public List<WritePlan> Plans { get; } = [];

        public string Name => "OnvifImagingControlAdapter";
        public int Priority => 35;
        public TransportKind TransportKind => TransportKind.OnvifRtsp;

        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });
        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new SettingsSnapshot { DeviceId = device.Id, AdapterName = Name });
        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken) => ReadAsync(device, cancellationToken);

        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            return Task.FromResult(new WriteResult
            {
                Success = true,
                AdapterName = Name,
                StatusCode = 200,
                Response = plan.Payload?.DeepClone()
            });
        }

        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }
}
