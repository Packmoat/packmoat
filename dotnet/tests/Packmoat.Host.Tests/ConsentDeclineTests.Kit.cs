using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Packmoat.Host.Tests;

/// <summary>A key and certificate on disk: the core's tests and the hosted tests (which compile this file in) share it.</summary>
public partial class ConsentDeclineTests
{
    /// <summary>A key and certificate in <paramref name="path"/>, in a folder of its own that the caller disposes.</summary>
    internal static TempDir WritePem(out string path)
    {
        var dir = TempDir.Create("packmoat-consent-");
        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=packmoat-consent-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            path = dir.File("app.pem");
            File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
            return dir;
        }
        catch
        {
            dir.Dispose();
            throw;
        }
    }
}
