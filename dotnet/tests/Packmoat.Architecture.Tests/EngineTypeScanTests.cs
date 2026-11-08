namespace Packmoat.Architecture.Tests;

/// <summary>
/// No database engine's types outside the adapters and the composition allowlist (#278 step 0, architecture test 1): in the core's src/,
/// only these files may name <c>Microsoft.Data.Sqlite</c>, a <c>Sqlite*</c> type or <c>Npgsql</c> in code (comments aside). Everything
/// else, the module seams and the lifecycle's ports first, sees <c>IRelationalDatabase</c> and <c>DbTransaction</c>. The hosted
/// projects have their own twin.
/// </summary>
public class EngineTypeScanTests
{
    /// <summary>The PostgreSQL adapter's folder (#288), and the SQLite adapters (SqliteStores among them: the one list of how each is built), the migration copy check, the
    /// organisation store, the server's registration of that list, and the two composition roots (server and CLI).</summary>
    internal static readonly string[] Allowed =
    [
        "Packmoat.Infrastructure/Sqlite*.cs",
        "Packmoat.Infrastructure/SqliteDatabase.cs",
        "Packmoat.Infrastructure/MigrationDryRun.cs",
        "Packmoat.Infrastructure/CoreSchema.cs",   // #288 P2: the core's track takes SQLite's statements where they are (SqliteDatabase.Migrations)
        "Packmoat.Infrastructure/Lifecycle/OrganisationCreation.cs",
        "Packmoat.Host/SqliteStoreRegistration.cs",
        "Packmoat.Host/PackmoatApp.cs",
        "Packmoat.Admin/AdminCli.cs",
        "Packmoat.Infrastructure.Postgres/*.cs",
    ];

    /// <summary>Where Npgsql may be named (#288): the PostgreSQL adapter's own folder, and nowhere else.</summary>
    internal const string PostgresAdapter = "Packmoat.Infrastructure.Postgres/";

    private static (string Named, string Source)[] CoreSource() => EngineTypeScan.SourceFiles(Path.Combine(CoreRoot.Path(), "dotnet", "src"));

    // mutations: a SqliteTransaction back in TenantLifecycle.cs; SqliteDatabase back in IPackmoatModule.cs or IAdminCliModule.cs
    [Fact]
    public void Only_the_adapters_and_the_composition_roots_name_an_engine_type()
    {
        var files = CoreSource();
        Assert.True(files.Length > 100, "the scan found too few source files to mean anything");
        var found = EngineTypeScan.Offenders(files, Allowed);
        Assert.True(found.Length == 0, "These files name a database engine's type. Use IRelationalDatabase or DbTransaction, or move the code " +
            "into a SQLite adapter:\n  " + string.Join("\n  ", found));
    }

    [Fact]   // the list shrinks with the code: an entry that names no engine type any more (or is gone) is taken off
    public void Every_exact_entry_on_the_allowlist_still_names_an_engine_type() =>
        Assert.Empty(EngineTypeScan.StaleEntries(CoreSource(), Allowed));

    /// <summary>Each file that names an engine type of the wrong engine for where it is: Npgsql outside <see cref="PostgresAdapter"/>, or
    /// a SQLite type inside it.</summary>
    internal static string[] WrongEngine(IEnumerable<(string Named, string Source)> files) =>
        files.Select(f => (f.Named, Types: EngineTypeScan.EngineTypesNamed(f.Source)))
            .Select(f => (f.Named, Wrong: f.Named.StartsWith(PostgresAdapter, StringComparison.Ordinal)
                ? f.Types.Where(t => t != "Npgsql").ToArray()
                : f.Types.Where(t => t == "Npgsql").ToArray()))
            .Where(f => f.Wrong.Length > 0)
            .Select(f => $"{f.Named}: {string.Join(", ", f.Wrong)}")
            .Order(StringComparer.Ordinal).ToArray();

    // #288. mutations: an Npgsql using in a SQLite adapter (SqliteDatabase.cs); a SqliteConnection in the PostgreSQL adapter
    [Fact]
    public void Npgsql_is_named_only_in_the_Postgres_adapter_and_the_adapter_names_no_SQLite_type()
    {
        var files = CoreSource();
        Assert.Contains(files, f => f.Named.StartsWith(PostgresAdapter, StringComparison.Ordinal) && EngineTypeScan.EngineTypesNamed(f.Source).Contains("Npgsql"));
        var wrong = WrongEngine(files);
        Assert.True(wrong.Length == 0, "Npgsql belongs in src/Packmoat.Infrastructure.Postgres/ only, and SQLite's types outside it:\n  " + string.Join("\n  ", wrong));
    }

