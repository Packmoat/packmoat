using Npgsql;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The self-service feed writes on PostgreSQL, the twin of <see cref="SqliteSelfServiceFeedStore"/>: the same
/// checks in the same order, the same refusals, the same writes. Times are timestamptz, in UTC to the microsecond
/// (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para><b>Connect</b> is one transaction. SQLite's BEGIN IMMEDIATE serialises it with every other write; here it takes two locks before it
/// reads anything that decides it. First the tenant's row, FOR NO KEY UPDATE (the directory claim's lock): the operator's bind and own-app
/// set, the directory unbind, the operator's onboard record and another Connect for the same tenant all take it, so none of them runs
/// between this Connect's checks and its writes. Then the active shared-app slot's row, FOR SHARE, which a promote's write of "active" waits
/// for, so the client id it checks is the one it records. Another tenant's claim of the same directory takes another tenant's row, so it is
/// not serialised with this one: the ux_shared_directory unique index is the backstop. Of two tenants claiming one directory at once
/// exactly one wins; the other either sees the winner's row and is refused (<see cref="ConnectResult.DirectoryTaken"/>) or meets the index,
/// with nothing written, and that 23505 on ux_shared_directory alone is answered DirectoryTaken too, once the transaction is rolled back
/// (<see cref="PostgresDirectoryRace"/>). Every early return rolls the transaction back. No catch inside it, no SAVEPOINT.</para>
/// <para><b>Withdraw</b> is one transaction: the exact wizard-made record's delete, then its admin-action row.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresSelfServiceFeedStore : ISelfServiceFeedStore
{
    private readonly PostgresDatabase _db;

    public PostgresSelfServiceFeedStore(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<ConnectResult> ConnectSelfServiceFeedAsync(ConnectFeedRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var directory = CustomerSignIn.CanonicalDirectory(r.DirectoryId);
        if (directory is null) return ConnectResult.NotLinked;
        // Another tenant's binding of this directory, committed after step 5 below and met at the binding's insert, is DirectoryTaken, as
        // step 5's own check would have said: nothing was written.
        return await PostgresDirectoryRace.TakenOnConflictAsync(PostgresDirectoryRace.SharedDirectory, ConnectResult.DirectoryTaken,
            () => ConnectOnceAsync(r, directory, ct));
    }

    /// <summary>Connect's one transaction (see <see cref="PostgresSelfServiceFeedStore"/>). Any failure rolls it back and is thrown.</summary>
    private async Task<ConnectResult> ConnectOnceAsync(ConnectFeedRequest r, string directory, CancellationToken ct)
    {
        var at = PostgresTime.ToDatabase(r.At);

        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Every early return below disposes the transaction, which rolls it back: nothing is written on a refusal.

        // 1. An active tenant whose customer sets up the feed: FeedSetupRules.CustomerSetsUpFeed, in SQL. Its row is locked here, for
        //    the rest of the transaction (the directory claim's lock), and the condition is read again on the row as it is once locked.
        await using (var tenant = new NpgsqlCommand(
            "SELECT 1 FROM tenants WHERE id=@t AND active=1 AND (origin='self' OR feed_setup='customer') FOR NO KEY UPDATE", c, tx))
        {
            tenant.Parameters.AddWithValue("t", r.TenantId);
            if (await tenant.ExecuteScalarAsync(ct) is null) return ConnectResult.NotSelfMade;
        }

        // 2. The person's directory is still linked to it.
        await using (var linked = new NpgsqlCommand("SELECT COUNT(*) FROM sign_in_directories WHERE azure_tenant_id=@d AND tenant_id=@t", c, tx))
        {
            linked.Parameters.AddWithValue("d", directory);
            linked.Parameters.AddWithValue("t", r.TenantId);
            if ((long)(await linked.ExecuteScalarAsync(ct))! == 0) return ConnectResult.NotLinked;
        }

        // 3. The active shared client id is the one the page showed (its row held FOR SHARE, so a promote waits for this Connect).
        string active;
        await using (var slot = new NpgsqlCommand("SELECT client_id FROM shared_app_credentials WHERE slot='active' FOR SHARE", c, tx))
        {
            if (await slot.ExecuteScalarAsync(ct) is not string a || !SameGuid(a, r.SeenClientId)) return ConnectResult.ClientChanged;
            active = a;
        }

        // 4. Never overwrite an own-app identity; a shared binding may only be this directory's.
        await using (var identity = new NpgsqlCommand("SELECT kind, azure_tenant_id FROM upstream_identities WHERE tenant_id=@t", c, tx))
        {
            identity.Parameters.AddWithValue("t", r.TenantId);
            await using var i = await identity.ExecuteReaderAsync(ct);
            if (await i.ReadAsync(ct))
            {
                if (i.GetString(0) != "shared") return ConnectResult.OwnApp;
                if (CustomerSignIn.CanonicalDirectory(i.GetString(1)) != directory) return ConnectResult.DirectoryTaken;
            }
        }

        // 5. The directory is free: no other tenant uses the shared app in it. lower() under COLLATE "C" folds ASCII letters only, as
        //    SQLite's lower() and the ux_shared_directory index do.
        await using (var taken = new NpgsqlCommand(
            "SELECT COUNT(*) FROM upstream_identities WHERE kind='shared' AND lower(azure_tenant_id)=@d AND tenant_id<>@t", c, tx))
        {
            taken.Parameters.AddWithValue("d", directory);
            taken.Parameters.AddWithValue("t", r.TenantId);
            if ((long)(await taken.ExecuteScalarAsync(ct))! > 0) return ConnectResult.DirectoryTaken;
        }

        // 6. Both rows, by id, and they must be THIS tenant's (a foreign key cannot say so).
        var settings = await ReadCheckAsync(c, tx, r.SettingsCheckId, ct);
        var developer = await ReadCheckAsync(c, tx, r.DeveloperCheckId, ct);
        if (settings is null || developer is null) return ConnectResult.CheckNotFound;
        if (!string.Equals(settings.TenantId, r.TenantId, StringComparison.Ordinal) || !string.Equals(developer.TenantId, r.TenantId, StringComparison.Ordinal))
            return ConnectResult.CheckNotFound;
        if (settings.Kind != FeedCheckKind.Settings || developer.Kind != FeedCheckKind.Developer) return ConnectResult.CheckNotFound;

        // 7. The settings row is the latest settings check: a newer one (passed or not) replaces it.
        await using (var latest = new NpgsqlCommand("SELECT id FROM feed_checks WHERE tenant_id=@t AND kind='settings' ORDER BY checked_at DESC, id DESC LIMIT 1", c, tx))
        {
            latest.Parameters.AddWithValue("t", r.TenantId);
            if (await latest.ExecuteScalarAsync(ct) is not long id || id != settings.Id) return ConnectResult.NotLatest;
        }

        // 7b. The developer row is the latest developer check for this feed: an older pass never outweighs a newer failure.
        await using (var latest = new NpgsqlCommand(
            "SELECT id FROM feed_checks WHERE tenant_id=@t AND kind='developer' AND feed_index_url=@f ORDER BY checked_at DESC, id DESC LIMIT 1", c, tx))
        {
            latest.Parameters.AddWithValue("t", r.TenantId);
            latest.Parameters.AddWithValue("f", settings.FeedIndexUrl);
            if (await latest.ExecuteScalarAsync(ct) is not long id || id != developer.Id) return ConnectResult.NotLatest;
        }

        // 8. What the page showed, and what the rows are about: this directory, the feed seen, the active client.
        if (!string.Equals(settings.FeedIndexUrl, r.SeenFeedUrl, StringComparison.Ordinal)) return ConnectResult.FeedChanged;
        if (CustomerSignIn.CanonicalDirectory(settings.AzureTenantId) != directory) return ConnectResult.ChecksDisagree;
        if (!SameGuid(settings.ClientId, active)) return ConnectResult.ClientChanged;

        // 9. The pure rules: results, windows, two different people, the URL shape.
        if (ConnectRules.Check(settings, developer, r.At) is { } refused) return refused;
        var (upstreamBase, prefix) = ConnectRules.UrlsFor(settings.FeedIndexUrl)!.Value;

        // Writes. 1. The tenant's URLs; the policy revision bumps so every cache drops what it had.
        await using (var urls = new NpgsqlCommand(
            "UPDATE tenants SET upstream_base=@b, allowed_path_prefix=@p, feed_index_url=@f, policy_revision=policy_revision+1 WHERE id=@t", c, tx))
        {
            urls.Parameters.AddWithValue("t", r.TenantId);
            urls.Parameters.AddWithValue("b", upstreamBase);
            urls.Parameters.AddWithValue("p", prefix);
            urls.Parameters.AddWithValue("f", settings.FeedIndexUrl);
            if (await urls.ExecuteNonQueryAsync(ct) != 1) return ConnectResult.NotSelfMade;
        }

        // 2. The shared binding (step 4 refused an own-app row, so no key is ever dropped here). Another tenant's binding of the same
        //    directory, committed since step 5, is refused here by ux_shared_directory (23505): the transaction rolls back, and the caller
        //    (ConnectSelfServiceFeedAsync) answers DirectoryTaken.
        await using (var bind = new NpgsqlCommand(
            """
            INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@t,'shared',@d,NULL,NULL,@at)
            ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@d, updated_at=@at WHERE upstream_identities.kind='shared'
            """, c, tx))
        {
            bind.Parameters.AddWithValue("t", r.TenantId);
            bind.Parameters.AddWithValue("d", directory);
            bind.Parameters.AddWithValue("at", at);
            if (await bind.ExecuteNonQueryAsync(ct) != 1) return ConnectResult.OwnApp;
        }

        // 3. The verification, made by the wizard, naming both checks it relied on.
        await using (var verify = new NpgsqlCommand(
            """
            INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by, verified_via, feed_check_id, developer_check_id)
            VALUES(@t,@d,@f,@c,@at,@by,'wizard',@s,@dev)
            ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@d, feed_index_url=@f, client_id=@c, verified_at=@at, verified_by=@by,
                verified_via='wizard', feed_check_id=@s, developer_check_id=@dev
            """, c, tx))
        {
            verify.Parameters.AddWithValue("t", r.TenantId);
            verify.Parameters.AddWithValue("d", directory);
            verify.Parameters.AddWithValue("f", settings.FeedIndexUrl);
            verify.Parameters.AddWithValue("c", active);
            verify.Parameters.AddWithValue("at", at);
            verify.Parameters.AddWithValue("by", r.VerifiedBy);
            verify.Parameters.AddWithValue("s", settings.Id);
            verify.Parameters.AddWithValue("dev", developer.Id);
            await verify.ExecuteNonQueryAsync(ct);
        }

        // 4. The record, in the same transaction: no record, no connection.
        await InsertActionAsync(c, tx, r.Action, ct);
        await tx.CommitAsync(ct);
        return ConnectResult.Connected;
    }

    public async Task<bool> WithdrawAsync(SharedAppVerification v, AdminActionRecord action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(action);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM shared_app_verifications WHERE tenant_id=@id AND azure_tenant_id=@a AND feed_index_url=@f AND client_id=@c AND verified_at=@t AND verified_by=@by
                AND verified_via='wizard'
            """, c, tx))
        {
            PostgresSharedAppCredentialStore.AddExact(delete, v);
            if (await delete.ExecuteNonQueryAsync(ct) == 0) return false;
        }
        await InsertActionAsync(c, tx, action, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<FeedCheck?> ReadCheckAsync(NpgsqlConnection c, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT " + PostgresFeedCheckStore.Columns + " FROM feed_checks WHERE id=@id", c, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? PostgresFeedCheckStore.Read(r) : null;
    }

    private static async Task InsertActionAsync(NpgsqlConnection c, NpgsqlTransaction tx, AdminActionRecord a, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site) VALUES(@t,@tid,@oid,@n,@a,@tn,@dt,@s)", c, tx);
        cmd.Parameters.AddWithValue("t", PostgresTime.ToDatabase(a.Time));
        cmd.Parameters.AddWithValue("tid", a.ActorTid);
        cmd.Parameters.AddWithValue("oid", a.ActorOid);
        cmd.Parameters.AddWithValue("n", a.ActorName);
        cmd.Parameters.AddWithValue("a", a.Action);
        cmd.Parameters.AddWithValue("tn", a.TenantId);
        cmd.Parameters.AddWithValue("dt", a.Detail);
        cmd.Parameters.AddWithValue("s", a.Site);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool SameGuid(string? a, string? b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y && x != Guid.Empty;
}
