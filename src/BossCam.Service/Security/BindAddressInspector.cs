using System.Net;

namespace BossCam.Service.Security;

/// <summary>
/// Inspects the comma-separated Kestrel bind URL list (the value of
/// <c>urls</c> / <c>BossCam:LocalApiBaseUrl</c>) and reports whether ANY
/// of the listed endpoints resolves to a non-loopback address.
/// </summary>
/// <remarks>
/// Loopback detection rules (in order):
/// <list type="bullet">
///   <item>The literal hostname <c>localhost</c> (case-insensitive).</item>
///   <item>Any <see cref="IPAddress"/> parseable value for which
///     <see cref="IPAddress.IsLoopback(IPAddress)"/> returns true. This
///     covers <c>127.0.0.0/8</c> on IPv4 and <c>::1</c> on IPv6.</item>
/// </list>
/// <para>Anything else — literal <c>0.0.0.0</c>, IPv6 <c>::</c>, RFC1918
/// LAN addresses (<c>10.x</c>, <c>192.168.x</c>, <c>172.16-31.x</c>),
/// public IPs, and DNS names other than <c>localhost</c> — is
/// treated as non-loopback so that the LAN gate engages. Operators who
/// bind via DNS hostname should prefer a literal IP to make their
/// intent unambiguous.</para>
/// </remarks>
internal static class BindAddressInspector
{
    /// <summary>
    /// Returns <c>true</c> iff any URL in <paramref name="urls"/> resolves to a
    /// host that is NOT a loopback address. Empty / whitespace input is treated
    /// as loopback-only.
    /// </summary>
    public static bool IsAnyNonLoopback(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return false;
        }

        foreach (var raw in urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ClassifyHost(raw) != LoopbackClass.Loopback)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies a single URL string (with optional scheme/port/path). Returns
    /// <see cref="LoopbackClass.Loopback"/>, <see cref="LoopbackClass.NonLoopback"/>,
    /// or <see cref="LoopbackClass.NonLoopback"/> if the URL itself cannot be parsed.
    /// </summary>
    public static LoopbackClass ClassifyUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return LoopbackClass.Loopback;
        }

        return ClassifyHost(url);
    }

    private static LoopbackClass ClassifyHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return LoopbackClass.Loopback;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Unparseable URL is conservatively non-loopback so the gate engages;
            // an operator typo shouldn't accidentally leave the LAN wide open.
            return LoopbackClass.NonLoopback;
        }

        var host = uri.Host;
        // Uri.Host returns IPv6 zone addresses enclosed in square brackets, e.g.
        // "[::1]" or "[fe80::1]". Strip the brackets before parsing.
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return LoopbackClass.Loopback;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return IPAddress.IsLoopback(ip) ? LoopbackClass.Loopback : LoopbackClass.NonLoopback;
        }

        // Unknown DNS hostname — treat as non-loopback to err on the side of
        // requiring a token. Operators should pin a literal IP for clarity.
        return LoopbackClass.NonLoopback;
    }
}

internal enum LoopbackClass
{
    Loopback = 0,
    NonLoopback = 1
}
