using Npgsql;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The core's one way to make an organisation on PostgreSQL, as SQLite's organisation store does it, with its SQL written
/// again for PostgreSQL: the tenant, its directory link, the first member and the record in one transaction, or nothing. Where SQLite's
/// BEGIN IMMEDIATE takes the write lock before the first read, this takes the <see cref="PostgresLock.TenantCreation"/> lock first, before
/// anything is read, so two creations for one directory run one after the other and the second sees the first's link and gets
/// <see cref="OrganisationCreateResult.DirectoryTaken"/>. It is the lock <see cref="PostgresTenantStore"/>'s tenant insert takes first too
/// (taken again there, it is already held), so no lock is held before it, as that insert requires. In order:
/// <list type="number">
/// <item>the <see cref="PostgresLock.TenantCreation"/> lock;</item>
/// <item>a directory already linked to any tenant: DirectoryTaken, nothing written;</item>
/// <item>the tenant row, which runs the lifecycle's creation participants;</item>
/// <item>the caller's extra participants, in order (any may veto with <see cref="TenantCreationRefusedException"/>);</item>
/// <item>the link, with no ON CONFLICT: the sign_in_directories primary key is the backstop (the operator's bind takes no creation lock),
/// and its failure undoes everything. That failure (23505 on sign_in_directories_pkey, and only that) is then answered DirectoryTaken, as
/// step 2 would have (<see cref="PostgresDirectoryRace"/>);</item>
/// <item>the first member, when given (an older row of that person in another tenant is moved here, as a sign-in would);</item>
/// <item>the record, when given: no record, no organisation;</item>
/// <item>commit.</item>
/// </list>
/// No catch inside the transaction, no SAVEPOINT: a veto or any failure rolls the whole transaction back and throws. Times are timestamptz, in UTC to the
/// microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresOrganisationStore : IOrganisationStore
{
    private readonly PostgresDatabase _db;
    private readonly IReadOnlyList<ITenantCreationParticipant> _participants;

    /// <param name="participants">The lifecycle's creation participants (<see cref="TenantLifecycle.Participants"/>, empty for none).</param>
    public PostgresOrganisationStore(PostgresDatabase db, IReadOnlyList<ITenantCreationParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(participants);
        _db = db;
        _participants = participants;
    }

    /// <param name="extraParticipants">The caller's own participants, run after the lifecycle's, in the same transaction.</param>
    public async Task<OrganisationCreateResult> CreateAsync(NewOrganisation o, IReadOnlyList<ITenantCreationParticipant> extraParticipants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(extraParticipants);
        var directory = CustomerSignIn.CanonicalDirectory(o.AzureTenantId) ?? throw new ArgumentException("A directory id must be a GUID.", nameof(o));
        if (!AdminCommands.IsValidTenantId(o.Tenant.Id)) throw new ArgumentException("Not a valid tenant id.", nameof(o));
        var memberOid = o.FirstMember is { } first
            ? CustomerSignIn.CanonicalDirectory(first.ObjectId) ?? throw new ArgumentException("The member's object id must be a GUID.", nameof(o))
            : null;
        // The operator's bind of this directory, committed after step 2 below and met at the link's insert, is DirectoryTaken, as step 2's
        // own check would have said: nothing was written.
        return await PostgresDirectoryRace.TakenOnConflictAsync(PostgresDirectoryRace.SignInDirectory, OrganisationCreateResult.DirectoryTaken,
            () => CreateOnceAsync(o, extraParticipants, directory, memberOid, ct));
    }

    /// <summary>The creation's one transaction (see <see cref="PostgresOrganisationStore"/>). Any failure rolls it back and is thrown.</summary>
    private async Task<OrganisationCreateResult> CreateOnceAsync(NewOrganisation o, IReadOnlyList<ITenantCreationParticipant> extraParticipants, string directory,
        string? memberOid, CancellationToken ct)
    {
        var at = PostgresTime.ToDatabase(o.At);

        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        // 1. The creation lock, before the first read: another creation for this directory waits here, then sees this one's link.
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.TenantCreation, ct);

        // 2. A directory already linked to any tenant is never linked again, and its link is not touched.
        await using (var linked = new NpgsqlCommand("SELECT COUNT(*) FROM sign_in_directories WHERE azure_tenant_id=@d", c, tx))
        {
            linked.Parameters.AddWithValue("d", directory);
            if ((long)(await linked.ExecuteScalarAsync(ct))! > 0) return OrganisationCreateResult.DirectoryTaken;   // disposing the transaction rolls it back
        }

        // 3. The tenant, then the lifecycle's creation participants.
        await PostgresTenantStore.InsertAsync(c, tx, _participants, o.Tenant, o.At, ct);

        // 4. The caller's participants, after the lifecycle's, on the same transaction.
        foreach (var participant in extraParticipants) await participant.OnCreatingAsync(tx, o.Tenant, o.At, ct);

        // 5. The directory link. No ON CONFLICT: the primary key is the backstop, and a failure here undoes steps 3 and 4.
        await using (var link = new NpgsqlCommand("INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES(@d,@t,@at,@by)", c, tx))
        {
            link.Parameters.AddWithValue("d", directory);
            link.Parameters.AddWithValue("t", o.Tenant.Id);
            link.Parameters.AddWithValue("at", at);
            link.Parameters.AddWithValue("by", o.BoundBy);
            await link.ExecuteNonQueryAsync(ct);
        }

        // 6. The first member, in the new tenant.
        if (o.FirstMember is { } m)
        {
            await using var member = new NpgsqlCommand(
                """
                INSERT INTO members(azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at) VALUES(@d,@o,@t,@n,@s,@s)
                ON CONFLICT(azure_tenant_id, object_id) DO UPDATE SET tenant_id=excluded.tenant_id, display_name=excluded.display_name, last_seen_at=excluded.last_seen_at
                """, c, tx);
            member.Parameters.AddWithValue("d", directory);
            member.Parameters.AddWithValue("o", memberOid!);
            member.Parameters.AddWithValue("t", o.Tenant.Id);
            member.Parameters.AddWithValue("n", m.DisplayName);
            member.Parameters.AddWithValue("s", at);
            await member.ExecuteNonQueryAsync(ct);
        }

        // 7. The record, in the same transaction.
        if (o.Record is { } a)
        {
            await using var record = new NpgsqlCommand(
                "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site) VALUES(@t,@tid,@oid,@n,@a,@tn,@dt,@s)", c, tx);
            record.Parameters.AddWithValue("t", PostgresTime.ToDatabase(a.Time));
            record.Parameters.AddWithValue("tid", a.ActorTid);
            record.Parameters.AddWithValue("oid", a.ActorOid);
            record.Parameters.AddWithValue("n", a.ActorName);
            record.Parameters.AddWithValue("a", a.Action);
            record.Parameters.AddWithValue("tn", a.TenantId);
            record.Parameters.AddWithValue("dt", a.Detail);
            record.Parameters.AddWithValue("s", a.Site);
            await record.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return OrganisationCreateResult.Created;
    }
}
