namespace BossCam.Core.Security;

/// <summary>
/// Pass-through cipher used only by tests that construct <c>SqliteApplicationStore</c>
/// without a real keyfile / DPAPI state. Production code always uses
/// <see cref="CompositePasswordCipher"/> via DI.
/// </summary>
public sealed class NoOpPasswordCipher : IPasswordCipher
{
    public static readonly NoOpPasswordCipher Instance = new();

    public string Encrypt(string plaintext)
        => plaintext ?? string.Empty;

    public string Decrypt(string ciphertext)
        => ciphertext ?? string.Empty;
}
