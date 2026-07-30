namespace BossCam.Core.Security;

/// <summary>
/// Encrypts camera passwords for at-rest storage.
/// </summary>
/// <remarks>
/// The default implementation (<see cref="CompositePasswordCipher"/>) uses a 0600-permissioned
/// AES-GCM keyfile on Linux/macOS and DPAPI / CurrentUser on Windows. The interface is exposed
/// so persistence + import paths can depend on the contract without dragging in OS-specific
/// dependencies, and so unit tests can substitute a deterministic cipher when needed.
///
/// Output format is "v1:&lt;base64&gt;" for keyfile ciphertext and "v2:&lt;base64&gt;" for DPAPI.
/// Strings without a version prefix are treated as legacy plaintext on read so existing
/// DeviceIdentity rows that predate the cipher keep working (the next save round-trips
/// them through the cipher).
/// </remarks>
public interface IPasswordCipher
{
    /// <summary>
    /// Encrypt the plaintext password. Empty / null input returns empty output.
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypt a ciphertext previously produced by <see cref="Encrypt"/>. Empty / null input
    /// returns empty output. Unknown-prefix strings are returned as-is (legacy plaintext).
    /// </summary>
    string Decrypt(string ciphertext);
}
