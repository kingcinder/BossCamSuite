using System.Net;
using System.Text;
using BossCam.Contracts;
using BossCam.Core;
using BossCam.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Systematically fuzzes known camera CGI endpoints with path variants, HTTP methods,
/// auth-bypass headers, and parameter injections to discover authentication bypasses.
///
/// A "finding" is any probe that returns 2xx with real content (not a login page or
/// the "check in falied" gate message), indicating the endpoint served data without
/// proper authentication.
/// </summary>
public sealed class CgiFuzzer(
    IOptions<BossCamRuntimeOptions> options,
    IHttpClientFactory httpClientFactory,
    IApplicationStore store,
    ILogger<CgiFuzzer> logger)
{
    // ── Target endpoint families (mined from codebase, firmware extraction, and protocol manifests) ──

    private static readonly string[] UserEndpoints =
    [
        "/user/user_list.xml",
        "/user/set_pass.xml",
        "/user/add_user.xml",
        "/user/del_user.xml",
        "/user/get_sn_num",
        "/user/user_reset",
        "/user/checkin",
    ];

    private static readonly string[] CgiBinEndpoints =
    [
        "/cgi-bin/magicBox.cgi",
        "/cgi-bin/configManager.cgi",
        "/cgi-bin/snapshot.cgi",
        "/cgi-bin/gw2.cgi",
        "/cgi-bin/login.cgi",
        "/cgi-bin/upload.cgi",
        "/cgi-bin/upgrade_rate.cgi",
    ];

    private static readonly string[] ParamEndpoints =
    [
        "/param/get_system_info",
        "/param/get_network",
        "/param/get_wireless",
        "/param/get_video",
        "/param/get_image",
        "/param/get_user",
    ];

    private static readonly string[] NvrEndpoints =
    [
        "/NetSDK/System/deviceInfo",
        "/NetSDK/Image/irCutfilter",
        "/NetSDK/User",
    ];

    // ── Gated-body markers: responses that mean "still gated" ──

    private static readonly string[] GatedMarkers =
    [
        "check in falied",
        "Invalid Operation",
        "ret=\"sorry\"",
        "login.cgi",
        "please login",
        "unauthorized",
        "access denied",
        "Authentication Failed",
    ];

    /// <summary>Endpoints that should never receive destructive methods (PUT/DELETE/PATCH).</summary>
    private static readonly string[] DestructiveEndpoints =
    [
        "/user/set_pass.xml",
        "/user/add_user.xml",
        "/user/del_user.xml",
        "/user/user_reset",
    ];

    // ── Auth-bypass header strategies ──

    private static readonly (string Name, string Value)[] BypassHeaders =
    [
        ("X-Forwarded-For", "127.0.0.1"),
        ("X-Real-IP", "127.0.0.1"),
        ("X-Client-IP", "127.0.0.1"),
        ("X-Remote-IP", "127.0.0.1"),
        ("X-Originating-IP", "127.0.0.1"),
        ("Referer", "{BASE}/"),
        ("X-Requested-With", "XMLHttpRequest"),
    ];

    // ── Auth query-param injection strategies ──

    private static readonly string[] AuthQueryParams =
    [
        "usr=admin&pwd=",
        "usr=admin&pwd=admin",
        "loginuse=admin&loginpas=",
        "username=admin&password=",
        "user=admin&pass=",
        "user=admin&password=admin",
    ];

    // ── Empty-cred Authorization headers (probe various malformed/empty Basic auth forms) ──

    private static readonly string[] EmptyCredHeaders =
    [
        "Basic Og==",                    // base64(":") — empty user, empty pass
        "Basic YWRtaW46",                // base64("admin:") — user with empty pass
        "Basic OnBhc3N3b3Jk",           // base64(":password") — empty user with pass
    ];

    private static bool IsDestructive(string endpoint)
    {
        foreach (var d in DestructiveEndpoints)
        {
            if (endpoint.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<CgiFuzzResult> FuzzAsync(
        CgiFuzzRequest request,
        CancellationToken cancellationToken)
    {
        var device = await ResolveDeviceAsync(request, cancellationToken);
        if (device is null || string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return new CgiFuzzResult
            {
                Success = false,
                Message = "No device resolved — supply DeviceId or IpAddress.",
                Findings = [],
                GatedEndpoints = []
            };
        }

        var ip = device.IpAddress!;
        var port = device.Port > 0 ? device.Port : 80;
        var baseUri = $"http://{ip}:{port}";
        var timeout = TimeSpan.FromSeconds(Math.Max(3, options.Value.HttpTimeoutSeconds));
        var findings = new List<CgiFuzzFinding>();
        var gated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalProbes = 0;

        // Collect all target endpoints
        var allEndpoints = new List<string>();
        allEndpoints.AddRange(UserEndpoints);
        allEndpoints.AddRange(CgiBinEndpoints);
        allEndpoints.AddRange(ParamEndpoints);
        allEndpoints.AddRange(NvrEndpoints);

        // Also add endpoints from stored protocol manifests
        var manifests = await store.GetProtocolManifestsAsync(cancellationToken);
        foreach (var manifest in manifests)
        {
            foreach (var ep in manifest.Endpoints)
            {
                if (!string.IsNullOrWhiteSpace(ep.Path)
                    && !allEndpoints.Contains(ep.Path, StringComparer.OrdinalIgnoreCase))
                {
                    allEndpoints.Add(ep.Path);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── Phase 1: Baseline — hit each endpoint with GET and POST WITHOUT auth ──
        foreach (var endpoint in allEndpoints)
        {
            foreach (var method in new[] { "GET", "POST" })
            {
                totalProbes++;
                var (status, ct, body) = await ProbeAsync(baseUri + endpoint, method,
                    auth: "none",
                    timeout, cancellationToken);

                if (IsFinding(status, body))
                {
                    var finding = BuildFinding(endpoint, method, endpoint, "baseline-noauth",
                        status, ct, body);
                    findings.Add(finding);
                }
                else if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    || (status == HttpStatusCode.OK && ContainsGated(body)))
                {
                    gated.Add(endpoint);
                }
            }
        }

        // Quick scan stops after baseline — only the most common bypasses
        if (request.QuickScan)
        {
            return Finalize(findings, gated, totalProbes);
        }

        // Restrict remaining phases to endpoints that were confirmed gated
        var gatedList = gated.ToList();

        // ── Phase 2: Method fuzz — try HEAD, OPTIONS (safe methods only) on gated endpoints ──
        // Never send PUT/DELETE/PATCH to destructive endpoints (user management).
        foreach (var endpoint in gatedList)
        {
            foreach (var method in new[] { "HEAD", "OPTIONS" })
            {
                totalProbes++;
                var (status, ct, body) = await ProbeAsync(baseUri + endpoint, method,
                    auth: "none", timeout, cancellationToken);
                if (IsFinding(status, body))
                {
                    findings.Add(BuildFinding(endpoint, method, endpoint, "method-fuzz",
                        status, ct, body));
                }
            }

            // PUT/DELETE/PATCH only on non-destructive endpoints
            if (IsDestructive(endpoint)) continue;
            foreach (var method in new[] { "PUT", "DELETE", "PATCH" })
            {
                totalProbes++;
                var (status, ct, body) = await ProbeAsync(baseUri + endpoint, method,
                    auth: "none", timeout, cancellationToken);
                if (IsFinding(status, body))
                {
                    findings.Add(BuildFinding(endpoint, method, endpoint, "method-fuzz-destructive",
                        status, ct, body));
                }
            }
        }

        // ── Phase 3: Path fuzz — try mutations on gated endpoints ──
        foreach (var endpoint in gatedList)
        {
            foreach (var variant in PathMutations(endpoint))
            {
                totalProbes++;
                var (status, ct, body) = await ProbeAsync(baseUri + variant, "GET",
                    auth: "none", timeout, cancellationToken);
                if (IsFinding(status, body))
                {
                    findings.Add(BuildFinding(endpoint, "GET", variant, "path-fuzz",
                        status, ct, body));
                }
            }
        }

        // ── Phase 4: Auth-param injection ──
        foreach (var endpoint in gatedList)
        {
            foreach (var qp in AuthQueryParams)
            {
                var separator = endpoint.Contains('?') ? "&" : "?";
                var variant = endpoint + separator + qp;
                totalProbes++;
                var (status, ct, body) = await ProbeAsync(baseUri + variant, "GET",
                    auth: "none", timeout, cancellationToken);
                if (IsFinding(status, body))
                {
                    findings.Add(BuildFinding(endpoint, "GET", variant, "param-inject",
                        status, ct, body));
                }
            }
        }

        // ── Phase 5: Header fuzz — auth-bypass and empty-cred headers ──
        foreach (var endpoint in gatedList)
        {
            // Try IP-spoofing headers
            foreach (var (name, value) in BypassHeaders)
            {
                var headerValue = value.Replace("{BASE}", baseUri, StringComparison.Ordinal);
                var headers = new Dictionary<string, string> { [name] = headerValue };
                totalProbes++;
                var (status, ct, body) = await ProbeWithHeadersAsync(
                    baseUri + endpoint, "GET", headers, timeout, cancellationToken);
                if (IsFinding(status, body))
                {
                    findings.Add(BuildFinding(endpoint, "GET", endpoint,
                        $"header-fuzz:{name}", status, ct, body));
                }
            }

            // Try empty-cred Authorization headers
            foreach (var authHeader in EmptyCredHeaders)
            {
                var headers = new Dictionary<string, string>
                {
                    ["Authorization"] = authHeader
                };
                totalProbes++;
                var (status, ct, body) = await ProbeWithHeadersAsync(
                    baseUri + endpoint, "GET", headers, timeout, cancellationToken);
                if (IsFinding(status, body))
                {
                    findings.Add(BuildFinding(endpoint, "GET", endpoint,
                        "header-fuzz:empty-cred", status, ct, body));
                }
            }
        }

        return Finalize(findings, gated, totalProbes);
    }

    // ── Path mutation generators ────────────────────────────────

    private static IEnumerable<string> PathMutations(string endpoint)
    {
        // Trailing slash
        yield return endpoint + "/";

        // Double slash
        if (endpoint.Contains('/'))
        {
            var idx = endpoint.LastIndexOf('/');
            yield return endpoint[..idx] + "//" + endpoint[(idx + 1)..];
        }

        // Append extension variants
        if (!endpoint.Contains('?'))
        {
            yield return endpoint + ".cgi";
            yield return endpoint + ".xml";
            yield return endpoint + ".json";
            yield return endpoint + ".html";
        }

        // Case flip
        yield return FlipCase(endpoint);

        // Path traversal
        yield return "/../.." + endpoint;
        yield return "/.." + endpoint;

        // Empty query (may confuse CGI parser)
        if (!endpoint.Contains('?'))
        {
            yield return endpoint + "?";
            yield return endpoint + "?action=";
            yield return endpoint + "?cmd=";
        }

        // URL-encoded path traversal
        yield return "/..%2f.." + endpoint;
    }

    private static string FlipCase(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var c in path)
        {
            sb.Append(char.IsLetter(c)
                ? (char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))
                : c);
        }
        return sb.ToString();
    }

    // ── HTTP probing ────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, string? ContentType, string Body)> ProbeAsync(
        string url, string method, string auth, TimeSpan timeout, CancellationToken ct)
    {
        var result = await ProbeExceptionSwallow.RunAsync(async () =>
        {
            using var client = httpClientFactory.CreateClient("probe");
            client.Timeout = timeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var request = new HttpRequestMessage(new HttpMethod(method), url);

            if (auth == "default")
            {
                request.Headers.TryAddWithoutValidation(
                    "Authorization", "Basic YWRtaW46YWRtaW4="); // admin:admin
            }

            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var contentType = response.Content.Headers.ContentType?.ToString();

            return (StatusCode: response.StatusCode, ContentType: contentType, Body: body ?? string.Empty);
        }, logger, $"CGI fuzz probe {method} {url}");

        return result is var (status, ct2, body2)
            ? (status, ct2, body2)
            : (HttpStatusCode.ServiceUnavailable, null, string.Empty);
    }

    private async Task<(HttpStatusCode Status, string? ContentType, string Body)> ProbeWithHeadersAsync(
        string url, string method, Dictionary<string, string> extraHeaders,
        TimeSpan timeout, CancellationToken ct)
    {
        var result = await ProbeExceptionSwallow.RunAsync(async () =>
        {
            using var client = httpClientFactory.CreateClient("probe");
            client.Timeout = timeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            foreach (var (name, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var contentType = response.Content.Headers.ContentType?.ToString();

            return (StatusCode: response.StatusCode, ContentType: contentType, Body: body ?? string.Empty);
        }, logger, $"CGI fuzz probe {method} {url} (headers)");

        return result is var (status, ct2, body2)
            ? (status, ct2, body2)
            : (HttpStatusCode.ServiceUnavailable, null, string.Empty);
    }

    // ── Finding detection ───────────────────────────────────────

    private static bool IsFinding(HttpStatusCode status, string body)
    {
        if (body.Length == 0) return false;
        if (ContainsGated(body)) return false;
        // Reject HTML login/error pages masquerading as 200 OK
        if (IsHtmlGatePage(body)) return false;

        // 2xx with non-gated, non-HTML body is a positive finding
        return (int)status >= 200 && (int)status < 300;
    }

    private static bool IsHtmlGatePage(string body)
    {
        var trimmed = body.TrimStart();
        return (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            && (body.Contains("login", StringComparison.OrdinalIgnoreCase)
                || body.Contains("password", StringComparison.OrdinalIgnoreCase)
                || body.Contains("auth", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsGated(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        foreach (var marker in GatedMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static CgiFuzzFinding BuildFinding(
        string endpoint, string method, string variant, string? strategy,
        HttpStatusCode status, string? contentType, string body)
    {
        return new CgiFuzzFinding
        {
            Endpoint = endpoint,
            Method = method,
            Variant = variant,
            Strategy = strategy,
            StatusCode = (int)status,
            ContentType = contentType,
            BodyLength = body.Length,
            BodyPreview = body.Length > 200 ? body[..200] + "…" : body,
            Description = body.Length > 0
                ? $"BYPASS: {method} {variant} returned HTTP {(int)status} with "
                    + $"{body.Length}B body (Content-Type: {contentType ?? "unknown"})"
                : $"BYPASS: {method} {variant} returned HTTP {(int)status} (empty body)"
        };
    }

    private static CgiFuzzResult Finalize(
        List<CgiFuzzFinding> findings, HashSet<string> gated, int totalProbes)
    {
        findings.Sort((a, b) => b.BodyLength.CompareTo(a.BodyLength)); // most data first

        return new CgiFuzzResult
        {
            Success = true,
            TotalProbes = totalProbes,
            Findings = findings,
            GatedEndpoints = gated,
            Message = findings.Count > 0
                ? $"Found {findings.Count} auth bypass(es) across {totalProbes} probes. "
                    + $"Top find: {findings[0].Description}"
                : $"No auth bypasses found across {totalProbes} probes on "
                    + $"{gated.Count} gated endpoints."
        };
    }

    // ── Device resolution ───────────────────────────────────────

    private async Task<DeviceIdentity?> ResolveDeviceAsync(
        CgiFuzzRequest request, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(request.DeviceId, out var id))
        {
            return await store.GetDeviceAsync(id, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(request.IpAddress))
        {
            return new DeviceIdentity
            {
                Name = $"cgi-fuzz-{request.IpAddress}",
                IpAddress = request.IpAddress,
                Port = 80,
                DeviceType = "CGI-FUZZ"
            };
        }
        return null;
    }
}
