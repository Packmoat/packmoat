using Packmoat.Domain;

namespace Packmoat.Policy;

/// <summary>
/// A version is hidden while it is younger than the cooldown. A version exactly as old as the cooldown is allowed.
/// A per-package override replaces the tenant default. An unknown publish date hides the version (fail closed),
/// unless the effective cooldown is 0. Every verdict reports the cooldown and the publish date it used.
/// </summary>
public sealed class CooldownPolicy : IPolicy
{
    public PolicyOutcome Decide(PolicyContext context)
    {
        var days = EffectiveCooldownDays(context);
        var published = context.Facts.PublishedAt;   // reported as given (null when unknown), so the record shows the date that was used
        if (days == 0)
            return PolicyOutcome.Decide(new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: 0, PublishedAt: published));

        if (published is not { } date)
            return PolicyOutcome.Decide(new Verdict(Decision.Hide, VerdictReason.DateUnknown, CooldownDaysApplied: days));

        var age = context.Now - date;
        return age < TimeSpan.FromDays(days)
            ? PolicyOutcome.Decide(new Verdict(Decision.Hide, VerdictReason.Cooldown, CooldownDaysApplied: days, PublishedAt: date))
            : PolicyOutcome.Decide(new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: days, PublishedAt: date));
    }

    private static int EffectiveCooldownDays(PolicyContext context)
    {
        var id = context.Facts.NormalizedId;
        foreach (var o in context.Tenant.PackageOverrides)
            if (string.Equals(o.PackageId.Trim(), id, StringComparison.OrdinalIgnoreCase))
                return o.CooldownDays;
        return context.Tenant.CooldownDays;
    }
}
