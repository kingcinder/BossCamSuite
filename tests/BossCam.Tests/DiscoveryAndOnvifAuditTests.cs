using System.Net;
using System.Text.Json.Nodes;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Discovery;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Regression coverage for the discovery/ONVIF audit pass:
/// 1. SettingsService.WriteAsync persists a REDACTED SnapshotBeforeWrite (defense-in-depth seam).
/// 2. DiscoveryCoordinator merge keys on MAC first, IP second (DHCP renumber / IP-reuse safety).
/// 3. SubnetScanDiscoveryProvider runs only as a fallback (or on explicit range request) and
///    requires a 200 + NetSDK-shaped body — not "any HTTP response".
/// 4. OnvifDiscoveryProvider validates ProbeMatches (NetworkVideoTransmitter) and tries all XAddrs.
/// 5. OnvifImagingControlAdapter maps fields to real SOAP elements (no more Exposure.MANUAL stub),
///    parses SOAP with XDocument (entity-safe), and prefers the discovered XAddr.
/// </summary>
public sealed class DiscoveryAndOnvifAuditTests
{
    // ── 1. SnapshotBeforeWrite redaction ────────────────────────────

    [Fact]
    public async Task SettingsService_WriteAsync_Persists_Redacted_SnapshotBeforeWrite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-snapwrite-{Guid.NewGuid():N}.db");
        var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
        var device = new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "127.0.0.1", LoginName = "admin", Password = "supersecret", EseeId = "unit-test" };
        await store.UpsertDevicesAsync([device], CancellationToken.None);

        var adapter = new SnapshotEchoAdapter();
        var settings = BuildSettingsService(store, adapter);

        _ = await settings.WriteAsync(device.Id, new WritePlan
        {
            Endpoint = "/NetSDK/Image/brightness",
            Method = "PUT",
            Payload = new JsonObject { ["brightness"] = 60 },
            SnapshotBeforeWrite = true,
            RequireWriteVerification = false,
            AllowRollback = false
        }, CancellationToken.None);

        var persisted = await store.GetSettingsSnapshotAsync(device.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.DoesNotContain("supersecret", System.Text.Json.JsonSerializer.Serialize(persisted), StringComparison.Ordinal);
    }

    // ── 2. MAC-first merge key + subnet fallback ────────────────────

    [Fact]
    public async Task DiscoveryCoordinator_Merges_By_Mac_Not_Ip_So_Ip_Reuse_Does_Not_Collide()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var cam = new DeviceIdentity { Name = "5523-W", DeviceType = "IPC", IpAddress = "10.0.0.4", MacAddress = "AA:BB:CC:DD:EE:01" };
        var foreign = new DeviceIdentity { Name = "laptop", DeviceType = "OTHER", IpAddress = "10.0.0.4", MacAddress = "11:22:33:44:55:66" };
        await store.UpsertDevicesAsync([cam, foreign], CancellationToken.None);

        var coordinator = BuildCoordinator(store, []);

        _ = await coordinator.RunAsync(CancellationToken.None);

        var devices = await store.GetDevicesAsync(CancellationToken.None);
        // Same IP but different MACs MUST NOT merge — the old IP-only key would have collapsed
        // the foreign host into the camera's slot and inherited its credentials.
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task DiscoveryCoordinator_Merges_Same_Mac_Across_Dhcp_Renumbered_Ips()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        // Prior pass persisted the camera with credentials at the old lease.
        var leaseOld = new DeviceIdentity { Name = "5523-W", DeviceType = "IPC", IpAddress = "10.0.0.4", MacAddress = "AA:BB:CC:DD:EE:01", LoginName = "admin" };
        await store.UpsertDevicesAsync([leaseOld], CancellationToken.None);

        // Next discovery pass finds the same camera (stable MAC) at a NEW IP. Seeding both leases
        // into the store first would collide on the same dedupe key and the later row would
        // overwrite the credentials — the realistic flow is store-row + new provider result,
        // which is exactly what the coordinator's in-memory merge combines.
        var leaseNew = new DeviceIdentity { Name = "5523-W", DeviceType = "IPC", IpAddress = "10.0.0.5", MacAddress = "AA:BB:CC:DD:EE:01" };
        var coordinator = BuildCoordinator(store, [new StubPassiveProvider(leaseNew)]);

        _ = await coordinator.RunAsync(CancellationToken.None);

        var devices = await store.GetDevicesAsync(CancellationToken.None);
        // DHCP renewal changed the IP but the MAC is stable → one identity, credentials preserved.
        var single = Assert.Single(devices);
        Assert.Equal("admin", single.LoginName);
    }

    [Fact]
    public async Task DiscoveryCoordinator_Runs_Subnet_Scan_Only_When_Passive_Finds_Nothing()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var subnet = new CountingSubnetProvider();

        // Passive provider found a device → subnet sweep must be skipped (fallback semantics).
        var coordinator = BuildCoordinator(store, [new StubPassiveProvider(new DeviceIdentity { IpAddress = "10.0.0.9" })], subnet);
        _ = await coordinator.RunAsync(CancellationToken.None);
        Assert.Equal(0, subnet.Invocations);
    }

    [Fact]
    public async Task DiscoveryCoordinator_Subnet_Scan_Fires_When_Passive_Finds_Nothing()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var subnet = new CountingSubnetProvider();

        var coordinator = BuildCoordinator(store, [new StubPassiveProvider()], subnet);
        _ = await coordinator.RunAsync(CancellationToken.None);

        Assert.Equal(1, subnet.Invocations);
        Assert.Null(subnet.LastOverride); // cleared after the pass
    }

    [Fact]
    public async Task DiscoveryCoordinator_Explicit_Range_Forces_Subnet_Scan_And_Passes_Override()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        var subnet = new CountingSubnetProvider();

        var coordinator = BuildCoordinator(store, [new StubPassiveProvider(new DeviceIdentity { IpAddress = "10.0.0.9" })], subnet);
        _ = await coordinator.RunAsync("10.0.0.0/24", CancellationToken.None);

        // Even though passive found a device, an explicit range request forces the sweep and the
        // override is delivered (then cleared) so the scanner restricts to that /24.
        Assert.Equal(1, subnet.Invocations);
        Assert.Equal("10.0.0.0/24", subnet.LastOverride);
        Assert.Null(subnet.SubnetRangeOverride);
    }

    // ── 3. Subnet scan acceptance bar ───────────────────────────────

    [Theory]
    [InlineData("""{"serial":"SN123456","model":"5523-w","firmware":"v1.0.0","mac":"AA:BB"}""", true)]
    [InlineData("""{"model":"5523-w"}""", true)]
    [InlineData("""{"deviceName":"Dome"}""", true)]
    [InlineData("""{"name":"Some NAS API"}""", false)] // generic key must NOT pass the bar
    [InlineData("""{"hello":"world"}""", false)]
    [InlineData("""[{"serial":"x"}]""", false)] // array, not an object
    [InlineData("<!DOCTYPE html><html><body>404</body></html>", false)]
    [InlineData("", false)]
    public void SubnetScanDiscoveryProvider_Accepts_Only_NetSdk_Shaped_Bodies(string body, bool expected)
    {
        Assert.Equal(expected, SubnetScanDiscoveryProvider.LooksLikeNetSdkDeviceInfo(body));
    }

    // ── 4. ONVIF WS-Discovery ProbeMatch validation ─────────────────

    [Fact]
    public void OnvifDiscoveryProvider_Accepts_NetworkVideoTransmitter_ProbeMatch()
    {
        var xml = ProbeMatch("http://10.0.0.7/onvif/device_service", "dn:NetworkVideoTransmitter");
        var device = OnvifDiscoveryProvider.TryParseProbeMatch(xml, IPAddress.Parse("10.0.0.1"), NullLogger<OnvifDiscoveryProvider>.Instance);

        Assert.NotNull(device);
        Assert.Equal("10.0.0.7", device!.IpAddress);
        Assert.Equal("ONVIF", device.DeviceType);
        Assert.Contains("http://10.0.0.7/onvif/device_service", device.Metadata["xaddrs"]);
    }

    [Fact]
    public void OnvifDiscoveryProvider_Rejects_Non_Camera_ProbeMatch()
    {
        // Printer answers WS-Discovery but does not claim NetworkVideoTransmitter.
        var xml = ProbeMatch("http://10.0.0.8/printer", "dn:Printer");
        var device = OnvifDiscoveryProvider.TryParseProbeMatch(xml, IPAddress.Parse("10.0.0.1"), NullLogger<OnvifDiscoveryProvider>.Instance);

        Assert.Null(device);
    }

    [Fact]
    public void OnvifDiscoveryProvider_Tries_Every_XAddr_Not_Just_The_First()
    {
        // First XAddr does not parse as an absolute URI; the second (valid) one must be picked
        // instead of committing to the first entry blindly.
        var xml = ProbeMatch("not-a-uri http://10.0.0.42/onvif/device_service", "dn:NetworkVideoTransmitter");
        var device = OnvifDiscoveryProvider.TryParseProbeMatch(xml, IPAddress.Parse("10.0.0.1"), NullLogger<OnvifDiscoveryProvider>.Instance);

        Assert.NotNull(device);
        Assert.Equal("10.0.0.42", device!.IpAddress);
        Assert.Equal(80, device.Port);
    }

    [Fact]
    public void OnvifDiscoveryProvider_Falls_Back_To_Responder_Address_Without_XAddr()
    {
        var xml = ProbeMatch("", "dn:NetworkVideoTransmitter");
        var device = OnvifDiscoveryProvider.TryParseProbeMatch(xml, IPAddress.Parse("10.0.0.9"), NullLogger<OnvifDiscoveryProvider>.Instance);

        Assert.NotNull(device);
        Assert.Equal("10.0.0.9", device!.IpAddress);
        Assert.Equal($"http://10.0.0.9/onvif/device_service", device.Metadata["xaddrs"]);
    }

    [Fact]
    public void OnvifDiscoveryProvider_Extracts_Mac_From_Scopes_For_Mac_First_Merge()
    {
        // Scopes commonly advertise onvif://www.onvif.org/mac/aa:bb:cc:dd:ee:ff; the parse must
        // surface it on MacAddress so MAC-first merge keying (coordinator + store) collapses the
        // ONVIF copy with a HiChip/SubnetScan copy of the same camera instead of fragmenting.
        var xml = ProbeMatchWithScopes(
            "http://10.0.0.7/onvif/device_service",
            "dn:NetworkVideoTransmitter",
            "onvif://www.onvif.org/name/Camera onvif://www.onvif.org/mac/aa:bb:cc:dd:ee:ff");
        var device = OnvifDiscoveryProvider.TryParseProbeMatch(xml, IPAddress.Parse("10.0.0.1"), NullLogger<OnvifDiscoveryProvider>.Instance);

        Assert.NotNull(device);
        Assert.Equal("aa:bb:cc:dd:ee:ff", device!.MacAddress);
    }

    // ── 5. ONVIF field → SOAP mapping + XDocument parsing ───────────

    [Fact]
    public void OnvifAdapter_BuildImagingSettingsElement_Maps_Brightness_To_Tt_Element()
    {
        var element = OnvifImagingControlAdapter.BuildImagingSettingsElement("brightness", JsonValue.Create(60));

        Assert.NotNull(element);
        Assert.Contains("<tt:Brightness", element, StringComparison.Ordinal);
        Assert.Contains(">60<", element, StringComparison.Ordinal);
    }

    [Fact]
    public void OnvifAdapter_BuildImagingSettingsElement_Maps_Exposure_Mode()
    {
        var element = OnvifImagingControlAdapter.BuildImagingSettingsElement("exposure", JsonValue.Create("MANUAL"));

        Assert.NotNull(element);
        Assert.Contains("<tt:Exposure", element, StringComparison.Ordinal);
        Assert.Contains("<tt:Mode>MANUAL</tt:Mode>", element, StringComparison.Ordinal);
    }

    [Fact]
    public void OnvifAdapter_BuildImagingSettingsElement_Unmapped_Field_Returns_Null()
    {
        // mirror/encoder fields have no SetImagingSettings mapping → must NOT silently write
        // Exposure.MANUAL (the old stub) nor report success.
        Assert.Null(OnvifImagingControlAdapter.BuildImagingSettingsElement("mirror", JsonValue.Create(true)));
        Assert.Null(OnvifImagingControlAdapter.BuildImagingSettingsElement("resolution", JsonValue.Create("1920x1080")));
    }

    [Fact]
    public async Task OnvifAdapter_ApplyAsync_Unmapped_Field_Fails_Loudly_Without_Network()
    {
        // A handler that throws on ANY request proves the mapping check happens before the network.
        var adapter = new OnvifImagingControlAdapter(
            Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 2 }),
            new ThrowingHttpClientFactory(),
            NullLogger<OnvifImagingControlAdapter>.Instance);
        var device = new DeviceIdentity { IpAddress = "10.0.0.7", Port = 8899, LoginName = "admin", Password = "pw" };

        var result = await adapter.ApplyAsync(device, new WritePlan
        {
            ContractKey = "image.mirror",
            Endpoint = "/NetSDK/Video/input/channel/1",
            Method = "PUT",
            Payload = new JsonObject { ["mirrorEnabled"] = true }
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no mapped SetImagingSettings element", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnvifAdapter_ExtractFieldValue_Handles_Level_Suffixed_Payload_Keys()
    {
        var payload = new JsonObject { ["brightnessLevel"] = 42 };
        var value = OnvifImagingControlAdapter.ExtractFieldValue(payload, "brightness");

        Assert.Equal(42, value?.GetValue<int>());
    }

    [Fact]
    public void OnvifAdapter_ResolveFieldKey_Prefers_Contract_Key()
    {
        var plan = new WritePlan { ContractKey = "image.brightness", Endpoint = "/onvif/image_service", Method = "PUT" };
        Assert.Equal("brightness", OnvifImagingControlAdapter.ResolveFieldKey(plan));
    }

    [Fact]
    public void OnvifAdapter_ResolveVideoSourceToken_Reads_SourceToken_From_GetProfiles()
    {
        var profilesXml = """
            <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
              <trt:Profiles token="PROFILE_000">
                <tt:VideoSourceConfiguration token="vs0" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tt:SourceToken>vs0</tt:SourceToken>
                </tt:VideoSourceConfiguration>
              </trt:Profiles>
            </trt:GetProfilesResponse>
            """;

        Assert.Equal("vs0", OnvifImagingControlAdapter.ResolveVideoSourceToken(profilesXml));
        Assert.Null(OnvifImagingControlAdapter.ResolveVideoSourceToken("<broken"));
    }

    [Fact]
    public void OnvifAdapter_ExtractTag_Decodes_Xml_Entities_In_Stream_Uri()
    {
        // Dahua/Hikvision GetStreamUri legally escapes '&' as &amp;; the old regex returned the
        // literal &amp; and broke the RTSP query separator.
        var xml = """<tt:GetStreamUriResponse xmlns:tt="http://www.onvif.org/ver10/schema"><tt:Uri>rtsp://10.0.0.7/cam/realmonitor?channel=1&amp;subtype=0</tt:Uri></tt:GetStreamUriResponse>""";

        var uri = MultiBrandHighResTransportAdapter.ExtractTag(xml, "Uri");

        Assert.Equal("rtsp://10.0.0.7/cam/realmonitor?channel=1&subtype=0", uri);
    }

    [Fact]
    public void OnvifAdapter_BuildDeviceServiceCandidates_Prefers_Discovered_XAddr()
    {
        var adapter = new OnvifImagingControlAdapter(
            Options.Create(new BossCamRuntimeOptions { OnvifProbePorts = [8899, 8888] }),
            new NullHttpClientFactory(),
            NullLogger<OnvifImagingControlAdapter>.Instance);
        var device = new DeviceIdentity
        {
            IpAddress = "10.0.0.7",
            Port = 80,
            Metadata = new Dictionary<string, string>
            {
                ["xaddrs"] = "http://10.0.0.7:8080/onvif/device_service"
            }
        };

        var candidates = adapter.BuildDeviceServiceCandidates(device, appendDevicePort: true);

        Assert.Equal("http://10.0.0.7:8080/onvif/device_service", candidates[0]);
        Assert.Contains("http://10.0.0.7:8899/onvif/device_service", candidates);
        Assert.Contains("http://10.0.0.7:80/onvif/device_service", candidates);
    }

    // ── 6. WS-Security UsernameToken ────────────────────────────────

    [Fact]
    public void OnvifWsse_Computes_Spec_Correct_PasswordDigest()
    {
        // WS-Security UsernameToken profile: PasswordDigest = Base64(SHA1(nonce + created + password))
        // where nonce is the RAW bytes and created/password are UTF-8. Deterministic nonce bytes
        // pin the exact wire format so a regression in the digest never silently weakens auth.
        byte[] nonce = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var created = "2024-01-01T00:00:00Z";

        var digest = OnvifWsse.ComputePasswordDigest(nonce, created, "hunter2");
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
            [.. nonce, .. System.Text.Encoding.UTF8.GetBytes(created + "hunter2")]));

        Assert.Equal(expected, digest);
        Assert.NotEqual(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hunter2")), digest);
    }

    [Fact]
    public void OnvifWsse_BuildSecurityHeader_Carries_Username_Nonce_And_Digest()
    {
        var header = OnvifWsse.BuildSecurityHeader("admin", "hunter2");

        Assert.Contains("<wsse:Username>admin</wsse:Username>", header, StringComparison.Ordinal);
        Assert.Contains("PasswordDigest", header, StringComparison.Ordinal);
        Assert.Contains("<wsse:Nonce", header, StringComparison.Ordinal);
        Assert.Contains("<wsu:Created>", header, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", header, StringComparison.Ordinal); // plaintext never leaves the header
    }

    // ── helpers ─────────────────────────────────────────────────────

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

    private static SqliteApplicationStore CreateStore()
        => new(Options.Create(new BossCamRuntimeOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"bosscam-audit-{Guid.NewGuid():N}.db")
        }));

    private static DiscoveryCoordinator BuildCoordinator(SqliteApplicationStore store, IReadOnlyList<IDiscoveryProvider> providers, params ISubnetScanDiscoveryProvider[] subnetProviders)
        => new(
            providers.Concat(subnetProviders).ToList(),
            [],
            store,
            NullBossCamEventBroadcaster.Instance,
            new AuditFakeHostEnvironment { EnvironmentName = Environments.Production },
            Options.Create(new BossCamRuntimeOptions()),
            NullLogger<DiscoveryCoordinator>.Instance);

    private static string ProbeMatch(string xAddrs, string types)
        => ProbeMatchWithScopes(xAddrs, types, "onvif://www.onvif.org/name/Camera");

    private static string ProbeMatchWithScopes(string xAddrs, string types, string scopes)
        => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <e:Header><w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</w:Action></e:Header>
              <e:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <w:Address>urn:uuid:test</w:Address>
                    <d:Types>{types}</d:Types>
                    <d:Scopes>{scopes}</d:Scopes>
                    <d:XAddrs>{xAddrs}</d:XAddrs>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </e:Body>
            </e:Envelope>
            """;

    /// <summary>Adapter that echoes a secret-bearing payload in its snapshot — proves the
    /// SettingsService boundary redacts the pre-write snapshot too, not just ReadAsync.</summary>
    private sealed class SnapshotEchoAdapter : IControlAdapter
    {
        public string Name => "SnapshotEcho";
        public int Priority => 1;
        public TransportKind TransportKind => TransportKind.LanRest;
        public Task<bool> CanHandleAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CapabilityMap> ProbeAsync(DeviceIdentity device, CancellationToken cancellationToken) => Task.FromResult(new CapabilityMap { DeviceId = device.Id });

        public Task<SettingsSnapshot> SnapshotAsync(DeviceIdentity device, CancellationToken cancellationToken)
        {
            var secretNode = JsonNode.Parse("""{"authorization":{"password":"supersecret"},"user":"admin"}""");
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

        public Task<SettingsSnapshot> ReadAsync(DeviceIdentity device, CancellationToken cancellationToken) => SnapshotAsync(device, cancellationToken);

        public Task<WriteResult> ApplyAsync(DeviceIdentity device, WritePlan plan, CancellationToken cancellationToken)
            => Task.FromResult(new WriteResult { Success = true, AdapterName = Name, StatusCode = 200 });

        public Task<MaintenanceResult> ExecuteMaintenanceAsync(DeviceIdentity device, MaintenanceOperation operation, JsonObject? payload, CancellationToken cancellationToken)
            => Task.FromResult(new MaintenanceResult { Success = true, AdapterName = Name, Operation = operation });
    }

    private sealed class CountingSubnetProvider : ISubnetScanDiscoveryProvider
    {
        public int Invocations;
        public string? LastOverride;

        public string Name => "CountingSubnet";
        public string? SubnetRangeOverride { get; set; }

        public Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
        {
            Invocations++;
            LastOverride = SubnetRangeOverride;
            return Task.FromResult<IReadOnlyCollection<DeviceIdentity>>([]);
        }
    }

    private sealed class StubPassiveProvider(params DeviceIdentity[] devices) : IDiscoveryProvider
    {
        public string Name => "StubPassive";
        public Task<IReadOnlyCollection<DeviceIdentity>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<DeviceIdentity>>(devices);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"Unexpected network call: {request.RequestUri}");
    }

    private sealed class AuditFakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "BossCamTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
