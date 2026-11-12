using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Host.Admin;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// What the REAL OpenID Connect handler does with Microsoft's answer when someone declines a consent page. Each test starts a real
/// challenge (so the state and the correlation cookie are the handler's own), then sends the callback Microsoft would send, as a GET
/// (response mode query: what the sites ask for) and as a form_post. Only Microsoft's error code picks the page: access_denied and
/// consent_required are a decline; anything else, in any case, is a failed sign-in.
/// </summary>
[Collection("sqlite-pools")]
public partial class ConsentDeclineTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const string AppClient = CustomerSiteTests.AppClient;
    /// <summary>What Microsoft sends with a cancel on its consent page (error_description). Never shown, logged or used to decide.</summary>
    internal const string Aadsts65004 = "AADSTS65004: User declined to consent to access the app. ECHO-ME <script>x</script>";

    internal sealed class RealSite : IAsyncDisposable
    {
        public required AdminSiteFixture F { get; init; }
        public required TempDir Keys { get; init; }
        public required CapturingLoggerProvider Logs { get; init; }
        public async ValueTask DisposeAsync()
        {
            try { await F.DisposeAsync(); }
            finally { Keys.Dispose(); }
        }
    }

    /// <summary>Microsoft's endpoints without the network: the handler builds its authorize URL from this and never fetches metadata.</summary>
    internal static void OfflineMetadata(OpenIdConnectOptions o)
    {
        var c = new OpenIdConnectConfiguration
        {
            Issuer = "https://login.microsoftonline.com/{tenantid}/v2.0",
            AuthorizationEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
            TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
        };
        o.Configuration = c;
        o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(c);
    }

    /// <summary>The customer site with the real Microsoft handler (no fake scheme).</summary>
    internal static async Task<RealSite> StartAsync(Action<IServiceCollection>? services = null)
    {
        var keys = WritePem(out var pem);
        try
        {
            var logs = new CapturingLoggerProvider();
            var f = await AdminSiteFixture.StartAsync(o => { o.AppHost = AppHost; o.AppClientId = AppClient; o.AppCertPath = pem; },
                services: s =>
                {
                    s.AddSingleton<ILoggerProvider>(logs);
                    s.PostConfigure<OpenIdConnectOptions>(AppSite.RemoteScheme, OfflineMetadata);
                    services?.Invoke(s);
                });
            return new RealSite { F = f, Keys = keys, Logs = logs };
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks a challenge's redirect is the real one to Microsoft and returns the state it carries. The browser now holds the handler's
    /// correlation cookie. Code flow with response mode query: the handler leaves response_mode out (query is the code flow's default), so
    /// Microsoft answers with a GET to the callback, errors included.
    /// </summary>
    internal static string StateOf(HttpResponseMessage challenge, AdminBrowser b)
    {
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var location = challenge.Headers.Location!;
        Assert.Equal("login.microsoftonline.com", location.Host);
        var q = QueryHelpers.ParseQuery(location.Query);
        Assert.False(q.ContainsKey("response_mode"));
        Assert.Contains(b.Cookies.Keys, k => k.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        return q["state"].ToString();
    }

    internal static List<KeyValuePair<string, string>> Reply(string? state, string? error, string? subcode = "cancel", string description = Aadsts65004)
    {
        var reply = new List<KeyValuePair<string, string>>();
        if (error is not null) reply.Add(new("error", error));
        reply.Add(new("error_description", description));
        if (subcode is not null) reply.Add(new("error_subcode", subcode));
        if (state is not null) reply.Add(new("state", state));
        return reply;
    }

    internal static Task<HttpResponseMessage> CallbackAsync(AdminBrowser b, string path, List<KeyValuePair<string, string>> reply, bool formPost) =>
        formPost
            ? b.SendAsync(HttpMethod.Post, path, reply)
            : b.GetAsync(QueryHelpers.AddQueryString(path, reply.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))));

    internal static void DropCorrelationCookies(AdminBrowser b)
    {
        var browserCookies = (Dictionary<string, string>)b.Cookies;
        foreach (var k in b.Cookies.Keys.Where(k => k.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)).ToList()) browserCookies.Remove(k);
    }

    private static string CancelledLink(AdminSiteFixture f) =>
        "<pre class=\"snippet\">" + AppHtml.E(AppAzureSetup.OrganisationConsentUrl(f.App.Services.GetRequiredService<AppSiteSettings>(), null)) + "</pre>";

    private static async Task AssertCancelledAsync(HttpResponseMessage r, AdminSiteFixture f)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("<h1>Sign-in cancelled</h1>", body);
        Assert.Contains(CustomerSiteTests.CancelledText, body);
        Assert.Contains(CancelledLink(f), body);
        Assert.Contains("/organizations/v2.0/adminconsent", body);
        Assert.DoesNotContain("Sign-in failed", body);
        AssertNoEcho(body);
    }

    private static async Task AssertFailedAsync(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("<h1>Sign-in failed</h1>", body);
        Assert.DoesNotContain("Sign-in cancelled", body);
        Assert.DoesNotContain("adminconsent", body);
        AssertNoEcho(body);
    }

    /// <summary>
    /// The text holds none of the reply's markers. A random temp folder name in a logged path (packmoat-admin-XXXXXX) can never form
    /// them: ECHO-ME and echo_me need a hyphen or underscore, and Microsoft's codes are AADSTS followed by a digit.
    /// </summary>
    internal static void AssertNoEcho(string text)
    {
        Assert.DoesNotContain("ECHO-ME", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("echo_me", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"AADSTS\d", text);
        Assert.DoesNotContain("<script>x", text);
        Assert.DoesNotContain("cancel&", text);
    }

    /// <summary>
    /// No log line, from any category, holds anything from the reply. ASP.NET's OpenID Connect handler logs Microsoft's error and its
    /// description (event 12, at Error); production drops that handler's log lines in code (PackmoatApp), and the tests run with
    /// exactly production's logging configuration, so this checks what production would write.
    /// </summary>
    internal static void AssertLogsHoldNoReply(CapturingLoggerProvider logs)
    {
        lock (logs.Lines) foreach (var line in logs.Lines) AssertNoEcho(line);
    }

    // ---- the customer sign-in

    [Theory]
    [InlineData(false, "access_denied", "cancel")]
    [InlineData(true, "access_denied", "cancel")]
    [InlineData(false, "access_denied", null)]
    [InlineData(true, "access_denied", null)]
    [InlineData(false, "consent_required", null)]
    [InlineData(true, "consent_required", null)]
    public async Task A_decline_on_microsofts_consent_page_shows_the_cancelled_card_with_the_approval_link(bool formPost, string error, string? subcode)
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        var r = await CallbackAsync(b, AppSite.CallbackPath, Reply(state, error, subcode), formPost);
        await AssertCancelledAsync(r, site.F);
        Assert.False(b.HasCookie(AppSite.CookieName));   // nobody is signed in
        AssertLogsHoldNoReply(site.Logs);
    }

    [Theory]
    [InlineData(false, "interaction_required")]
    [InlineData(true, "interaction_required")]
    [InlineData(false, "server_error")]
    [InlineData(true, "server_error")]
    [InlineData(false, "ACCESS_DENIED")]
    [InlineData(true, "Consent_Required")]
    [InlineData(false, "access_denied ")]
    [InlineData(false, null)]   // AADSTS65004 in the description alone
    [InlineData(true, null)]
    public async Task Any_other_error_code_shows_the_failed_page(bool formPost, string? error)
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        var r = await CallbackAsync(b, AppSite.CallbackPath, Reply(state, error), formPost);
        await AssertFailedAsync(r);
        AssertLogsHoldNoReply(site.Logs);
    }

    [Theory]
    [InlineData(false, false, true)]   // the reply carries no state
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]   // the browser lost the correlation cookie
    [InlineData(true, true, false)]    // (a form_post across sites does not carry a SameSite=Lax cookie)
    public async Task A_decline_the_handler_cannot_match_to_a_sign_in_still_shows_the_cancelled_card(bool formPost, bool withState, bool withCookie)
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        if (!withCookie) DropCorrelationCookies(b);
        var r = await CallbackAsync(b, AppSite.CallbackPath, Reply(withState ? state : null, "access_denied"), formPost);
        await AssertCancelledAsync(r, site.F);
        Assert.False(b.HasCookie(AppSite.CookieName));
        // The log says which way it went: the handler's failure type and the allowlisted code, nothing else from the reply.
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in cancelled or not approved (AuthenticationFailureException, error access_denied)"));
        AssertLogsHoldNoReply(site.Logs);
    }

    [Fact]
    public async Task A_lost_sign_in_with_another_error_code_still_shows_the_failed_page()
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        StateOf(await b.GetAsync("/signin"), b);
        DropCorrelationCookies(b);
        await AssertFailedAsync(await CallbackAsync(b, AppSite.CallbackPath, Reply(null, "server_error"), formPost: false));
    }

    [Fact]
    public async Task An_unknown_error_code_is_logged_as_other_and_a_known_one_by_name()
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        await AssertFailedAsync(await CallbackAsync(b, AppSite.CallbackPath, Reply(state, "x_echo_me"), formPost: false));
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in failed (OpenIdConnectProtocolException, error other, token none)"));
        state = StateOf(await b.GetAsync("/signin"), b);
        await AssertFailedAsync(await CallbackAsync(b, AppSite.CallbackPath, Reply(state, "interaction_required"), formPost: false));
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in failed (OpenIdConnectProtocolException, error interaction_required, token none)"));
        AssertLogsHoldNoReply(site.Logs);
    }

    [Theory]
    [InlineData(true)]    // matched to the sign-in: the handler's own failure
    [InlineData(false)]   // the browser lost the correlation cookie
    public async Task A_repeated_error_code_is_no_code_at_all_and_shows_the_failed_page(bool matched)
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        if (!matched) DropCorrelationCookies(b);
        // Two error values: the handler reads the first; SignInReply refuses to pick one, so this is no decline.
        var code = matched ? "consent_required" : "access_denied";
        var reply = Reply(matched ? state : null, code);
        reply.Add(new("error", code));
        await AssertFailedAsync(await CallbackAsync(b, AppSite.CallbackPath, reply, formPost: false));
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in failed (") && l.EndsWith(", error none, token none)", StringComparison.Ordinal));
        AssertLogsHoldNoReply(site.Logs);
    }

    [Fact]
    public async Task A_consent_required_decline_is_logged_by_its_code()
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        await AssertCancelledAsync(await CallbackAsync(b, AppSite.CallbackPath, Reply(state, "consent_required"), formPost: false), site.F);
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in cancelled or not approved (OpenIdConnectProtocolException, error consent_required)"));
    }

    [Theory]
    [InlineData(true, "server_error")]
    [InlineData(true, "access_denied")]
    [InlineData(true, "consent_required")]
    [InlineData(false, "access_denied")]
    [InlineData(false, "x_echo_me")]
    public async Task Microsofts_error_description_reaches_no_log_line_of_any_category(bool matched, string error)
    {
        await using var site = await StartAsync();
        var b = site.F.Browser(AppHost);
        var state = StateOf(await b.GetAsync("/signin"), b);
        if (!matched) DropCorrelationCookies(b);
        await CallbackAsync(b, AppSite.CallbackPath, Reply(matched ? state : null, error), formPost: false);
        string all;
        lock (site.Logs.Lines) all = string.Join("\n", site.Logs.Lines);
        Assert.Contains("Customer sign-in", all);   // the callback was handled and logged
        Assert.DoesNotContain("Microsoft.AspNetCore.Authentication.OpenIdConnect", all);
        AssertLogsHoldNoReply(site.Logs);
        // The rule is in code, so it holds whatever configuration file the host does or does not find.
        var rules = site.F.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoggerFilterOptions>>().Value.Rules;
        Assert.Contains(rules, r => r.CategoryName == "Microsoft.AspNetCore.Authentication.OpenIdConnect" && r.ProviderName is null && r.LogLevel == LogLevel.Critical);
    }

    // ---- the pure rule: reading the code

    private sealed class ThrowingForm : Microsoft.AspNetCore.Http.Features.IFormFeature
    {
        private readonly Exception _e;
        public ThrowingForm(Exception e) => _e = e;
        public bool HasFormContentType => true;
        public Microsoft.AspNetCore.Http.IFormCollection? Form { get; set; }
        public Microsoft.AspNetCore.Http.IFormCollection ReadForm() => throw _e;
        public Task<Microsoft.AspNetCore.Http.IFormCollection> ReadFormAsync(CancellationToken cancellationToken) => Task.FromException<Microsoft.AspNetCore.Http.IFormCollection>(_e);
    }

    private static Microsoft.AspNetCore.Http.HttpRequest UnreadableForm(Exception e)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=access_denied");   // never read for a form post
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new ThrowingForm(e));
        return http.Request;
    }

    [Fact]
    public async Task An_unreadable_form_has_no_error_code()
    {
        Assert.Null(await SignInReply.ErrorAsync(UnreadableForm(new IOException("cut short"))));
        Assert.Null(await SignInReply.ErrorAsync(UnreadableForm(new InvalidDataException("too many fields"))));
        Assert.Null(await SignInReply.ErrorAsync(UnreadableForm(new Microsoft.AspNetCore.Http.BadHttpRequestException("too large"))));
    }

    [Fact]
    public async Task Any_other_exception_while_reading_the_form_is_not_swallowed()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => SignInReply.ErrorAsync(UnreadableForm(new InvalidOperationException("a bug"))));
        Assert.Equal("a bug", e.Message);
    }

    // ---- the admin site: a decline there is a failed sign-in, as before

    [Theory]
    [InlineData(false, "access_denied")]
    [InlineData(true, "access_denied")]
    [InlineData(false, "consent_required")]
    public async Task On_the_admin_site_a_decline_shows_the_failed_page(bool formPost, string error)
    {
        await using var f = await AdminSiteFixture.StartAsync(fakeProvider: false,
            services: s => s.PostConfigure<OpenIdConnectOptions>(AdminSite.RemoteScheme, OfflineMetadata));
        var b = f.Browser();
        var landing = await b.GetAsync("/");
        var field = AdminBrowser.Field(await landing.Content.ReadAsStringAsync())!;
        var start = await b.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, field) });
        var state = StateOf(start, b);
        var r = await CallbackAsync(b, AdminSite.CallbackPath, Reply(state, error), formPost);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("<h1>Sign-in failed</h1>", body);
        Assert.DoesNotContain("cancelled", body);
        Assert.DoesNotContain("adminconsent", body);
        AssertNoEcho(body);
    }

    // ---- the pure rule

    [Fact]
    public void The_echo_check_ignores_temp_folder_names_and_catches_the_reply_markers()
    {
        AssertNoEcho("/tmp/packmoat-admin-aEChof/x");
        AssertNoEcho("/tmp/packmoat-admin-AADSTS/x");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoEcho("description ECHO-ME here"));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoEcho("error x_echo_me"));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoEcho("AADSTS65004: User declined"));
    }

    [Theory]
    [InlineData("access_denied", true)]
    [InlineData("consent_required", true)]
    [InlineData("interaction_required", false)]
    [InlineData("login_required", false)]
    [InlineData("server_error", false)]
    [InlineData("ACCESS_DENIED", false)]
    [InlineData("Access_Denied", false)]
    [InlineData(" access_denied", false)]
    [InlineData("access_denied,access_denied", false)]
    [InlineData("AADSTS65004", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_exact_decline_codes_are_a_decline(string? error, bool declined) => Assert.Equal(declined, SignInReply.IsDeclined(error));

    [Theory]
    [InlineData("access_denied", "access_denied")]
    [InlineData("interaction_required", "interaction_required")]
    [InlineData("ACCESS_DENIED", "other")]
    [InlineData("<script>", "other")]
    [InlineData(null, "none")]
    public void A_log_line_names_only_a_known_code(string? error, string logged) => Assert.Equal(logged, SignInReply.LogCode(error));
}
