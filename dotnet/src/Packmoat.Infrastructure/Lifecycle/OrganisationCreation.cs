using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Lifecycle;

/// <summary>The person a new organisation starts with (a member row in its directory), or none: their first sign-in makes it.</summary>
public sealed record OrganisationMember(string ObjectId, string DisplayName);

/// <summary>
/// One organisation to make: the tenant, the customer Microsoft directory linked to it, who linked it, and optionally its first
/// member and the admin_actions record of making it. All are written in one transaction, or nothing is.
/// </summary>
/// <param name="AzureTenantId">The directory (tenant) id: a GUID, stored lower-case.</param>
/// <param name="At">When: the tenant's creation time and the link's.</param>
/// <param name="BoundBy">Who linked the directory, as the link records it.</param>
public sealed record NewOrganisation(NewTenant Tenant, string AzureTenantId, DateTimeOffset At, string BoundBy, OrganisationMember? FirstMember = null,
    AdminActionRecord? Record = null);

public enum OrganisationCreateResult
{
    Created,
    /// <summary>The directory is already linked to a tenant. Nothing was written and that link was not touched.</summary>
    DirectoryTaken,
}

/// <summary>
/// Where an organisation is made: the tenant, its directory link, the first member and the record, in one transaction or
/// not at all. <see cref="SqliteOrganisationStore"/> is the SQLite adapter; the store contract holds what every adapter must do.
/// </summary>
public interface IOrganisationStore
{
    /// <summary>
    /// Makes the organisation, or returns <see cref="OrganisationCreateResult.DirectoryTaken"/> having written nothing when the directory
    /// is already linked to a tenant. A veto (<see cref="TenantCreationRefusedException"/>) or any failure rolls everything back and throws.
    /// </summary>
    /// <param name="extraParticipants">The caller's own participants, run after the lifecycle's, in the same transaction.</param>
    Task<OrganisationCreateResult> CreateAsync(NewOrganisation o, IReadOnlyList<ITenantCreationParticipant> extraParticipants, CancellationToken ct);
}

/// <summary>
/// The core's one way to make an organisation: a tenant and its directory link in a single IMMEDIATE transaction (the write lock
/// is taken before the first read), so two creations for one directory, or a creation and the operator's bind, are serialised: the
/// second sees the first's link and gets <see cref="OrganisationCreateResult.DirectoryTaken"/>. In order:
/// <list type="number">
/// <item>a directory already linked to any tenant: DirectoryTaken, nothing written;</item>
/// <item>the tenant row, which runs the lifecycle's creation participants;</item>
/// <item>the caller's extra participants, in order (any may veto with <see cref="TenantCreationRefusedException"/>);</item>
/// <item>the link, with no ON CONFLICT: the sign_in_directories primary key is the race backstop, and its failure undoes everything;</item>
/// <item>the first member, when given (an older row of that person in another tenant is moved here, as a sign-in would);</item>
/// <item>the record, when given: no record, no organisation;</item>
/// <item>commit.</item>
/// </list>
/// A veto or any failure rolls the whole transaction back and throws.
/// </summary>
public sealed class SqliteOrganisationStore : IOrganisationStore
{
    private readonly SqliteDatabase _db;
    private readonly IReadOnlyList<ITenantCreationParticipant> _participants;

    /// <param name="participants">The lifecycle's creation participants (<see cref="TenantLifecycle.Participants"/>, empty for none).</param>
    public SqliteOrganisationStore(SqliteDatabase db, IReadOnlyList<ITenantCreationParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        _db = db;
        _participants = participants;
    }

    /// <param name="extraParticipants">The caller's own participants, run after the lifecycle's, in the same transaction.</param>
    public async Task<OrganisationCreateResult> CreateAsync(NewOrganisation o, IReadOnlyList<ITenantCreationParticipant> extraParticipants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extraParticipants);
        var directory = CustomerSignIn.CanonicalDirectory(o.AzureTenantId) ?? throw new ArgumentException("A directory id must be a GUID.", nameof(o));
        if (!AdminCommands.IsValidTenantId(o.Tenant.Id)) throw new ArgumentException("Not a valid tenant id.", nameof(o));
        var memberOid = o.FirstMember is { } first
            ? CustomerSignIn.CanonicalDirectory(first.ObjectId) ?? throw new ArgumentException("The member's object id must be a GUID.", nameof(o))
            : null;
        var at = Format(o.At);

        await using var c = await _db.OpenAsync(ct);
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;

        // 1. A directory already linked to any tenant is never linked again, and its link is not touched.
        cmd.CommandText = "SELECT COUNT(*) FROM sign_in_directories WHERE azure_tenant_id=@d";
        cmd.Parameters.AddWithValue("@d", directory);
        if ((long)(await cmd.ExecuteScalarAsync(ct))! > 0) return OrganisationCreateResult.DirectoryTaken;   // disposing the transaction rolls it back

        // 2. The tenant, then the lifecycle's creation participants.
        await SqliteTenantStore.InsertAsync(cmd, _participants, o.Tenant, o.At, ct);

        // 3. The caller's participants, after the lifecycle's, on the same transaction.
        foreach (var participant in extraParticipants) await participant.OnCreatingAsync(tx, o.Tenant, o.At, ct);

        // 4. The directory link. No ON CONFLICT: the primary key is the race backstop, and a failure here undoes steps 2 and 3.
        cmd.Parameters.Clear();
        cmd.CommandText = "INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES(@d,@t,@at,@by)";
        cmd.Parameters.AddWithValue("@d", directory);
        cmd.Parameters.AddWithValue("@t", o.Tenant.Id);
        cmd.Parameters.AddWithValue("@at", at);
        cmd.Parameters.AddWithValue("@by", o.BoundBy);
        await cmd.ExecuteNonQueryAsync(ct);

        // 5. The first member, in the new tenant.
        if (o.FirstMember is { } m)
        {
            cmd.Parameters.Clear();
            cmd.CommandText = """
                INSERT INTO members(azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at) VALUES(@d,@o,@t,@n,@s,@s)
                ON CONFLICT(azure_tenant_id, object_id) DO UPDATE SET tenant_id=excluded.tenant_id, display_name=excluded.display_name, last_seen_at=excluded.last_seen_at
                """;
            cmd.Parameters.AddWithValue("@d", directory);
            cmd.Parameters.AddWithValue("@o", memberOid);
            cmd.Parameters.AddWithValue("@t", o.Tenant.Id);
            cmd.Parameters.AddWithValue("@n", m.DisplayName);
            cmd.Parameters.AddWithValue("@s", at);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 6. The record, in the same transaction.
        if (o.Record is { } a)
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

        await tx.CommitAsync(ct);
        return OrganisationCreateResult.Created;
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
