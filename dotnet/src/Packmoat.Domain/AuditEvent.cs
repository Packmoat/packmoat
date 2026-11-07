namespace Packmoat.Domain;

public enum AuditKind
{
    /// <summary>A nupkg or nuspec was requested.</summary>
    Download,
    /// <summary>A version was removed from a version list or registration.</summary>
    Listing,
    /// <summary>A registration leaf was requested.</summary>
    Metadata,
}

/// <summary>One decision. Holds ids and names only: never a token, header, credential or query string.</summary>
public sealed record AuditEvent(
    DateTimeOffset Time,
    string RequestId,
    string TenantId,
    string TokenId,
    AuditKind Kind,
    string PackageId,
    string Version,
    Decision Decision,
    VerdictReason Reason,
    string? WaiverId);
