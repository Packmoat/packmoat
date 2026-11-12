using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The one order tenant locks are taken in: <see cref="PostgresTenantLocks"/>, which the audit batch and the
/// decision-record chunks both use. No server needed; the race tests that show the order on a server are
/// <see cref="PostgresAuditRetentionStoreTests"/>' and <see cref="PostgresDecisionRecordStoreTests"/>'.
/// </summary>
public class PostgresTenantLocksTests
{
    [Fact]   // mutations: the helper's comparer a culture's or case-blind; the duplicates kept
    public void The_tenants_are_distinct_and_in_ordinal_order()
    {
        Assert.Same(StringComparer.Ordinal, PostgresTenantLocks.Comparer);
        Assert.Equal(new[] { "Zed", "acme", "globex", "Émile" }, PostgresTenantLocks.InLockOrder(new[] { "globex", "acme", "Émile", "Zed", "acme" }));
        Assert.Empty(PostgresTenantLocks.InLockOrder(Array.Empty<string>()));
    }

    /// <summary>A list that is not as InLockOrder gives it is refused before any lock is taken: no connection is even touched.</summary>
    [Fact]   // mutation: the take does not check the order it is given
    public async Task A_list_out_of_lock_order_or_with_a_repeat_is_refused_before_any_lock()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PostgresTenantLocks.TakeSharedAsync(null!, null!, new[] { "acme", "Zed" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => PostgresTenantLocks.TakeSharedAsync(null!, null!, new[] { "Zed", "acme", "acme" }, default));
    }
}
