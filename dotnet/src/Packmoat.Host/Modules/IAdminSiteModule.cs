using System.Security.Claims;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using Packmoat.Host.Web;

namespace Packmoat.Host.Modules;

/// <summary>
/// What a module adds to the operator admin site (#193): a section of the tenant page, write actions on a tenant, and the canned
/// notices those actions redirect with. At most one is registered (more refuses the start); with none, the core registers
/// <see cref="NoAdminSiteModule"/>, which adds nothing. A verb or notice key the core already uses, or one listed twice, refuses the start.
/// <para>The core owns every check. A module action is a POST to <c>/tenants/{id}/{verb}</c>; in order: the antiforgery token (400), a
/// signed-in person still on the allowlist (401), the verb (an unknown one is the site's 404), the Origin (403), the write limit (429),
/// reading the form, the tenant (an unknown one redirects with "no-tenant"), the confirm page when the action has
/// <see cref="AdminTenantAction.ConfirmText"/>, and only then <see cref="AdminTenantAction.Handle"/>. A handler works only through
/// <see cref="AdminTenantPage"/>.</para>
/// <para>No record, no change: a handler writes its admin_actions row before its change. When that write fails it throws
/// <see cref="AdminTrailException"/> (the guard <see cref="AdminTrail.Guard"/> does this for any log), and the core answers "trail-failed".</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IAdminSiteModule
{
    /// <summary>
    /// Trusted markup for the tenant page, after the upstream identity line and before the core's Tenant section (fixed text, encoded
    /// values and forms made with <see cref="AdminTenantPage.PostForm"/> only). <paramref name="serving"/> is the serving gate's answer for
    /// this tenant now; its <see cref="ServingAnswer.OperatorText"/> is the module's to show.
    /// </summary>
    Task<string> TenantSectionAsync(AdminTenantPage page, ServingAnswer serving);

    /// <summary>The module's write actions on a tenant.</summary>
    IReadOnlyList<AdminTenantAction> Actions { get; }

    /// <summary>The canned notices the module's actions redirect with, by key: (true for a success, the plain text, encoded by the core).</summary>
    IReadOnlyDictionary<string, (bool Ok, string Text)> Notices { get; }
}

/// <summary>
/// One write action of a module, posted to <c>/tenants/{id}/{verb}</c>. <paramref name="ConfirmText"/>, when given, makes the core ask
/// first: "You are about to {text}." (plain text, encoded by the core), re-posting the form with confirm=yes. <paramref name="Handle"/> runs
/// only after every check, and answers through the page (usually <see cref="AdminTenantPage.Redirect"/>).
/// </summary>
public sealed record AdminTenantAction(string Verb, Func<AdminTenantPage, string>? ConfirmText, Func<AdminTenantPage, Task> Handle);

/// <summary>Who is acting: the operator scope named after the signed-in person, and their directory (tid) and object (oid) ids.</summary>
public sealed record AdminWho(ActorScope Scope, string DirectoryId, string ObjectId);

/// <summary>
/// A module's only surface on the admin site: the tenant, the signed-in operator, the form the core read once, and what a page may do.
/// The core keeps the session, the checks and the trail of its own actions.
/// </summary>
public sealed class AdminTenantPage
{
    private readonly HttpContext _http;
    private readonly IFormCollection? _form;
    private readonly string? _antiforgery;
    private readonly IAdminSiteModule _module;

    internal AdminTenantPage(HttpContext http, ClaimsPrincipal who, Tenant tenant, IFormCollection? form, IAdminSiteModule module, string? antiforgery = null)
    {
        _http = http;
        _form = form;
        _module = module;
        _antiforgery = antiforgery;
        Tenant = tenant;
        Who = new AdminWho(AdminWrites.ActorFor(who), who.FindFirst(AdminAllowlist.TenantClaim)?.Value ?? "", who.FindFirst(AdminAllowlist.ObjectClaim)?.Value ?? "");
    }

    /// <summary>The tenant, as the core looked it up on this request (its upstream identity is never read).</summary>
    public Tenant Tenant { get; }

    /// <summary>The signed-in operator, from the session only (never a form field).</summary>
    public AdminWho Who { get; }

    /// <summary>The server's services, for the module's own commands.</summary>
    public IServiceProvider Services => _http.RequestServices;

    /// <summary>Cancelled when the browser goes away. For reads only: a change runs to the end once its record is written.</summary>
    public CancellationToken RequestAborted => _http.RequestAborted;

    /// <summary>A field of the posted form (read once by the core), or null when it is missing or sent more than once. Null on the tenant page.</summary>
    public string? Form(string name) => _form is not null && _form.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;

    /// <summary>The antiforgery hidden field, for a form that posts to this site.</summary>
    public string AntiforgeryField() => AdminHtml.Hidden(AdminSite.AntiforgeryField, Token());

    /// <summary>A form that posts to this tenant's <paramref name="verb"/>, with the antiforgery field first. <paramref name="fieldsHtml"/> is trusted markup.</summary>
    public string PostForm(string verb, string fieldsHtml, string button, string css = "stack") =>
        AdminWrites.PostForm(AdminWrites.TenantAction(Tenant.Id, verb), Token(), fieldsHtml, button, css);

    /// <summary>A labelled input (the label, value and hint are encoded).</summary>
    public static string Field(string name, string label, int max, string type = "text", bool required = true, string? hint = null, string? value = null) =>
        AdminWrites.Field(name, label, max, type, required, hint, value);

    /// <summary>
    /// Sends the operator back to the tenant page with the canned notice <paramref name="noticeKey"/> (the core's or this module's; any
    /// other key throws). Nothing from the request goes in the URL.
    /// </summary>
    public void Redirect(string noticeKey)
    {
        if (!AdminWrites.Messages.ContainsKey(noticeKey) && !_module.Notices.ContainsKey(noticeKey))
            throw new InvalidOperationException("A module may redirect only with a notice key the core or the module defines.");
        AdminWrites.Redirect(_http, Tenant.Id, noticeKey);
    }

    /// <summary>The site's error page (plain text, encoded here).</summary>
    public Task ErrorPage(int status, string title, string message) => AdminSite.ErrorPage(_http, status, title, message);

    private string Token() => _antiforgery ?? SignedInSite.AntiforgeryToken(_http);
}
