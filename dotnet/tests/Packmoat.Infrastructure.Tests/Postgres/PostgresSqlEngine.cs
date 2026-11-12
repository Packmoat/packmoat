using System.Data.Common;
using System.Globalization;
using Npgsql;
using Packmoat.Application;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL as the contracts that run on it before its stores exist see it: a fresh database at the core's current schema
/// (made through <see cref="ISchemaMigrator"/>, so with <see cref="CoreSchema.Track"/>, then any <see cref="Modules"/>), raw SQL, and the master-key check. Each database
/// is its own (<see cref="PostgresScratchDatabase"/>), cloned from one migrated template, dropped when its handle is disposed.
/// <para>What it reads back reads as SQLite's does: a timestamptz as the "O" text the SQLite writers store (UTC, offset +00:00,
/// whatever the session's time zone), a date as yyyy-MM-dd, an integer as long. A refusal is a constraint's (SQLSTATE class 23) or a
/// rule's (P0001) and nothing else: a malformed value or a syntax error is a broken test, not a refusal.</para>
/// </summary>
public class PostgresSqlEngine : IMasterKeyEngine
{
    public static PostgresSqlEngine Instance { get; } = new();

    /// <summary>The same engine, signed in as the app role: see <see cref="AsAppRole"/>.</summary>
    public static PostgresSqlEngine AppRoleInstance { get; } = new(asAppRole: true);

    protected PostgresSqlEngine(bool asAppRole = false) => AsAppRole = asAppRole;

    /// <summary>
    /// Whether each database is the owner's and the stores sign in as the app role, as production runs: the template is made
    /// by an owner role (no superuser) with <c>db migrate --grant-to</c> an app role, and every store, and every raw statement of a test
    /// but DDL, runs as that app role. A test's own DDL (CREATE, ALTER, DROP: a probe's trigger, a module's table) is the owner's, as a
    /// migration's would be, and the grants are applied again after it, as a migration applies them (<see cref="ExecAsync"/>). Otherwise
    /// the test server's own login (a superuser) owns and signs in to everything.
    /// </summary>
    public bool AsAppRole { get; }

    /// <summary>
    /// The module tracks a database is migrated with after the core's: none here. An engine over a module's tables names
    /// its own, and its databases are copied from a template of their own.
    /// </summary>
    protected virtual IReadOnlyList<MigrationTrack> Modules => [];

    public async Task<IEngineDb> CreateAsync()
    {
        var (scratch, roles) = await PostgresScratchDatabase.FromTemplateAsync(Modules, AsAppRole);
        return roles is null ? new PostgresTestDb(scratch, scratch.Database())
            : new PostgresTestDb(scratch, roles.Database(scratch, roles.App), roles.Database(scratch, roles.Owner), roles.App);
    }

    /// <summary>The one place a contract's handle is taken back to the PostgreSQL database it is. A handle from another engine is refused.</summary>
    protected static PostgresTestDb Pg(IEngineDb t) => t as PostgresTestDb ?? throw new ArgumentException($"Not a PostgreSQL test database: {t.GetType().Name}.", nameof(t));

