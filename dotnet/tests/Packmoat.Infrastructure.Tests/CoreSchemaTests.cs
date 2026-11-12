using Microsoft.Data.Sqlite;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The core's track on every engine: <see cref="CoreSchema.Track"/> is SQLite's frozen statements (unmoved) and PostgreSQL's
/// baseline, with the same versions. The schema comparison itself needs a server: PostgresCoreBaselineTests; the rules' behaviour is
/// SchemaRulesContract's. PostgreSQL stays refused until P5 (DatabaseEngineTests pins it).
/// </summary>
[Collection("sqlite-pools")]
public class CoreSchemaTests
{
    // Test 5. mutation: an engine added to DatabaseEngine with no core statements; the Postgres dialect dropped from CoreSchema
    [Fact]
    public void The_core_track_has_every_engines_statements()
    {
        Assert.Equal(Enum.GetValues<DatabaseEngine>(), CoreSchema.Track.Engines);
        Assert.Same(CoreSchema.Track, SqliteDatabase.Core);
        Assert.Same(SqliteDatabase.Migrations, CoreSchema.Track.StatementsFor(DatabaseEngine.Sqlite));   // the SQLite text is not moved or copied
        Assert.Same(PostgresCoreMigrations.Statements, CoreSchema.Track.StatementsFor(DatabaseEngine.Postgres));
        Assert.Equal(MigrationTrack.CoreName, CoreSchema.Track.Name);
        Assert.Null(CoreSchema.Track.Baseline);
    }

    // mutation: a SQLite v27 with no PostgreSQL v27 (the track then cannot be made: CoreSchema's type initialiser throws)
    [Fact]
    public void A_core_version_on_one_engine_only_cannot_make_the_track()
    {
        string[][] sqlite27 = [.. SqliteDatabase.Migrations, ["CREATE TABLE later(x TEXT)"]];
        var ex = Assert.Throws<ArgumentException>(() => new MigrationTrack(MigrationTrack.CoreName, new Dictionary<DatabaseEngine, string[][]>
        {
            [DatabaseEngine.Sqlite] = sqlite27,
            [DatabaseEngine.Postgres] = PostgresCoreMigrations.Statements,
        }));
        Assert.Contains("sqlite 27, postgres 26", ex.Message);
    }

    [Fact]   // mutation: a statement in one of 1 to 22; the baseline moved to another version
    public void On_postgres_versions_1_to_22_are_empty_and_23_is_the_whole_schema()
    {
        var pg = PostgresCoreMigrations.Statements;
        Assert.Equal(SqliteDatabase.LatestVersion, pg.Length);
        Assert.Equal(23, PostgresCoreMigrations.BaselineVersion);
        Assert.All(pg[..22], Assert.Empty);
        Assert.NotEmpty(pg[22]);
        // One statement per element, as SQLite's: no statement ends in a semicolon with more after it. A function's $$ body is one
        // string to PostgreSQL, so its semicolons are not statement ends.
        Assert.All(pg[22], s => Assert.DoesNotContain(";", WithoutDollarBodies(s).TrimEnd().TrimEnd(';'), StringComparison.Ordinal));
        Assert.Equal(20, pg[22].Count(s => s.TrimStart().StartsWith("CREATE TABLE", StringComparison.Ordinal)));
        // After the baseline, SQLite's versions one for one (24 is rate_counters and its index).
        Assert.Equal(2, pg[23].Length);
        Assert.StartsWith("CREATE TABLE rate_counters(", pg[23][0]);
        Assert.Equal("CREATE INDEX ix_rate_counters_ends_at ON rate_counters(ends_at)", pg[23][1]);
        // 25 is form_nonces and its two indexes.
        Assert.Equal(3, pg[24].Length);
        Assert.StartsWith("CREATE TABLE form_nonces(", pg[24][0]);
        Assert.Equal("CREATE INDEX ix_form_nonces_subject ON form_nonces(scope, subject, expires_at)", pg[24][1]);
        Assert.Equal("CREATE INDEX ix_form_nonces_expires_at ON form_nonces(expires_at)", pg[24][2]);
        // 26 is feed_setup_scratch and its index.
        Assert.Equal(2, pg[25].Length);
        Assert.StartsWith("CREATE TABLE feed_setup_scratch(", pg[25][0]);
        Assert.Equal("CREATE INDEX ix_feed_setup_scratch_expires_at ON feed_setup_scratch(expires_at)", pg[25][1]);
        Assert.Equal(SchemaTriggerTests.Current.Length + SchemaShape.PostgresOnlyTriggers.Length,
            pg[22].Count(s => s.TrimStart().StartsWith("CREATE TRIGGER", StringComparison.Ordinal)));
    }

