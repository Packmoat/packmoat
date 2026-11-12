using Microsoft.AspNetCore.Authentication;
using Packmoat.Application;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Modules;

/// <summary>
/// What a module adds to the customer site: its own pages for members and for prospects, a banner and links on the home page, the
/// Connect sentence, and what a directory that is not on this server is told. At most one is registered (more refuses the start); with
/// none, the core registers <see cref="NoCustomerSiteModule"/>, which adds nothing. A page whose path does not start with exactly one "/", or
/// prospect pages with no GET page among them, refuse the start too.
/// <para>The core owns every check. A module's route is matched by path and method; then <see cref="CustomerRoute{T}.Exists"/> (false: the
/// site's 404, before anything else); then, for a POST, the Origin (403); then <see cref="CustomerRoute{T}.Handle"/>. Every POST has passed
/// the antiforgery gate before any routing. A handler works only through <see cref="CustomerPage{T}"/>.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface ICustomerSiteModule
{
    /// <summary>
    /// True when a person of this directory, which is not on this server, may set up an organisation for it. Asked at sign-in (no prospect
    /// session is made otherwise) and on every request (a prospect session whose directory it no longer offers is not a session).
    /// </summary>
    bool OffersSetup(string directoryId);

    /// <summary>
    /// The card for a person whose directory is not on this server. <paramref name="needsAdmin"/>: <see cref="OffersSetup"/> said yes, but
    /// this person lacks the Admin role.
    /// </summary>
    NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin);

    /// <summary>Pages for a signed-in member, tried after the core's own pages and before the Azure setup wizard's.</summary>
    IReadOnlyList<CustomerRoute<CustomerActor>> MemberRoutes { get; }

    /// <summary>
    /// The only pages a prospect reaches, tried before the member check. Any other address sends the prospect to the first GET route here.
    /// With none, no prospect session is made, whatever <see cref="OffersSetup"/> says.
    /// </summary>
    IReadOnlyList<CustomerRoute<Prospect>> ProspectRoutes { get; }

    /// <summary>Trusted markup at the top of the home page (fixed text and encoded values only), or "".</summary>
    string HomeBanner(CustomerActor actor, CustomerHome view);

    /// <summary>Trusted markup appended to the home page's links (fixed links only), or "".</summary>
    string HomeLinks(CustomerActor actor, CustomerHome view);

    /// <summary>The sentence above the Azure setup wizard's Connect button (plain text, encoded by the core).</summary>
    string ConnectSentence(CustomerActor actor);

    /// <summary>
    /// Trusted markup appended to the footer's links (fixed links only), or "". Shown only when the operator's website is set
    /// (<paramref name="op"/>): the footer's links are the website's.
    /// </summary>
    string FooterLinks(OperatorLinks op) => "";

    /// <summary>
    /// The token page's words when the serving gate refuses the tenant, from the gate's <paramref name="notice"/> (null: the gate gave
    /// none), or null for the core's own neutral words. Plain text, encoded by the core.
    /// </summary>
    ServingRefusedWords? ServingRefused(ServingNotice? notice, SupportContact support) => null;

    /// <summary>
    /// A sentence the suspended page (and the token page's suspended refusal) adds after the core's "what to do next", or null for
    /// none. Plain text, encoded by the core.
    /// </summary>
    string? SuspendedNote => null;
}

/// <summary>A refusal page's words: its title, what happened and what to do next (plain text, encoded by the core).</summary>
public sealed record ServingRefusedWords(string Title, string Message, string Next);

/// <summary>The not-linked card: the page title, and the card's trusted markup (fixed text and encoded values only).</summary>
public sealed record NotLinkedCard(string Title, string Html);

/// <summary>
/// One page of a module. <paramref name="Path"/> is matched exactly (case-sensitive) and <paramref name="IsPost"/> picks the method.
/// <paramref name="Exists"/> says whether the page exists for this person now (false: the site's ordinary 404).
/// </summary>
public sealed record CustomerRoute<T>(string Path, bool IsPost, Func<T, bool> Exists, Func<CustomerPage<T>, Task> Handle) where T : class;