    /// <summary>Runs <paramref name="sql"/>; on an app-role database, DDL runs as the owner, followed by the grants (see <see cref="AsAppRole"/>).</summary>
    public async Task ExecAsync(IEngineDb t, string sql)
    {
        var pg = Pg(t);
        if (pg.Owner is { } owner && IsDdl(sql))
        {
            await using var o = await owner.OpenAsync();
            await using var tx = await o.BeginTransactionAsync();
            await using (var ddl = new NpgsqlCommand(sql, o, tx)) await ddl.ExecuteNonQueryAsync();
            await using (var schema = new NpgsqlCommand("SELECT current_schema()", o, tx))
                await PostgresRoleGrants.ApplyAsync(o, tx, (string)(await schema.ExecuteScalarAsync())!, pg.AppRole!);
            await tx.CommitAsync();
            return;
        }
        await using var c = await pg.Db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The first column of the first row of <paramref name="sql"/>, read as the test server's own login (a superuser), whichever role the
    /// stores sign in as: for a test's own bookkeeping that is not the stores' (a probe's sequence, which the app role may only use).
    /// </summary>
    public static async Task<object?> TestServerScalarAsync(IEngineDb t, string sql)
    {
        await using var c = await Pg(t).OpenAsTestServerAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Render(await cmd.ExecuteScalarAsync());
    }

    /// <summary>A statement that changes the schema or its privileges: the owner's, never the app role's.</summary>
    internal static bool IsDdl(string sql) =>
        System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*(CREATE|ALTER|DROP|COMMENT|GRANT|REVOKE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public async Task<object?> ScalarAsync(IEngineDb t, string sql)
    {
        await using var c = await Pg(t).Db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Render(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// A value as the SQLite engine gives it back: an integer as long; a timestamptz (Npgsql: a UTC <see cref="DateTime"/>) as "O" text
    /// with offset +00:00, as the SQLite writers store a time; a date as yyyy-MM-dd. Anything else as it is.
    /// </summary>
    internal static object? Render(object? value) => value switch
    {
        int i => (long)i,
        short s => (long)s,
        DateTime d => PostgresTime.FromDatabase(d).ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        var v => v,
    };

    public async Task<List<string>> ColumnAsync(IEngineDb t, string sql)
    {
        await using var c = await Pg(t).Db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.IsDBNull(0) ? "NULL" : Convert.ToString(Render(r.GetValue(0)), CultureInfo.InvariantCulture)!);
        return rows;
    }

    public async Task<DbException> RefusedAsync(Func<Task> write)
    {
        var refusal = await Assert.ThrowsAsync<PostgresException>(write);
        Assert.True(IsRefusal(refusal.SqlState),
            $"PostgreSQL failed the write with SQLSTATE {refusal.SqlState} ({refusal.MessageText}), which is not a refusal: only a constraint's (class 23) or a rule's (P0001) is.");
        return refusal;
    }

    /// <summary>Whether <paramref name="sqlState"/> is a refusal: integrity_constraint_violation's class (23…) or raise_exception (P0001).</summary>
    internal static bool IsRefusal(string sqlState) =>
        sqlState.Length == 5 && sqlState.StartsWith("23", StringComparison.Ordinal) || sqlState == PostgresErrorCodes.RaiseException;

    /// <summary>Upper-case, as SQLite's hex() is, so a row reads the same on both.</summary>
    public string HexSql(string column) => $"upper(encode({column}, 'hex'))";

    public async Task<List<string>> TablesAsync(IEngineDb t) =>
        (await ColumnAsync(t, "SELECT relname::text FROM pg_catalog.pg_class WHERE relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = current_schema()) " +
            "AND relkind IN ('r', 'p')")).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// From the server's own clock (statement_timestamp(), as the triggers read it), in seconds, not days: the test machine's clock and the
    /// server's (a container in a VM) can drift apart, and a day-based interval follows the session's time zone.
    /// </summary>
    public string TimeAgoSql(TimeSpan ago) =>
        $"(statement_timestamp() - interval '{ago.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)} seconds')";

    public string BlobSql(string hex) => $"'\\x{hex}'::bytea";

    /// <summary>check_violation, of the constraint named <paramref name="name"/>.</summary>
    public bool IsCheckViolation(DbException refusal, string name, string check) =>
        refusal is PostgresException { SqlState: PostgresErrorCodes.CheckViolation } e && e.ConstraintName == name;

    public IMasterKeyCheck MasterKeyCheck(IEngineDb t, AesGcmSecretProtector key) => new PostgresMasterKeyCheck(Pg(t).Db, key);

    /// <summary>
    /// A plpgsql trigger function. Its own insert fires the trigger again, and the value test stops it there. The rival row is in the table
    /// before the start's own row is checked, so the start's ON CONFLICT DO NOTHING finds the conflict and skips its insert.
    /// </summary>
    public async Task ArrangeRivalMarkerAsync(IEngineDb t, byte[] rivalMarker)
    {
        var rival = BlobSql(Convert.ToHexString(rivalMarker));
        await ExecAsync(t, $"""
            CREATE FUNCTION test_rival_marker() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                IF NEW.key = 'key_check' AND NEW.value <> {rival} AND NOT EXISTS (SELECT 1 FROM meta WHERE key = 'key_check') THEN
                    INSERT INTO meta(key, value) VALUES('key_check', {rival});
                END IF;
                RETURN NEW;
            END
            $fn$
            """);
        await ExecAsync(t, "CREATE TRIGGER test_rival_marker BEFORE INSERT ON meta FOR EACH ROW EXECUTE FUNCTION test_rival_marker()");
    }
}

/// <summary>PostgreSQL's <see cref="IEngineDb"/>: its own database, at the core's current schema. Disposing it drops the database.</summary>
public sealed class PostgresTestDb : IEngineDb
{
    private readonly PostgresScratchDatabase? scratch;

    internal PostgresTestDb(PostgresScratchDatabase scratch, PostgresDatabase db, PostgresDatabase? owner = null, string? appRole = null)
    {
        this.scratch = scratch;
        Db = db;
        Owner = owner;
        AppRole = appRole;
    }

    /// <summary>On an app-role database: the database signed in as its owner, for a test's DDL. Otherwise null.</summary>
    public PostgresDatabase? Owner { get; }

    /// <summary>On an app-role database: the app role <see cref="Db"/> signs in as. Otherwise null.</summary>
    public string? AppRole { get; }

    /// <summary>
    /// A connection to this database as the test server's own login (a superuser), unpooled: for a session a test stands up beside the
    /// stores (another process holding a row, say) that sets what only a superuser may (deadlock_timeout).
    /// </summary>
    public Task<NpgsqlConnection> OpenAsTestServerAsync() =>
        scratch?.OpenAsync() ?? throw new InvalidOperationException("This handle has no database of its own on the test server.");

    /// <summary>
    /// A handle over <paramref name="db"/> with no scratch database behind it: for a test that never connects (the pending stores' throws,
    /// which need no server). Disposing it disposes <paramref name="db"/> only.
    /// </summary>
    public PostgresTestDb(PostgresDatabase db) => Db = db;

    public FakeClock Clock { get; } = new();
    public AesGcmSecretProtector Protector { get; } = AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());
    public PostgresDatabase Db { get; }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        if (Owner is not null) await Owner.DisposeAsync();
        if (scratch is not null) await scratch.DisposeAsync();
    }
}
