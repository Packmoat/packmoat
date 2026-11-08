namespace Packmoat.Infrastructure;

/// <summary>
/// The master-key marker, whatever the engine (#288 P2): the meta row <see cref="Key"/>, holding <see cref="Plaintext"/> encrypted with
/// the master key, so a database can be checked against the master key without touching any customer secret. Each engine's
/// <see cref="Packmoat.Application.IMasterKeyCheck"/> stores and reads the row; making and judging the bytes is here.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class MasterKeyMarker
{
    /// <summary>The meta row's key, and the encryption's context.</summary>
    public const string Key = "key_check";

    /// <summary>What the marker decrypts to under the right key.</summary>
    public const string Plaintext = "packmoat-master-key-check-v1";

    /// <summary>The start's refusal when the marker does not decrypt.</summary>
    public const string WrongKeyMessage = "The master key does not match this database. Refusing to continue: customer identities would not decrypt.";

    /// <summary>A new marker under <paramref name="protector"/>'s key (a fresh nonce each time).</summary>
    public static byte[] Create(AesGcmSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return protector.Protect(Plaintext, Key);
    }

    /// <summary>True when <paramref name="stored"/> decrypts to the marker under <paramref name="protector"/>'s key.</summary>
    public static bool Matches(AesGcmSecretProtector protector, byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(stored);
        try { return protector.Unprotect(stored, Key) == Plaintext; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }
}
