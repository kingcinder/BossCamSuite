using System.Security.Cryptography;
using System.Text;

namespace BossCam.Infrastructure.Video;

/// <summary>
/// Shared RFC 2617 Digest challenge parser and <c>Authorization</c> header builder.
///
/// Used by two credential probes with different request-target forms: the HTTP REST plane
/// (<see cref="NativeNetSdkStreamAdapter.TryBuildDigestAuthorization"/>, origin-form
/// <c>GET</c> uri) and the RTSP plane (<see cref="RtspDigestHandshake"/>, absolute-form
/// <c>DESCRIBE</c> uri <c>rtsp://host:port/path</c>). The digest response composition is
/// identical; only the method and request-target differ, so the hardened parsing — unquoted
/// qop tokens, quoted qop lists, boundary-guarded directive matching, honest refusal of
/// auth-int / MD5-sess — lives in exactly one place.
/// </summary>
internal static class DigestChallenge
{
    /// <summary>
    /// Builds the RFC 2617 / RFC 7616 <c>Authorization</c> header value for the given challenge
    /// parameter string (everything after the <c>Digest </c> scheme token). Returns false —
    /// and the caller treats the 401 as unanswerable (continue to the next candidate /
    /// fail the handshake) — when the challenge lacks realm/nonce, advertises only auth-int,
    /// advertises a sess algorithm variant, advertises userhash, or offers no supported
    /// algorithm (MD5 or SHA-256). Never answers with a wrong hash.
    /// </summary>
    internal static bool TryBuildAuthorization(string challengeParameter, string method, string requestTarget, string user, string password, out string authorization)
    {
        authorization = string.Empty;
        var realm = ParseDigestParameter(challengeParameter, "realm");
        var nonce = ParseDigestParameter(challengeParameter, "nonce");
        if (realm is null || nonce is null)
        {
            return false;
        }

        // RFC 2617 allows the challenge to advertise several qop values as a quoted list
        // (qop="auth,auth-int"). The client must choose ONE token for both the response
        // input and the outgoing qop= header. We implement "auth" (HA2 = H(method:uri));
        // "auth-int" additionally hashes the entity body into HA2, which neither probe does,
        // so a challenge offering only auth-int is refused rather than answered wrongly.
        // Case-insensitive: a qop="AUTH-INT" challenge must be refused too. The parser accepts
        // BOTH quoted values (qop="auth") and bare tokens (qop=auth) — embedded HTTP servers
        // commonly emit the latter, and mis-reading it as "no qop" would answer with the legacy
        // no-qop form that a qop-advertising server may reject.
        var qop = ChooseQop(ParseDigestParameter(challengeParameter, "qop"));
        if (qop is not null && qop.Equals("auth-int", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // RFC 2617 §3.2.2 / RFC 7616 §3.9: the algorithm directive defaults to MD5; SHA-256 is
        // the RFC 7616 upgrade newer firmware may offer (a quoted list of supported algorithms
        // is allowed — the client picks one). The sess variants (MD5-sess / SHA-256-sess)
        // derive a session key HA1 whose input depends on the live nonce AND a client nonce,
        // which neither probe implements (same honesty rule as auth-int: never answer with a
        // wrong hash), so a challenge advertising ANY sess variant — even inside a list that
        // also offers plain MD5 — is refused rather than retried with a mismatched response.
        var algorithm = NegotiateAlgorithm(ParseDigestParameter(challengeParameter, "algorithm"));
        if (algorithm is null)
        {
            return false;
        }

        // RFC 7616 §3.4.3: userhash=true means the username directive must carry
        // SHA-256(user:realm) instead of the plaintext — not implemented, so refuse rather than
        // answer with a plaintext username a userhash server would reject. Both the bare token
        // (userhash=true) and quoted (userhash="true") forms are honored.
        var userhash = ParseDigestParameter(challengeParameter, "userhash");
        if (userhash is not null && userhash.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var nc = "00000001";
        // qop=auth: response = H(HA1:nonce:nc:cnonce:qop:HA2). Without qop the legacy
        // H(HA1:nonce:HA2) form is used, which happytimesoft firmware also accepts.
        var response = DigestAuth.ComputeResponse(user, password, method, requestTarget, realm, nonce, qop, cnonce, nc, algorithm);

        var builder = new StringBuilder($"Digest username=\"{EscapeDigest(user)}\", realm=\"{EscapeDigest(realm)}\", nonce=\"{EscapeDigest(nonce)}\", uri=\"{EscapeDigest(requestTarget)}\", response=\"{response}\"");
        // RFC 7616 §3.4: the algorithm directive is required in the response when it is not the
        // MD5 default. MD5 keeps the legacy header shape (no algorithm= directive), matching
        // happytimesoft firmware that has never seen it.
        if (!algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append($", algorithm={algorithm}");
        }
        if (qop is not null)
        {
            builder.Append($", qop={qop}, nc={nc}, cnonce=\"{cnonce}\"");
        }

        authorization = builder.ToString();
        return true;
    }

    /// <summary>
    /// Picks the single algorithm this client will use from the raw (possibly comma-separated,
    /// possibly quoted-list) challenge value: prefers SHA-256, else MD5, else null when the
    /// challenge offers no supported algorithm or advertises a sess variant. The default (null
    /// raw value) is MD5 per RFC 2617 §3.2.2.
    /// </summary>
    private static string? NegotiateAlgorithm(string? raw)
    {
        if (raw is null)
        {
            return "MD5";
        }

        var tokens = raw.Split(',')
            .Select(static token => token.Trim())
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Any(static token => token.EndsWith("-sess", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Prefer the strongest supported algorithm the server actually offered.
        if (tokens.Contains("SHA-256", StringComparer.OrdinalIgnoreCase))
        {
            return "SHA-256";
        }

        return tokens.Contains("MD5", StringComparer.OrdinalIgnoreCase) ? "MD5" : null;
    }

    /// <summary>
    /// Picks the single qop token this client will use from the raw (possibly comma-separated,
    /// possibly quoted-list) challenge value: prefers "auth", else returns the first known token,
    /// else null when the challenge offers no usable qop (legacy no-qop digest). The caller treats
    /// a chosen "auth-int" as refuse-to-answer since the entity-body hash is not implemented.
    /// </summary>
    private static string? ChooseQop(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var tokens = raw.Split(',')
            .Select(static token => token.Trim())
            .Where(static token => token.Length > 0)
            .ToArray();
        return tokens.Contains("auth", StringComparer.OrdinalIgnoreCase)
            ? "auth"
            : tokens.FirstOrDefault();
    }

    /// <summary>
    /// Extracts a single RFC 2617 directive value from the challenge parameter string. Accepts
    /// BOTH forms §3.2.1 allows: quoted-string (<c>key="value"</c>) and bare token
    /// (<c>key=value</c>). A bare token runs to the next comma or the end of the parameter
    /// string. The match is bounded: the key must be preceded by the start of the string, a
    /// comma, or whitespace, so a needle like <c>nonce=</c> cannot match inside another
    /// directive's value (e.g. <c>opaque="...nonce=x..."</c>).
    /// <para>
    /// CR/LF are deliberately NOT in the separator set: .NET rejects newlines in header values
    /// outright (<c>HttpHeaders.CheckContainsNewLine</c>), so a real challenge from an
    /// <see cref="System.Net.Http.HttpResponseMessage"/> can never contain a line break — obsolete
    /// RFC 7230 header folding cannot reach this parser. The RTSP plane's challenge arrives as raw
    /// socket bytes (not through .NET header parsing), so <see cref="RtspDigestHandshake"/> strips
    /// the <c>WWW-Authenticate:</c> header value with its own line-ending handling before this
    /// parser runs.
    /// </para>
    /// </summary>
    internal static string? ParseDigestParameter(string challenge, string key)
    {
        var marker = $"{key}=";
        var searchFrom = 0;
        while (true)
        {
            var start = challenge.IndexOf(marker, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            // Boundary guard: the char before the key must be a directive separator, not a
            // value character — otherwise the needle matched a substring inside another value
            // (e.g. opaque="...nonce=x..."). Malformed input without a separator is
            // conservatively treated as "no match" → legacy no-qop / no-algorithm, which is
            // the safe direction for a probe.
            if (start == 0 || challenge[start - 1] is ',' or ' ' or '\t')
            {
                start += marker.Length;
                if (start >= challenge.Length)
                {
                    return null;
                }

                // Quoted-string value: may contain commas (qop="auth,auth-int"), so read to the
                // closing quote rather than the next comma.
                if (challenge[start] == '"')
                {
                    start++;
                    var end = challenge.IndexOf('"', start);
                    return end < 0 ? null : challenge[start..end];
                }

                // Bare token value (qop=auth): runs to the next comma or the end of the string.
                var comma = challenge.IndexOf(',', start);
                var endUnquoted = comma < 0 ? challenge.Length : comma;
                return challenge[start..endUnquoted].Trim();
            }

            searchFrom = start + marker.Length;
        }
    }

    private static string EscapeDigest(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
