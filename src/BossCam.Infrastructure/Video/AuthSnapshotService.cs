using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Runs the ONVIF / RTSP / NetSDK auth-state probe matrix against one or more cameras
/// and returns a structured report (the <c>POST /api/devices/auth-snapshot</c> backing
/// service). Each plane is probed independently and reported with its exact status so
/// the operator can see at a glance which planes answer and which credentials work.
///
/// Planes probed per camera:
///   * NetSDK REST deviceInfo with <c>admin:</c> (blank) and <c>admin:admin</c>
///   * web user-management gate <c>/user/user_list.xml</c> (open vs "check in falied")
///   * ONVIF GetUsers with <c>admin:admin</c> on the device-service ports
///   * RTSP :554 (TCP open), OPTIONS playability, and DESCRIBE status + challenge scheme
///
/// Reuses the established probe primitives (RtspProbe, NetSdkPortCandidates) and the
/// ONVIF SOAP envelope shape from <see cref="OnvifCredentialScanner"/>.
/// </summary>
// CS9113: 'logger' is reserved for future diagnostic logging; suppressed intentionally.
#pragma warning disable CS9113
public sealed class AuthSnapshotService(
    IApplicationStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<BossCamRuntimeOptions> options,
    ILogger<AuthSnapshotService> logger)
