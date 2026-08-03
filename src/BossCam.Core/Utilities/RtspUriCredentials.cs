namespace BossCam.Core.Utilities;

/// <summary>
/// RTSP URI credential handling shared by the endpoint-truth probe and the source projection.
/// <para>
/// The endpoint-truth profile persists a <b>sanitized</b> (userinfo-stripped) URI — never the
/// password — and the stream adapter <see cref="Rebuild"/>s the playable credentialed URI at
/// projection time from the device record, so server-side recording stays authenticated without
/// storing credentials as clear text (CodeQL: clear text storage of sensitive information).
/// </para>
/// </summary>
public static class RtspUriCredentials
{
    /// <summary>Embeds <paramref name="username"/>/<paramref name="password"/> into an RTSP URI for probing/playback.</summary>
    public static string Build(string uri, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !parsed.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        // Build userinfo manually: UriBuilder collapses an explicit empty password into the bare
        // "user@" form, but the 5523-W's happytimesoft RTSP plane treats "user:@" (empty password
        // supplied) as distinct from "user@" (no password) — the same rule the desktop
        // TryBuildCredentialedVariants follows.
        var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password ?? string.Empty)}";
        var host = parsed.Host.Contains(':')
            ? parsed.IsDefaultPort ? $"[{parsed.Host}]" : $"[{parsed.Host}]:{parsed.Port}"
            : parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}";
        return $"{parsed.Scheme}://{userInfo}@{host}{parsed.PathAndQuery}{parsed.Fragment}";
    }

    /// <summary>Strips userinfo so the value is safe to persist and log (redacted display value).</summary>
    public static string Sanitize(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return uri;
        }

        var builder = new UriBuilder(parsed)
        {
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.ToString();
    }

    /// <summary>Sanitizes then re-applies device credentials — the projection-time "playable URI".</summary>
    public static string Rebuild(string uri, string? username, string? password)
        => Build(Sanitize(uri), username, password);
}
