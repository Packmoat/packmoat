using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// A named lock: for a write that has no one row to lock with FOR UPDATE. Each is a transaction-level advisory
/// lock, held until its transaction ends.
/// </summary>
public enum PostgresLock
{
    /// <summary>Tenant creation: the check that an id is free, then the insert.</summary>
    TenantCreation = 1,

    /// <summary>The signed-in sites' cookie keys: the import.</summary>
    CookieKeys = 2,

    /// <summary>The shared app's two credential slots: the promote.</summary>
    SharedAppSlots = 3,

    /// <summary>The form nonces: an issue's trim of the key's oldest, then its insert, so no key goes over its cap.</summary>
    FormNonces = 4,
}

/// <summary>
/// The named advisory-lock keys. A named lock is the two-key form: <see cref="NamedLockKey"/>, then the lock's number
/// (<see cref="PostgresLock"/>). So it never meets the migrator's one-key lock (<c>PostgresDatabase.MigrationLockKey</c>) nor a tenant id's
/// lock (<see cref="PostgresCoreMigrations.TenantIdLockKey"/> with <c>hashtext(id)</c>). A number is never reused for another lock.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresLocks
{
    /// <summary>The first key of every named lock: "pmlk" in ASCII.</summary>
    public const int NamedLockKey = 0x706D6C6B;

    /// <summary>The SQL that takes <paramref name="name"/> for the rest of the transaction, waiting for it.</summary>
    public static string TakeSql(PostgresLock name) =>
        Enum.IsDefined(name) ? $"SELECT pg_catalog.pg_advisory_xact_lock({NamedLockKey}, {(int)name})"
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Not a named lock.");

    /// <summary>Takes <paramref name="name"/> in <paramref name="transaction"/>, waiting for it; it is let go when the transaction ends.</summary>
    public static async Task TakeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PostgresLock name, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(TakeSql(name), connection, transaction);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// The tenant-id locks a write takes for several tenants at once: <c>lock_tenant_id(id, false)</c>, the shared lock the schema's
/// dropped-row triggers take, for each distinct tenant, all of them before any row, and always in one order: <see cref="Comparer"/>,
/// ordinal. Writers that take their tenants' locks in one order never wait for each other in a circle, and one that waits for a tenant
/// the purge holds exclusively has written nothing yet. The audit batch and the decision-record chunks work their order out here, and the
/// purge takes its one tenant's lock exclusively here too (<see cref="TakeExclusiveAsync"/>), so every lock_tenant_id a store
/// takes is taken in this class and no caller sorts its tenants another way.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresTenantLocks
{
    /// <summary>The one order tenant locks are taken in: ordinal, so it never depends on a culture or on case rules.</summary>
    public static readonly StringComparer Comparer = StringComparer.Ordinal;

    /// <summary>The distinct tenant ids of <paramref name="tenantIds"/>, in the order their locks are taken (<see cref="Comparer"/>).</summary>
    public static string[] InLockOrder(IEnumerable<string> tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        return tenantIds.Distinct(Comparer).Order(Comparer).ToArray();
    }

    /// <summary>
    /// Takes <c>lock_tenant_id(id, false)</c> in <paramref name="transaction"/> for each of <paramref name="tenantsInLockOrder"/>, one after
    /// another, waiting for each; they are let go when the transaction ends. The ids must be as <see cref="InLockOrder"/> gives them: a list
    /// that is not distinct and in that order is refused before any lock is taken.
    /// </summary>
    public static async Task TakeSharedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<string> tenantsInLockOrder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantsInLockOrder);
        for (var i = 1; i < tenantsInLockOrder.Count; i++)
            if (Comparer.Compare(tenantsInLockOrder[i - 1], tenantsInLockOrder[i]) >= 0)
                throw new ArgumentException("The tenants are not distinct and in lock order: take them from PostgresTenantLocks.InLockOrder.",
                    nameof(tenantsInLockOrder));
        await using var take = new NpgsqlCommand("SELECT lock_tenant_id(@id, false)", connection, transaction);
        var id = take.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Text);
        foreach (var tenant in tenantsInLockOrder)
        {
            id.Value = tenant;
            await take.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Takes <c>lock_tenant_id(id, true)</c>, <paramref name="tenantId"/>'s lock exclusively, in <paramref name="transaction"/>, waiting
    /// for it; it is let go when the transaction ends. The purge's, taken before anything else it does: one tenant, so no order.
    /// </summary>
    public static async Task TakeExclusiveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        await using var take = new NpgsqlCommand("SELECT lock_tenant_id(@id, true)", connection, transaction);
        take.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Text, tenantId);
        await take.ExecuteNonQueryAsync(cancellationToken);
    }
}