#pragma warning restore CS9113
{
    private const string GetUsersBody =
        "<tds:GetUsers xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"/>";

    private static readonly int[] OnvifDefaults = [8899, 8888, 80];

    /// <summary>
    /// Snapshot the auth state of the requested targets. An empty request (or one with no
    /// ids and no IPs) resolves to every stored device that has an IP address.
    /// </summary>
    public async Task<AuthSnapshotResult> SnapshotAsync(
        AuthSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var targets = await ResolveTargetsAsync(request, cancellationToken);
        if (targets.Count == 0)
        {
            return new AuthSnapshotResult
            {
                Message = "No devices resolved — supply DeviceIds / IpAddresses or enroll devices first.",
                Devices = []
            };
        }

        var entries = new List<AuthSnapshotEntry>(targets.Count);
        foreach (var device in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await SnapshotDeviceAsync(device, cancellationToken));
        }

        var open = entries.Count(e => e.WebGateState == "open");
        var netSdk = entries.Count(e => e.NetSdkBlank.HttpStatusCode == 200 || e.NetSdkAdminAdmin.HttpStatusCode == 200);

        return new AuthSnapshotResult
        {
            Devices = entries,
            Message = $"Snapshot complete: {entries.Count} device(s); " +
                $"{netSdk} NetSDK-open, {open} web-gate-open."
        };
    }

    private async Task<List<DeviceIdentity>> ResolveTargetsAsync(
        AuthSnapshotRequest request, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceIdentity>();

        if (request.DeviceIds.Count == 0 && request.IpAddresses.Count == 0)
        {
            var all = await store.GetDevicesAsync(cancellationToken);
            return all.Where(static d => !string.IsNullOrWhiteSpace(d.IpAddress)).ToList();
        }

        foreach (var id in request.DeviceIds)
        {
            var device = await store.GetDeviceAsync(id, cancellationToken);
            if (device is not null)
            {
                devices.Add(device);
            }
        }

        foreach (var ip in request.IpAddresses)
        {
            if (string.IsNullOrWhiteSpace(ip)) continue;
            if (!devices.Any(d => string.Equals(d.IpAddress, ip, StringComparison.OrdinalIgnoreCase)))
            {
                devices.Add(new DeviceIdentity
                {
                    Name = $"auth-snapshot-{ip}",
                    IpAddress = ip,
                    Port = 80,
                    DeviceType = "IPC"
                });
            }
        }

        return devices;
    }

    private async Task<AuthSnapshotEntry> SnapshotDeviceAsync(
        DeviceIdentity device, CancellationToken cancellationToken)
    {
        var ip = device.IpAddress!;
        var timeout = TimeSpan.FromSeconds(Math.Max(3, options.Value.HttpTimeoutSeconds));

        var netSdkBlank = await ProbeNetSdkAsync(ip, device.Port, "admin", "", timeout, cancellationToken);
        var netSdkAdmin = await ProbeNetSdkAsync(ip, device.Port, "admin", "admin", timeout, cancellationToken);
        var webGate = await ProbeWebGateAsync(ip, timeout, cancellationToken);
        var onvif = await ProbeOnvifGetUsersAsync(ip, device, timeout, cancellationToken);
        var rtspTcp = await ProbeTcpPortAsync(ip, 554, cancellationToken);
        var rtspPlayable = await ProbeRtspOptionsAsync(ip, 554, cancellationToken);
        var rtspDescribe = await ProbeRtspDescribeAsync(ip, 554, cancellationToken);

        var verdict = BuildVerdict(netSdkBlank, netSdkAdmin, webGate.State, onvif.Result.Reachable, rtspTcp.Reachable);

        return new AuthSnapshotEntry
        {
            DeviceId = device.Id,
            IpAddress = ip,
            DisplayName = device.DisplayName,
            HardwareModel = device.HardwareModel,
            FirmwareVersion = device.FirmwareVersion,
            LoginName = device.LoginName,
            HasStoredCredential = !string.IsNullOrWhiteSpace(device.LoginName),
            Verdict = verdict,
            NetSdkBlank = netSdkBlank,
            NetSdkAdminAdmin = netSdkAdmin,
            WebGate = webGate.Result,
            WebGateState = webGate.State,
            Onvif = onvif.Result,
            OnvifUsers = onvif.Users,
            RtspTcp = rtspTcp,
            RtspPlayable = rtspPlayable,
            RtspDescribe = rtspDescribe.Result,
            RtspChallengeScheme = rtspDescribe.ChallengeScheme
        };
    }

    private static string BuildVerdict(
        AuthPlaneResult netSdkBlank, AuthPlaneResult netSdkAdmin, string? webGateState,
        bool onvifReachable, bool rtspTcpReachable)
    {
        var netSdkOpen = netSdkBlank.HttpStatusCode == 200 || netSdkAdmin.HttpStatusCode == 200;
        if (netSdkOpen)
        {
            var working = netSdkBlank.HttpStatusCode == 200 ? "blank" : "admin:admin";
            return $"semi-open (NetSDK works with {working})";
        }
        if (webGateState == "open")
        {
            return "web-open";
        }
        if (netSdkBlank.HttpStatusCode is not null || netSdkAdmin.HttpStatusCode is not null
            || onvifReachable || rtspTcpReachable)
        {
            return "locked (planes reachable but auth-gated)";
        }
        return "offline";
    }

    private async Task<AuthPlaneResult> ProbeNetSdkAsync(
        string ip, int recordedPort, string user, string password,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Recorded port first, then :80 (5523-W: NetSDK REST surface answers on :80 even when the
        // recorded port is the ONVIF/media port). NetSdkPortCandidates.For gives exactly that order.
        foreach (var port in NetSdkPortCandidates.For(recordedPort))
        {
            var result = await ProbeHttpGetAsync(
                $"http://{ip}:{port}/NetSDK/System/deviceInfo", user, password, timeout, cancellationToken);
            if (result.HttpStatusCode is not null)
            {
                return result;
            }
        }
        return new AuthPlaneResult { Detail = $"NetSDK unreachable on all candidate ports ({user}:***)." };
    }

    private async Task<(AuthPlaneResult Result, string? State)> ProbeWebGateAsync(
        string ip, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (outcome, state) = await ProbeHttpGetWithStateAsync(
            $"http://{ip}:80/user/user_list.xml", timeout, cancellationToken,
            static body => body.Contains("check in falied", StringComparison.OrdinalIgnoreCase)
                ? "closed"
                : body.Contains('<') ? "open" : null);
        return (
            outcome with { Detail = $"{outcome.Detail} (gate state: {state ?? "unknown"})" },
            state);
    }

    private async Task<(AuthPlaneResult Result, IReadOnlyCollection<string> Users)> ProbeOnvifGetUsersAsync(
        string ip, DeviceIdentity device, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ports = BuildOnvifPorts(device);
        foreach (var port in ports)
        {
            var url = $"http://{ip}:{port}/onvif/device_service";
            var envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">"
                + "<s:Body>" + GetUsersBody + "</s:Body></s:Envelope>";

            var (outcome, body) = await ProbeSoapAsync(url, envelope, "admin", "admin", timeout, cancellationToken);
            if (body is not null)
            {
                var users = ExtractUsernames(body);
                return (
                    outcome with { Detail = $"ONVIF GetUsers {url} → {outcome.Detail}; users: {string.Join(", ", users.Count == 0 ? ["(none)"] : users)}" },
                    users);
            }
            if (outcome.HttpStatusCode is not null)
            {
                // Port answered but SOAP failed (e.g. auth rejected) — record and keep probing other ports.
                continue;
            }
        }
        return (
            new AuthPlaneResult { Detail = "ONVIF unreachable on all candidate ports (admin:***)." },
            []);
    }

    private async Task<(AuthPlaneResult Result, string? ChallengeScheme)> ProbeRtspDescribeAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, cts.Token);
            await using var stream = client.GetStream();

            var request = Encoding.ASCII.GetBytes(
                $"DESCRIBE rtsp://{ip}:{port}/ch0_main.h264 RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n");
            await stream.WriteAsync(request, cts.Token);
            await stream.FlushAsync(cts.Token);

            var buffer = new byte[2048];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token);
                if (read <= 0) break;
                total += read;
                if (ContainsDoubleCrlf(buffer, total)) break;
            }

            var head = Encoding.ASCII.GetString(buffer, 0, total);
            var statusLine = head.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            var status = ParseRtspStatus(statusLine);
            var scheme = ExtractChallengeScheme(head);

            sw.Stop();
            return (
                new AuthPlaneResult
                {
                    Reachable = status is not null,
                    HttpStatusCode = status,
                    Detail = $"RTSP DESCRIBE {ip}:{port} → {statusLine} in {sw.ElapsedMilliseconds}ms",
                    LatencyMs = (int)sw.ElapsedMilliseconds
                },
                scheme);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (
                new AuthPlaneResult { Detail = $"RTSP DESCRIBE {ip}:{port} — {ex.GetType().Name}: {ex.Message}" },
                null);
        }
    }

    private static bool ContainsDoubleCrlf(byte[] buffer, int length)
    {
        for (var i = 0; i < length - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return true;
            }
        }
        return false;
    }

    private static int? ParseRtspStatus(string statusLine)
    {
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : null;
    }

    private static string? ExtractChallengeScheme(string head)
    {
        var index = head.IndexOf("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var rest = head[index..];
        var first = rest.Split('\n').FirstOrDefault() ?? string.Empty;
        var colon = first.IndexOf(':');
        if (colon < 0) return null;
        var value = first[(colon + 1)..].Trim();
        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }

    private static IReadOnlyCollection<string> ExtractUsernames(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("Username", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .Where(static u => u.Length > 0)
                .Distinct()
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private List<int> BuildOnvifPorts(DeviceIdentity device)
    {
        var ports = new List<int>();
        if (device.OnvifMediaPort is > 0) ports.Add(device.OnvifMediaPort.Value);
        foreach (var p in options.Value.OnvifProbePorts)
        {
            if (p > 0 && !ports.Contains(p)) ports.Add(p);
        }
        foreach (var p in OnvifDefaults)
        {
            if (!ports.Contains(p)) ports.Add(p);
        }
        return ports;
    }

    private async Task<AuthPlaneResult> ProbeHttpGetAsync(
        string url, string user, string password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (outcome, _) = await ProbeHttpGetWithStateAsync(url, timeout, cancellationToken, buildState: null,
            user: user, password: password);
        return outcome;
    }

    private async Task<(AuthPlaneResult Result, string? State)> ProbeHttpGetWithStateAsync(
        string url, TimeSpan timeout, CancellationToken cancellationToken,
        Func<string, string?>? buildState, string user = "", string password = "")
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var client = httpClientFactory.CreateClient("probe");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (user.Length > 0)
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = response.Content is not null
                ? await response.Content.ReadAsStringAsync(cts.Token)
                : string.Empty;
            sw.Stop();

            string? state = null;
            if (buildState is not null && response.IsSuccessStatusCode)
            {
                state = buildState(body);
            }

            return (
                new AuthPlaneResult
                {
                    Reachable = true,
                    HttpStatusCode = (int)response.StatusCode,
                    Detail = $"HTTP {url} → {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms",
                    LatencyMs = (int)sw.ElapsedMilliseconds
                },
                state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (
                new AuthPlaneResult
                {
                    Detail = $"HTTP {url} — {ex.GetType().Name}: {ex.Message}",
                    LatencyMs = (int)sw.ElapsedMilliseconds
                },
                null);
        }
    }

    private async Task<(AuthPlaneResult Result, string? Body)> ProbeSoapAsync(
        string url, string envelope, string user, string password,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var client = httpClientFactory.CreateClient("onvif");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {token}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            return (
                new AuthPlaneResult
                {
                    Reachable = true,
                    HttpStatusCode = (int)response.StatusCode,
                    Detail = $"{(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms",
                    LatencyMs = (int)sw.ElapsedMilliseconds
                },
                response.IsSuccessStatusCode ? body : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (
                new AuthPlaneResult
                {
                    Detail = $"SOAP {url} — {ex.GetType().Name}: {ex.Message}",
                    LatencyMs = (int)sw.ElapsedMilliseconds
                },
                null);
        }
    }

    private async Task<AuthPlaneResult> ProbeTcpPortAsync(string host, int port, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var socket = new TcpClient();
            await socket.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new AuthPlaneResult
            {
                Reachable = socket.Connected,
                HttpStatusCode = null,
                Detail = $"TCP {host}:{port} open in {sw.ElapsedMilliseconds}ms",
                LatencyMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new AuthPlaneResult
            {
                Detail = $"TCP {host}:{port} — {ex.GetType().Name}: {ex.Message}",
                LatencyMs = (int)sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<AuthPlaneResult> ProbeRtspOptionsAsync(string host, int port, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var playable = await RtspProbe.ProbeAsync(host, port, cancellationToken, TimeSpan.FromSeconds(3));
        sw.Stop();
        return new AuthPlaneResult
        {
            Reachable = playable,
            Detail = playable
                ? $"RTSP OPTIONS {host}:{port} answered in {sw.ElapsedMilliseconds}ms"
                : $"RTSP {host}:{port} — no RTSP response",
            LatencyMs = (int)sw.ElapsedMilliseconds
        };
    }
}