    [Fact]
    public void The_wrong_engine_check_sees_each_way_round()
    {
        Assert.Equal(["Packmoat.Infrastructure.Postgres/PostgresDatabase.cs: SqliteConnection", "Packmoat.Infrastructure/SqliteDatabase.cs: Npgsql"],
            WrongEngine([
                ("Packmoat.Infrastructure/SqliteDatabase.cs", "using Npgsql; SqliteConnection c;"),
                ("Packmoat.Infrastructure.Postgres/PostgresDatabase.cs", "using Npgsql; SqliteConnection c;"),
                ("Packmoat.Infrastructure.Postgres/PostgresConnectionString.cs", "using Npgsql; // SqliteConnection"),
            ]));
        Assert.True(EngineTypeScan.IsAllowed("Packmoat.Infrastructure.Postgres/PostgresDatabase.cs", Allowed));
        Assert.False(EngineTypeScan.IsAllowed("Packmoat.Infrastructure.Postgres/Sub/X.cs", Allowed));
        Assert.False(EngineTypeScan.IsAllowed("Packmoat.Infrastructure.PostgresX/X.cs", Allowed));
    }

    [Fact]
    public void The_scan_sees_engine_types_in_code_and_not_in_comments()
    {
        Assert.Equal(["SqliteTransaction"], EngineTypeScan.EngineTypesNamed("var t = (SqliteTransaction)tx;"));
        Assert.Equal(["Microsoft.Data.Sqlite"], EngineTypeScan.EngineTypesNamed("using Microsoft.Data.Sqlite;"));
        Assert.Equal(["Npgsql"], EngineTypeScan.EngineTypesNamed("var c = new Npgsql.NpgsqlConnection(s);"));
        Assert.Equal(["SqliteDatabase"], EngineTypeScan.EngineTypesNamed("Func<SqliteDatabase, int> f;"));
        // comments are not code; nor is the word SQLite, or a longer name that only contains it
        Assert.Empty(EngineTypeScan.EngineTypesNamed("/// <see cref=\"SqliteDatabase\"/>\n// SqliteTransaction\n/* Npgsql */ var x = 1;"));
        Assert.Empty(EngineTypeScan.EngineTypesNamed("// SQLite only\nvar isSqlite = IsSqliteEngine; DbTransaction t;"));
        // a string literal is kept: "//" inside one does not start a comment
        Assert.Equal(["SqliteDatabase"], EngineTypeScan.EngineTypesNamed("var u = \"http://x\"; SqliteDatabase d;"));
    }

    // mutation (review): strip comments with a regex that knows only regular strings again; each of these then hides the code after it
    [Theory]
    [InlineData("var q = '\"'; var x = \"a//\"; SqliteDatabase? hidden = null;")]                       // a char literal holding a quote
    [InlineData("var q = '\\''; var x = \"a//\"; SqliteDatabase? hidden = null;")]                      // an escaped quote char
    [InlineData("var p = @\"C:\\\"; var x = \"a//\"; SqliteDatabase? hidden = null;")]                  // a verbatim string ending in a backslash
    [InlineData("var p = @\"say \"\"//\"\" \"; SqliteDatabase? hidden = null;")]                        // a verbatim string with "" and //
    [InlineData("var r = \"\"\" \" \"\"\"; var x = \"a//\"; SqliteDatabase? hidden = null;")]          // a raw string holding a quote
    [InlineData("var r = \"\"\"\n  \"\n  \"\"\"; var x = \"a//\"; SqliteDatabase? hidden = null;")]  // a multi-line raw string
    [InlineData("var r = $\"\"\"{x} \"/*\" \"\"\"; SqliteDatabase? hidden = null; // */")]           // a raw interpolated string with /*
    public void String_syntax_cannot_hide_code_from_the_scan(string source) =>
        Assert.Equal(["SqliteDatabase"], EngineTypeScan.EngineTypesNamed(source));

    [Fact]
    public void The_allowlist_takes_exact_paths_and_one_folders_prefix()
    {
        Assert.True(EngineTypeScan.IsAllowed("Packmoat.Infrastructure/SqliteAuditLog.cs", Allowed));
        Assert.True(EngineTypeScan.IsAllowed("Packmoat.Host/PackmoatApp.cs", Allowed));
        Assert.False(EngineTypeScan.IsAllowed("Packmoat.Infrastructure/Lifecycle/SqliteSomething.cs", Allowed));   // not in a subfolder
        Assert.False(EngineTypeScan.IsAllowed("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", Allowed));
        Assert.False(EngineTypeScan.IsAllowed("Packmoat.Host/Modules/IPackmoatModule.cs", Allowed));
        var files = new[]
        {
            ("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", "Task OnCreatingAsync(SqliteTransaction transaction);"),
            ("Packmoat.Infrastructure/SqliteAuditLog.cs", "using Microsoft.Data.Sqlite;"),
            ("Packmoat.Host/Modules/IPackmoatModule.cs", "/// <see cref=\"SqliteDatabase\"/>\npublic interface IPackmoatModule;"),
        };
        Assert.Equal(["Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs: SqliteTransaction"], EngineTypeScan.Offenders(files, Allowed));
        Assert.Equal(["Packmoat.Host/PackmoatApp.cs"], EngineTypeScan.StaleEntries(files.Append(("Packmoat.Host/PackmoatApp.cs", "// SqliteDatabase")),
            ["Packmoat.Infrastructure/Sqlite*.cs", "Packmoat.Infrastructure/SqliteAuditLog.cs", "Packmoat.Host/PackmoatApp.cs"]));
    }
}
