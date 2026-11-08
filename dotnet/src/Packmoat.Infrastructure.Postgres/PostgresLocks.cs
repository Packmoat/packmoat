using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// A named lock (#303 decision "Locks"): for a write that has no one row to lock with FOR UPDATE. Each is a transaction-level advisory
/// lock, held until its transaction ends.
/// </summary>
public enum PostgresLock
{
    /// <summary>Tenant creation: the check that an id is free, then the insert (PR 1).</summary>
    TenantCreation = 1,

    /// <summary>The signed-in sites' cookie keys: the import (PR 2).</summary>
    CookieKeys = 2,

    /// <summary>The shared app's two credential slots: the promote (PR 5).</summary>
    SharedAppSlots = 3,
}

/// <summary>
/// The named advisory-lock keys (#303). A named lock is the two-key form: <see cref="NamedLockKey"/>, then the lock's number
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
