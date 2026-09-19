using System.Security.Cryptography;

namespace RdpManager.Services;

/// <summary>
/// Authenticated encryption for the connection store, keyed by the master password.
/// AES-GCM is used rather than plain AES so that a tampered store file - for example one where
/// a host name was swapped to redirect a login to an attacker's server - fails to decrypt
/// instead of being silently trusted.
/// </summary>
public static class StoreCrypto
{
    public const int DefaultIterations = 600_000;
    private const int SaltSize = 32;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] CreateSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] DeriveKey(string masterPassword, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(masterPassword, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return (nonce, ciphertext, tag);
    }

    /// <summary>Throws <see cref="CryptographicException"/> if the key is wrong or the data was altered.</summary>
    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
