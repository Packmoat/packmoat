using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The master-key check behind its port (#278 step 0, A4; #288 P2 on every engine): <see cref="IMasterKeyCheck.EnsureAsync"/> records the
/// marker on a fresh database, keeps it, and refuses a wrong key with the start's exact message; <see cref="IMasterKeyCheck.MatchesAsync"/>
/// only reads. Fixtures: MasterKeyCheckTests (SQLite) and PostgresMasterKeyCheckTests.
/// </summary>
public abstract class MasterKeyCheckContract : SqlContract<IMasterKeyEngine>
{
    private const string WrongKeyMessage = "The master key does not match this database. Refusing to continue: customer identities would not decrypt.";

    private static AesGcmSecretProtector NewKey() => AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());

    private async Task<long> Markers(IEngineDb t) => (long)(await ScalarAsync(t, "SELECT COUNT(*) FROM meta WHERE key='key_check'"))!;

    private async Task<string?> Marker(IEngineDb t) => (await ColumnAsync(t, $"SELECT {Engine.HexSql("value")} FROM meta WHERE key='key_check'")).SingleOrDefault();

    [EngineFact]   // mutation: EnsureAsync no longer records the marker
    public async Task A_fresh_database_gets_the_marker()
    {
        await using var t = await CreateDbAsync();
        var check = Engine.MasterKeyCheck(t, t.Protector);
        Assert.Equal(0, await Markers(t));

        await check.EnsureAsync(default);

        Assert.Equal(1, await Markers(t));
    }

    [EngineFact]   // mutation: the marker overwritten on each start (an upsert instead of insert-if-none)
    public async Task The_same_key_passes_again_and_the_marker_is_kept()
    {
        await using var t = await CreateDbAsync();
        var check = Engine.MasterKeyCheck(t, t.Protector);
        await check.EnsureAsync(default);
        var first = await Marker(t);

        await check.EnsureAsync(default);
        await Engine.MasterKeyCheck(t, t.Protector).EnsureAsync(default);

        Assert.Equal(1, await Markers(t));
        Assert.Equal(first, await Marker(t));
    }

    [EngineFact]   // mutations: the message reworded; a wrong key's marker written over the right one's
    public async Task A_wrong_key_is_refused_with_the_exact_message_and_the_marker_is_kept()
    {
        await using var t = await CreateDbAsync();
        await Engine.MasterKeyCheck(t, t.Protector).EnsureAsync(default);
        var first = await Marker(t);

        var wrong = Engine.MasterKeyCheck(t, NewKey());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.EnsureAsync(default));

        Assert.Equal(WrongKeyMessage, ex.Message);
        Assert.Equal(first, await Marker(t));
        await Engine.MasterKeyCheck(t, t.Protector).EnsureAsync(default);   // the right key still passes
    }

    /// <summary>
    /// #295 (review F4): two first starts at once on a new database with different keys. Exactly one passes, the other stops with the
    /// start's message, and one marker is kept: the first writer's. The rival start is made deterministic by
    /// <see cref="IMasterKeyEngine.ArrangeRivalMarkerAsync"/>: its marker, under key B, lands after this start (key A) has found no marker
    /// and before its own write, so A must read the marker back and stop.
    /// </summary>
    [EngineFact]   // mutations: no read-back after the insert; a read-back that checks only that a row exists; an upsert (OR REPLACE / DO UPDATE)
    public async Task Of_two_first_starts_with_different_keys_exactly_one_passes()
    {
        await using var t = await CreateDbAsync();
        var a = t.Protector;
        var b = NewKey();
        var rival = MasterKeyMarker.Create(b);
        await Engine.ArrangeRivalMarkerAsync(t, rival);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.MasterKeyCheck(t, a).EnsureAsync(default));

        Assert.Equal(WrongKeyMessage, ex.Message);
        Assert.Equal(1, await Markers(t));
        Assert.Equal(Convert.ToHexString(rival), await Marker(t));
        Assert.True(await Engine.MasterKeyCheck(t, b).MatchesAsync(default));
        Assert.False(await Engine.MasterKeyCheck(t, a).MatchesAsync(default));
    }

    [EngineFact]   // mutations: MatchesAsync records a marker; MatchesAsync true without one
    public async Task Matches_is_false_before_the_marker_true_after_it_and_never_writes()
    {
        await using var t = await CreateDbAsync();
        var check = Engine.MasterKeyCheck(t, t.Protector);

        Assert.False(await check.MatchesAsync(default));
        Assert.False(await check.MatchesAsync(default));
        Assert.Equal(0, await Markers(t));          // never writes, not even a missing marker

        await check.EnsureAsync(default);
        var first = await Marker(t);
        Assert.True(await check.MatchesAsync(default));
        Assert.False(await Engine.MasterKeyCheck(t, NewKey()).MatchesAsync(default));
        Assert.Equal(1, await Markers(t));
        Assert.Equal(first, await Marker(t));
    }
}
