using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using BossCam.Infrastructure.Control;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the security hardening pass:
/// 1. RemoteCommand envelope passwords never reach settings snapshots / API responses / audits.
/// 2. SettingsService.ReadAsync redacts secret-bearing fields before persist + return.
/// 3. Firmware upload/register paths enforce a directory allow-list.
/// 4. The Aegon-LAN convenience batch is config-driven (no hardcoded home-LAN topology).
/// 5. StreamDescriptorAdapter logs transport probe failures instead of swallowing them.
/// </summary>
public sealed class SecurityHardeningTests
{
    // ── 1. RemoteCommand envelope password redaction ─────────────────

    [Fact]
    public async Task OwnedRemoteCommandAdapter_ReadAsync_Snapshot_Does_Not_Contain_Plaintext_Password()
    {
        var adapter = BuildRemoteAdapter();
        var device = NewRemoteDevice();

        var snapshot = await adapter.ReadAsync(device, CancellationToken.None);
        var json = JsonSerializer.Serialize(snapshot);

        // The adapter never embeds device.Password in the persisted/returned snapshot even when
        // the relay is unconfigured (the failure path used to echo the live envelope).
        Assert.DoesNotContain("supersecret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnedRemoteCommandAdapter_ApplyAsync_Failure_Response_Does_Not_Contain_Plaintext_Password()
    {
        var adapter = BuildRemoteAdapter();
        var device = NewRemoteDevice();

        // Relay endpoint is not configured, so SendEnvelopeAsync returns the failure echo of the
        // envelope — which must be redacted before it becomes WriteResult.Response.
        var result = await adapter.ApplyAsync(device, new WritePlan { GroupName = "UserManager" }, CancellationToken.None);
        var json = result.Response?.ToJsonString();

        Assert.DoesNotContain("supersecret", json, StringComparison.Ordinal);
    }

    // ── 2. SettingsService persistence + API boundary redaction ─────

    [Fact]
    public async Task SettingsService_ReadAsync_Persists_And_Returns_Redacted_Snapshot()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-security-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "127.0.0.1", LoginName = "admin", Password = "supersecret", EseeId = "unit-test" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new PasswordEchoingAdapter();
        var settings = BuildSettingsService(store, adapter);

        var returned = await settings.ReadAsync(device.Id, CancellationToken.None);
        var returnedJson = JsonSerializer.Serialize(returned);
        Assert.DoesNotContain("supersecret", returnedJson, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.RedactedMarker, returnedJson, StringComparison.Ordinal);

        // The same redaction must land in SQLite (settings_snapshots table), not just the response.
        var persisted = await store.GetSettingsSnapshotAsync(device.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.DoesNotContain("supersecret", JsonSerializer.Serialize(persisted), StringComparison.Ordinal);
    }

    // ── 3. Firmware path allow-list ──────────────────────────────────

    [Fact]
    public void FirmwarePathPolicy_Allows_File_Inside_ArtifactDirectory()
    {
        var dir = CreateTempDir();
        var file = Path.Combine(dir, "fw.bin");
        File.WriteAllBytes(file, [0x01]);
        var options = new BossCamRuntimeOptions { FirmwareArtifactDirectory = dir };

        Assert.True(FirmwarePathPolicy.IsAllowed(file, options, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void FirmwarePathPolicy_Allows_File_Inside_Configured_Extra_Root()
    {
        var dir = CreateTempDir();
        var file = Path.Combine(dir, "fw.bin");
        File.WriteAllBytes(file, [0x01]);
        var options = new BossCamRuntimeOptions
        {
            FirmwareArtifactDirectory = "/nonexistent/artifact",
            FirmwareAllowedDirectories = [dir]
        };

        Assert.True(FirmwarePathPolicy.IsAllowed(file, options, out _));
    }

    [Fact]
    public void FirmwarePathPolicy_Denies_File_Outside_Configured_Roots()
    {
        var root = CreateTempDir();
        var outside = Path.Combine(Path.GetTempPath(), $"bosscam-outside-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(outside, [0x01]);
        try
        {
            var options = new BossCamRuntimeOptions { FirmwareArtifactDirectory = root };
            Assert.False(FirmwarePathPolicy.IsAllowed(outside, options, out var reason));
            Assert.Contains("inside a configured firmware directory", reason, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void FirmwarePathPolicy_Denies_Sibling_Prefix_Not_Masked_As_Inside()
    {
        // /tmp/fw2 must NOT be accepted when the root is /tmp/fw (segment-aware containment).
        var root = CreateTempDir();
        var sibling = root + "2";
        Directory.CreateDirectory(sibling);
        var file = Path.Combine(sibling, "fw.bin");
        File.WriteAllBytes(file, [0x01]);
        try
        {
            var options = new BossCamRuntimeOptions { FirmwareArtifactDirectory = root };
            Assert.False(FirmwarePathPolicy.IsAllowed(file, options, out _));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void FirmwarePathPolicy_Denies_Missing_File()
    {
        var options = new BossCamRuntimeOptions { FirmwareArtifactDirectory = CreateTempDir() };
        Assert.False(FirmwarePathPolicy.IsAllowed("/nonexistent/fw.bin", options, out var reason));
        Assert.Contains("existing firmware file", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwarePathPolicy_Denies_When_No_Roots_Configured()
    {
        var file = Path.Combine(CreateTempDir(), "fw.bin");
        File.WriteAllBytes(file, [0x01]);
        var options = new BossCamRuntimeOptions(); // both root knobs empty

        Assert.False(FirmwarePathPolicy.IsAllowed(file, options, out var reason));
        Assert.Contains("No firmware directory is configured", reason, StringComparison.Ordinal);
    }

    // ── 4. Aegon-LAN batch is config-driven ──────────────────────────

    [Fact]
    public void BuildAegonLanRequests_Maps_Configured_Devices_And_Brand_Passwords()
    {
        var requests = DeviceRegistrationService.BuildAegonLanRequests(
        [
            new AegonLanDeviceOptions { IpAddress = "192.168.1.10", Port = 80, Name = "Front", HardwareModel = "5523-W" },
            new AegonLanDeviceOptions { IpAddress = "192.168.1.11", Port = 8899, HardwareModel = "W5C" },
            new AegonLanDeviceOptions { IpAddress = "192.168.1.12", HardwareModel = "Lorex" }
        ], lorexPassword: "lorex-secret", wvcPassword: "wvc-secret").ToList();

        Assert.Equal(3, requests.Count);
        var juan = requests[0];
        Assert.Equal("Front", juan.Name);
        Assert.Null(juan.Password); // no per-call password mapped for non-W5C/Lorex
        Assert.Equal("wvc-secret", requests[1].Password);
        Assert.Equal("lorex-secret", requests[2].Password);
    }

    [Fact]
    public void BuildAegonLanRequests_Skips_Entries_Without_Ip()
    {
        var requests = DeviceRegistrationService.BuildAegonLanRequests(
        [
            new AegonLanDeviceOptions { IpAddress = "" },
            new AegonLanDeviceOptions { IpAddress = "192.168.1.20" }
        ], lorexPassword: null, wvcPassword: null).ToList();

        Assert.Single(requests);
        Assert.Equal("192.168.1.20", requests[0].IpAddress);
    }

    [Fact]
    public async Task RegisterAegonLanDefaultsAsync_Empty_Config_Registers_Nothing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-aegon-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        // SettingsService is injected but never exercised here (empty Aegon config returns early
        // before RegisterAsync, so no auto clock-sync fires) — an empty adapter set marks it inert.
        var settings = new SettingsService(
            [],
            store,
            new ProtocolValidationService([], new EndpointContractCatalogService(store, NullLogger<EndpointContractCatalogService>.Instance), store, NullLogger<ProtocolValidationService>.Instance),
            NullLogger<SettingsService>.Instance);
        var registration = new DeviceRegistrationService(
            store,
            new Static404HttpClientFactory(),
            new CapabilityProbeService([], store, NullBossCamEventBroadcaster.Instance, NullLogger<CapabilityProbeService>.Instance),
            settings,
            Options.Create(new BossCamRuntimeOptions()), // AegonLanDevices defaults to empty
            NullLogger<DeviceRegistrationService>.Instance);

        var devices = await registration.RegisterAegonLanDefaultsAsync("lorex", "wvc", CancellationToken.None);

        Assert.Empty(devices);
        Assert.Empty(await store.GetDevicesAsync(CancellationToken.None));
    }

    // ── 5. StreamDescriptorAdapter logs probe failures ───────────────

    [Fact]
    public async Task StreamDescriptorAdapter_Logs_Probe_Failure_Instead_Of_Swallowing()
    {
        var logger = new ListLogger<StreamDescriptorAdapter>();
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("connection refused"));
        var adapter = new StreamDescriptorAdapter(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 2 }),
            new HandlerBackedFactory(handler),
            logger);
        var device = new DeviceIdentity
        {
            Id = Guid.NewGuid(),
            IpAddress = "127.0.0.1",
            Port = 8888,
            LoginName = "admin",
            Password = "secret",
            Name = "logging-test",
            HardwareModel = "5523-W"
        };

        _ = await adapter.GetSourcesAsync(device, CancellationToken.None);

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Stream descriptor probe failed", StringComparison.Ordinal));
    }

    // ── helpers ──────────────────────────────────────────────────────

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

    private static OwnedRemoteCommandAdapter BuildRemoteAdapter()
        => new(
            Options.Create(new BossCamRuntimeOptions { RemoteCommandEndpoint = null }),
            new Static404HttpClientFactory(),
            new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), $"bosscam-remote-{Guid.NewGuid():N}.db")
            })),
            NullLogger<OwnedRemoteCommandAdapter>.Instance);

    private static DeviceIdentity NewRemoteDevice() => new()
    {
        Id = Guid.NewGuid(),
        IpAddress = "10.0.0.5",
        Port = 80,
        LoginName = "admin",
        Password = "supersecret",
        EseeId = "unit-test-relay"
    };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bosscam-fwpolicy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Adapter that deliberately echoes a secret-bearing payload, used to prove the
    /// SettingsService boundary redacts regardless of adapter behaviour.</summary>
    private sealed class PasswordEchoingAdapter : IControlAdapter
    {
        public string Name => "PasswordEcho";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;

        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => Task.FromResult(new CapabilityMap { DeviceId = device.Id });

        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
            => ReadAsync(device, cancellationToken);

        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken)
        {
            var secretNode = JsonNode.Parse("{\"authorization\":{\"password\":\"supersecret\"},\"user\":\"admin\"}");
            return Task.FromResult(new SettingsSnapshot
            {
                DeviceId = device.Id,
                AdapterName = Name,
                Groups =
                [
                    new SettingGroup
                    {
                        Name = "Auth",
                        RawPayload = secretNode,
                        Values = new Dictionary<string, SettingValue>
                        {
                            ["Auth"] = new() { Key = "Auth", Value = secretNode, ValueKind = SettingValueKind.Object }
                        }
                    }
                ]
            });
        }

        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
            => Task.FromResult(new WriteResult { Success = true, AdapterName = Name });

        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }

    private sealed class Static404HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Static404Handler());
    }

    private sealed class Static404Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
