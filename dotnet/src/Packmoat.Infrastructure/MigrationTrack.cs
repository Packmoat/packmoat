using System.Text.RegularExpressions;

namespace Packmoat.Infrastructure;

/// <summary>
/// A list of migrations with its own version table (#196). The core's track is <see cref="SqliteDatabase.Core"/> (table
/// schema_migrations); a module's is named by the module and kept in <c>{name}_schema_migrations</c>. The core runs first, then each
/// module's track in order; each track refuses a database its own table says is newer than it knows.
/// </summary>
public sealed partial class MigrationTrack
{
    /// <summary>The core's track name. Its table is schema_migrations (named before there were tracks).</summary>
    public const string CoreName = "core";

    [GeneratedRegex("^[a-z][a-z0-9]{0,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <param name="name">Lower-case letters and digits, starting with a letter: it becomes part of a table name.</param>
    /// <param name="migrations">One element per migration (version = index + 1), one statement per inner element. An empty migration
    /// still records its version.</param>
    /// <param name="baseline">How the track takes over a database an older build made with the track's statements in the core's track;
    /// null for a track that was never anywhere else.</param>
    public MigrationTrack(string name, string[][] migrations, TrackBaseline? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(migrations);
        if (!NamePattern().IsMatch(name)) throw new ArgumentException($"A migration track's name must be lower-case letters and digits, starting with a letter (\"{name}\").", nameof(name));
        if (baseline is not null && (baseline.Version < 1 || baseline.Version > migrations.Length))
            throw new ArgumentException($"Track {name}'s baseline adopts version {baseline.Version}, which the track does not have.", nameof(baseline));
        Name = name;
        Migrations = migrations;
        Baseline = baseline;
    }

    public string Name { get; }
    public string[][] Migrations { get; }
    public TrackBaseline? Baseline { get; }

    /// <summary>The newest version this build knows of the track.</summary>
    public int LatestVersion => Migrations.Length;

    /// <summary>The track's version table.</summary>
    public string Table => TableFor(Name);

    internal static string TableFor(string name) => name == CoreName ? "schema_migrations" : name + "_schema_migrations";
}

/// <summary>
/// Where one track stands in a database (#233: the readiness check): this build's <see cref="Latest"/> version, the <see cref="Newest"/>
/// recorded (null when the track has no table) and how many of versions 1 to <see cref="Latest"/> are <see cref="Recorded"/>.
/// </summary>
public sealed record TrackVersion(string Track, int Latest, int? Newest, int Recorded)
{
    /// <summary>Every version this build knows is recorded, and none newer.</summary>
    public bool IsCurrent => Newest == Latest && Recorded == Latest;
}

/// <summary>
/// How a module's track takes over a database an older build made when the track's statements were part of the core's track (#196).
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
/// The decision and the record are made in one BEGIN IMMEDIATE transaction; a refusal writes nothing.
/// </summary>
public sealed record TrackBaseline(int Version, int FirstCoreVersion, int CoreVersion, IReadOnlyList<string> Tables, IReadOnlyList<(string Table, string Column)> Columns);

/// <summary>What one run did to one track: its version before (null: it had no table) and after, and whether it was adopted.</summary>
public sealed record TrackOutcome(string Name, int? Before, int After, bool Adopted);

/// <summary>The database's migration tracks do not match this build's (an undeclared track's table, or a track declared twice). Nothing was written.</summary>
public sealed class SchemaTrackException(string message) : InvalidOperationException(message);

/// <summary>A database an older build made cannot be taken over by a track's baseline (#196). Nothing was written.</summary>
public sealed class SchemaBaselineException(string message) : InvalidOperationException(message);
