namespace Packmoat.Domain;

public enum Decision
{
    Allow,
    Hide,
}

public enum VerdictReason
{
    /// <summary>Younger than the cooldown.</summary>
    Cooldown,
    /// <summary>Old enough.</summary>
    CooldownElapsed,
    /// <summary>A waiver allows it.</summary>
    Waiver,
    /// <summary>Matches a tenant internal prefix: never served from the public feed.</summary>
    InternalNamespace,
    /// <summary>Publish date unknown: fail closed.</summary>
    DateUnknown,
    /// <summary>The publish-date source could not answer (an outage, not an unknown date): fail closed, but tell clients to retry.</summary>
    DateSourceUnavailable,
    /// <summary>No rule produced a decision: fail closed.</summary>
    NoPolicyDecided,
    /// <summary>A rule threw: fail closed.</summary>
    PolicyError,
}

/// <summary>
/// One decision about one version. <paramref name="CooldownDaysApplied"/> and <paramref name="PublishedAt"/> are what the cooldown rule
/// used (null when it did not decide, or when the publish date was unknown or never looked up), so a decision can be explained later.
/// </summary>
public sealed record Verdict(Decision Decision, VerdictReason Reason, string? WaiverId = null, int? CooldownDaysApplied = null, DateTimeOffset? PublishedAt = null)
{
    public bool IsAllowed => Decision == Decision.Allow;
}
