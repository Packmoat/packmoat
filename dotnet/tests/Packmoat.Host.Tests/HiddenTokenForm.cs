using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Host.Customer;

namespace Packmoat.Host.Tests;

/// <summary>
/// The token pages hide their create form when the POST would refuse, and show the refusal's words in its place. A test of the
/// POST's own check still posts, as a stale or hand-made form would: with the page's antiforgery field (the sign-out form's) and a nonce
/// from the customer site's store, since the page offers none. The store keys a nonce by the person: directory and object id.
/// </summary>
public static class HiddenTokenForm
{
    /// <summary>The refusal shown in the create form's place.</summary>
    public const string BlockedMarker = "<div class=\"blocked\" role=\"status\">";

    public static string? PageNonce(string html) =>
        Regex.Match(html, "name=\"nonce\" value=\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>A nonce from the site's store for this person, as a form opened earlier would carry.</summary>
    public static Task<string> StoreNonceAsync(IServiceProvider services, string directory, string oid) =>
        services.GetRequiredService<AppFormNonces>().IssueAsync($"{directory}/{oid}", default);

    /// <summary>True when the page shows a refusal and no create form posting to <paramref name="action"/>.</summary>
    public static bool IsBlocked(string html, string action = "/tokens/create") =>
        html.Contains(BlockedMarker, StringComparison.Ordinal) && !html.Contains($"action=\"{action}\"", StringComparison.Ordinal);
}