/// <summary>
/// A module's only surface on the customer site: the request, the person (a member or a prospect), and what a page may do. The core
/// keeps the session, the checks and the CSP; a page may only widen its forms' targets to named https origins, and may only send the
/// person to a fresh sign-in that returns to one of its module's own GET pages.
/// </summary>
public sealed class CustomerPage<T> where T : class
{
    private readonly IReadOnlySet<string> _returnPaths;
    private readonly bool _prospect;

    internal CustomerPage(HttpContext http, T who, IReadOnlySet<string> returnPaths, bool prospect)
    {
        Http = http;
        Who = who;
        _returnPaths = returnPaths;
        _prospect = prospect;
    }

    /// <summary>The request. Its form and query are read by the module's handler; the person comes only from <see cref="Who"/>.</summary>
    public HttpContext Http { get; }

    /// <summary>The signed-in member, or the prospect, as the core resolved them on this request.</summary>
    public T Who { get; }

    /// <summary>Who runs this server: the website, the support address and the operator's name, each possibly unset.</summary>
    public OperatorLinks Operator => AppHtml.Op(Http);

    /// <summary>The antiforgery hidden field, for a form that posts to this site.</summary>
    public string AntiforgeryField() => AppHtml.Hidden(SignedInSite.AntiforgeryField, SignedInSite.AntiforgeryToken(Http));

    /// <summary>A single-use form nonce for <paramref name="key"/> (the customer site's own scope).</summary>
    public Task<string> IssueNonceAsync(string key) => Http.RequestServices.GetRequiredService<AppFormNonces>().IssueAsync(key, Http.RequestAborted);

    /// <summary>True once for a live nonce issued for <paramref name="key"/>.</summary>
    public Task<bool> ConsumeNonceAsync(string? nonce, string key) => Http.RequestServices.GetRequiredService<AppFormNonces>().ConsumeAsync(nonce, key, Http.RequestAborted);

    /// <summary>
    /// A fresh Microsoft sign-in (prompt=login) that comes back to <paramref name="returnPath"/>, which must be one of this module's own
    /// GET pages for the same kind of person. <paramref name="confirm"/> (a prospect's confirmed sign-in) only on a prospect page.
    /// </summary>
    public Task Reauth(string returnPath, bool confirm = false)
    {
        if (!_returnPaths.Contains(returnPath))
            throw new InvalidOperationException("A module's fresh sign-in may return only to one of its own GET pages.");
        if (confirm && !_prospect) throw new InvalidOperationException("Only a prospect's page may ask for a confirmed sign-in.");
        return AppSite.ReauthAsync(Http, returnPath, confirm);
    }

    /// <summary>Ends the session here (not at Microsoft).</summary>
    public Task SignOutAsync() => Http.SignOutAsync(AppSite.CookieScheme);

    /// <summary>A normal sign-in that lands on the home page (after <see cref="SignOutAsync"/>, a new session is decided from scratch).</summary>
    public Task SignInAsync() => AppSite.StartSignInAsync(Http, "/home", automatic: false);

    /// <summary>A page in the site's chrome, with the sign-out button unless <paramref name="signOut"/> is false. The body is trusted markup.</summary>
    public Task Write(int status, string title, string bodyHtml, bool signOut = true) =>
        SignedInSite.Write(Http, status, AppHtml.Page(Http, title, bodyHtml, signOut ? SignedInSite.SignOutForm(Http) : null));

    /// <summary>The site's error page (plain text, encoded here).</summary>
    public Task ErrorPage(int status, string title, string message, string? next = null) => SignedInSite.ErrorPage(Http, status, title, message, next: next);

    /// <summary>
    /// Lets this page's forms lead to <paramref name="httpsOrigins"/> (each exactly https://host, nothing more) as well as this site and
    /// Microsoft's sign-in. With none, the forms may lead to this site and Microsoft's sign-in only.
    /// </summary>
    public void AllowFormTargets(params string[] httpsOrigins) => SignedInSite.UseFormTargetsCsp(Http, httpsOrigins);
}
