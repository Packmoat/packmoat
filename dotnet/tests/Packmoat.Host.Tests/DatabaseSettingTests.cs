using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Packmoat.Host.Tests;

/// <summary>
/// PACKMOAT_DB, read from the real process environment, is <see cref="PackmoatOptions.Database"/> (r1: nothing tied the two
/// together, so the setting's name could change unnoticed). It sets a real process variable, so it runs in the non-parallel collection
/// and puts the variable back.
/// </summary>
[Collection("sqlite-pools")]
public class DatabaseSettingTests
{
    [Fact]   // mutation: PackmoatApp reads the database from another variable (PACKMOAT_DATABASE)
    public async Task PACKMOAT_DB_from_the_environment_is_the_options_database()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-db-env-").FullName;
        var saved = Environment.GetEnvironmentVariable("PACKMOAT_DB");
        try
        {
            var db = Path.Combine(dir, "packmoat.db");
            Environment.SetEnvironmentVariable("PACKMOAT_DB", db);
            WebApplication app;
            try
            {
                app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, o =>
                {
                    o.MasterKeyBase64 = Packmoat.Infrastructure.AesGcmSecretProtector.GenerateKeyBase64();
                    o.AllowedHosts = "feeds.example.test";
                });
            }
            finally
            {
                // Put back at once: the variable is process-wide, and other collections' tests build servers in parallel.
                Environment.SetEnvironmentVariable("PACKMOAT_DB", saved);
            }
            try
            {
                Assert.Equal(db, app.Services.GetRequiredService<PackmoatOptions>().Database);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally
        {
            Environment.SetEnvironmentVariable("PACKMOAT_DB", saved);
            TempDir.Delete(dir, clearSqlitePools: true);
        }
    }
}
