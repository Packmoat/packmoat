using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Customer;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// Connect and withdraw through the running host: the button only when both checks qualify (developer rows arrive with I2, so
/// they are written here directly), the POST's gates (switch, antiforgery, Origin, Admin, a fresh sign-in, self-made only), the writes,
/// a failed re-check withdrawing the feed, and the regressions: the canary still pings and operator-made tenants are untouched. What
/// the hosted module adds (its Connect card wording, and the plan a token needs) is the hosted tests'.
/// </summary>
public partial class AzureSetupSiteTests
{
    [Fact]
    public async Task Connect_shows_only_when_both_checks_qualify_and_makes_packmoat_serve_the_checked_feed()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await SignIn(f);
        Assert.DoesNotContain(AppAzureSetup.ConnectPath, (await b.GetTextAsync(AppAzureSetup.Path)).Body);

        // what the delegated check records (no 'private', no developer proof): still no button
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        Assert.DoesNotContain(AppAzureSetup.ConnectPath, (await b.GetTextAsync(AppAzureSetup.Path)).Body);

        var ids = await SeedChecksAsync(f);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains($"action=\"{AppAzureSetup.ConnectPath}\"", page);
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectSentence), page);   // the core's; the hosted module's wording is the hosted tests'
        Assert.Contains(AppHtml.E("Grace Hopper confirmed on " + f.Clock.UtcNow.AddHours(-2).ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + " that they can't reach the feed directly."), page);
        Assert.Contains($">{AppAzureSetup.ConnectButton}</button>", page);
        Assert.Contains($"value=\"{ids.Settings}\"", page);

        var connect = await Post(b, page, AppAzureSetup.ConnectPath, true, ConnectFields(ids));
        Assert.Equal(HttpStatusCode.SeeOther, connect.StatusCode);
        Assert.Equal(AppAzureSetup.Path, connect.Headers.Location!.OriginalString);
        Assert.False(WroteSessionCookie(connect));

        var tenant = (await f.Tenants.GetAsync("selfco", default))!;
        Assert.Equal(("https://pkgs.dev.azure.com/", CheckedFeed), (tenant.UpstreamBase, tenant.UpstreamFeedIndexUrl));
        Assert.Equal(new UpstreamBinding(IdentityKind.Shared, DirS, null), await f.Tenants.GetUpstreamBindingAsync("selfco", default));
        Assert.Equal("wizard", await Scalar(f, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal(ids.Developer, await Scalar(f, "SELECT developer_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
        var action = (await f.Actions.LatestAsync(10, default)).Single(a => a.Action == ConnectRules.ConnectAction);
        Assert.Equal(("selfco", DirS, Ada, "app"), (action.TenantId, action.ActorTid, action.ActorOid, action.Site));

        var after = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectedLine), after);
        Assert.DoesNotContain(AppAzureSetup.ConnectPath, after);
    }

    [Fact]
    public async Task Connect_needs_antiforgery_this_sites_origin_and_writes_nothing_without_them()
    {
        await using var site = await StartAsync();
        var ids = await SeedChecksAsync(site.F);
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.Forbidden, (await ConnectFrom(b, ids, origin: false)).StatusCode);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        var foreign = await b.SendAsync(HttpMethod.Post, AppAzureSetup.ConnectPath,
            new[] { new KeyValuePair<string, string>(Packmoat.Host.Web.SignedInSite.AntiforgeryField, AdminBrowser.Field(page)!) }
                .Concat(ConnectFields(ids).Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2))),
            r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        var noToken = await b.SendAsync(HttpMethod.Post, AppAzureSetup.ConnectPath, ConnectFields(ids).Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)),
            r => r.Headers.TryAddWithoutValidation("Origin", Origin));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Null(await Verification(site.F));
        Assert.Null(await site.F.Tenants.GetUpstreamBindingAsync("selfco", default));
        Assert.DoesNotContain(await site.F.Actions.LatestAsync(50, default), a => a.Action == ConnectRules.ConnectAction);
    }

    [Fact]
    public async Task Connect_is_for_admins_with_a_fresh_sign_in_only()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var ids = await SeedChecksAsync(f);

        var dev = await SignIn(f, role: CustomerSignIn.DeveloperRole);
        var home = (await dev.GetTextAsync("/home")).Body;
        var refused = await Post(dev, home, AppAzureSetup.ConnectPath, true, ConnectFields(ids));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Admins only", await refused.Content.ReadAsStringAsync());

        var admin = await SignIn(f);
        var page = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        f.Clock.Advance(TimeSpan.FromMinutes(16));   // the session's sign-in is no longer fresh
        var stale = await Post(admin, page, AppAzureSetup.ConnectPath, true, ConnectFields(ids));
        var body = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Contains("Sign in again first", body);
        Assert.Contains($"action=\"{AppAzureSetup.ReauthPath}\"", body);
        Assert.Null(await Verification(f));
        Assert.Null(await f.Tenants.GetUpstreamBindingAsync("selfco", default));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")]
    public async Task With_the_switch_off_for_the_directory_connect_does_not_exist(string flag)
    {
        await using var site = await StartAsync(flag);
        var ids = await SeedChecksAsync(site.F);
        var b = await SignIn(site.F);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, home, AppAzureSetup.ConnectPath, true, ConnectFields(ids))).StatusCode);
        Assert.Null(await Verification(site.F));
    }

    [Fact]
    public async Task An_operator_made_shared_tenant_like_e2e_shared_is_never_connected_and_keeps_its_operator_record()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var stored = Shape((await f.Tenants.GetAsync("acme", default))!);
        await f.Tenants.BindSharedIdentityAsync("acme", DirA, default);
        var ids = await SeedChecksAsync(f, "acme", DirA);
        var b = await SignIn(f, dir: DirA);
        var refused = await ConnectFrom(b, ids);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(AppHtml.E(AppAzureSetup.ReadOnlyLine(TestOperator.Support)), await refused.Content.ReadAsStringAsync());
        Assert.Equal(stored, Shape((await f.Tenants.GetAsync("acme", default))!));
        Assert.Null(await Verification(f, "acme"));
        Assert.DoesNotContain(await f.Actions.LatestAsync(50, default), a => a.Action == ConnectRules.ConnectAction);
    }

    [Fact]
    public async Task Stale_or_foreign_check_ids_are_refused_with_a_fixed_message()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var ids = await SeedChecksAsync(f);
        var acme = await SeedChecksAsync(f, "acme", DirS);   // another tenant's rows, claiming this directory
        var b = await SignIn(f);
        foreach (var bad in new[] { (ids.Settings, acme.Developer), (acme.Settings, ids.Developer), (ids.Developer, ids.Settings), (4242L, ids.Developer) })
        {
            var r = await ConnectFrom(b, bad);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Contains(AppHtml.E(AppAzureSetup.ConnectStaleLine), await r.Content.ReadAsStringAsync());
        }
        Assert.Null(await Verification(f));
        Assert.Equal(HttpStatusCode.SeeOther, (await ConnectFrom(b, ids)).StatusCode);
    }

    // ---- withdraw on a failed re-check

    [Fact]
    public async Task A_failed_recheck_of_the_connected_feed_withdraws_it_and_says_so()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await Connected(site);
        site.Ado.Upstreams = new JsonArray(
            new JsonObject { ["name"] = "nuget.org", ["protocol"] = "nuget", ["location"] = "https://api.nuget.org/v3/index.json" },
            new JsonObject { ["name"] = "evil", ["protocol"] = "nuget", ["location"] = "https://evil.example/v3/index.json" });
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);

        Assert.Null(await Verification(f));
        var action = (await f.Actions.LatestAsync(10, default)).Single(a => a.Action == ConnectRules.WithdrawnAction);
        Assert.Equal(("selfco", DirS, Ada, "app"), (action.TenantId, action.ActorTid, action.ActorOid, action.Site));
        Assert.Contains("failed=upstreams", action.Detail);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.WithdrawnLine), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ConnectedLine), page);
        // the tenant no longer issues tokens: not verified
        Assert.Null((await CreateToken(await SignIn(f, role: CustomerSignIn.DeveloperRole))).Token);
    }

    /// <summary>The real store, except that withdrawing always throws (the database is unwritable, say).</summary>
    private sealed class BrokenWithdrawStore(ISelfServiceFeedStore inner) : ISelfServiceFeedStore
    {
        public int Withdrawals;
        public Task<ConnectResult> ConnectSelfServiceFeedAsync(ConnectFeedRequest request, CancellationToken ct) => inner.ConnectSelfServiceFeedAsync(request, ct);
        public Task<bool> WithdrawAsync(SharedAppVerification verification, AdminActionRecord action, CancellationToken ct)
        {
            Interlocked.Increment(ref Withdrawals);
            throw new IOException("disk I/O error");
        }
    }

    [Fact]
    public async Task A_withdrawal_that_keeps_failing_is_logged_as_an_error_and_the_page_says_to_email_support()
    {
        BrokenWithdrawStore? broken = null;
        await using var site = await StartAsync(feeds: fx => broken = new BrokenWithdrawStore(new SqliteSelfServiceFeedStore(fx.Db)));
        var f = site.F;
        var b = await Connected(site);
        site.Ado.Upstreams = new JsonArray(
            new JsonObject { ["name"] = "nuget.org", ["protocol"] = "nuget", ["location"] = "https://api.nuget.org/v3/index.json" },
            new JsonObject { ["name"] = "evil", ["protocol"] = "nuget", ["location"] = "https://evil.example/v3/index.json" });
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);

        Assert.Equal(2, broken!.Withdrawals);
        Assert.NotNull(await Verification(f));   // still served: that is what the page and the log say
        Assert.Equal(FeedCheckOutcome.Failed, (await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!.Outcome);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E("Packmoat couldn't stop serving your feed automatically. Email support@contoso.example."), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ConnectedLine), page);
        lock (site.Logs.Lines) Assert.Contains(site.Logs.Lines, l => l.StartsWith("Packmoat.App: ", StringComparison.Ordinal) && l.Contains("failed twice", StringComparison.Ordinal));   // logged at Error (the level: ConnectFeedTests)
    }

    [Fact]
    public async Task The_reauth_for_connect_returns_to_exactly_the_setup_page()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await SignIn(f);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        var start = await Post(b, page, AppAzureSetup.ReauthPath, true);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var props = Assert.IsType<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectChallengeProperties>(f.AppIdentity.LastChallenge);
        Assert.Equal("/setup/azure?m=reauth", props.RedirectUri);
        Assert.Equal("login", props.Prompt);
        Assert.Equal(TimeSpan.FromMinutes(15), props.MaxAge);
    }

    // ---- serving stops soon after a withdrawal

    /// <summary>The proxy's upstream: the connected feed answers Packmoat's own token with a service index. Anything else: 401 or 404.</summary>
    private sealed class ConnectedFeed : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri != CheckedFeed) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Headers.Authorization?.ToString() != FakeAzureDevOps.PackmoatAuthorization) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"version":"3.0.0","resources":[]}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>
    /// The site with production's credential source (the Entra token comes from <see cref="FakePackmoatCredential"/>) and a real shared
    /// certificate, serving the connected feed from <paramref name="feed"/>.
    /// </summary>
    private static async Task<Site> StartServingAsync(ConnectedFeed feed, FakePackmoatCredential entra)
    {
        var site = await StartAsync(services: s =>
        {
            s.AddSingleton<IUpstreamCredentialSource>(sp => new EntraCertificateCredentialSource(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ISharedAppCredentialStore>(), createCredential: entra.Factory));
            s.AddSingleton<IUpstreamClient>(sp => new UpstreamHttpClient(new HttpClient(feed), sp.GetRequiredService<IUpstreamCredentialSource>()));
        });
        await site.F.App.Services.GetRequiredService<ISharedAppCredentialStore>().SetAsync(SharedAppSlot.Active,
            new SharedAppCredential(FakeAzureDevOps.SharedApp, CertificateTools.Generate("packmoat-shared", 365, site.F.Clock.UtcNow).CombinedPem), default);
        return site;
    }

    /// <summary>A developer token of the connected tenant.</summary>
    private static async Task<string> ServingTokenAsync(AdminSiteFixture f)
    {
        return (await f.Tokens.IssueAsync("selfco", "ci", default)).Plaintext;
    }

    private static async Task<HttpStatusCode> RestoreIndex(AdminSiteFixture f, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (await http.GetAsync("/v3/index.json")).StatusCode;
    }

    [Fact]
    public async Task A_withdrawn_feed_stops_being_served_within_five_seconds()
    {
        var feed = new ConnectedFeed();
        await using var site = await StartServingAsync(feed, new FakePackmoatCredential());
        var f = site.F;
        var b = await Connected(site);
        var token = await ServingTokenAsync(f);
        Assert.Equal(HttpStatusCode.OK, await RestoreIndex(f, token));   // connected: served, and the verification was just read
        f.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(HttpStatusCode.OK, await RestoreIndex(f, token));   // still within the re-check interval: not read again
        var served = feed.Calls;

        // Contributors get Reader on the feed: a definite permissions FAIL, so the re-check withdraws the feed.
        site.Ado.ExtraPermissions.Add(new JsonObject
        {
            ["role"] = "reader", ["displayName"] = "[Platform Team]\\Contributors", ["isInheritedRole"] = false,
            ["identityDescriptor"] = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-3000000001-3000000002-3000000003-3-1-2-3-4",
        });
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        Assert.Null(await Verification(f));
        Assert.Contains("failed=permissions", (await f.Actions.LatestAsync(10, default)).Single(a => a.Action == ConnectRules.WithdrawnAction).Detail);

        // The verification was last read 4 s before the withdrawal, so 1 s after it the 5 s re-check is due. (The old 2-minute
        // credential cache kept serving here.) 6 s after it, any re-check has happened, however recent the last read was.
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadGateway, await RestoreIndex(f, token));
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadGateway, await RestoreIndex(f, token));
        Assert.Equal(served, feed.Calls);   // the customer's feed was not called for the refused requests
    }

    [Fact]
    public async Task A_recheck_that_could_not_complete_keeps_the_feed_served()
    {
        var feed = new ConnectedFeed();
        await using var site = await StartServingAsync(feed, new FakePackmoatCredential());
        var f = site.F;
        var b = await Connected(site);
        var token = await ServingTokenAsync(f);
        var before = await Verification(f);
        Assert.Equal(HttpStatusCode.OK, await RestoreIndex(f, token));

        site.Ado.PermissionsStatus = 403;   // the permissions call errors: a FAIL that "could not complete", never a definite one
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        var check = (await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!;
        Assert.Equal(FeedCheckOutcome.Failed, check.Outcome);
        Assert.StartsWith(ConnectRules.CouldNotComplete, check.Results.Single(r => r.Id == "permissions").Detail);

        Assert.Equal(before, await Verification(f));
        Assert.DoesNotContain(await f.Actions.LatestAsync(50, default), a => a.Action == ConnectRules.WithdrawnAction);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, await RestoreIndex(f, token));
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, await RestoreIndex(f, token));
    }

    [Fact]
    public async Task An_incomplete_recheck_changes_nothing()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await Connected(site);
        var before = await Verification(f);
        site.Ado.TimeoutOn = "/permissions";   // one call times out: not verified, never failed
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        Assert.Equal(FeedCheckOutcome.Incomplete, (await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!.Outcome);
        Assert.Equal(before, await Verification(f));
        Assert.DoesNotContain(await f.Actions.LatestAsync(50, default), a => a.Action == ConnectRules.WithdrawnAction);
    }

    // ---- regressions

    [Fact]
    public async Task The_canary_still_pings_and_operator_made_tenants_are_untouched_by_a_connect()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await f.Tenants.CreateAsync(new NewTenant("canary", "Deploy canary", "https://pkgs.dev.azure.com/", "", "https://pkgs.dev.azure.com/none/_packaging/none/nuget/v3/index.json", 14, TenantOrigin.Operator), default);
        await f.Tenants.CreateAsync(new NewTenant("e2e-shared", "E2E shared", "https://pkgs.dev.azure.com/", "org/", AdminSiteFixture.StrictFeed, 14, TenantOrigin.Operator), default);
        const string E2EDir = "e2e2e2e2-e2e2-4e2e-8e2e-e2e2e2e2e2e2";
        await f.Tenants.BindSharedIdentityAsync("e2e-shared", E2EDir, default);
        var shared = f.App.Services.GetRequiredService<ISharedAppCredentialStore>();
        var operatorRecord = new SharedAppVerification("e2e-shared", E2EDir, AdminSiteFixture.StrictFeed, FakeAzureDevOps.SharedApp, f.Clock.UtcNow, "onboard check");
        Assert.True(await shared.RecordVerificationAsync(operatorRecord, default));
        var before = (await f.Tenants.ListAsync(default)).Where(t => t.Id != "selfco").Select(Shape).ToList();
        var canaryToken = await f.Tokens.IssueAsync("canary", "deploy canary", default);

        await Connected(site);

        Assert.Equal(before, (await f.Tenants.ListAsync(default)).Where(t => t.Id != "selfco").Select(Shape).ToList());
        Assert.Equal(operatorRecord, await shared.GetVerificationAsync("e2e-shared", default));
        Assert.Equal("operator", await Scalar(f, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='e2e-shared'"));
        Assert.Null(await f.Tenants.GetUpstreamBindingAsync("canary", default));
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", canaryToken.Plaintext);
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/v3/ping")).StatusCode);
    }

    // ---- retention

    [Fact]
    public async Task The_daily_retention_pass_blanks_personal_data_of_checks_older_than_30_days()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var store = f.App.Services.GetRequiredService<IFeedCheckStore>();
        var named = new[] { new CheckResult("permissions", "Only Packmoat holds a role", CheckStatus.Warn, "named admin \"Grace Hopper\"; confirm") };
        var old = await store.RecordAsync(new FeedCheck("selfco", f.Clock.UtcNow - TimeSpan.FromDays(31), DirS, Ada, "Ada", DirS, CheckedFeed, FakeAzureDevOps.SharedApp,
            FeedCheckOutcome.Passed, named, new[] { "Grace Hopper" }), default);
        var recent = await store.RecordAsync(new FeedCheck("selfco", f.Clock.UtcNow - TimeSpan.FromDays(29), DirS, Ada, "Ada", DirS, CheckedFeed, FakeAzureDevOps.SharedApp,
            FeedCheckOutcome.Passed, named, new[] { "Grace Hopper" }), default);
        var service = f.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<RetentionService>().Single();   // registered with the store
        Assert.Equal(1, await service.RunOnceAsync(default));
        Assert.Equal("[]", await Scalar(f, $"SELECT confirmed_admins FROM feed_checks WHERE id={old}"));
        Assert.Equal("", await Scalar(f, $"SELECT checked_by_name FROM feed_checks WHERE id={old}"));
        Assert.DoesNotContain("Grace Hopper", (string)(await Scalar(f, $"SELECT results FROM feed_checks WHERE id={old}"))!);
        Assert.Equal((DirS, Ada), ((string)(await Scalar(f, $"SELECT checked_by_tid FROM feed_checks WHERE id={old}"))!, (string)(await Scalar(f, $"SELECT checked_by_oid FROM feed_checks WHERE id={old}"))!));
        Assert.Contains("Grace Hopper", (string)(await Scalar(f, $"SELECT confirmed_admins FROM feed_checks WHERE id={recent}"))!);
        Assert.Contains("Grace Hopper", (string)(await Scalar(f, $"SELECT results FROM feed_checks WHERE id={recent}"))!);
        Assert.Equal("Ada", await Scalar(f, $"SELECT checked_by_name FROM feed_checks WHERE id={recent}"));

        // the loop itself: a short interval runs the pass without a restart or a recorded check
        f.Clock.Advance(TimeSpan.FromDays(2));
        using var loop = new RetentionService(store, f.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        await loop.StartAsync(default);
        for (var i = 0; i < 200 && (string)(await Scalar(f, $"SELECT confirmed_admins FROM feed_checks WHERE id={recent}"))! != "[]"; i++) await Task.Delay(25);
        await loop.StopAsync(default);
        Assert.Equal("[]", await Scalar(f, $"SELECT confirmed_admins FROM feed_checks WHERE id={recent}"));
        Assert.Equal(RetentionService.Interval, TimeSpan.FromDays(1));
    }

    private sealed class ThrowingFeedChecks : IFeedCheckStore
    {
        public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct) => throw new NullReferenceException("row 7 Grace Hopper");
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// The start-up blanking never stops the server starting. A row whose results are <c>[null]</c> is redacted like any
    /// unreadable row, and a store that throws is logged by kind only.
    /// </summary>
    [Fact]
    public async Task The_start_up_blanking_redacts_a_null_result_row_and_a_failure_never_stops_the_start()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var store = f.App.Services.GetRequiredService<IFeedCheckStore>();
        var bad = await store.RecordAsync(new FeedCheck("selfco", f.Clock.UtcNow - TimeSpan.FromDays(31), DirS, Ada, "Ada", DirS, CheckedFeed, FakeAzureDevOps.SharedApp,
            FeedCheckOutcome.Passed, Passing("feed"), new[] { "Grace Hopper" }), default);
        await using (var c = await f.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"UPDATE feed_checks SET results='[null]' WHERE id={bad}";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var log = new RecordingLogger();
        Assert.Equal(1, RetentionService.BlankAtStartup(store, f.Clock, log));
        Assert.Equal("[]", await Scalar(f, $"SELECT results FROM feed_checks WHERE id={bad}"));
        Assert.Equal("", await Scalar(f, $"SELECT checked_by_name FROM feed_checks WHERE id={bad}"));
        Assert.Empty(log.Lines);

        Assert.Equal(0, RetentionService.BlankAtStartup(new ThrowingFeedChecks(), f.Clock, log));   // does not throw
        var line = Assert.Single(log.Lines);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, line.Level);
        Assert.Contains(nameof(NullReferenceException), line.Message);
        Assert.DoesNotContain("Grace", line.Message);
        Assert.Equal(0, RetentionService.BlankAtStartup(null, f.Clock, log));
    }
}
