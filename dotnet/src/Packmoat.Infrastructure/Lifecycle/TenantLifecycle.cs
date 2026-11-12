using System.Data.Common;
using System.Text.RegularExpressions;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Lifecycle;

/// <summary>
/// Takes part in making a tenant: runs after the tenant row is inserted, inside the same transaction (for example a module writes
/// its own row for the tenant). To veto, it throws <see cref="TenantCreationRefusedException"/>: the transaction rolls back and no tenant
/// row is left. Any other exception rolls back too.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface ITenantCreationParticipant
{
    Task OnCreatingAsync(DbTransaction transaction, NewTenant tenant, DateTimeOffset createdAt, CancellationToken cancellationToken);
}

/// <summary>
/// A creation participant refused the new tenant. Nothing was written. The message names the reason, never a secret; <see cref="Code"/>
/// is the participant's own short name for it, so its caller can tell its refusals apart. The core gives it no meaning.
/// </summary>
public sealed class TenantCreationRefusedException : InvalidOperationException
{
    public TenantCreationRefusedException(string message, string? code = null) : base(message) => Code = code;

    /// <summary>The participant's short name for the refusal, or null.</summary>
    public string? Code { get; }
}

/// <summary>What a purge does to one table: deletes the tenant's rows, keeps them on purpose, or the table holds no tenant's data.</summary>
public enum PurgeTableFate
{
    /// <summary>The purged tenant's rows are deleted. After the purge none may be left (the purge checks, by the tenant column).</summary>
    Deleted,
    /// <summary>Kept on purpose, with the reason given.</summary>
    Kept,
    /// <summary>Holds no tenant's data.</summary>
    NoTenantData,
}

/// <summary>
/// One table's classification for the purge, with the reason. <paramref name="TenantColumn"/> names the tenant's rows (for a deleted
/// table it is required: the purge counts by it afterwards and refuses unless the count is 0).
/// </summary>
public sealed record PurgeTable(string Table, PurgeTableFate Fate, string Why, string? TenantColumn = null)
{
    private static readonly Regex Identifier = new("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>A plain lower-case SQL identifier: what a table or column name may be here (it is put into SQL as it is).</summary>
    public static bool IsIdentifier(string? name) => name is not null && Identifier.IsMatch(name);
}

/// <summary>
/// Deletes its own tables' rows for a purged tenant, inside the purge's one transaction, before the tenant row is deleted.
/// Every table in the database must be classified by the core or by exactly one contributor, or the purge is refused.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IPurgeContributor
{
    /// <summary>Each of its tables: deleted (by its tenant column), kept, or holding no tenant data, with the reason.</summary>
    IReadOnlyList<PurgeTable> Tables { get; }

    /// <summary>What it holds that the purge does not reach (for the operator's summary), for example a payment provider's own records.</summary>
    IReadOnlyList<string> NotReached { get; }

    /// <summary>Deletes the tenant's rows in its deleted tables, on <paramref name="transaction"/>. Returns the rows deleted per table.</summary>
    Task<IReadOnlyList<PurgedTable>> DeleteAsync(DbTransaction transaction, string tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The purge eligibility that can also re-check inside the purge, where no other writer can change it: one object decides who is due and checks again
/// inside the delete's transaction, so the two cannot disagree.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IPurgeRecheck : IPurgeEligibility
{
    /// <summary>
    /// Inside the purge's transaction: true only when <paramref name="candidate"/> is still due as decided, at <paramref name="now"/>. False
    /// refuses the purge and nothing is deleted. Anything it reads that the purge's own locks (the tenant's row and its tokens) do not
    /// already hold, it must lock first on the purge's transaction, so no other writer can change it between the re-check and the purge's
    /// commit.
    /// </summary>
    Task<bool> StillDueAsync(DbTransaction transaction, PurgeCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// What the re-check reads, in the operator's words, for the refusal when it says no: "changed while it was being checked
    /// (&lt;this&gt;)". Null: the refusal names nothing.
    /// </summary>
    string? Rechecked => null;
}

/// <summary>
/// The tenant lifecycle's parts a module gives: creation participants, purge contributors, and the purge eligibility (made when
/// it is needed, so its settings are read only then). The core has none (<see cref="None"/>): a new tenant gets only its own row, and
/// nobody is ever purged.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed record TenantLifecycle(
    IReadOnlyList<ITenantCreationParticipant> Participants,
    IReadOnlyList<IPurgeContributor> Contributors,
    Func<IPurgeRecheck>? Eligibility)
{
    public static readonly TenantLifecycle None = new(Array.Empty<ITenantCreationParticipant>(), Array.Empty<IPurgeContributor>(), null);
}

/// <summary>
/// How a server module gives its <see cref="TenantLifecycle"/>: made by the composition root for its database, so the module itself
/// never names the database. At most one may be registered.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed record TenantLifecycleFactory(Func<IRelationalDatabase, TenantLifecycle> For);
