using System.Globalization;
using System.Text.RegularExpressions;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// The rules for waivers a customer Admin makes (#157). The operator's waivers keep the operator's own limits
/// (<see cref="AdminCommands.MinWaiverDays"/> to <see cref="AdminCommands.MaxWaiverDays"/>, wildcards allowed).
/// </summary>
public static class WaiverRules
{
    /// <summary>Active (not revoked, not expired) customer waivers one tenant may hold. Operator waivers do not count.</summary>
    public const int MaxActiveCustomerWaivers = 25;
    public const int DefaultCustomerDays = 7;
    public const int MinCustomerDays = 1;
    public const int MaxCustomerDays = 30;
    public const int MinReason = 10;
    public const int MaxReason = 200;
    public const int MaxPackageId = 100;
    public const int MaxVersion = 64;

    private static readonly Regex PackageId = new(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex ExactVersion = new(
        @"^[0-9]+(\.[0-9]+){1,3}(-[0-9a-z-]+(\.[0-9a-z-]+)*)?(\+[0-9a-z-]+(\.[0-9a-z-]+)*)?$", RegexOptions.CultureInvariant);

    /// <summary>The package id trimmed and lower-cased, or null unless it is letters, digits, '.', '-' and '_' (starting with a letter or digit), at most <see cref="MaxPackageId"/>.</summary>
    public static string? CleanPackageId(string? packageId)
    {
        var t = (packageId ?? "").Trim().ToLowerInvariant();
        return t.Length is > 0 and <= MaxPackageId && PackageId.IsMatch(t) ? t : null;
    }

    /// <summary>
    /// One exact NuGet version, normalised as the policy engine compares it (<see cref="NuGetVersionKey.Normalize"/>: "1.0" is "1.0.0",
    /// build metadata dropped, lower case), or null. Two to four numeric parts, an optional pre-release and build part; never '*' or a range.
    /// </summary>
    public static string? CleanExactVersion(string? version)
    {
        var t = (version ?? "").Trim().ToLowerInvariant();
        if (t.Length is 0 or > MaxVersion || t.Contains('*') || !ExactVersion.IsMatch(t)) return null;
        return NuGetVersionKey.Normalize(t);
    }

    /// <summary>The reason trimmed, or null unless it is <see cref="MinReason"/> to <see cref="MaxReason"/> characters with no control or invisible characters.</summary>
    public static string? CleanReason(string? reason)
    {
        var t = (reason ?? "").Trim();
        if (t.Length is < MinReason or > MaxReason) return null;
        foreach (var c in t)
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return null;
        return t;
    }
}

/// <summary>Why a waiver was refused. Each has a fixed message: nothing a person typed is echoed.</summary>
public enum WaiverRefusal
{
    /// <summary>Not a package id.</summary>
    BadPackage,
    /// <summary>Not one exact version (a wildcard, a range, or not a version).</summary>
    BadVersion,
    /// <summary>Days outside <see cref="WaiverRules.MinCustomerDays"/> to <see cref="WaiverRules.MaxCustomerDays"/>.</summary>
    BadExpiry,
    /// <summary>Reason too short, too long, or with control or invisible characters.</summary>
    BadReason,
    /// <summary>The package is under one of the tenant's internal prefixes: the engine hides it whatever a waiver says.</summary>
    InternalPrefix,
    /// <summary>An active waiver for this package and version already exists in the tenant.</summary>
    Duplicate,
    /// <summary>The tenant already holds <see cref="WaiverRules.MaxActiveCustomerWaivers"/> active customer waivers.</summary>
    TooManyWaivers,
}

/// <summary>A refused waiver. An <see cref="ArgumentException"/>, so callers that already report bad input report this too.</summary>
public sealed class WaiverRefusedException : ArgumentException
{
    public WaiverRefusedException(WaiverRefusal reason) : base(MessageFor(reason)) => Reason = reason;

    public WaiverRefusal Reason { get; }

    private static string MessageFor(WaiverRefusal reason) => reason switch
    {
        WaiverRefusal.BadPackage => "The package id may use letters, digits, '.', '-' and '_' only (up to 100 characters).",
        WaiverRefusal.BadVersion => "The version must be one exact version, such as 1.2.3 or 1.2.3-beta.1. Wildcards are not allowed.",
        WaiverRefusal.BadExpiry => $"A waiver lasts from {WaiverRules.MinCustomerDays} to {WaiverRules.MaxCustomerDays} days.",
        WaiverRefusal.BadReason => $"A reason of {WaiverRules.MinReason} to {WaiverRules.MaxReason} characters is required, with no line breaks.",
        WaiverRefusal.InternalPrefix => "That package is under one of your internal prefixes, so it is never served from the public feed and cannot be waived.",
        WaiverRefusal.Duplicate => "That package and version already has an active waiver.",
        WaiverRefusal.TooManyWaivers => $"Your organisation already has {WaiverRules.MaxActiveCustomerWaivers} active waivers. Revoke one first.",
        _ => "The waiver was refused.",
    };
}

/// <summary>One waiver as stored, active or not, for the waiver list and its history. <c>RevokedBy</c> is null for rows revoked before migration 20.</summary>
public sealed record WaiverRecord(
    string Id, string PackageId, string Version, DateTimeOffset ExpiresAt, string Reason, string CreatedBy, DateTimeOffset CreatedAt,
    WaiverSource Source, DateTimeOffset? RevokedAt, string? RevokedBy)
{
    /// <summary>Not revoked and expiring after <paramref name="now"/> (an expiry equal to now has ended, as in the policy engine).</summary>
    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

public enum WaiverInsertOutcome { Added, Duplicate, CapReached }

/// <summary>
/// What a customer waiver insert did. <c>ActiveCustomerWaivers</c> is the tenant's count of active customer waivers after it (including the
/// new one when added); <c>Origin</c> is read in the same transaction, for naming the tenant in an alert.
/// </summary>
public sealed record CustomerWaiverInsert(WaiverInsertOutcome Outcome, Waiver? Waiver, int ActiveCustomerWaivers, TenantOrigin Origin);
