using System.Security.Cryptography.X509Certificates;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

public class CertificateToolsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_public_certificate_has_no_private_key_and_the_bundle_does()
    {
        var gen = CertificateTools.Generate("packmoat-acme", 365, Now);

        using var publicOnly = X509CertificateLoader.LoadCertificate(gen.PublicCer);
        using var bundle = X509Certificate2.CreateFromPem(gen.CombinedPem, gen.CombinedPem);

        Assert.False(publicOnly.HasPrivateKey);
        Assert.True(bundle.HasPrivateKey);
        Assert.Equal(publicOnly.Thumbprint, bundle.Thumbprint);
        Assert.Equal("CN=packmoat-acme", publicOnly.Subject);
        Assert.DoesNotContain("PRIVATE KEY", System.Text.Encoding.ASCII.GetString(gen.PublicCer));
    }

    [Fact]
    public void Expiry_matches_the_requested_lifetime_and_can_be_read_back_without_the_key()
    {
        var gen = CertificateTools.Generate("x", 90, Now);
        Assert.InRange((gen.NotAfter - Now).TotalDays, 89.9, 90.1);
        Assert.Equal(gen.NotAfter, CertificateTools.ExpiryOf(gen.CombinedPem));
    }

    [Fact]
    public void The_bundle_loads_the_way_the_credential_source_loads_it()
    {
        var gen = CertificateTools.Generate("x", 365, Now);
        using var pem = X509Certificate2.CreateFromPem(gen.CombinedPem, gen.CombinedPem);
        using var loaded = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
        Assert.True(loaded.HasPrivateKey);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(826)]
    public void Silly_lifetimes_are_refused(int days) => Assert.Throws<ArgumentOutOfRangeException>(() => CertificateTools.Generate("x", days, Now));

    [Fact]
    public void Every_certificate_has_a_different_key()
    {
        Assert.NotEqual(CertificateTools.Generate("x", 365, Now).Thumbprint, CertificateTools.Generate("x", 365, Now).Thumbprint);
    }
}

[Collection("sqlite-pools")]
public class OnboardingCliTests
{
    private sealed class Cli : IAsyncDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("packmoat-onb-").FullName;
        public FakeClock Clock { get; } = new();
        private readonly CliEnvironment _env;
        public Cli()
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            _env = new CliEnvironment(Path.Combine(Dir, "p.db"), () => AesGcmSecretProtector.FromBase64(key), Clock);
        }
        public async Task<(int Code, string Out, string Err)> Run(params string[] a)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var c = await AdminCli.RunAsync(a, o, e, _env, TestCli.Modules);
            return (c, o.ToString(), e.ToString());
        }
        public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(Dir, true); } catch { } return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task New_cert_writes_a_public_file_to_send_and_a_private_bundle_only_the_operator_can_read()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
        var outDir = Path.Combine(cli.Dir, "certs");

        var r = await cli.Run("identity", "new-cert", "acme", "--out-dir", outDir, "--days", "365");

        Assert.Equal(0, r.Code);
        var cer = Path.Combine(outDir, "acme.cer"); var pem = Path.Combine(outDir, "acme.pem");
        Assert.True(File.Exists(cer)); Assert.True(File.Exists(pem));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pem));
        using (var pub = X509CertificateLoader.LoadCertificate(await File.ReadAllBytesAsync(cer))) Assert.False(pub.HasPrivateKey);
        Assert.Contains("thumbprint", r.Out);
        Assert.DoesNotContain("PRIVATE KEY", r.Out); // the bundle's contents are never printed
    }

    [Fact]
    public async Task A_generated_bundle_goes_straight_into_identity_set_and_shows_its_expiry()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
        var outDir = Path.Combine(cli.Dir, "certs");
        await cli.Run("identity", "new-cert", "acme", "--out-dir", outDir, "--days", "365");

        Assert.Equal(0, (await cli.Run("identity", "set", "acme", "--azure-tenant", "aaaaaaaa-0000-4000-8000-000000000000", "--client-id", "bbbbbbbb-0000-4000-8000-000000000000", "--pem-file", Path.Combine(outDir, "acme.pem"))).Code);

        Assert.Contains("expires 2027-", (await cli.Run("tenant", "show", "acme")).Out);
        var expiry = (await cli.Run("identity", "expiry")).Out;
        Assert.Contains("acme", expiry);
        Assert.DoesNotContain("RENEW SOON", expiry);
    }

    [Fact]
    public async Task Expiry_warns_before_a_certificate_runs_out_and_again_after()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
        await cli.Run("tenant", "create", "globex", "Globex", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
        var outDir = Path.Combine(cli.Dir, "certs");
        await cli.Run("identity", "new-cert", "acme", "--out-dir", outDir, "--days", "60");
        await cli.Run("identity", "set", "acme", "--azure-tenant", "aaaaaaaa-0000-4000-8000-000000000000", "--client-id", "bbbbbbbb-0000-4000-8000-000000000000", "--pem-file", Path.Combine(outDir, "acme.pem"));

        Assert.DoesNotContain("RENEW SOON", (await cli.Run("identity", "expiry")).Out);
        cli.Clock.Advance(TimeSpan.FromDays(30));
        var soon = (await cli.Run("identity", "expiry")).Out;
        Assert.Contains("RENEW SOON", soon);
        Assert.Contains("globex\tno identity set", soon);
        cli.Clock.Advance(TimeSpan.FromDays(40));
        Assert.Contains("EXPIRED", (await cli.Run("identity", "expiry")).Out);
    }

    [Fact]
    public async Task Onboard_check_refuses_to_run_without_the_required_inputs_and_reads_secrets_only_from_the_environment()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");

        var noOrg = await cli.Run("onboard", "check", "acme", "--feed", "f");
        var missingVar = await cli.Run("onboard", "check", "acme", "--org", "o", "--feed", "f", "--admin-token-env", "PACKMOAT_TEST_DEFINITELY_UNSET");
        var noTenant = await cli.Run("onboard", "check", "nobody", "--org", "o", "--feed", "f");

        foreach (var bad in new[] { "o/x", "o%2F", "o?x", "o#x", "..", "" })
        {
            var refused = await cli.Run("onboard", "check", "acme", "--org", bad, "--feed", "f");
            Assert.Equal(1, refused.Code);
            Assert.Contains(OnboardingChecks.BadNameMessage, refused.Err);
        }
        Assert.Equal(1, noOrg.Code); Assert.Contains("--org is required", noOrg.Err);
        Assert.Equal(1, missingVar.Code); Assert.Contains("PACKMOAT_TEST_DEFINITELY_UNSET is not set", missingVar.Err);
        Assert.Equal(1, noTenant.Code);
    }
}
