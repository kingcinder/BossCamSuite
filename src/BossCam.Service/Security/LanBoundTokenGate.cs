using System.Security.Cryptography;
using System.Text;

namespace BossCam.Service.Security;

/// <summary>
/// Host-aware bearer-token gate. Engaged by <see cref="LanBoundTokenGateExtensions.UseLanBoundTokenGate"/>
/// only when the service is bound to a non-loopback address and a token has been
/// resolved from <c>BOSSCAM_LAN_TOKEN</c> (preferred) or <c>BossCam:LanAuthToken</c> config.
/// Per-request behaviour: <c>/api/health</c> stays open, all other <c>/api/*</c> and
/// <c>/swagger/*</c> requests must present a matching token via either the
/// <c>X-LAN-Token</c> header or an <c>Authorization: Bearer ...</c> scheme.
/// Includes constant-time compare via <see cref="CryptographicOperations.FixedTimeEquals"/>
/// to prevent timing side-channels.
/// </summary>
internal sealed class LanBoundTokenGate
{
    private const string HeaderName = "X-LAN-Token";
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedTokenBytes;

    public LanBoundTokenGate(RequestDelegate next, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (string.IsNullOrEmpty(expectedToken))
        {
            throw new InvalidOperationException(
                "LanBoundTokenGate requires a non-empty expected token. " +
                "The middleware should not be registered when no token is available.");
        }

        _next = next;
        _expectedTokenBytes = Encoding.UTF8.GetBytes(expectedToken);
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Open paths: health endpoint and any non-/api/non-/swagger path
        // (static SPA assets, fallback HTML). These stay reachable regardless
        // of token state so the operator UI can still load and prompt.
        if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
            || !(path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)))
        {
            return _next(context);
        }

        if (!TryReadToken(context, out var presented))
        {
            return RejectAsync(context, "missing");
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        // CryptographicOperations.FixedTimeEquals returns false on length mismatch
        // already, so there is no need for an explicit length pre-check (which would
        // make the length-differ path observably faster than the equal-length miss).
        if (!CryptographicOperations.FixedTimeEquals(presentedBytes, _expectedTokenBytes))
        {
            return RejectAsync(context, "invalid");
        }

        return _next(context);
    }

    private static bool TryReadToken(HttpContext context, out string presented)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var headerValue))
        {
            var raw = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                presented = raw.Trim();
                return true;
            }
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var raw = auth.ToString();
            if (raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                presented = raw[BearerPrefix.Length..].Trim();
                return !string.IsNullOrEmpty(presented);
            }
        }

        // SignalR negotiate/WebSocket upgrade: when the JS client uses
        // accessTokenFactory, @microsoft/signalr appends ?access_token=<token>
        // to the negotiate request and the subsequent WebSocket upgrade.
        // This is the standard SignalR transport mechanism — the token is
        // never in a referer or server log because the browser never navigates
        // to a URL containing it. Accept it ONLY for /hub/ paths.
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase))
        {
            var qsToken = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(qsToken))
            {
                presented = qsToken.Trim();
                return true;
            }
        }

        // Intentionally do NOT accept tokens in query strings for non-SignalR paths:
        // they leak via referer headers, browser history, server access logs, and any
        // web analytics pings.

        presented = string.Empty;
        return false;
    }

    private static async Task RejectAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["WWW-Authenticate"] = $"XLAN realm=\"BossCam\", error=\"{reason}\"";
        await context.Response.WriteAsync($"{{\"error\":\"LAN token required.\",\"reason\":\"{reason}\"}}");
    }
}

internal static class LanBoundTokenGateExtensions
{
    /// <summary>
    /// Registers the host-aware LAN bearer-token gate. Callers MUST resolve the
    /// expected token from <c>BOSSCAM_LAN_TOKEN</c> env var (preferred) or
    /// <c>BossCam:LanAuthToken</c> config BEFORE invoking this extension, and
    /// MUST verify the service is in fact bound to a non-loopback address.
    /// </summary>
    public static IApplicationBuilder UseLanBoundTokenGate(this IApplicationBuilder app, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.IsNullOrEmpty(expectedToken))
        {
            throw new InvalidOperationException(
                "UseLanBoundTokenGate requires a non-empty token. The host-aware gate " +
                "should not be registered when no token is available; check the " +
                "BOSSCAM_LAN_TOKEN env var or BossCam:LanAuthToken config.");
        }
        return app.UseMiddleware<LanBoundTokenGate>(expectedToken);
    }
}
