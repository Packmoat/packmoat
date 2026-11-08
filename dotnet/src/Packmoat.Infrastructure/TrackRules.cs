namespace Packmoat.Infrastructure;

/// <summary>
/// The migration tracks' rules that hold on every engine (#196, #288): the migrators call them before anything is written, so SQLite and
/// PostgreSQL refuse the same databases with the same words. Reading the database (which tables exist, which versions are recorded) is
/// the engine's; judging what was read is here. Public, not internal: the PostgreSQL adapter is another assembly, and a core assembly may
/// share its internals only with its own .Tests project (CoreSplitArchitectureTests).
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class TrackRules
{
    /// <summary>Refuses a module's track named "core" (the core's name) and a track declared twice. Needs no database.</summary>
    public static void RequireDistinct(IReadOnlyList<MigrationTrack> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Any(m => m.Name == MigrationTrack.CoreName))
            throw new SchemaTrackException("A module's migration track is named \"core\", which is the core's. Refusing to run.");
        if (modules.GroupBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new SchemaTrackException($"The migration track \"{twice.Key}\" is declared twice. Refusing to run.");
    }

    /// <summary>
    /// Refuses a database with a track table (<paramref name="trackTables"/>: every <c>*_schema_migrations</c> table it has, in order)
    /// that neither <paramref name="core"/> nor <paramref name="modules"/> declares: a module this build does not have made it, and its
    /// tables could be misread or damaged.
    /// </summary>
    public static void RequireDeclared(MigrationTrack core, IReadOnlyList<MigrationTrack> modules, IEnumerable<string> trackTables)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(trackTables);
        var declared = modules.Select(m => m.Table).Append(core.Table).ToHashSet(StringComparer.Ordinal);
        var undeclared = trackTables.Where(t => !declared.Contains(t)).ToList();
        if (undeclared.Count > 0)
            throw new SchemaTrackException($"The database has the migration track table(s) {string.Join(", ", undeclared)}, which this build does not declare " +
                "(a module it does not have made them). Refusing to run: it could misread or damage that module's tables. Run a build with that module.");
    }

    /// <summary>Refuses a track whose newest recorded <paramref name="version"/> (null: no table) is newer than this build knows.</summary>
    public static void RequireNotNewer(MigrationTrack track, int? version)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (version > track.LatestVersion)
            throw track.Name == MigrationTrack.CoreName ? new SchemaTooNewException(version.Value, track.LatestVersion) : new SchemaTooNewException(track.Name, version.Value, track.LatestVersion);
    }
}
