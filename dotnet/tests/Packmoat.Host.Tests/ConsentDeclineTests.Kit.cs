using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Packmoat.Host.Tests;

/// <summary>A key and certificate on disk (#197): the core's tests and the hosted tests (which compile this file in) share it.</summary>
public partial class ConsentDeclineTests
{
    internal static string WritePem()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=packmoat-consent-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(Path.GetTempPath(), "packmoat-consent-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        return path;
    }
}