    /// <summary><paramref name="sql"/> with every $$…$$ body (a dollar-quoted string) taken out.</summary>
    internal static string WithoutDollarBodies(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"\$\$.*?\$\$", "$$$$",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]   // the check above must still see a second statement outside a body (mutation: the whole text stripped)
    public void A_dollar_body_is_stripped_and_nothing_else()
    {
        Assert.Equal("CREATE FUNCTION f() AS $$ LANGUAGE plpgsql", WithoutDollarBodies("CREATE FUNCTION f() AS $$BEGIN x; y; END$$ LANGUAGE plpgsql"));
        Assert.Contains(";", WithoutDollarBodies("CREATE FUNCTION f() AS $$BEGIN x; END$$; DROP TABLE t"), StringComparison.Ordinal);
    }

    // mutation: a time column added to SQLite and left off the pinned list (PostgreSQL would then make it text)
    [Fact]
    public async Task Every_column_named_like_a_time_is_on_the_pinned_lists()
    {
        var lines = await SqliteShapeAsync();
        var columns = lines.Where(l => l.Contains('#', StringComparison.Ordinal))
            .Select(l => (Table: l[..l.IndexOf('#')], Parts: l[(l.IndexOf(' ') + 1)..].Split(' ')))
            .Select(c => (Name: $"{c.Table}.{c.Parts[0]}", Type: c.Parts[1])).ToList();
        string[] timeLike = [.. columns.Where(c => c.Name.EndsWith("_at", StringComparison.Ordinal) || c.Name.EndsWith(".time", StringComparison.Ordinal)
            || c.Name.EndsWith(".created", StringComparison.Ordinal) || c.Name.EndsWith(".day", StringComparison.Ordinal)).Select(c => c.Name)];

        Assert.Equal(timeLike.Order(StringComparer.Ordinal), SchemaShape.TimeColumns.Concat(SchemaShape.DateColumns).Order(StringComparer.Ordinal));
        Assert.Equal(SchemaShape.TimeColumns.Order(StringComparer.Ordinal), SchemaShape.TimeColumns);
        Assert.All(SchemaShape.TimeColumns, n => Assert.Equal("timestamptz", columns.Single(c => c.Name == n).Type));
        Assert.All(SchemaShape.DateColumns, n => Assert.Equal("date", columns.Single(c => c.Name == n).Type));
    }

    [Fact]   // the SQLite reader sees what the comparison relies on (so an empty or wrong read cannot pass it)
    public async Task The_sqlite_shape_reads_every_kind_of_fact()
    {
        var lines = await SqliteShapeAsync();
        Assert.Equal(23, lines.Count(l => l.StartsWith("table ", StringComparison.Ordinal)));   // 20 at the baseline, rate_counters since 24, form_nonces since 25, feed_setup_scratch since 26
        Assert.Contains("table tenants", lines);
        Assert.DoesNotContain("table schema_migrations", lines);
        Assert.Contains("tenants#01 id text NOT NULL pk1", lines);
        Assert.Contains("audit_events#01 id bigint NOT NULL pk1 identity always start 1 increment 1", lines);
        Assert.Contains("tokens check-literals=['automation', 'developer', 'operator', 'self']", lines);
        Assert.Contains("members check-literals=[36, 36]", lines);
        Assert.Contains("auto_join_settings check-literals=[0, 1]", lines);
        Assert.Contains("tokens fks deferrable=0 initially-deferred=0", lines);
        Assert.Contains("tokens collations=[]", lines);
        Assert.Contains("feed_checks#12 confirmed_admins text NOT NULL default='[]'", lines);
        Assert.Contains("tokens unique(token_hash)", lines);
        Assert.Contains("shared_app_verifications fk(feed_check_id) -> feed_checks(id) on update NO ACTION on delete NO ACTION", lines);
        Assert.Contains("upstream_identities checks=2", lines);
        Assert.Contains("index ux_shared_directory on upstream_identities(lowerazure_tenant_id) unique where kind='shared'", lines);
        Assert.Contains("index ix_tokens_owner on tokens(tenant_id, owner_tid, owner_oid)", lines);
        var triggers = lines.Where(l => l.StartsWith("trigger ", StringComparison.Ordinal)).ToList();
        Assert.Equal(SchemaTriggerTests.Current, triggers.Select(SchemaShape.TriggerName));
        Assert.Contains("trigger tenants_no_rename_to_purged_id on tenants before update of id row", triggers);
        Assert.Contains("trigger audit_purged_tenant on audit_events before insert row", triggers);
        Assert.Contains("trigger audit_no_delete on audit_events before delete row", triggers);
    }

