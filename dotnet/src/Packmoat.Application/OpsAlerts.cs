using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// How an alert names a tenant. A self-made id (<c>org-</c> and 16 random characters) is shown as it is: it says nothing about the
/// customer. An operator-made id may be a customer's name, so it is shown as <c>operator tenant #</c> and the first 8 hex characters of
/// SHA-256 of the id; a module's command may print the same text, so the operator can match it.
/// </summary>
public sealed record TenantRef
{
    public const string OperatorPrefix = "operator tenant #";
    private static readonly Regex SelfMadeId = new("^org-[a-z2-7]{16}$", RegexOptions.CultureInvariant);

    private TenantRef(string text) => Text = text;

    public string Text { get; }

    /// <summary>Verbatim only for a self-made tenant whose id has the self-made shape; every other id is hashed.</summary>
    public static TenantRef For(string tenantId, TenantOrigin origin) =>
        new(origin == TenantOrigin.Self && SelfMadeId.IsMatch(tenantId) ? tenantId : OperatorPrefix + Hash(tenantId));

    private static string Hash(string tenantId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)))[..8];

    public override string ToString() => Text;
}

/// <summary>
/// Something the operator should hear about at once (an open set). Every alert says itself in one line (<see cref="Text"/>); no
/// alert has a field that could hold a person's name, an email, a directory id, an organisation name or a card. Tenants are named by
/// <see cref="TenantRef"/> only. The core raises <see cref="PurgeDue"/> and <see cref="WaiverCapReached"/>; a module may add its own
/// alerts as further subtypes. A test checks the property types of every subtype.
/// </summary>
public abstract record OpsAlert
{
    protected OpsAlert() { }

    /// <summary>The alert's one line for the operator, without the sandbox prefix (<see cref="OpsAlertText.Format"/> adds it).</summary>
    public abstract string Text { get; }

    /// <summary>The daily retention pass found tenants due for purge. A count only: never a tenant, a name or a date.</summary>
    public sealed record PurgeDue(int Count) : OpsAlert
    {
        public override string Text =>
            $"Tenant purge: {Count} organisation{(Count == 1 ? " is" : "s are")} due for deletion. Run packmoat-admin tenant purge-due on the server.";
    }

    /// <summary>
    /// A tenant's Admins just made its <paramref name="Cap"/>th active customer waiver: raised by the insert that reaches the cap exactly,
    /// not by later refusals. A tenant reference and a count only: never a package, a reason or a name.
    /// </summary>
    public sealed record WaiverCapReached(TenantRef Tenant, int Cap) : OpsAlert
    {
        public override string Text =>
            $"Waiver cap reached: {Tenant} has {Cap} active customer waivers. Its Admins cannot add another until one is revoked or expires.";
    }
}

/// <summary>Where alerts are sent from: the payment mode decides the <c>[sandbox]</c> prefix.</summary>
public enum OpsAlertMode { Sandbox, Live }

/// <summary>
/// Sends alerts to the operator. <see cref="Notify"/> is synchronous, returns at once and never throws: an alert is best effort and must
/// never slow down or fail the work that raised it.
/// </summary>
public interface IOpsAlerts
{
    void Notify(OpsAlert alert);
}

/// <summary>No alerts: always in the admin tool (the operator is the one running it). The server's default is a warning in its log.</summary>
public sealed class NullOpsAlerts : IOpsAlerts
{
    public static readonly NullOpsAlerts Instance = new();
    private NullOpsAlerts() { }
    public void Notify(OpsAlert alert) { }
}

/// <summary>The text of an alert. Pure.</summary>
public static class OpsAlertText
{
    public const string SandboxPrefix = "[sandbox] ";
    private static readonly Regex ProviderId = new("^[A-Za-z0-9_]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>A provider id as it is when it has the shape of one (letters, digits, underscores); anything else is never shown.</summary>
    public static string SafeId(string? id) => id is not null && ProviderId.IsMatch(id) ? id : "(unreadable id)";

    /// <summary>One line for the operator. Sandbox alerts start with <see cref="SandboxPrefix"/>.</summary>
    public static string Format(OpsAlert alert, OpsAlertMode mode) => mode == OpsAlertMode.Live ? alert.Text : SandboxPrefix + alert.Text;

    /// <summary>A date as alerts show it: its UTC day, yyyy-MM-dd.</summary>
    public static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Raising an alert (from the core or a module): never lets a broken sink fail the work that raised it.</summary>
public static class OpsAlertsExtensions
{
    public static void Raise(this IOpsAlerts alerts, OpsAlert? alert)
    {
        if (alert is null) return;
        try { alerts.Notify(alert); }
        catch (Exception) { /* best effort by contract; the work that raised it already succeeded */ }
    }
}
