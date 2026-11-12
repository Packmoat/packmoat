using System.Globalization;
using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The P3 plumbing with no server: times (<see cref="PostgresTime"/>), the named locks (<see cref="PostgresLocks"/>), and
/// how the test engine reads values back and tells a refusal (<see cref="PostgresSqlEngine"/>).
/// </summary>
public class PostgresPlumbingTests
{
    // mutation: rounding instead of cutting (…0019 would become …002); the UTC conversion dropped
    [Fact]
    public void A_time_goes_in_as_utc_cut_to_whole_microseconds()
    {
        var local = new DateTimeOffset(2026, 3, 29, 2, 30, 0, TimeSpan.FromHours(2)).AddTicks(19);   // 1.9 µs past the second
        var stored = PostgresTime.ToDatabase(local);
        Assert.Equal(TimeSpan.Zero, stored.Offset);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero).AddTicks(10), stored);
        Assert.Equal(0, stored.Ticks % 10);
        Assert.True(stored <= local);
        Assert.Equal(stored, PostgresTime.ToDatabase(stored));
        Assert.Null(PostgresTime.ToDatabase((DateTimeOffset?)null));
        Assert.Equal(stored, PostgresTime.ToDatabase((DateTimeOffset?)local));
    }

    // The two edges, decided (PostgresTime.ToDatabase). mutation: MinValue let through (Npgsql writes it as -infinity)
    [Fact]
    public void The_earliest_time_is_refused_and_the_latest_is_stored_as_a_finite_time()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresTime.ToDatabase(DateTimeOffset.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresTime.ToDatabase(DateTimeOffset.MinValue.AddTicks(9)));   // cut to MinValue
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresTime.ToDatabase((DateTimeOffset?)DateTimeOffset.MinValue));
        Assert.Equal(new DateTimeOffset(10, TimeSpan.Zero), PostgresTime.ToDatabase(DateTimeOffset.MinValue.AddTicks(10)));   // 0001-01-01T00:00:00.000001Z

        var latest = PostgresTime.ToDatabase(DateTimeOffset.MaxValue);
        Assert.Equal(DateTimeOffset.MaxValue.AddTicks(-9), latest);   // 9999-12-31T23:59:59.999999Z
        Assert.NotEqual(DateTime.MaxValue, latest.UtcDateTime);       // so not the value Npgsql writes as infinity
        Assert.Equal(latest, PostgresTime.ToDatabase(latest));
    }

    // The upstream identity's key is encrypted with its tenant's context: the two engines' stores must give the same one,
    // or a database copied from one engine to the other would hold keys that no longer decrypt.
    [Theory]
    [InlineData("acme")]
    [InlineData("org-k3j7x2m6q4w7z4ab")]
    [InlineData("shared-app:active")]
    [InlineData("a:b")]
    public void A_tenants_key_context_is_the_same_on_both_engines(string tenantId)
    {
        if (tenantId.Contains(':'))
        {
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => PostgresTenantStore.TenantContext(tenantId));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => SqliteTenantStore.TenantContext(tenantId));
        }
        else
        {
            Assert.Equal(SqliteTenantStore.TenantContext(tenantId), PostgresTenantStore.TenantContext(tenantId));
        }
    }

    // A cookie key is encrypted under its row's context: the two engines' stores must give the same one, as for the
    // tenant's key above. mutation: a different prefix, or the friendly name in place of the id, in PostgresCookieKeyStore.Context
    [Theory]
    [InlineData("11111111-2222-4333-8444-555555555555")]
    [InlineData("")]
    public void A_cookie_keys_context_is_the_same_on_both_engines(string id) =>
        Assert.Equal(SqliteCookieKeyStore.Context(id), PostgresCookieKeyStore.Context(id));

    [Fact]
    public void A_time_comes_back_with_offset_0_and_only_from_utc()
    {
        var utc = new DateTime(2026, 11, 8, 9, 25, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTimeOffset(utc), PostgresTime.FromDatabase(utc));
        Assert.Equal(TimeSpan.Zero, PostgresTime.FromDatabase(utc).Offset);
        Assert.Throws<ArgumentException>(() => PostgresTime.FromDatabase(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
        Assert.Throws<ArgumentException>(() => PostgresTime.FromDatabase(DateTime.SpecifyKind(utc, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => PostgresTime.FromDatabase(new DateTimeOffset(utc.Ticks, TimeSpan.FromHours(13.75))));
        Assert.Equal(new DateTimeOffset(utc), PostgresTime.FromDatabase(new DateTimeOffset(utc)));
    }

    [Fact]
    public void The_named_locks_have_keys_of_their_own()
    {
        Assert.Equal("pmlk", System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(PostgresLocks.NamedLockKey).Reverse().ToArray()));
        Assert.NotEqual(PostgresCoreMigrations.TenantIdLockKey, PostgresLocks.NamedLockKey);
        var numbers = Enum.GetValues<PostgresLock>().Select(l => (int)l).ToList();
        Assert.Equal(numbers.Distinct(), numbers);
        Assert.Equal([1, 2, 3, 4], numbers);   // pinned: a number is never reused for another lock (4: the form nonces)
        Assert.Equal($"SELECT pg_catalog.pg_advisory_xact_lock({0x706D6C6B}, 2)", PostgresLocks.TakeSql(PostgresLock.CookieKeys));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresLocks.TakeSql((PostgresLock)99));
    }

    // mutation: any PostgresException accepted as a refusal
    [Theory]
    [InlineData("23505", true)]    // unique_violation
    [InlineData("23503", true)]    // foreign_key_violation
    [InlineData("23502", true)]    // not_null_violation
    [InlineData("23514", true)]    // check_violation
    [InlineData("P0001", true)]    // raise_exception: a rule's refusal
    [InlineData("22007", false)]   // invalid_datetime_format: 'x' as a time
    [InlineData("22P02", false)]   // invalid_text_representation: 'on' as a bigint
    [InlineData("42601", false)]   // syntax_error
    [InlineData("42P01", false)]   // undefined_table
    [InlineData("40P01", false)]   // deadlock_detected
    [InlineData("230", false)]
    [InlineData("P0002", false)]
    public void A_refusal_is_class_23_or_p0001_only(string sqlState, bool refusal) => Assert.Equal(refusal, PostgresSqlEngine.IsRefusal(sqlState));

    [Fact]
    public async Task The_refusal_check_fails_on_a_server_error_that_is_not_a_refusal()
    {
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => PostgresSqlEngine.Instance.RefusedAsync(
            () => throw new PostgresException("invalid input syntax for type timestamp with time zone: \"x\"", "ERROR", "ERROR", "22007")));
        var unique = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
        Assert.Same(unique, await PostgresSqlEngine.Instance.RefusedAsync(() => throw unique));
    }

    // The contracts compare what they read back as text with SQLite's: a time as the "O" text the SQLite writers store.
    [Fact]
    public void A_time_reads_back_as_sqlites_O_text_and_a_date_as_yyyy_mm_dd()
    {
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);
        Assert.Equal(instant.ToString("O", CultureInfo.InvariantCulture), PostgresSqlEngine.Render(instant.UtcDateTime));
        Assert.Equal("2026-01-02T03:04:05.1234560+00:00", PostgresSqlEngine.Render(instant.UtcDateTime));
        Assert.Equal("1900-01-01", PostgresSqlEngine.Render(new DateOnly(1900, 1, 1)));
        Assert.Equal(7L, PostgresSqlEngine.Render(7));
        Assert.Equal(7L, PostgresSqlEngine.Render((short)7));
        Assert.Equal(7L, PostgresSqlEngine.Render(7L));
        Assert.Equal("x", PostgresSqlEngine.Render("x"));
        Assert.Null(PostgresSqlEngine.Render(null));
    }
}

