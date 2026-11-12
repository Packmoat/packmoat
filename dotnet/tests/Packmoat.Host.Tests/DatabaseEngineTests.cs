using Microsoft.Extensions.DependencyInjection;
using Packmoat.Host.Modules;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// PACKMOAT_DB_ENGINE: unset (as on the live server) and <c>sqlite</c> compose exactly as before; any unknown value stops the
/// start with one <c>error:</c> line naming the setting, before anything is written. <c>postgres</c> is selectable
/// (declared change: it was refused): it starts only with a connection string and the database's cookie keys, and only once the start's
/// checks pass (PostgresServerStartTests).
/// </summary>
[Collection("sqlite-pools")]
public class DatabaseEngineTests
{
    [Theory]   // mutation: the default (unset or empty) is postgres
    [InlineData(null, DatabaseEngine.Sqlite)]
    [InlineData("", DatabaseEngine.Sqlite)]
    [InlineData("sqlite", DatabaseEngine.Sqlite)]
    [InlineData("postgres", DatabaseEngine.Postgres)]
    public void Unset_is_sqlite_and_each_engine_is_its_own_word(string? value, DatabaseEngine engine) => Assert.Equal(engine, DatabaseEngines.Parse(value));

    [Theory]   // mutation: an unknown value is taken (as sqlite or as postgres)
    [InlineData("SQLite")]
    [InlineData("Postgres")]
    [InlineData("postgresql")]
    [InlineData("mysql-xyz")]
    [InlineData(" sqlite")]
    [InlineData("sqlite ")]
    [InlineData("   ")]   // whitespace only is not unset: refused, not read as the default
    [InlineData("\t")]
    public void An_unknown_value_is_refused_naming_the_setting_not_the_value(string value)
    {
        var ex = Assert.Throws<SettingsException>(() => DatabaseEngines.Parse(value));
        Assert.Equal("PACKMOAT_DB_ENGINE must be sqlite or postgres.", ex.Message);
        Assert.Throws<SettingsException>(() => DatabaseEngines.RequireAvailable(value));
    }

    [Theory]   // mutation: postgres read as a file (a key folder would be derived from its connection string)
    [InlineData(DatabaseEngine.Sqlite, true)]
    [InlineData(DatabaseEngine.Postgres, false)]
    public void Only_sqlites_database_is_a_file(DatabaseEngine engine, bool file) => Assert.Equal(file, DatabaseEngines.IsFile(engine));

    [Fact]   // mutation: RequireAvailable still refuses postgres
    public void Both_engines_are_available_in_this_build()
    {
        Assert.Equal(DatabaseEngine.Sqlite, DatabaseEngines.RequireAvailable(null));
        Assert.Equal(DatabaseEngine.Sqlite, DatabaseEngines.RequireAvailable("sqlite"));
        Assert.Equal(DatabaseEngine.Postgres, DatabaseEngines.RequireAvailable("postgres"));
    }

    [Fact]   // the setting is read through the one settings reader, so it can come from a file as every other can
    public void The_setting_is_read_by_name_through_the_settings_reader()
    {
        var reader = new Settings(n => n == DatabaseEngines.Variable + Settings.FileSuffix ? "/run/secrets/engine" : null, _ => "postgres\n");
        Assert.Equal(DatabaseEngine.Postgres, DatabaseEngines.Parse(reader.Read(DatabaseEngines.Variable)));
    }

    /// <summary>Notes the engine the core hands the modules.</summary>
    private sealed class EngineProbe : IPackmoatModule
    {
        public DatabaseEngine? Seen;
        public void ConfigureServices(PackmoatModuleContext context) => Seen = context.Engine;
        public ModuleSite? ConfigureSite(PackmoatModuleContext context, IReadOnlyCollection<string> takenHosts) => null;
        public MigrationTrack? Migrations => null;
    }

    private static void Options(PackmoatOptions o, string dir, string? engine)
    {
        o.Database = Path.Combine(dir, "packmoat.db");
        o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
        o.AllowedHosts = "feeds.example.test";
        o.DatabaseEngine = engine;
    }

    private static async Task<(string[] Registrations, DatabaseEngine? Seen)> Compose(string? engine)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-engine-").FullName;
        try
        {
            IServiceCollection? services = null;
            var probe = new EngineProbe();
            await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [probe], s => services = s, o => Options(o, dir, engine));
            var lines = services!.Select(d => $"{d.ServiceType.FullName} {d.Lifetime} {d.ImplementationType?.FullName ?? d.ImplementationInstance?.GetType().FullName ?? d.ImplementationFactory?.Method.ReturnType.FullName}").ToArray();
            return (lines, probe.Seen);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // the live server has no PACKMOAT_DB_ENGINE: unset must compose as sqlite does, and the modules are told sqlite
    public async Task Unset_and_sqlite_compose_the_same_and_the_modules_are_told_sqlite()
    {
        var unset = await Compose(null);
        var sqlite = await Compose("sqlite");
        Assert.Equal(unset.Registrations, sqlite.Registrations);
        Assert.Equal(DatabaseEngine.Sqlite, unset.Seen);
        Assert.Equal(DatabaseEngine.Sqlite, sqlite.Seen);
    }

    /// <summary>Thrown when the server built, which these tests say it must not.</summary>
    private sealed class StartedException : Exception;

    [Theory]   // mutations: an unknown value is taken; postgres given a key folder, a file path or a bad PACKMOAT_DB_SINGLE_ROLE starts
    [InlineData("mysql-xyz", null, null, "error: PACKMOAT_DB_ENGINE must be sqlite or postgres.")]
    // Postgres is taken now, but not with folder cookie keys (the default), whose folder would sit beside no file
    [InlineData("postgres", null, null, "error: PACKMOAT_DB_ENGINE=postgres needs PACKMOAT_COOKIE_KEYS=database: its PACKMOAT_DB is no file to keep a key folder next to.")]
    // nor with a file path for PACKMOAT_DB (named, never echoed)
    [InlineData("postgres", "database", null, "error: PACKMOAT_DB is not a valid PostgreSQL connection string.")]
    // nor with PACKMOAT_DB_SINGLE_ROLE anything but on or off (read on postgres only)
    [InlineData("postgres", "database", "yes", "error: PACKMOAT_DB_SINGLE_ROLE must be on or off.")]
    public void A_bad_engine_setting_stops_the_start_with_one_error_line_before_anything_is_written(string engine, string? cookieKeys, string? singleRole, string line)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-engine-bad-").FullName;
        try
        {
            var probe = new EngineProbe();
            var error = new StringWriter();
            // A build that succeeds is never run (that would serve until killed): it is disposed and reported as a plain failure.
            var code = StartupFailure.Run(() =>
            {
                var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [probe], null, o =>
                {
                    Options(o, dir, engine);
                    o.CookieKeys = cookieKeys;
                    o.DatabaseSingleRole = singleRole;
                });
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new StartedException();
            }, error);
            Assert.Equal(1, code);
            Assert.Equal(line, error.ToString().TrimEnd());
            Assert.Null(probe.Seen);   // no module was configured
            Assert.False(File.Exists(Path.Combine(dir, "packmoat.db")));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
