using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>
/// The self-service feed writes. Connect is one IMMEDIATE transaction (the write lock is taken before the first read), so
/// every refusal is decided on the rows as they are when the write happens, and two Connects, or a Connect and the operator's bind, are
/// serialised. The ux_shared_directory index is the backstop for the directory check.
/// </summary>
public sealed class SqliteSelfServiceFeedStore : ISelfServiceFeedStore
{
    private readonly SqliteDatabase _db;

    public SqliteSelfServiceFeedStore(SqliteDatabase db) => _db = db;

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public async Task<ConnectResult> ConnectSelfServiceFeedAsync(ConnectFeedRequest r, CancellationToken ct)
    {
        var directory = CustomerSignIn.CanonicalDirectory(r.DirectoryId);
        if (directory is null) return ConnectResult.NotLinked;

        await using var c = await _db.OpenAsync(ct);
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        // Every early return below disposes the transaction, which rolls it back: nothing is written on a refusal.

        // 1. An active tenant whose customer sets up the feed: FeedSetupRules.CustomerSetsUpFeed, in SQL.
        cmd.CommandText = "SELECT COUNT(*) FROM tenants WHERE id=@t AND active=1 AND (origin='self' OR feed_setup='customer')";
        cmd.Parameters.AddWithValue("@t", r.TenantId);
        if ((long)(await cmd.ExecuteScalarAsync(ct))! == 0) return ConnectResult.NotSelfMade;

        // 2. The person's directory is still linked to it.
        cmd.CommandText = "SELECT COUNT(*) FROM sign_in_directories WHERE azure_tenant_id=@d AND tenant_id=@t";
        cmd.Parameters.AddWithValue("@d", directory);
        if ((long)(await cmd.ExecuteScalarAsync(ct))! == 0) return ConnectResult.NotLinked;

        // 3. The active shared client id is the one the page showed.
        cmd.CommandText = "SELECT client_id FROM shared_app_credentials WHERE slot='active'";
        if (await cmd.ExecuteScalarAsync(ct) is not string active || !SameGuid(active, r.SeenClientId)) return ConnectResult.ClientChanged;

        // 4. Never overwrite an own-app identity; a shared binding may only be this directory's.
        cmd.CommandText = "SELECT kind, azure_tenant_id FROM upstream_identities WHERE tenant_id=@t";
        await using (var i = await cmd.ExecuteReaderAsync(ct))
        {
            if (await i.ReadAsync(ct))
            {
                if (i.GetString(0) != "shared") return ConnectResult.OwnApp;
                if (CustomerSignIn.CanonicalDirectory(i.GetString(1)) != directory) return ConnectResult.DirectoryTaken;
            }
        }

        // 5. The directory is free: no other tenant uses the shared app in it.
        cmd.CommandText = "SELECT COUNT(*) FROM upstream_identities WHERE kind='shared' AND lower(azure_tenant_id)=@d AND tenant_id<>@t";
        if ((long)(await cmd.ExecuteScalarAsync(ct))! > 0) return ConnectResult.DirectoryTaken;

        // 6. Both rows, by id, and they must be THIS tenant's (a foreign key cannot say so).
        var settings = await ReadCheckAsync(cmd, r.SettingsCheckId, ct);
        var developer = await ReadCheckAsync(cmd, r.DeveloperCheckId, ct);
        if (settings is null || developer is null) return ConnectResult.CheckNotFound;
        if (!string.Equals(settings.TenantId, r.TenantId, StringComparison.Ordinal) || !string.Equals(developer.TenantId, r.TenantId, StringComparison.Ordinal))
            return ConnectResult.CheckNotFound;
        if (settings.Kind != FeedCheckKind.Settings || developer.Kind != FeedCheckKind.Developer) return ConnectResult.CheckNotFound;

        // 7. The settings row is the latest settings check: a newer one (passed or not) replaces it.
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT id FROM feed_checks WHERE tenant_id=@t AND kind='settings' ORDER BY checked_at DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@t", r.TenantId);
        if (await cmd.ExecuteScalarAsync(ct) is not long latest || latest != settings.Id) return ConnectResult.NotLatest;

        // 7b. The developer row is the latest developer check for this feed: an older pass never outweighs a newer failure.
        cmd.CommandText = "SELECT id FROM feed_checks WHERE tenant_id=@t AND kind='developer' AND feed_index_url=@f ORDER BY checked_at DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@f", settings.FeedIndexUrl);
        if (await cmd.ExecuteScalarAsync(ct) is not long latestDeveloper || latestDeveloper != developer.Id) return ConnectResult.NotLatest;

