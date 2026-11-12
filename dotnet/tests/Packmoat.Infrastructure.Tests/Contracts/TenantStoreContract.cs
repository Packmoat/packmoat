using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The tenant store's contract: policy, waivers and their validation through the store.</summary>
public abstract class TenantStoreContract : StoreContract
{
    [EngineFact]
    public async Task Policy_round_trips_through_the_database()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Tenants(t).AddInternalPrefixAsync("acme", "Contoso.", default);
        await Engine.Tenants(t).SetOverrideAsync("acme", "Moq", 3, default);
        var waiver = await Engine.Tenants(t).AddWaiverAsync("acme", "Newtonsoft.Json", "13.0.4", t.Clock.UtcNow.AddDays(2), "hotfix", "olivia", default);

        var tenant = (await Engine.Tenants(t).GetAsync("acme", default))!;

        Assert.Equal(14, tenant.Policy.CooldownDays);
        Assert.Equal(new[] { "Contoso." }, tenant.Policy.InternalPrefixes);
        Assert.Equal(new PackageOverride("moq", 3), Assert.Single(tenant.Policy.PackageOverrides));
        var w = Assert.Single(tenant.Policy.Waivers);
        Assert.Equal(waiver.Id, w.Id);
        Assert.Equal("newtonsoft.json", w.PackageId);
        Assert.Equal("hotfix", w.Reason);
        Assert.Equal("https://pkgs.dev.azure.com/", tenant.UpstreamBase);
    }

    [EngineFact]
    public async Task Every_policy_change_bumps_the_revision()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var seen = new List<long> { (await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Revision };

        async Task Record() => seen.Add((await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Revision);
        await Engine.Tenants(t).SetCooldownAsync("acme", 7, default); await Record();
        await Engine.Tenants(t).AddInternalPrefixAsync("acme", "X.", default); await Record();
        await Engine.Tenants(t).SetOverrideAsync("acme", "a", 1, default); await Record();
        var w = await Engine.Tenants(t).AddWaiverAsync("acme", "a", "*", t.Clock.UtcNow.AddDays(1), "r", "d", default); await Record();
        await Engine.Tenants(t).RevokeWaiverAsync(null, w.Id, "olivia", default); await Record();
        await Engine.Tenants(t).RemoveOverrideAsync("acme", "a", default); await Record();

        Assert.Equal(seen.Order().ToArray(), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [EngineFact]
    public async Task A_revoked_waiver_no_longer_appears_in_the_policy()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var w = await Engine.Tenants(t).AddWaiverAsync("acme", "a", "1.0.0", t.Clock.UtcNow.AddDays(1), "r", "d", default);
        Assert.True(await Engine.Tenants(t).RevokeWaiverAsync(null, w.Id, "olivia", default));
        Assert.False(await Engine.Tenants(t).RevokeWaiverAsync(null, w.Id, "olivia", default));
        Assert.Empty((await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Waivers);
    }

    [EngineFact]
    public async Task A_tenant_scoped_waiver_revoke_cannot_touch_another_tenants_waiver()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "other");
        var theirs = await Engine.Tenants(t).AddWaiverAsync("other", "a", "1.0.0", t.Clock.UtcNow.AddDays(1), "r", "d", default);
        var revisionBefore = (await Engine.Tenants(t).GetAsync("other", default))!.Policy.Revision;

        Assert.False(await Engine.Tenants(t).RevokeWaiverAsync("acme", theirs.Id, "mallory", default));   // a foreign tenant id revokes nothing
        var after = (await Engine.Tenants(t).GetAsync("other", default))!;
        Assert.Single(after.Policy.Waivers);
        Assert.Equal(revisionBefore, after.Policy.Revision);

        Assert.True(await Engine.Tenants(t).RevokeWaiverAsync("other", theirs.Id, "olivia", default));
        Assert.Empty((await Engine.Tenants(t).GetAsync("other", default))!.Policy.Waivers);
    }

    [EngineFact]
    public async Task Bad_input_is_refused()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Engine.Tenants(t).SetCooldownAsync("acme", -1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).AddInternalPrefixAsync("acme", "  *", default)); // would hide everything
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).AddWaiverAsync("acme", "a", "*", t.Clock.UtcNow.AddSeconds(-1), "r", "d", default));
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).AddWaiverAsync("acme", "a", "*", t.Clock.UtcNow.AddDays(1), " ", "d", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tenants(t).SetCooldownAsync("nobody", 3, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tenants(t).AddInternalPrefixAsync("nobody", "X.", default));
    }

    [EngineFact]
    public async Task The_engine_sees_database_policy_the_same_way_as_in_memory_policy()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Tenants(t).AddInternalPrefixAsync("acme", "Contoso.", default);
        var tenant = (await Engine.Tenants(t).GetAsync("acme", default))!;
        var engine = Packmoat.Policy.PolicyEngine.CreateDefault(t.Clock);

        var verdict = engine.Decide(new PackageVersionFacts(Ecosystem.NuGet, "contoso.core", "1.0.0", t.Clock.UtcNow.AddDays(-900)), tenant.Policy);

        Assert.Equal(VerdictReason.InternalNamespace, verdict.Reason);
    }

    /// <summary>
    /// A time is kept as an instant: given with an offset (+02:00 here), it reads back as the same instant with
    /// offset 0, through the policy and the waiver list, and is held as that instant's UTC text. A time finer than a microsecond is never
    /// stored later than given, and is within a microsecond of it (SQLite keeps all seven digits; PostgreSQL cuts to six).
    /// </summary>
    [EngineFact]   // mutations: PostgresTime's UTC conversion dropped; rounding instead of cutting to the microsecond
    public async Task A_time_given_with_an_offset_reads_back_as_the_same_instant_in_utc()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var given = new DateTimeOffset(2026, 10, 20, 14, 30, 5, TimeSpan.FromHours(2)).AddTicks(1_234_560);   // 12:30:05.123456 UTC: whole microseconds
        var w = await Engine.Tenants(t).AddWaiverAsync("acme", "a", "1.0.0", given, "r", "olivia", default);

        var inPolicy = Assert.Single((await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Waivers).ExpiresAt;
        var listed = Assert.Single(await Engine.Tenants(t).ListWaiversAsync("acme", 10, default)).ExpiresAt;
        foreach (var read in new[] { inPolicy, listed })
        {
            Assert.Equal(TimeSpan.Zero, read.Offset);
            Assert.Equal(given.UtcTicks, read.UtcTicks);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 30, 5, TimeSpan.Zero).AddTicks(1_234_560), read);
        }
        Assert.Equal("2026-10-20T12:30:05.1234560+00:00", await ScalarAsync(t, $"SELECT expires_at FROM waivers WHERE id='{w.Id}'"));
        Assert.Equal(given.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), await ScalarAsync(t, $"SELECT expires_at FROM waivers WHERE id='{w.Id}'"));

        var fine = given.AddDays(1).AddTicks(19);   // 1.9 µs past a whole microsecond
        await Engine.Tenants(t).AddWaiverAsync("acme", "b", "1.0.0", fine, "r", "olivia", default);
        var stored = (await Engine.Tenants(t).ListWaiversAsync("acme", 10, default)).Single(x => x.PackageId == "b").ExpiresAt;
        Assert.True(stored <= fine, $"{stored:O} was stored for {fine:O}: later than given");
        Assert.True(fine - stored < TimeSpan.FromMicroseconds(1), $"{stored:O} was stored for {fine:O}: a microsecond or more early");
    }

    /// <summary>
    /// Tenant creations run one after the other: a creation participant that looks at the other tenants sees every
    /// creation that began before its own. Here it refuses a second tenant, and waits (up to two seconds) until both creations are inside
    /// it, if the store lets both in at once; then exactly one tenant is made, never both.
    /// </summary>
    [EngineFact]   // mutations: PostgresLocks.TenantCreation not taken (PostgreSQL); a deferred transaction (SQLite)
    public async Task Two_tenants_made_at_once_are_made_one_after_the_other()
    {
        await using var t = await CreateDbAsync();
        var onlyOne = new OnlyOneTenant();
        var store = Engine.TenantsWith(t, new TenantLifecycle([onlyOne], [], null));

        var made = await Task.WhenAll(new[] { "first", "second" }.Select(id => Task.Run(async () =>
        {
            try
            {
                await store.CreateAsync(TestTenants.Operator(id), default);
                return true;
            }
            catch (TenantCreationRefusedException)
            {
                return false;
            }
        })));

        Assert.Equal(1, made.Count(m => m));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenants"));
        Assert.Equal(2, onlyOne.Calls);
    }

    // ---------------------------------------------------------------- the upstream identity (the first contract tests of it)

    private const string Pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";
    private const string DirectoryId = "11111111-2222-4333-8444-555555555555";

    private static NewTenant SelfService(string id) =>
        new(id, "Self", "", "o/", "https://pkgs.dev.azure.com/o/p/index.json", 14, TenantOrigin.Self) { FeedSetup = FeedSetup.Customer };

    [EngineFact]   // mutations: the key not encrypted with the tenant's context; an upsert that keeps the old client id; feed_setup not taken over
    public async Task An_own_identity_round_trips_encrypted_replaces_the_last_and_takes_the_feed_over()
    {
        await using var t = await CreateDbAsync();
        await Engine.Tenants(t).CreateAsync(SelfService("selfco"), default);
        Assert.Equal(new TenantSetup(TenantOrigin.Self, FeedSetup.Customer), await Engine.Tenants(t).GetSetupAsync("selfco", default));
        Assert.Null(await Engine.Tenants(t).GetUpstreamIdentityAsync("selfco", default));
        Assert.Null(await Engine.Tenants(t).GetUpstreamBindingAsync("selfco", default));

        await Engine.Tenants(t).SetUpstreamIdentityAsync("selfco", new UpstreamIdentity("az-1", "client-1", Pem), default);
        await Engine.Tenants(t).SetUpstreamIdentityAsync("selfco", new UpstreamIdentity("az-2", "client-2", Pem + "2"), default);

        Assert.Equal(new UpstreamIdentity("az-2", "client-2", Pem + "2"), await Engine.Tenants(t).GetUpstreamIdentityAsync("selfco", default));
        Assert.Equal(new UpstreamBinding(IdentityKind.Own, "az-2", "client-2"), await Engine.Tenants(t).GetUpstreamBindingAsync("selfco", default));
        Assert.Equal(new TenantSetup(TenantOrigin.Self, FeedSetup.Operator), await Engine.Tenants(t).GetSetupAsync("selfco", default));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM upstream_identities"));
        Assert.Null(await Engine.Tenants(t).GetSetupAsync("nobody", default));

        // The key is bound to its tenant: another tenant's row holding it does not decrypt.
        await AddTenantAsync(t, "globex");
        await Engine.Tenants(t).SetUpstreamIdentityAsync("globex", new UpstreamIdentity("az-3", "client-3", "other"), default);
        await ExecAsync(t, "UPDATE upstream_identities SET cert_encrypted = (SELECT cert_encrypted FROM upstream_identities WHERE tenant_id='selfco') WHERE tenant_id='globex'");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Engine.Tenants(t).GetUpstreamIdentityAsync("globex", default));
    }

    [EngineFact]   // mutations: the shared bind keeps the client id or the key; the old verification kept; feed_setup not taken over
    public async Task A_shared_binding_clears_the_own_key_drops_the_old_verification_and_takes_the_feed_over()
    {
        await using var t = await CreateDbAsync();
        await Engine.Tenants(t).CreateAsync(SelfService("selfco"), default);
        await Engine.Tenants(t).SetUpstreamIdentityAsync("selfco", new UpstreamIdentity("az-1", "client-1", Pem), default);
        await ExecAsync(t, "UPDATE tenants SET feed_setup='customer' WHERE id='selfco'");
        await ExecAsync(t, "INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) " +
            $"VALUES('selfco', '{DirectoryId}', 'https://f/index.json', 'app', '2026-10-10T12:00:00.0000000+00:00', 'op')");

        await Engine.Tenants(t).BindSharedIdentityAsync("selfco", DirectoryId, default);

        Assert.Equal(new UpstreamBinding(IdentityKind.Shared, DirectoryId, null), await Engine.Tenants(t).GetUpstreamBindingAsync("selfco", default));
        Assert.Null(await Engine.Tenants(t).GetUpstreamIdentityAsync("selfco", default));
        Assert.Equal(["NULL|NULL"], await ColumnAsync(t, "SELECT COALESCE(client_id, 'NULL') || '|' || CASE WHEN cert_encrypted IS NULL THEN 'NULL' ELSE 'key' END FROM upstream_identities"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM shared_app_verifications"));
        Assert.Equal(FeedSetup.Operator, (await Engine.Tenants(t).GetSetupAsync("selfco", default))!.FeedSetup);

        // And back to an own app: the shared row becomes an own one again.
        await Engine.Tenants(t).SetUpstreamIdentityAsync("selfco", new UpstreamIdentity("az-2", "client-2", Pem), default);
        Assert.Equal(new UpstreamBinding(IdentityKind.Own, "az-2", "client-2"), await Engine.Tenants(t).GetUpstreamBindingAsync("selfco", default));
    }

    [EngineFact]
    public async Task An_identity_for_a_missing_tenant_is_refused_and_nothing_is_written()
    {
        await using var t = await CreateDbAsync();
        await RefusedAsync(() => Engine.Tenants(t).SetUpstreamIdentityAsync("nobody", new UpstreamIdentity("a", "c", Pem), default));
        await RefusedAsync(() => Engine.Tenants(t).BindSharedIdentityAsync("nobody", DirectoryId, default));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM upstream_identities"));
    }

    /// <summary>A participant that refuses a tenant when another exists, read in the creation's own transaction.</summary>
    private sealed class OnlyOneTenant : ITenantCreationParticipant
    {
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public async Task OnCreatingAsync(DbTransaction transaction, NewTenant tenant, DateTimeOffset createdAt, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 2) both.TrySetResult();
            await Task.WhenAny(both.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            await using var count = transaction.Connection!.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM tenants";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 1)
                throw new TenantCreationRefusedException("Only one tenant here.", "only-one");
        }
    }
}
