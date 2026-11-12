namespace Packmoat.Infrastructure;

/// <summary>
/// The schema's migrations: what the server's start, the command line and the readiness check ask of a database's
/// schema, whatever its engine. The start migrates before anything else touches the database; the readiness check only reads.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface ISchemaMigrator
{
    /// <summary>
    /// Applies the core's migrations, then each of <paramref name="modules"/>' tracks in order. Refuses, before anything is
    /// written, a database whose tracks do not match this build's (<see cref="SchemaTrackException"/>), one newer than this build knows
    /// (<see cref="SchemaTooNewException"/>) and one a module's baseline cannot take over (<see cref="SchemaBaselineException"/>).
    /// </summary>
    Task<IReadOnlyList<TrackOutcome>> MigrateAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default);

    /// <summary>Where the core's and <paramref name="modules"/>' tracks stand. Read only: nothing is created or recorded.</summary>
    Task<IReadOnlyList<TrackVersion>> TrackVersionsAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default);
}
