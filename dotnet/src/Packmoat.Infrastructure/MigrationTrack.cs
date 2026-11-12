using System.Text.RegularExpressions;

namespace Packmoat.Infrastructure;

/// <summary>
/// A list of migrations with its own version table. The core's track is named <see cref="CoreName"/> (table
/// schema_migrations); a module's is named by the module and kept in <c>{name}_schema_migrations</c>. The core runs first, then each
/// module's track in order; each track refuses a database its own table says is newer than it knows.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed partial class MigrationTrack
{
    /// <summary>The core's track name. Its table is schema_migrations (named before there were tracks).</summary>
    public const string CoreName = "core";

    [GeneratedRegex("^[a-z][a-z0-9]{0,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private readonly Dictionary<DatabaseEngine, string[][]> _statements;

    /// <summary>A track with SQLite statements only: the per-engine constructor given SQLite alone.</summary>
    /// <param name="name">Lower-case letters and digits, starting with a letter: it becomes part of a table name.</param>
    /// <param name="migrations">One element per migration (version = index + 1), one statement per inner element. An empty migration
    /// still records its version.</param>
    /// <param name="baseline">How the track takes over a database an older build made with the track's statements in the core's track;
    /// null for a track that was never anywhere else.</param>
    public MigrationTrack(string name, string[][] migrations, TrackBaseline? baseline = null)
        : this(name, OnSqliteAlone(migrations), baseline)
    {
    }

    /// <summary>
    /// A track with statements per engine. Every engine has the same versions (the same number of migrations, version
    /// = index + 1), so a track's version table means the same on any engine.
    /// </summary>
    /// <param name="name">Lower-case letters and digits, starting with a letter: it becomes part of a table name.</param>
    /// <param name="statements">For each engine the track runs on, one element per migration and one statement per inner element. At
    /// least one engine, and every engine the same number of migrations.</param>
    /// <param name="baseline">As for the SQLite-only constructor.</param>
    public MigrationTrack(string name, IReadOnlyDictionary<DatabaseEngine, string[][]> statements, TrackBaseline? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(statements);
        if (!NamePattern().IsMatch(name)) throw new ArgumentException($"A migration track's name must be lower-case letters and digits, starting with a letter (\"{name}\").", nameof(name));
        if (statements.Count == 0) throw new ArgumentException($"Track {name} has no engine's statements.", nameof(statements));
        foreach (var (engine, migrations) in statements)
            if (migrations is null) throw new ArgumentException($"Track {name} has no list of migrations for {DatabaseEngines.Name(engine)}.", nameof(statements));
        var counts = statements.OrderBy(e => e.Key).Select(e => (Engine: e.Key, Count: e.Value.Length)).ToList();
        if (counts.Select(c => c.Count).Distinct().Count() > 1)
            throw new ArgumentException($"Track {name}'s engines have different numbers of migrations ({string.Join(", ", counts.Select(c => $"{DatabaseEngines.Name(c.Engine)} {c.Count}"))}). " +
                "Every engine must have the same versions.", nameof(statements));
        var latest = counts[0].Count;
        if (baseline is not null && (baseline.Version < 1 || baseline.Version > latest))
            throw new ArgumentException($"Track {name}'s baseline adopts version {baseline.Version}, which the track does not have.", nameof(baseline));
        Name = name;
        _statements = statements.ToDictionary(e => e.Key, e => e.Value);
        Engines = counts.Select(c => c.Engine).ToList();
        LatestVersion = latest;
        Baseline = baseline;
    }

    private static Dictionary<DatabaseEngine, string[][]> OnSqliteAlone(string[][] migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        return new() { [DatabaseEngine.Sqlite] = migrations };
    }

    public string Name { get; }
    public TrackBaseline? Baseline { get; }

    /// <summary>The engines this track has statements for, in the enum's order.</summary>
    public IReadOnlyList<DatabaseEngine> Engines { get; }

    /// <summary>
    /// The track's migrations on <paramref name="engine"/>: one element per migration (version = index + 1), one statement per inner
    /// element. Throws <see cref="SchemaTrackException"/>, naming the track and the engine, when the track has none for it.
    /// </summary>
    public string[][] StatementsFor(DatabaseEngine engine) =>
        _statements.TryGetValue(engine, out var migrations) ? migrations
            : throw new SchemaTrackException($"The migration track \"{Name}\" has no {DatabaseEngines.Name(engine)} statements. Refusing to run. Nothing was written.");

    /// <summary>The newest version this build knows of the track: its number of migrations, the same on every engine.</summary>
    public int LatestVersion { get; }

    /// <summary>The track's version table.</summary>
    public string Table => TableFor(Name);

    internal static string TableFor(string name) => name == CoreName ? "schema_migrations" : name + "_schema_migrations";
}

/// <summary>
/// Where one track stands in a database (the readiness check): this build's <see cref="Latest"/> version, the <see cref="Newest"/>
/// recorded (null when the track has no table) and how many of versions 1 to <see cref="Latest"/> are <see cref="Recorded"/>.
/// </summary>
public sealed record TrackVersion(string Track, int Latest, int? Newest, int Recorded)
{
    /// <summary>Every version this build knows is recorded, and none newer.</summary>
    public bool IsCurrent => Newest == Latest && Recorded == Latest;

    /// <summary>
    /// Every version this build knows is recorded, and newer ones too: a newer build migrated the database while this one runs (a
    /// rolling upgrade of several copies). Migrations are expand-only for one release, so the previous release keeps working on the
    /// schema its successor made; the readiness check on PostgreSQL takes this as ready. The start still refuses it.
    /// </summary>
    public bool IsCurrentOrMigratedPast => IsCurrent || (Newest > Latest && Recorded == Latest);
}

/// <summary>
/// How a module's track takes over a database an older build made when the track's statements were part of the core's track.
/// That build ran them at core versions <see cref="FirstCoreVersion"/> to <see cref="CoreVersion"/>, so a database without the track's
/// table is judged by its core version before anything runs, then by structure (never by SQL text: the text an older build stored may
/// differ in whitespace):
/// <list type="bullet">
/// <item>core version from <see cref="FirstCoreVersion"/> to below <see cref="CoreVersion"/>: refused (the older build ran only part);</item>
/// <item>every one of <see cref="Tables"/> and <see cref="Columns"/> present, at <see cref="CoreVersion"/>: versions 1 to
/// <see cref="Version"/> are recorded as applied without running;</item>
/// <item>none present: the track runs from version 1, after the core;</item>
/// <item>anything else: refused, naming what is present and what is missing.</item>
/// </list>
/// The decision and the record are made together, with no other writer able to change the database in between; a refusal writes nothing.
/// </summary>
public sealed record TrackBaseline(int Version, int FirstCoreVersion, int CoreVersion, IReadOnlyList<string> Tables, IReadOnlyList<(string Table, string Column)> Columns);

/// <summary>What one run did to one track: its version before (null: it had no table) and after, and whether it was adopted.</summary>
public sealed record TrackOutcome(string Name, int? Before, int After, bool Adopted);

/// <summary>The database's migration tracks do not match this build's (an undeclared track's table, a track declared twice, or a track
/// with no statements for the database's engine). Nothing was written.</summary>
public sealed class SchemaTrackException(string message) : InvalidOperationException(message);

/// <summary>A database an older build made cannot be taken over by a track's baseline. Nothing was written.</summary>
public sealed class SchemaBaselineException(string message) : InvalidOperationException(message);
