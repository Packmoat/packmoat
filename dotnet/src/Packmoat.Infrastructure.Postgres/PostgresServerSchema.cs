namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The schema port the server's start and readiness check get on PostgreSQL: on PostgreSQL the server never migrates. The
/// owner role migrates, with <c>packmoat-admin db migrate --grant-to</c> (<see cref="PostgresDatabase.MigrateAndGrantAsync(IReadOnlyList{MigrationTrack}, string, CancellationToken)"/>),
/// and the server signs in as the app role, which may not change the schema at all. So <see cref="MigrateAsync"/> writes nothing: it reads
/// where each track stands and refuses the start unless every one is at this build's version. <see cref="TrackVersionsAsync"/> is the
/// database's own read, which takes no lock and writes nothing.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresServerSchema : ISchemaMigrator
{
    private readonly PostgresDatabase db;

    public PostgresServerSchema(PostgresDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    /// <summary>
    /// Migrates nothing. Each track's version, read only: every one at this build's latest returns each as unchanged; any other refuses
    /// (<see cref="SchemaNotMigratedException"/>), naming each track and what to run.
    /// </summary>
    public async Task<IReadOnlyList<TrackOutcome>> MigrateAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default)
    {
        var versions = await TrackVersionsAsync(modules, cancellationToken);
        var behind = versions.Where(v => !v.IsCurrent).ToList();
        if (behind.Count > 0)
            throw new SchemaNotMigratedException($"The PostgreSQL database at {db.Describe()} is not at this build's schema: " +
                string.Join("; ", behind.Select(v => $"track {v.Track} is at {(v.Newest is { } n ? "v" + n : "no version")}, this build needs v{v.Latest}")) +
                ". On PostgreSQL the server never migrates. Run packmoat-admin db migrate --grant-to <app role> as the owner role first. Refusing to start.");
        return versions.Select(v => new TrackOutcome(v.Track, v.Newest, v.Latest, false)).ToList();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TrackVersion>> TrackVersionsAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default) =>
        ((ISchemaMigrator)db).TrackVersionsAsync(modules, cancellationToken);
}

/// <summary>The server found the PostgreSQL schema not at its build's version, and does not migrate it. Nothing was written.</summary>
public sealed class SchemaNotMigratedException(string message) : Exception(message);
