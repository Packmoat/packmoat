using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>
/// A tenant purge store that also names, for the operator's summary after a purge, what a purge keeps and what it never reaches. Every engine's purge store gives it, so the command line prints the summary through this port, not an adapter type.
/// </summary>
public interface IPurgeSummaryStore : ITenantPurgeStore
{
    /// <summary>The tables a purge keeps: the operator's evidence first, the contributors', the purge record last.</summary>
    IReadOnlyList<string> Kept { get; }

    /// <summary>What the purge never reaches: the core's, then each contributor's.</summary>
    IReadOnlyList<string> NotReached { get; }
}
