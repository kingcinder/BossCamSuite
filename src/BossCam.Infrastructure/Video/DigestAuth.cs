using System.Security.Cryptography;
using System.Text;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Minimal RFC 2617 / RFC 7616 HTTP Digest authentication helper.
///
/// Exists because the NetSDK family probe must answer a Digest challenge through the injected
/// <see cref="System.Net.Http.IHttpClientFactory"/> — a credential-cache HttpClientHandler
/// bypasses the factory and cannot be unit-tested through the stub. Supports the "auth"
/// quality-of-protection with both MD5 (RFC 2617, the default) and SHA-256 (RFC 7616, newer
/// firmware); "auth-int" additionally hashes the entity body into HA2, which a GET probe does
/// not need, so a challenge offering only auth-int is refused upstream.
/// </summary>
internal static class DigestAuth
{
    /// <summary>
    /// Computes the digest response value (lowercase hex) for the given challenge parameters.
    /// RFC 2617 §3.2.2 / RFC 7616 §3.4.2: HA1 = H(username:realm:password); HA2 = H(method:uri);
    /// response = H(HA1:nonce:nc:cnonce:qop:HA2) with qop, else the legacy H(HA1:nonce:HA2)
    /// form without qop — where H is MD5 or SHA-256 per <paramref name="algorithm"/>.
    /// <paramref name="algorithm"/> defaults to MD5 (the RFC default when the challenge omits
    /// the directive). Any other value is a caller bug and throws rather than silently computing
    /// with the wrong hash. Internal for unit tests — the RFC 2617 §3.5 (MD5) and RFC 7616
    /// §3.9.1 (SHA-256) reference vectors pin the composition for both primitives.
    /// </summary>
    internal static string ComputeResponse(
        string username,
        string password,
        string method,
        string uri,
        string realm,
        string nonce,
        string? qop,
        string cnonce,
        string nc,
        string? algorithm = null)
    {
        var hash = SelectHash(algorithm);
        var ha1 = hash($"{username}:{realm}:{password}");
        var ha2 = hash($"{method}:{uri}");
        return qop is null
            ? hash($"{ha1}:{nonce}:{ha2}")
            : hash($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
    }

    private static Func<string, string> SelectHash(string? algorithm)
    {
        if (algorithm is null || algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
        {
            return Md5Hex;
        }

        if (algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            return Sha256Hex;
        }

        throw new ArgumentException($"Unsupported digest algorithm '{algorithm}' — negotiate before computing.", nameof(algorithm));
    }

    private static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