/// <summary>The P3 plumbing against the test server.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresPlumbingServerTests
{
    private static PostgresSqlEngine Engine => PostgresSqlEngine.Instance;

    // mutation: any PostgresException accepted as a refusal
    [PostgresFact]
    public async Task Only_a_constraints_or_a_rules_failure_is_a_refusal()
    {
        await using var t = await Engine.CreateAsync();
        await ExecAsync(t, "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) " +
            "VALUES('acme', 'Acme', 'u', 'p', 'f', 14, '2026-01-01T00:00:00+00:00')");

        Assert.Equal("23505", ((PostgresException)await Engine.RefusedAsync(() => ExecAsync(t,
            "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) VALUES('acme', 'A', 'u', 'p', 'f', 1, '2026-01-01T00:00:00+00:00')"))).SqlState);
        Assert.Equal("23514", ((PostgresException)await Engine.RefusedAsync(() => ExecAsync(t,
            "INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('acme', 2, '2026-01-01T00:00:00+00:00', 'y')"))).SqlState);
        Assert.Equal("P0001", ((PostgresException)await Engine.RefusedAsync(() => ExecAsync(t, "TRUNCATE audit_events"))).SqlState);

        foreach (var broken in new[]
        {
            "INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('acme', 1, 'x', 'y')",     // 22007
            "INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('acme', 'on', '2026-01-01T00:00:00+00:00', 'y')",   // 22P02
            "INSERT INTO no_such_table VALUES(1)",                                                                          // 42P01
            "INSRT INTO tenants VALUES(1)",                                                                                 // 42601
        })
            await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Engine.RefusedAsync(() => ExecAsync(t, broken)));
    }

    private static Task ExecAsync(IEngineDb t, string sql) => Engine.ExecAsync(t, sql);

    // The time zone the server's session runs in changes nothing read back. Every test session runs in
    // Pacific/Chatham (PostgresTestServer.SessionTimeZone): the reads in the zone's own terms below differ from UTC's, so
    // they prove the zone is really in force, on the engine's connections and the stores' pool alike.
    // mutation (F1): the TimeZone option removed from PostgresTestServer.ConnectionString (the session runs in UTC)
    [PostgresFact]
    public async Task A_time_reads_back_as_the_same_O_text_whatever_the_sessions_time_zone()
    {
        await using var t = await Engine.CreateAsync();
        var pg = (PostgresTestDb)t;
        var given = new DateTimeOffset(2026, 4, 5, 3, 30, 0, TimeSpan.FromHours(13.75)).AddTicks(19);   // Chatham, daylight time ending
        await using (var c = await pg.Db.OpenAsync())
        {
            await using (var zone = new NpgsqlCommand("SELECT current_setting('TimeZone')", c)) Assert.Equal(PostgresTestServer.SessionTimeZone, await zone.ExecuteScalarAsync());
            await using var insert = new NpgsqlCommand("INSERT INTO job_leases(job, holder, expires_at) VALUES('j', 'h', @at)", c);
            insert.Parameters.AddWithValue("at", PostgresTime.ToDatabase(given));
            await insert.ExecuteNonQueryAsync();
            await using var read = new NpgsqlCommand("SELECT expires_at FROM job_leases", c);
            Assert.Equal(PostgresTime.ToDatabase(given), PostgresTime.FromDatabase((DateTime)(await read.ExecuteScalarAsync())!));
        }

        // In the zone's terms: the text form, the day, and a time written with no offset all follow the session's zone (in UTC they would
        // read 2026-04-04 13:45:00.000001+00, 2026-04-04 00:00:00+00 and 2026-01-01T00:00:00Z).
        Assert.Equal(PostgresTestServer.SessionTimeZone, await Engine.ScalarAsync(t, "SELECT current_setting('TimeZone')"));
        Assert.Equal("2026-04-05 03:30:00.000001+13:45", await Engine.ScalarAsync(t, "SELECT expires_at::text FROM job_leases"));
        Assert.Equal("2026-04-05 00:00:00+13:45", await Engine.ScalarAsync(t, "SELECT date_trunc('day', expires_at)::text FROM job_leases"));
        Assert.Equal("2025-12-31T10:15:00.0000000+00:00", await Engine.ScalarAsync(t, "SELECT '2026-01-01 00:00:00'::timestamptz"));

        var expected = PostgresTime.ToDatabase(given).ToString("O", CultureInfo.InvariantCulture);
        Assert.Equal("2026-04-04T13:45:00.0000010+00:00", expected);
        Assert.Equal(expected, await Engine.ScalarAsync(t, "SELECT expires_at FROM job_leases"));
        Assert.Equal([expected], await Engine.ColumnAsync(t, "SELECT expires_at FROM job_leases"));
        await Engine.ExecAsync(t, "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) " +
            "VALUES('acme', 'Acme', 'u', 'p', 'f', 14, '2026-01-01T00:00:00+00:00')");
        await Engine.ExecAsync(t, "INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at) VALUES('t1', 'acme', 'h', 'p', 'l', '2026-01-01T00:00:00+00:00')");
        await Engine.ExecAsync(t, "INSERT INTO token_activity(token_id, day) VALUES('t1', '1900-01-01')");
        Assert.Equal("1900-01-01", await Engine.ScalarAsync(t, "SELECT day FROM token_activity"));
        Assert.Equal(["1900-01-01"], await Engine.ColumnAsync(t, "SELECT day FROM token_activity"));
    }

    // The earliest time PostgresTime takes and the latest are stored as finite times and read back as the same instants.
    [PostgresFact]
    public async Task The_earliest_and_the_latest_time_round_trip_through_the_server()
    {
        await using var t = await Engine.CreateAsync();
        await using var c = await ((PostgresTestDb)t).Db.OpenAsync();
        foreach (var (job, time) in new[] { ("earliest", DateTimeOffset.MinValue.AddTicks(10)), ("latest", DateTimeOffset.MaxValue) })
        {
            await using (var insert = new NpgsqlCommand("INSERT INTO job_leases(job, holder, expires_at) VALUES(@j, 'h', @at)", c))
            {
                insert.Parameters.AddWithValue("j", job);
                insert.Parameters.AddWithValue("at", PostgresTime.ToDatabase(time));
                await insert.ExecuteNonQueryAsync();
            }
            await using var read = new NpgsqlCommand("SELECT expires_at, isfinite(expires_at) FROM job_leases WHERE job = @j", c);
            read.Parameters.AddWithValue("j", job);
            await using var r = await read.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(PostgresTime.ToDatabase(time), PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(0)));
            Assert.True(r.GetBoolean(1), $"{job} was stored as an infinity");
        }
        Assert.Equal(["0001-01-01T00:00:00.0000010+00:00", "9999-12-31T23:59:59.9999990+00:00"],
            await Engine.ColumnAsync(t, "SELECT expires_at FROM job_leases ORDER BY expires_at"));
    }

    // A named lock taken in one transaction is held against every other until that transaction ends.
    [PostgresFact]
    public async Task A_named_lock_is_held_until_its_transaction_ends_and_the_others_are_free()
    {
        await using var t = await Engine.CreateAsync();
        var db = ((PostgresTestDb)t).Db;
        await using var holder = await db.OpenAsync();
        await using var other = await db.OpenAsync();
        await using (var tx = await holder.BeginTransactionAsync())
        {
            await PostgresLocks.TakeAsync(holder, tx, PostgresLock.CookieKeys, default);
            Assert.False(await TryAsync(other, PostgresLock.CookieKeys));
            Assert.True(await TryAsync(other, PostgresLock.TenantCreation));
            Assert.True(await TryAsync(other, PostgresLock.SharedAppSlots));
            await tx.CommitAsync();
        }
        Assert.True(await TryAsync(other, PostgresLock.CookieKeys));
    }

    /// <summary>Whether <paramref name="name"/> is free: tried (not waited for) in a transaction of its own, let go at once.</summary>
    private static async Task<bool> TryAsync(NpgsqlConnection c, PostgresLock name)
    {
        await using var cmd = new NpgsqlCommand($"SELECT pg_catalog.pg_try_advisory_xact_lock({PostgresLocks.NamedLockKey}, {(int)name})", c);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    // Copies of the template are separate databases, each at the current schema.
    [PostgresFact]
    public async Task Each_database_is_a_separate_copy_at_the_current_schema()
    {
        await using var a = await Engine.CreateAsync();
        await using var b = await Engine.CreateAsync();
        Assert.Equal(26L, await Engine.ScalarAsync(a, "SELECT MAX(version) FROM schema_migrations"));
        await Engine.ExecAsync(a, "INSERT INTO meta(key, value) VALUES('only_a', '\\x00'::bytea)");
        Assert.Equal(0L, await Engine.ScalarAsync(b, "SELECT COUNT(*) FROM meta"));
        Assert.NotEqual(await Engine.ScalarAsync(a, "SELECT current_database()"), await Engine.ScalarAsync(b, "SELECT current_database()"));
    }
}
