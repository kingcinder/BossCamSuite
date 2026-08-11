using System.Net;
using System.Net.Sockets;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Infrastructure.Persistence;
using BossCam.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Microsoft.Extensions.Options;

namespace BossCam.Tests;

/// <summary>
/// Coverage for <see cref="AuthSnapshotService"/> — the ONVIF / RTSP / NetSDK auth-state probe
/// matrix backing <c>POST /api/devices/auth-snapshot</c>. Pins the per-plane verdicts (blank vs
/// admin:admin NetSDK, "check in falied" web gate, ONVIF GetUsers parsing, RTSP DESCRIBE
/// challenge scheme) and the guarantee that no Detail string ever embeds a plaintext password.
/// </summary>
public sealed class AuthSnapshotServiceTests
{
    private readonly ITestOutputHelper _output;

    public AuthSnapshotServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Empty_Request_Snapshots_Every_Stored_Device_With_Ip()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Store.UpsertDevicesAsync(
        [
            new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.1", Name = "cam-a", LoginName = "admin" },
            new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = "10.0.0.2", Name = "cam-b", LoginName = "admin" },
            new DeviceIdentity { Id = Guid.NewGuid(), IpAddress = null, Name = "no-ip" } // skipped
        ], CancellationToken.None);

        var result = await harness.Service.SnapshotAsync(new AuthSnapshotRequest(), CancellationToken.None);

        Assert.Equal(2, result.Devices.Count);
        Assert.Contains(result.Devices, d => d.IpAddress == "10.0.0.1");
        Assert.Contains(result.Devices, d => d.IpAddress == "10.0.0.2");
    }

    [Fact]
    public async Task Bare_Ip_Target_Is_Resolved_Without_Store_Record()
    {
        using var harness = await Harness.CreateAsync();
        harness.Handler.Responder = req => req.RequestUri!.Port == 80
            ? Json(new { }) // any HTTP answer marks the plane reachable
            : throw new HttpRequestException($"refused :{req.RequestUri!.Port}");

        var result = await harness.Service.SnapshotAsync(
            new AuthSnapshotRequest { IpAddresses = ["10.9.9.9"] }, CancellationToken.None);

        var entry = Assert.Single(result.Devices);
        Assert.Equal("10.9.9.9", entry.IpAddress);
        Assert.False(entry.HasStoredCredential);
    }

    [Fact]
    public async Task NetSdk_401_On_Both_Pairs_And_Closed_Gate_Yields_Locked_Verdict()
    {
        using var harness = await Harness.CreateAsync();
        // deviceInfo 401 for every credential; gate returns "check in falied"
        harness.Handler.Responder = req => req.RequestUri!.AbsolutePath.EndsWith("deviceInfo", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : GateClosed();

        var result = await harness.Service.SnapshotAsync(
            new AuthSnapshotRequest { IpAddresses = ["10.0.0.29"] }, CancellationToken.None);

        var entry = Assert.Single(result.Devices);
        Assert.Equal(401, entry.NetSdkBlank.HttpStatusCode);
        Assert.Equal(401, entry.NetSdkAdminAdmin.HttpStatusCode);
        Assert.Equal("closed", entry.WebGateState);
        Assert.StartsWith("locked", entry.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetSdk_200_On_Blank_Yields_Semi_Open_Verdict()
    {
        using var harness = await Harness.CreateAsync();
        // The blank probe authenticates with Basic "admin:" (empty password); the admin:admin
        // probe sends Basic "admin:admin". The responder must tell them apart to simulate the
        // real camera: blank works (200), admin:admin is rejected (401).
        harness.Handler.Responder = req => req.RequestUri!.AbsolutePath.EndsWith("deviceInfo", StringComparison.Ordinal)
            ? IsBlankAdmin(req) ? Json(new { deviceId = "x" }) : new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : GateClosed();

        var result = await harness.Service.SnapshotAsync(
            new AuthSnapshotRequest { IpAddresses = ["10.0.0.227"] }, CancellationToken.None);

        var entry = Assert.Single(result.Devices);
        Assert.Equal(200, entry.NetSdkBlank.HttpStatusCode);
        Assert.Equal(401, entry.NetSdkAdminAdmin.HttpStatusCode);
        Assert.StartsWith("semi-open", entry.Verdict, StringComparison.Ordinal);
        Assert.Contains("blank", entry.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_Web_Gate_Yields_Web_Open_Verdict()
    {
        using var harness = await Harness.CreateAsync();
        harness.Handler.Responder = req => req.RequestUri!.AbsolutePath.EndsWith("deviceInfo", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : GateOpen();

        var result = await harness.Service.SnapshotAsync(
            new AuthSnapshotRequest { IpAddresses = ["10.0.0.9"] }, CancellationToken.None);

        var entry = Assert.Single(result.Devices);
        Assert.Equal("open", entry.WebGateState);
        Assert.StartsWith("web-open", entry.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Onvif_GetUsers_Usernames_Are_Extracted()
    {
        using var harness = await Harness.CreateAsync();
        harness.Handler.Responder = req => req.RequestUri!.AbsolutePath.Contains("onvif", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
                    "<GetUsersResponse xmlns=\"http://www.onvif.org/ver10/device/wsdl\">" +
                    "<User><Username>admin</Username><UserLevel>Administrator</UserLevel></User>" +
                    "</GetUsersResponse></s:Body></s:Envelope>", Encoding.UTF8, "application/soap+xml")
            }
            : req.RequestUri!.AbsolutePath.EndsWith("deviceInfo", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : GateClosed();

        var result = await harness.Service.SnapshotAsync(
            new AuthSnapshotRequest { IpAddresses = ["10.0.0.169"] }, CancellationToken.None);

        var entry = Assert.Single(result.Devices);
        Assert.Equal(["admin"], entry.OnvifUsers);
        Assert.True(entry.Onvif.Reachable);
    }

    [Fact]
    public async Task No_Detail_String_Contains_A_Plaintext_Password()
    {
        using var harness = await Harness.CreateAsync();
        harness.Handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var result = await harness.Service.SnapshotAsync(
            new AuthSnapshotRequest { IpAddresses = ["10.0.0.29"] }, CancellationToken.None);

        var entry = Assert.Single(result.Devices);
        var details = new[]
        {
            entry.NetSdkBlank.Detail, entry.NetSdkAdminAdmin.Detail, entry.WebGate.Detail,
            entry.Onvif.Detail, entry.RtspTcp.Detail, entry.RtspPlayable.Detail, entry.RtspDescribe.Detail
        };
        foreach (var detail in details)
        {
            Assert.DoesNotContain("admin:admin", detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BossCam2026", detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Rtsp_Describe_Captures_Status_And_Digest_Challenge()
    {
        using var harness = await Harness.CreateAsync();
        harness.Handler.Responder = req => req.RequestUri!.AbsolutePath.EndsWith("deviceInfo", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : GateClosed();

        // The snapshot service hardcodes :554 for RTSP, so the only way to feed it the fake
        // RTSP server is to bind the real :554 on loopback. That port is privileged (<1024)
        // and may be unavailable on shared/CI hosts — then the test SKIPS (documented
        // behavior) instead of failing.
        var entry = await ProbeRtspThroughLoopbackAsync(harness);
        if (entry is null)
        {
            _output.WriteLine("skip: cannot bind :554 (privileged port) on this host — RTSP DESCRIBE path not exercised.");
            return;
        }

        Assert.Equal(401, entry.RtspDescribe.HttpStatusCode);
        Assert.Equal("Digest", entry.RtspChallengeScheme);
    }

    /// <summary>
    /// Binds the fake RTSP server on the real :554 (the service hardcodes that port) and runs
    /// a full snapshot against 127.0.0.1. Returns null — signaling a SKIP — when :554 cannot
    /// be bound (privileged port, shared/CI hosts).
    /// </summary>
    private static async Task<AuthSnapshotEntry?> ProbeRtspThroughLoopbackAsync(Harness harness)
    {
        var owner = TryBind554();
        if (owner is null)
        {
            return null;
        }

        var fake = owner;
        _ = Task.Run(async () =>
        {
            using var client = await fake.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buf = new byte[1024];
            var read = await stream.ReadAsync(buf);
            _ = read;
            var response = "RTSP/1.0 401 Unauthorized\r\n" +
                "WWW-Authenticate: Digest realm=\"rtsp\", nonce=\"abc123\"\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        });
        try
        {
            var result = await harness.Service.SnapshotAsync(
                new AuthSnapshotRequest { IpAddresses = ["127.0.0.1"] }, CancellationToken.None);
            return Assert.Single(result.Devices);
        }
        finally
        {
            fake.Stop();
        }
    }

    private static TcpListener? TryBind554()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 554);
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static HttpResponseMessage GateClosed() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "<user ver=\"1.0\" you=\"\" add_user=\"no\" ret=\"sorry\" mesg=\"check in falied\"></user>",
            Encoding.UTF8, "text/xml")
    };

    private static HttpResponseMessage GateOpen() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "<user ver=\"1.0\" you=\"admin\" add_user=\"yes\" ret=\"ok\" mesg=\"\"></user>",
            Encoding.UTF8, "text/xml")
    };

    private static bool IsBlankAdmin(HttpRequestMessage req)
    {
        var auth = req.Headers.Authorization;
        if (auth is null || auth.Scheme != "Basic" || string.IsNullOrEmpty(auth.Parameter))
        {
            return false;
        }
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter)) == "admin:";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class Harness : IDisposable
    {
        private readonly string _dbPath;

        private Harness(string dbPath, SqliteApplicationStore store, ScriptedHandler handler, AuthSnapshotService service)
        {
            _dbPath = dbPath;
            Store = store;
            Handler = handler;
            Service = service;
        }

        public SqliteApplicationStore Store { get; }
        public ScriptedHandler Handler { get; }
        public AuthSnapshotService Service { get; }

        public static async Task<Harness> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"bosscam-authsnap-{Guid.NewGuid():N}.db");
            var store = new SqliteApplicationStore(Options.Create(new BossCamRuntimeOptions { DatabasePath = dbPath }));
            await store.InitializeAsync(CancellationToken.None);
            var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var factory = new HandlerBackedFactory(handler);
            var service = new AuthSnapshotService(
                store,
                factory,
                Options.Create(new BossCamRuntimeOptions { HttpTimeoutSeconds = 4, OnvifProbePorts = [8888] }),
                NullLogger<AuthSnapshotService>.Instance);
            return new Harness(dbPath, store, handler, service);
        }

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Responder(request));
    }

    private sealed class HandlerBackedFactory(ScriptedHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
