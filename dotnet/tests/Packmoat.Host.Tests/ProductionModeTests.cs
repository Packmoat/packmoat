namespace Packmoat.Host.Tests;

/// <summary>
/// The production switch, named (#278 step 0, A5). Today it is exactly "PACKMOAT_DB is set", as before it had a name: no setting of its
/// own, and the other settings do not move it.
/// </summary>
[Collection("sqlite-pools")]
public class ProductionModeTests
{
    [Theory]   // mutations: inverted; read from another setting; an empty value read as unset (the truth table would change)
    [InlineData(null, false)]
    [InlineData("/data/packmoat.db", true)]
    [InlineData("packmoat.db", true)]
    [InlineData("", true)]
    public void Production_mode_is_on_exactly_when_a_database_is_configured(string? databasePath, bool on) =>
        Assert.Equal(on, ProductionMode.On(new PackmoatOptions { DatabasePath = databasePath }));

    [Theory]   // mutation: the switch follows the engine, the key folder or the cookie key storage
    [InlineData(null, false)]
    [InlineData("/data/packmoat.db", true)]
    public void The_other_settings_do_not_move_it(string? databasePath, bool on) =>
        Assert.Equal(on, ProductionMode.On(new PackmoatOptions
        {
            DatabasePath = databasePath,
            DatabaseEngine = "postgres",
            AdminKeysDir = "/srv/keys",
            CookieKeys = "database",
            AllowedHosts = "feeds.example.test",
        }));

    [Fact]   // mutation: the host filter's refusal no longer follows the production switch (inverted, it would refuse every test host)
    public void In_production_a_server_without_PACKMOAT_HOSTS_still_refuses_to_start()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-production-").FullName;
        try
        {
            var error = new StringWriter();
            // A build that succeeds is never run (that would serve until killed): it is disposed and reported as a plain failure.
            var code = StartupFailure.Run(() =>
            {
                var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, o =>
                {
                    o.DatabasePath = Path.Combine(dir, "packmoat.db");
                    o.MasterKeyBase64 = Packmoat.Infrastructure.AesGcmSecretProtector.GenerateKeyBase64();
                });
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new StartedException();
            }, error);
            Assert.Equal(1, code);
            Assert.Equal("error: PACKMOAT_HOSTS is not set. Refusing to start without a list of the hostnames this service answers to.", error.ToString().TrimEnd());
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    /// <summary>Thrown when the server built, which the test above says it must not.</summary>
    private sealed class StartedException : Exception;
}
