using Microsoft.Data.Sqlite;
using Npgsql;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The master-key check on SQLite: <see cref="MasterKeyCheckContract"/>, and how the SQLite stores give it.</summary>
[Collection("sqlite-pools")]
public partial class MasterKeyCheckTests : MasterKeyCheckContract
{
    protected override IMasterKeyEngine Engine => SqliteStoreEngine.Instance;

    [Fact]   // mutation: the stores make a new check each read, or one over another key
    public async Task The_stores_give_one_check_over_their_key()
    {
        await using var t = await TestDb.CreateAsync();
        var stores = new SqliteStores(t.Db, t.Protector);
        Assert.Same(stores.MasterKeyCheck, stores.MasterKeyCheck);
        Assert.Same(t.Db, stores.Migrator);

        await stores.MasterKeyCheck.EnsureAsync(default);
        Assert.True(await new SqliteMasterKeyCheck(t.Db, t.Protector).MatchesAsync(default));
        Assert.IsType<SqliteJobLease>(stores.JobLease(t.Clock));
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>The master-key check on PostgreSQL: <see cref="MasterKeyCheckContract"/>, each test on a database of its own.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresMasterKeyCheckTests : MasterKeyCheckContract
{
    protected override IMasterKeyEngine Engine => PostgresSqlEngine.Instance;

    /// <summary>
    /// Under real concurrency: two first starts at once with different keys, as two real sessions. Exactly one passes, the other
    /// stops with the start's message, and one marker is kept: the winner's. The deterministic case, the same on every engine, is
    /// <see cref="MasterKeyCheckContract.Of_two_first_starts_with_different_keys_exactly_one_passes"/>; this one checks the same
    /// outcome with no test hook in the database, PostgreSQL only.
    /// </summary>
    // mutation: read first, then insert or upsert (both read no marker and both pass)
    [PostgresFact]
    public async Task Of_two_first_starts_with_different_keys_exactly_one_passes_under_real_concurrency()
    {
        for (var round = 1; round <= 25; round++)
        {
            await using var t = await Engine.CreateAsync();
            var db = ((PostgresTestDb)t).Db;
            AesGcmSecretProtector[] keys = [t.Protector, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64())];
            var checks = keys.Select(k => Engine.MasterKeyCheck(t, k)).ToArray();

            // The start gate: meta is held in EXCLUSIVE mode, which lets reads through and stops every write. Both starts are let go only
            // once both wait on it, so whatever each reads first, both read before either has written: the worst interleaving of the reads,
            // every round. The order of the two writes once let go is left to chance: a variant that reads first, then
            // upserts with DO UPDATE and reads back, is caught here only in the rounds where the loser writes last; the contract's
            // deterministic test catches it every time.
            await using var holder = await db.OpenAsync();
            await using var hold = await holder.BeginTransactionAsync();
            await using (var lockMeta = new NpgsqlCommand("LOCK TABLE meta IN EXCLUSIVE MODE", holder, hold)) await lockMeta.ExecuteNonQueryAsync();

            var runs = checks.Select(c => Task.Run(async () =>
            {
                try { await c.EnsureAsync(default); return null; }
                catch (InvalidOperationException e) { return e.Message; }
            })).ToArray();
            await WaitForWritersAsync(holder, hold, 2, round);
            await hold.CommitAsync();
            var outcomes = await Task.WhenAll(runs);

            Assert.True(outcomes.Count(o => o is null) == 1, $"round {round}: {outcomes.Count(o => o is null)} of the two starts passed");
            Assert.Equal(MasterKeyMarker.WrongKeyMessage, Assert.Single(outcomes, o => o is not null));
            Assert.Equal(1L, await Engine.ScalarAsync(t, "SELECT COUNT(*) FROM meta WHERE key='key_check'"));
            var winner = Array.IndexOf(outcomes, null);
            Assert.True(await checks[winner].MatchesAsync(default));
            Assert.False(await checks[1 - winner].MatchesAsync(default));
        }
    }

    /// <summary>Waits (up to 30 s) until <paramref name="count"/> sessions wait for a lock on meta.</summary>
    private static async Task WaitForWritersAsync(NpgsqlConnection holder, NpgsqlTransaction hold, int count, int round)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var waiting = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_class c ON c.oid = l.relation WHERE c.relname = 'meta' AND NOT l.granted " +
                "AND l.database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())", holder, hold);
            if ((long)(await waiting.ExecuteScalarAsync())! >= count) return;
            Assert.True(DateTime.UtcNow < deadline, $"round {round}: the two starts did not both reach the write");
            await Task.Delay(10);
        }
    }
}