    // The trigger line from either engine's catalogue. mutations: tgtype's bits read wrongly; tgenabled ignored
    [Theory]
    [InlineData("CREATE TRIGGER t1 BEFORE UPDATE OF id ON x WHEN 1 BEGIN SELECT 1; END;", "trigger t1 on x before update of id row")]
    [InlineData("CREATE TRIGGER t1 DELETE ON x BEGIN SELECT 1; END;", "trigger t1 on x before delete row")]
    [InlineData("CREATE TRIGGER IF NOT EXISTS t1 AFTER UPDATE OF \"A\", b ON x BEGIN SELECT 1; END;", "trigger t1 on x after update of a, b row")]
    [InlineData("CREATE TRIGGER t1 INSTEAD  OF INSERT ON x BEGIN SELECT 1; END;", "trigger t1 on x instead of insert row")]
    public void A_sqlite_trigger_is_read_into_its_shape(string sql, string line) => Assert.Equal(line, SchemaShape.SqliteTrigger("t1", "x", sql));

    [Theory]
    [InlineData(1 | 2 | 16, "id", 'O', "trigger t1 on x before update of id row")]
    [InlineData(2 | 32, null, 'O', "trigger t1 on x before truncate statement")]
    [InlineData(1 | 4 | 8, null, 'O', "trigger t1 on x after insert or delete row")]
    [InlineData(1 | 2 | 8, null, 'D', "trigger t1 on x before delete row disabled")]
    [InlineData(1 | 2 | 8, null, 'R', "trigger t1 on x before delete row replica")]
    [InlineData(1 | 64 | 4, null, 'A', "trigger t1 on x instead of insert row always")]
    public void A_postgres_trigger_is_read_into_its_shape(int type, string? columns, char enabled, string line) =>
        Assert.Equal(line, SchemaShape.PostgresTrigger("t1", "x", type, columns, enabled));

    [Theory]
    [InlineData("CREATE INDEX ix ON t(a, b);", "a|b", null)]
    [InlineData("CREATE UNIQUE INDEX ux ON t(lower(a)) WHERE kind = 'shared';", "lower(a)|", "kind = 'shared'")]
    [InlineData("CREATE INDEX ix ON \"t\" (substr(a, 1, 2), b)", "substr(a, 1, 2)|b", null)]
    public void An_index_statement_is_read_into_its_columns_and_predicate(string sql, string columns, string? predicate)
    {
        var (read, where) = SchemaShape.ParseIndex(sql, "t");
        Assert.Equal(columns.Split('|').Where(c => c.Length > 0), read);
        Assert.Equal(predicate, where);
    }

    // F2 (review): a literal's case is part of its meaning; an identifier's is not.
    [Theory]
    [InlineData("kind = 'shared'::text", "kind='shared'")]
    [InlineData("(KIND = 'SHARED')", "kind='SHARED'")]
    [InlineData("lower(\"Azure_Tenant_Id\")", "lowerazure_tenant_id")]
    [InlineData("x = 'it''s A b'", "x='it''s A b'")]
    public void Normalise_lowercases_identifiers_and_keeps_literals(string expression, string normalised) =>
        Assert.Equal(normalised, SchemaShape.Normalise(expression));

    // F1 (review): what a CHECK tests against, as PostgreSQL rewrites it and as SQLite keeps it
    [Fact]
    public void A_checks_literals_read_the_same_from_either_engines_text()
    {
        var sqlite = SchemaShape.CheckBodies("CREATE TABLE t(kind TEXT CHECK(kind IN ('developer','automation')), n INTEGER CHECK (length(n) = 36), q TEXT CHECK(q <> ')'))");
        Assert.Equal(["kind IN ('developer','automation')", "length(n) = 36", "q <> ')'"], sqlite);
        Assert.Equal(["'automation'", "'developer'"], SchemaShape.Literals("CHECK ((kind = ANY (ARRAY['developer'::text, 'automation'::text])))").Order(StringComparer.Ordinal));
        Assert.Equal(["0", "1"], SchemaShape.Literals("CHECK ((enabled = ANY (ARRAY[(0)::bigint, (1)::bigint])))"));
        Assert.Equal(["36"], SchemaShape.Literals("CHECK ((length(azure_tenant_id) = 36))"));
        Assert.Empty(SchemaShape.Literals("CHECK ((v6x = col2))"));
    }

    /// <summary>The core's schema on a fresh SQLite database, as <see cref="SchemaShape"/> lines. Only this database's pool is cleared.</summary>
    internal static async Task<List<string>> SqliteShapeAsync()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-shape-").FullName;
        var db = new SqliteDatabase(Path.Combine(dir, "packmoat.db"));
        try
        {
            await db.MigrateAsync([]);
            await using var c = await db.OpenAsync();
            return await SchemaShape.ReadAsync(c);
        }
        finally
        {
            db.ReleaseConnections();
            TempDir.Delete(dir);
        }
    }
}
