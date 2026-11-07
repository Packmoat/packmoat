using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Packmoat.Infrastructure;

/// <summary>
/// Makes the per-customer certificate here, so a private key is never emailed or created on someone else's machine.
/// The customer receives only the public certificate (.cer) to upload to their app registration.
/// </summary>
public static class CertificateTools
{
    public sealed record Generated(string CombinedPem, byte[] PublicCer, string Thumbprint, DateTimeOffset NotAfter);

    public const int MinDays = 30;
    public const int MaxDays = 825;

    public static Generated Generate(string subjectName, int validDays, DateTimeOffset now)
    {
        if (validDays is < MinDays or > MaxDays) throw new ArgumentOutOfRangeException(nameof(validDays), $"Choose between {MinDays} and {MaxDays} days.");
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(validDays));

        var pem = cert.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n";
        return new Generated(pem, cert.Export(X509ContentType.Cert), cert.Thumbprint, cert.NotAfter.ToUniversalTime());
    }

    /// <summary>Creates the directory (mode 700 when it is new) if it is not there.</summary>
    public static void EnsurePrivateDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Creates a NEW file and fails if one exists. A private file is created with mode 600 from the first instant,
    /// so there is no moment when the key is readable by others (chmod after writing would leave one).
    /// </summary>
    public static async Task WriteNewAsync(string path, byte[] content, bool privateFile, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = privateFile ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(content, ct);
    }

    /// <summary>Public facts about a combined PEM: thumbprint, expiry, and whether a matching private key is in it. Never the key itself.</summary>
    public sealed record Facts(string Thumbprint, DateTimeOffset NotAfter, bool HasPrivateKey);

    public static Facts Describe(string combinedPem)
    {
        using var cert = X509Certificate2.CreateFromPem(combinedPem);
        bool hasKey;
        try
        {
            using var withKey = X509Certificate2.CreateFromPem(combinedPem, combinedPem);
            hasKey = withKey.HasPrivateKey;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { hasKey = false; }   // no key in the file, or a key that does not match the certificate
        return new Facts(cert.Thumbprint, cert.NotAfter.ToUniversalTime(), hasKey);
    }

    /// <summary>Expiry of the certificate inside a combined PEM, without needing the private key.</summary>
    public static DateTimeOffset ExpiryOf(string combinedPem)
    {
        using var cert = X509Certificate2.CreateFromPem(combinedPem);
        return cert.NotAfter.ToUniversalTime();
    }
}

/// <summary>The application's view of a PEM: thumbprint, expiry and whether its key is there. Never the key.</summary>
public sealed class CertificateInspector : Packmoat.Application.ICertificateInspector
{
    public Packmoat.Application.CertificateFacts Inspect(string pem)
    {
        try
        {
            var f = CertificateTools.Describe(pem);
            return new Packmoat.Application.CertificateFacts(f.Thumbprint, f.NotAfter, f.HasPrivateKey);
        }
        catch (CryptographicException) { throw new ArgumentException("The PEM file does not hold a readable certificate."); }
    }
}
