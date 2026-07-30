using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BossCam.Core;

namespace BossCam.Core.Security;

/// <summary>
/// OS-aware password cipher. Linux/macOS route uses AES-GCM with a 32-byte key
/// lazily created in a 0600-permissioned keyfile under the configured data root.
/// Windows route uses <see cref="ProtectedData"/> with the CurrentUser scope and
/// SHA-256 of "BossCamSuite.v1" as additional entropy.
///
/// The cipher output is versioned via a "v1:" / "v2:" base64 prefix so future
/// algorithms can co-exist with existing ciphertext. Legacy plaintext (strings
/// without a version prefix) is returned unchanged on <see cref="Decrypt"/> so
/// rows persisted before this code shipped continue to deserialize.
/// </summary>
public sealed class CompositePasswordCipher : IPasswordCipher
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("BossCamSuite.v1");

    private readonly object _gate = new();
    private readonly string _keyfilePath;
    private readonly bool _useKeyfile;
    private byte[]? _keyfileKey;

    public CompositePasswordCipher(BossCamRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _useKeyfile = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _keyfilePath = string.IsNullOrWhiteSpace(options.SecretKeyPath)
            ? DefaultKeyfilePath()
            : options.SecretKeyPath;
    }

    /// <summary>Constructor used by tests to force a specific keyfile path regardless of OS platform.</summary>
    internal CompositePasswordCipher(string keyfilePath)
    {
        _useKeyfile = true;
        _keyfilePath = keyfilePath;
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            return string.Empty;
        }

        if (_useKeyfile)
        {
            return "v1:" + EncryptWithKeyfile(plaintext);
        }

        // _useKeyfile is false only on Windows, so the following call is safe.
#pragma warning disable CA1416
        return "v2:" + EncryptWithDpapi(plaintext);
#pragma warning restore CA1416
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length == 0)
        {
            return string.Empty;
        }

        if (ciphertext.StartsWith("v1:", StringComparison.Ordinal))
        {
            return DecryptWithKeyfile(ciphertext[3..]);
        }

        if (ciphertext.StartsWith("v2:", StringComparison.Ordinal))
        {
            // v2: prefix is only produced on Windows (DPAPI).
#pragma warning disable CA1416
            return DecryptWithDpapi(ciphertext[3..]);
#pragma warning restore CA1416
        }

        // Legacy plaintext — return verbatim so pre-cipher rows still load.
        return ciphertext;
    }

    private string EncryptWithKeyfile(string plaintext)
    {
        var key = GetOrCreateKeyfile();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipherBytes, tag);
        var combined = new byte[12 + cipherBytes.Length + 16];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(cipherBytes, 0, combined, 12, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, combined, 12 + cipherBytes.Length, 16);
        return Convert.ToBase64String(combined);
    }

    private string DecryptWithKeyfile(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length < 12 + 16)
        {
            throw new CryptographicException("Ciphertext shorter than header+tag; refusing to decrypt.");
        }

        var nonce = bytes.AsSpan(0, 12).ToArray();
        var cipherLen = bytes.Length - 12 - 16;
        var enc = bytes.AsSpan(12, cipherLen).ToArray();
        var tag = bytes.AsSpan(12 + cipherLen, 16).ToArray();
        var plain = new byte[cipherLen];
        var key = GetOrCreateKeyfile();
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, enc, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    [SupportedOSPlatform("windows")]
    private static string EncryptWithDpapi(string plaintext)
    {
        var entropy = SHA256.HashData(Salt);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    private static string DecryptWithDpapi(string base64)
    {
        var entropy = SHA256.HashData(Salt);
        var raw = Convert.FromBase64String(base64);
        var plain = ProtectedData.Unprotect(raw, entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] GetOrCreateKeyfile()
    {
        lock (_gate)
        {
            if (_keyfileKey is not null)
            {
                return _keyfileKey;
            }

            if (File.Exists(_keyfilePath))
            {
                var raw = File.ReadAllText(_keyfilePath).Trim();
                _keyfileKey = Convert.FromBase64String(raw);
                return _keyfileKey;
            }

            _keyfileKey = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(_keyfilePath)!);
            File.WriteAllText(_keyfilePath, Convert.ToBase64String(_keyfileKey));
            try
            {
#pragma warning disable CA1416 // SetUnixFileMode is unsupported on Windows but guarded by _useKeyfile flag
                File.SetUnixFileMode(_keyfilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            }
            catch (PlatformNotSupportedException)
            {
                // Windows: NTFS ACLs are not enforced in this code path; the keyfile is normally
                // created on Linux/macOS only via the operating-system detection above.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort chmod; the encryption itself is intact.
            }

            return _keyfileKey;
        }
    }

    private static string DefaultKeyfilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = string.IsNullOrEmpty(localAppData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : localAppData;
        return Path.Combine(dataRoot, "BossCamSuite", "secret.key");
    }
}