        // 8. What the page showed, and what the rows are about: this directory, the feed seen, the active client.
        if (!string.Equals(settings.FeedIndexUrl, r.SeenFeedUrl, StringComparison.Ordinal)) return ConnectResult.FeedChanged;
        if (CustomerSignIn.CanonicalDirectory(settings.AzureTenantId) != directory) return ConnectResult.ChecksDisagree;
        if (!SameGuid(settings.ClientId, active)) return ConnectResult.ClientChanged;

        // 9. The pure rules: results, windows, two different people, the URL shape.
        if (ConnectRules.Check(settings, developer, r.At) is { } refused) return refused;
        var (upstreamBase, prefix) = ConnectRules.UrlsFor(settings.FeedIndexUrl)!.Value;

        // Writes. 1. The tenant's URLs; the policy revision bumps so every cache drops what it had.
        var at = Format(r.At);
        cmd.Parameters.Clear();
        cmd.CommandText = "UPDATE tenants SET upstream_base=@b, allowed_path_prefix=@p, feed_index_url=@f, policy_revision=policy_revision+1 WHERE id=@t";
        cmd.Parameters.AddWithValue("@t", r.TenantId);
        cmd.Parameters.AddWithValue("@b", upstreamBase);
        cmd.Parameters.AddWithValue("@p", prefix);
        cmd.Parameters.AddWithValue("@f", settings.FeedIndexUrl);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return ConnectResult.NotSelfMade;

        // 2. The shared binding (step 4 refused an own-app row, so no key is ever dropped here).
        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@t,'shared',@d,NULL,NULL,@at)
            ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@d, updated_at=@at WHERE kind='shared'
            """;
        cmd.Parameters.AddWithValue("@t", r.TenantId);
        cmd.Parameters.AddWithValue("@d", directory);
        cmd.Parameters.AddWithValue("@at", at);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return ConnectResult.OwnApp;

        // 3. The verification, made by the wizard, naming both checks it relied on.
        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by, verified_via, feed_check_id, developer_check_id)
            VALUES(@t,@d,@f,@c,@at,@by,'wizard',@s,@dev)
            ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@d, feed_index_url=@f, client_id=@c, verified_at=@at, verified_by=@by,
                verified_via='wizard', feed_check_id=@s, developer_check_id=@dev
            """;
        cmd.Parameters.AddWithValue("@t", r.TenantId);
        cmd.Parameters.AddWithValue("@d", directory);
        cmd.Parameters.AddWithValue("@f", settings.FeedIndexUrl);
        cmd.Parameters.AddWithValue("@c", active);
        cmd.Parameters.AddWithValue("@at", at);
        cmd.Parameters.AddWithValue("@by", r.VerifiedBy);
        cmd.Parameters.AddWithValue("@s", settings.Id);
        cmd.Parameters.AddWithValue("@dev", developer.Id);
        await cmd.ExecuteNonQueryAsync(ct);

        // 4. The record, in the same transaction: no record, no connection.
        await InsertActionAsync(cmd, r.Action, ct);
        await tx.CommitAsync(ct);
        return ConnectResult.Connected;
    }

    public async Task<bool> WithdrawAsync(SharedAppVerification v, AdminActionRecord action, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM shared_app_verifications WHERE tenant_id=@id AND azure_tenant_id=@a AND feed_index_url=@f AND client_id=@c AND verified_at=@t AND verified_by=@by
                AND verified_via='wizard'
            """;
        cmd.Parameters.AddWithValue("@id", v.TenantId);
        cmd.Parameters.AddWithValue("@a", v.AzureTenantId);
        cmd.Parameters.AddWithValue("@f", v.FeedIndexUrl);
        cmd.Parameters.AddWithValue("@c", v.ClientId);
        cmd.Parameters.AddWithValue("@t", Format(v.VerifiedAt));
        cmd.Parameters.AddWithValue("@by", v.VerifiedBy);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;
        await InsertActionAsync(cmd, action, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<FeedCheck?> ReadCheckAsync(SqliteCommand cmd, long id, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT " + SqliteFeedCheckStore.Columns + " FROM feed_checks WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? SqliteFeedCheckStore.Read(r) : null;
    }

    private static async Task InsertActionAsync(SqliteCommand cmd, AdminActionRecord a, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site) VALUES(@t,@tid,@oid,@n,@a,@tn,@dt,@s)";
        cmd.Parameters.AddWithValue("@t", Format(a.Time));
        cmd.Parameters.AddWithValue("@tid", a.ActorTid);
        cmd.Parameters.AddWithValue("@oid", a.ActorOid);
        cmd.Parameters.AddWithValue("@n", a.ActorName);
        cmd.Parameters.AddWithValue("@a", a.Action);
        cmd.Parameters.AddWithValue("@tn", a.TenantId);
        cmd.Parameters.AddWithValue("@dt", a.Detail);
        cmd.Parameters.AddWithValue("@s", a.Site);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool SameGuid(string? a, string? b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y && x != Guid.Empty;
}
