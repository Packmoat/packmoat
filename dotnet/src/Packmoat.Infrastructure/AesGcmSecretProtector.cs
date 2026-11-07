using System.Security.Cryptography;
using System.Text;

namespace Packmoat.Infrastructure;

/// <summary>
/// Encrypts small secrets (a tenant's service-principal PEM) for storage in the database. AES-256-GCM; the tenant id is
/// bound in as associated data, so a blob copied onto another tenant's row will not decrypt. The master key lives outside
/// the database (environment variable or a key service), never in the repository.
/// <para>
/// KNOWN GAP: there is no key rotation. Changing the master key makes every stored identity unreadable until it is entered again
/// (<c>identity set</c>), and stops the start (the key check in <c>meta</c>). Tokens, tenants and audit rows are not encrypted with this key
/// and are unaffected. A re-encrypt command is planned: it must re-encrypt every column the tests' <c>MasterKeyEncryptedColumns</c> list
/// marks encrypted, including <c>cookie_keys.xml_encrypted</c> (the cookie keys, with PACKMOAT_COOKIE_KEYS=database, #232 B1).
/// </para>
/// </summary>
public sealed class AesGcmSecretProtector
{
    private const byte Version = 1;
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The master key must be 32 bytes (256 bits).", nameof(key));
        if (IsTriviallyWeak(key)) throw new ArgumentException("The master key is trivially weak (all zeros, a repeated pattern or a simple sequence). Generate one with 'packmoat-admin keygen'.", nameof(key));
        _key = key.ToArray();
    }

    /// <summary>
    /// Catches keys nobody should use: one repeated byte (including all zeros), a short repeating pattern, a counting sequence,
    /// or too few distinct bytes. A random 32-byte key has about 28 distinct bytes; fewer than 12 is not plausible.
    /// This is a guard against mistakes, not a measure of strength.
    /// </summary>
    internal static bool IsTriviallyWeak(byte[] key)
    {
        if (key.Distinct().Count() < 12) return true;
        foreach (var period in new[] { 1, 2, 4, 8, 16 })
            if (Enumerable.Range(0, key.Length).All(i => key[i] == key[i % period])) return true;
        var ascending = Enumerable.Range(1, key.Length - 1).All(i => (byte)(key[i] - key[i - 1]) == 1);
        var descending = Enumerable.Range(1, key.Length - 1).All(i => (byte)(key[i - 1] - key[i]) == 1);
        return ascending || descending;
    }

    public static AesGcmSecretProtector FromBase64(string base64) => new(Convert.FromBase64String(base64.Trim()));

    /// <summary>The key from the process's setting (or its file). Not set, or set but empty: a <see cref="SettingsException"/> marked
    /// <see cref="SettingsException.NotSet"/>.</summary>
    public static AesGcmSecretProtector FromEnvironment(string variable = "PACKMOAT_MASTER_KEY") => FromSettings(Settings.Get, variable);

    /// <summary>The key read through <paramref name="read"/> (production: <see cref="Settings.Get"/>). An empty variable counts as not set,
    /// as the settings remarks say: a container started with the variable present but empty skips the optional key checks, it does not fail them.</summary>
    public static AesGcmSecretProtector FromSettings(Func<string, string?> read, string variable = "PACKMOAT_MASTER_KEY")
    {
        ArgumentNullException.ThrowIfNull(read);
        return FromBase64(read(variable) is { Length: > 0 } value ? value : throw SettingsException.Unset(variable));
    }

    public static string GenerateKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public byte[] Protect(string plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(context));
        return new[] { Version }.Concat(nonce).Concat(tag).Concat(cipher).ToArray();
    }

    public string Unprotect(byte[] blob, string context)
    {
        if (blob.Length < 1 + 12 + 16 || blob[0] != Version) throw new CryptographicException("Unrecognised secret format.");
        var nonce = blob.AsSpan(1, 12);
        var tag = blob.AsSpan(13, 16);
        var cipher = blob.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context)); // throws if tampered or bound to another tenant
        return Encoding.UTF8.GetString(plain);
    }
}
