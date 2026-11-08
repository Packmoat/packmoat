namespace Packmoat.Architecture.Tests;

/// <summary>
/// Port docs state guarantees, not mechanisms (#278 step 0, A6; architecture test 9). The doc comments of the Application project (the
/// ports and their rules) and of the module seams (<c>///</c> and <c>/** */</c>) name no storage mechanism: not SQLite, IMMEDIATE, WAL,
/// PRAGMA, rowid, a trigger, a write lock, an exclusive lock, the journal, a savepoint or busy_timeout, and no <c>Sqlite*</c> type. Say what a caller can rely on instead, for example "no other writer can change it between
/// the check and the write". A ratchet: each file's count is pinned and may only fall (a file not listed is pinned at zero). The hosted
/// projects have their own twin.
/// </summary>
public class PortDocWordTests
{
    /// <summary>The module seams' files outside Application: what a module, or a second engine's adapter, reads to learn the contract.</summary>
    internal static readonly string[] SeamFiles =
    [
        "Packmoat.Admin/IAdminCliModule.cs",
        "Packmoat.Host/Modules/IAdminSiteModule.cs",
        "Packmoat.Host/Modules/ICustomerSiteModule.cs",
        "Packmoat.Host/Modules/IPackmoatModule.cs",
        "Packmoat.Infrastructure/IRelationalDatabase.cs",
        "Packmoat.Infrastructure/ISchemaMigrator.cs",
        "Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs",
        "Packmoat.Infrastructure/MigrationTrack.cs",
    ];

    /// <summary>The counts that remain, each a pointer to the one engine there is or a type that is SQLite's alone. Lower a pin as it falls.</summary>
    internal static readonly Dictionary<string, int> Pinned = new(StringComparer.Ordinal)
    {
        // "for SQLite, SqliteDatabase.From": where the one engine's checked conversion is
        ["Packmoat.Infrastructure/IRelationalDatabase.cs"] = 2,
        // the constructor that takes SQLite statements alone (it is SQLite's, by design), named in its own doc and the other's
        ["Packmoat.Infrastructure/MigrationTrack.cs"] = 3,
    };

    private static (string Named, string Source)[] Scanned()
    {
        var src = Path.Combine(CoreRoot.Path(), "dotnet", "src");
        var all = EngineTypeScan.SourceFiles(src);
        return all.Where(f => f.Named.StartsWith("Packmoat.Application/", StringComparison.Ordinal) || SeamFiles.Contains(f.Named)).ToArray();
    }

    // mutations: "BEGIN IMMEDIATE" back in ConnectFeed's doc; "the purge's write lock" back in TenantLifecycle's; a <see cref="SqliteDatabase"/>
    // in a port's doc
    [Fact]
    public void Port_docs_name_no_storage_mechanism_beyond_the_pinned_counts()
    {
        var files = Scanned();
        Assert.True(files.Count(f => f.Named.StartsWith("Packmoat.Application/", StringComparison.Ordinal)) > 25, "the scan found too few Application files to mean anything");
        Assert.Equal(SeamFiles.Length, files.Count(f => SeamFiles.Contains(f.Named)));
        var off = DocWordScan.OffPin(files, Pinned);
        Assert.True(off.Length == 0, "A port's doc comment names a storage mechanism (or names fewer than pinned). Say the guarantee instead " +
            "(\"no other writer can change it between the check and the write\"); if a count fell, lower its pin:\n  " + string.Join("\n  ", off));
    }

    [Fact]   // the list shrinks with the code: no pin at zero, none for a file that is gone, and Application stays at zero
    public void Every_pin_is_for_a_scanned_file_outside_Application_and_above_zero()
    {
        Assert.Empty(DocWordScan.MissingPins(Scanned(), Pinned));
        Assert.All(Pinned, p => Assert.True(p.Value > 0, $"{p.Key} is pinned at zero: take it off"));
        Assert.DoesNotContain(Pinned.Keys, k => k.StartsWith("Packmoat.Application/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scan_counts_mechanism_words_in_doc_comments_only()
    {
        Assert.Equal(["IMMEDIATE"], DocWordScan.MechanismWords("/// In ONE write transaction (BEGIN IMMEDIATE), refuses unless\nTask F();"));
        Assert.Equal(["write lock"], DocWordScan.MechanismWords("    /// re-check under the purge's write lock\n"));
        Assert.Equal(["SQLite", "SqliteDatabase"], DocWordScan.MechanismWords("/// for SQLite, <see cref=\"SqliteDatabase.From\"/>\n"));
        Assert.Equal(["WAL", "PRAGMA", "rowid", "trigger", "Triggers", "write-lock"],
            DocWordScan.MechanismWords("/// WAL mode, PRAGMA x, the rowid\n/// the trigger's floor. Triggers; a write-lock\n"));
        Assert.Equal(["write-locked", "exclusive lock", "journal", "savepoint", "busy_timeout", "Journaling", "Savepoints", "write locked"],
            DocWordScan.MechanismWords("/// the row is write-locked; an exclusive lock; the journal\n/// a savepoint, busy_timeout\n/// Journaling. Savepoints; write locked\n"));
        // a /** */ doc comment, on one line or many, is read too; a plain /* */ or the empty /**/ is not
        Assert.Equal(["IMMEDIATE", "write lock", "SQLite"],
            DocWordScan.MechanismWords("/** BEGIN IMMEDIATE */ void A();\n/**\n * under the write lock,\n * on SQLite\n */ void B();\n/**/ var x = \"WAL\"; /* PRAGMA */"));
        Assert.Equal(["savepoint"], DocWordScan.MechanismWords("/** <summary>A savepoint.</summary> */\n//// four slashes: WAL\n"));
        // not a doc comment, not a comment, or not the word
        Assert.Empty(DocWordScan.MechanismWords("// BEGIN IMMEDIATE\n/* SQLite */ var s = \"/// WAL\"; var isSqlite = true;"));
        Assert.Empty(DocWordScan.MechanismWords("/// Takes effect immediately; a wall clock; the database's own floor.\n"));
        // a /// inside a string is data, and does not hide the doc comment after it
        Assert.Equal(["IMMEDIATE"], DocWordScan.MechanismWords("var u = \"/// WAL\";\n/// BEGIN IMMEDIATE\n"));
    }

    [Fact]
    public void The_ratchet_fails_a_rise_and_a_fall_and_a_missing_file()
    {
        var files = new[]
        {
            ("Packmoat.Application/Ports.cs", "/// No other writer can change it between the check and the write.\n"),
            ("Packmoat.Application/ConnectFeed.cs", "/// In ONE write transaction (BEGIN IMMEDIATE)\n"),
            ("Packmoat.Infrastructure/MigrationTrack.cs", "/// SQLite statements only\n"),
        };
        var pinned = new Dictionary<string, int> { ["Packmoat.Infrastructure/MigrationTrack.cs"] = 2, ["Packmoat.Infrastructure/Gone.cs"] = 1 };
        Assert.Equal(["Packmoat.Application/ConnectFeed.cs: found 1, pinned 0 (IMMEDIATE)", "Packmoat.Infrastructure/MigrationTrack.cs: found 1, pinned 2 (SQLite)"],
            DocWordScan.OffPin(files, pinned));
        Assert.Equal(["Packmoat.Infrastructure/Gone.cs"], DocWordScan.MissingPins(files, pinned));
    }
}
