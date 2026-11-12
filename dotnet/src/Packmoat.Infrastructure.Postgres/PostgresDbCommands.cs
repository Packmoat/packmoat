namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// <c>packmoat-admin db migrate</c> and <c>db check-migrations</c> on PostgreSQL, run as the owner role:
/// <list type="bullet">
/// <item><c>db migrate --grant-to &lt;app role&gt;</c>: the core's and the modules' migrations, then the app role's grants
/// (<see cref="PostgresRoleGrants"/>), in one transaction (<see cref="PostgresDatabase.MigrateAndGrantAsync(IReadOnlyList{MigrationTrack}, string, CancellationToken)"/>).
/// The server never migrates on PostgreSQL, so this is the only way a schema changes. <c>--single-role</c> instead migrates with no
/// grants, for an install that runs everything as one role (PACKMOAT_DB_SINGLE_ROLE=on), and says what that gives up.</item>
/// <item><c>db check-migrations</c>: this build's migrations on the database itself, in a transaction that is always rolled back
/// (<see cref="PostgresMigrationCheck"/>), reporting each track, the row counts and the schema against a fresh build's.</item>
/// </list>
/// AdminCli routes its <c>db</c> group here when PACKMOAT_DB_ENGINE=postgres; every other command there only checks the
/// schema, through the stores' own schema port, and never migrates.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresDbCommands
{
    /// <summary>The usage line, for an unknown or malformed db command.</summary>
    public const string Usage =
        "Usage: packmoat-admin db migrate --grant-to <app role>   (as the owner role: migrate, then grant the app role its rows)\n" +
        "       packmoat-admin db migrate --single-role           (as the one role, with PACKMOAT_DB_SINGLE_ROLE=on: migrate, no grants)\n" +
        "       packmoat-admin db check-migrations                (as the owner role: run this build's migrations in a transaction, compare, roll back)";

    /// <summary>
    /// Runs <paramref name="args"/> (starting "db") on <paramref name="db"/>: 0 done (or the check OK), 1 refused (or the check found
    /// differences), 2 an unknown or malformed command (nothing connected).
    /// </summary>
    public static async Task<int> RunAsync(string[] args, PostgresDatabase db, IReadOnlyList<MigrationTrack> modules, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        switch (args)
        {
            case ["db", "migrate", "--grant-to", var role] when !string.IsNullOrWhiteSpace(role) && !role.StartsWith('-'):
                return await MigrateAsync(db, modules, role, output, error, cancellationToken);
            case ["db", "migrate", "--single-role"]:
                return await MigrateAsync(db, modules, null, output, error, cancellationToken);
            case ["db", "check-migrations"]:
                return await CheckAsync(db, modules, output, cancellationToken);
            case ["db", "migrate", ..]:
                await error.WriteLineAsync("db migrate on PostgreSQL needs --grant-to <app role> (the role the server and the command line sign in as), " +
                    "or --single-role. Nothing was run.\n" + Usage);
                return 2;
            default:
                await error.WriteLineAsync("Unknown db command. Nothing was run.\n" + Usage);
                return 2;
        }
    }

    private static async Task<int> MigrateAsync(PostgresDatabase db, IReadOnlyList<MigrationTrack> modules, string? role, TextWriter output, TextWriter error, CancellationToken ct)
    {
        // The migrator's own refusals, each of which wrote nothing, are the refusal line. A database error is not caught here.
        PostgresMigration done;
        try
        {
            done = role is null
                ? new PostgresMigration(await db.MigrateSingleRoleAsync(modules, ct), null)
                : await db.MigrateAndGrantAsync(modules, role, ct);
        }
        catch (PostgresRoleException e) { return await RefusedAsync(error, e); }
        catch (SchemaTooNewException e) { return await RefusedAsync(error, e); }
        catch (SchemaTrackException e) { return await RefusedAsync(error, e); }
        catch (SchemaBaselineException e) { return await RefusedAsync(error, e); }
        catch (TimeoutException e) { return await RefusedAsync(error, e); }
        await output.WriteLineAsync($"migrated {db.Describe()}");
        foreach (var t in done.Tracks)
            await output.WriteLineAsync(t.Before == t.After ? $"track {t.Name}: unchanged at v{t.After}"
                : $"track {t.Name}: {(t.Before is { } b ? "v" + b : "none")} -> v{t.After}");
        if (done.Grants is { } g)
            await output.WriteLineAsync($"granted to {g.Role} on schema {g.Schema}: USAGE on the schema; {PostgresRoleGrants.TablePrivileges} on {g.Tables} table(s); " +
                $"{PostgresRoleGrants.VersionTablePrivileges} on {g.VersionTables} version table(s); {PostgresRoleGrants.SequencePrivileges} on {g.Sequences} sequence(s). " +
                "No TRUNCATE, no DDL, no ownership. Sign the server and the command line in as this role.");
        else
            await output.WriteLineAsync("warning: no app role was granted to (--single-role). The server will sign in as the owner, which can disable the triggers " +
                "that keep the retention floors and the purge guards. It starts only with PACKMOAT_DB_SINGLE_ROLE=on.");
        return 0;
    }

    private static async Task<int> RefusedAsync(TextWriter error, Exception e)
    {
        await error.WriteLineAsync("REFUSED: " + e.Message);
        return 1;
    }

    private static async Task<int> CheckAsync(PostgresDatabase db, IReadOnlyList<MigrationTrack> modules, TextWriter output, CancellationToken ct)
    {
        var report = await PostgresMigrationCheck.RunAsync(db, modules, ct);
        await output.WriteLineAsync($"checked {db.Describe()} in a transaction that was rolled back: nothing was written");
        foreach (var t in report.Tracks)
            await output.WriteLineAsync($"track {t.Name}: before {Version(t.Before)}, after {Version(t.After)}: {t.Verdict}");
        if (report.Refusal is not null)
        {
            await output.WriteLineAsync("REFUSED: " + report.Refusal);
            await output.WriteLineAsync("result: REFUSED. db migrate would not run on this database.");
            return 1;
        }
        if (report.CompareFailure is not null)
        {
            await output.WriteLineAsync("REFUSED: " + report.CompareFailure);
            await output.WriteLineAsync("result: NOT CHECKED. The migrations ran, but the result could not be compared with a fresh build.");
            return 1;
        }
        foreach (var r in report.Rows)
            await output.WriteLineAsync($"rows {r.Table}: {Count(r.Before)} -> {Count(r.After)}{(r.NewAndEmpty ? "  (new, empty)" : r.Equal ? "" : "  CHANGED")}");
        await output.WriteLineAsync(report.Rows.All(r => r.Equal) ? $"rows: equal in all {report.Rows.Count} tables" : $"rows: CHANGED in {report.Rows.Count(r => !r.Equal)} table(s)");
        foreach (var d in report.SchemaDifferences) await output.WriteLineAsync("schema: " + d);
        await output.WriteLineAsync(report.SchemaDifferences.Count == 0 ? "schema: equal to a fresh build" : $"schema: {report.SchemaDifferences.Count} difference(s) from a fresh build");
        await output.WriteLineAsync(report.Ok ? "result: OK" : "result: DIFFERENCES (see above). Do not deploy until they are understood.");
        return report.Ok ? 0 : 1;

        static string Version(int? v) => v is null ? "none" : $"v{v}";
        static string Count(long? n) => n is null ? "none" : n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
