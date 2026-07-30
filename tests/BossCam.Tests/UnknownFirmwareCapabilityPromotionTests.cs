using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Security;
using BossCam.Infrastructure.Persistence;
using BossCam.NativeBridge;
using Microsoft.Extensions.Options;
using Xunit;

namespace BossCam.Tests;

/// <summary>
/// Punch-list regression tests.
///
/// (1) CapabilityPromotionService + unknown firmware fingerprint → the promoted
/// profile's SupportedEndpointFamilies / SupportedSettingGroups / contract-derived
/// lists MUST be empty, NOT silently filled from a nearest-match contract meant
/// for a different model. The service trusts the contract catalog's verdict
/// when it returns zero contracts for a device; this test pins that behavior.
///
/// (2) Positive control: a known firmware with a stub catalog that returns
/// matching contracts DOES populate the lists. Proves the empty result above
/// is correct behavior, not "service is always empty".
///
/// (3) NativeInteropProbe.Probe(string.Empty, string.Empty) Linux-safety:
/// the bosscam service on Linux has both IpcamSuiteDirectory and EseeCloudDirectory
/// empty (Windows-OEM-only). The probe API must return zero results without
/// throwing and must NEVER claim any library Exists=true when its discovery
/// directories are empty.
/// </summary>
public sealed class UnknownFirmwareCapabilityPromotionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bosscam-unknown-fw-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public UnknownFirmwareCapabilityPromotionTests()
    {
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task Unknown_Firmware_With_Empty_Contracts_Yields_Empty_Lists_Not_Silently_Matched()
    {
        var store = new SqliteApplicationStore(
            Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }),
            NoOpPasswordCipher.Instance);
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity
        {
            Name = "alien-cam",
            DeviceType = "UnknownVendor-XYZ",
            IpAddress = "10.0.0.99",
            HardwareModel = "NotACatalogModel",
            FirmwareVersion = "9.9.9-unknown"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var fingerprint = $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";
        await store.SaveNormalizedSettingFieldsAsync(
        [
            new NormalizedSettingField
            {
                DeviceId = device.Id,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video",
                FieldKey = "brightness",
                DisplayName = "Brightness",
                AdapterName = "Fake",
                SourceEndpoint = "/video/1",
                RawSourcePath = "$.brightnessLevel",
                ContractKey = "video.input.channel.0",
                TypedValue = JsonValue.Create(50),
                WriteVerified = false,
                ReadVerified = true,
                SupportState = ContractSupportState.Uncertain,
                Validity = FieldValidityState.Unverified,
                FirmwareFingerprint = fingerprint
            }
        ], CancellationToken.None);

        var catalog = new EmptyContractCatalog();
        var service = new CapabilityPromotionService(store, catalog);
        var profile = await service.PromoteForDeviceAsync(device.Id, CancellationToken.None);

        // The promoted profile must be non-null (the field exists, the early-null
        // short-circuit on fields.Count == 0 must NOT trigger) AND the contract-derived
        // lists must be empty (catalog returned zero, service must propagate that).
        Assert.NotNull(profile);
        Assert.Empty(profile!.SupportedEndpointFamilies);
        Assert.Empty(profile.SupportedSettingGroups);
        Assert.Empty(profile.RebootRequiredFields);
        Assert.Empty(profile.NativeFallbackRequiredFields);
        Assert.Empty(profile.FullObjectWriteFields);
        Assert.Equal(fingerprint, profile.FirmwareFingerprint);
        // Field-level uncertainty still surfaces — the service knows "brightness" exists
        // but it cannot prove a contract applies, so the field lives in UncertainFields
        // for operator review instead of being silently promoted to a false positive.
        Assert.Contains("brightness", profile.UncertainFields, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Known_Firmware_With_Contracts_Populates_Lists()
    {
        // Positive control: when the catalog DOES return contracts, the lists populate.
        // Proves that the empty result in the unknown-firmware test is correct behavior
        // rather than the service always returning empty (which would falsely pass).
        var store = new SqliteApplicationStore(
            Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }),
            NoOpPasswordCipher.Instance);
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity
        {
            Name = "known-cam",
            DeviceType = "IPC",
            IpAddress = "10.0.0.50",
            HardwareModel = "5523",
            FirmwareVersion = "1.0.0"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var fingerprint = $"{device.HardwareModel}|{device.FirmwareVersion}|{device.DeviceType}";
        await store.SaveNormalizedSettingFieldsAsync(
        [
            new NormalizedSettingField
            {
                DeviceId = device.Id,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video",
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
                Validity = FieldValidityState.Proven,
                FirmwareFingerprint = fingerprint
            }
        ], CancellationToken.None);

        var catalog = new SeededContractCatalog(
        [
            new EndpointContract
            {
                ContractKey = "video.input.channel.0",
                Endpoint = "/NetSDK/Video/input/channel/*",
                Method = "PUT",
                Surface = ContractSurface.NetSdkRest,
                GroupKind = TypedSettingGroupKind.VideoImage,
                GroupName = "Video / Image"
            }
        ]);
        var service = new CapabilityPromotionService(store, catalog);
        var profile = await service.PromoteForDeviceAsync(device.Id, CancellationToken.None);

        Assert.NotNull(profile);
        // SupportedEndpointFamilies is derived from contract.Surface.ToString() — for a
        // NetSdkRest contract this is "NetSdkRest", not the ContractKey. Asserting on the
        // ContractKey here would falsely fail the test (even with the service behaving
        // correctly), so use the actual shape: surface enumeration OR the GroupName.
        Assert.Contains("NetSdkRest", profile!.SupportedEndpointFamilies, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Video / Image", profile.SupportedSettingGroups, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("brightness", profile.VerifiedWritableFields, StringComparer.OrdinalIgnoreCase);
        // Fingerprint round-trip: the field-derived fingerprint must propagate into the
        // promoted profile. Without this, a future refactor that alters how fingerprint
        // flows from NormalizedSettingField -> FirmwareCapabilityProfile could break the
        // unknown-firmware safety property silently.
        Assert.Equal(fingerprint, profile.FirmwareFingerprint);
    }

    [Fact]
    public async Task Returns_Null_When_No_Normalized_Fields_Exist()
    {
        // Pins the early-return safety property: a device with zero NormalizedSettingFields
        // MUST cause CapabilityPromotionService.PromoteForDeviceAsync to return null rather
        // than promoting an under-populated profile into the firmware-capability catalog.
        // A future refactor that removes this guard could leak empty-but-non-null profiles
        // for unobserved devices, polluting /api/firmware/capabilities. This test fails
        // loudly if that guard is removed.
        var store = new SqliteApplicationStore(
            Options.Create(new BossCamRuntimeOptions { DatabasePath = _dbPath }),
            NoOpPasswordCipher.Instance);
        await store.InitializeAsync(CancellationToken.None);

        var device = new DeviceIdentity
        {
            Name = "device-no-fields",
            DeviceType = "IPC",
            IpAddress = "10.0.0.60",
            HardwareModel = "5523",
            FirmwareVersion = "1.0.0"
        };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var catalog = new EmptyContractCatalog();
        var service = new CapabilityPromotionService(store, catalog);
        var profile = await service.PromoteForDeviceAsync(device.Id, CancellationToken.None);

        Assert.Null(profile);
    }

    [Fact]
    public void Native_Interop_Probe_With_Empty_Directories_Returns_No_Loaded_Libraries()
    {
        // Linux safety: the bosscam service starts with IpcamSuiteDirectory = "" and
        // EseeCloudDirectory = "" by default on Linux (Windows-OEM-only). Probe()
        // must not throw and must NEVER report any library as Exists=true, or
        // /api/diagnostics and the OEM-Native adapters will diverge from reality.
        var results = NativeInteropProbe.Probe(string.Empty, string.Empty);

        Assert.NotNull(results);
        Assert.DoesNotContain(results, result => result.Exists && result.Loaded);
        foreach (var result in results)
        {
            Assert.False(result.Exists, $"Linux probe must not claim a library Exists when input directories are empty (saw {result.Name}).");
            Assert.False(result.Loaded, $"Linux probe must not claim a library Loaded when input directories are empty (saw {result.Name}).");
        }
    }

    /// <summary>Stub IEndpointContractCatalog that returns zero contracts for any device — simulates an unrecognized firmware that has no contract bindings.</summary>
    private sealed class EmptyContractCatalog : IEndpointContractCatalog
    {
        public Task<IReadOnlyCollection<EndpointContract>> GetContractsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<EndpointContract>>([]);
        public Task<IReadOnlyCollection<EndpointContract>> GetContractsForDeviceAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<EndpointContract>>([]);
        public EndpointContract? MatchContract(string endpoint, string method, IEnumerable<EndpointContract> contracts)
            => null;
    }

    /// <summary>Stub IEndpointContractCatalog that returns a fixed set of contracts regardless of device — used by the positive-control test.</summary>
    private sealed class SeededContractCatalog : IEndpointContractCatalog
    {
        private readonly IReadOnlyCollection<EndpointContract> _seed;
        public SeededContractCatalog(IEnumerable<EndpointContract> seed) => _seed = seed.ToList();
        public Task<IReadOnlyCollection<EndpointContract>> GetContractsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_seed);
        public Task<IReadOnlyCollection<EndpointContract>> GetContractsForDeviceAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(_seed);
        public EndpointContract? MatchContract(string endpoint, string method, IEnumerable<EndpointContract> contracts)
            => contracts.FirstOrDefault();
    }
}
