using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

/// <summary>The write actions of the admin site. Every test goes through the real pipeline with the pretend Microsoft sign-in.</summary>
[Collection("sqlite-pools")]
public class AdminWriteTests
{
    private const string Origin = "https://" + AdminHost;
    private static readonly Regex TokenPattern = new("pmt_[0-9a-f]{48}", RegexOptions.Compiled);

    internal sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly CaptureLogs _o; private readonly string _c;
            public L(CaptureLogs o, string c) { _o = o; _c = c; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _o.Lines.Enqueue($"{_c} {logLevel}: {formatter(state, exception)} {exception}");
        }
    }

    private sealed class ThrowingActionLog : IAdminActionLog
    {
        public ValueTask AppendAsync(AdminActionRecord record, CancellationToken ct) => throw new InvalidOperationException("disk full");
        public Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdminActionRecord>>(Array.Empty<AdminActionRecord>());
        public Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken ct) => Task.FromResult<AdminActionRecord?>(null);
        public Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("disk full");
    }

    private sealed class ThrowingTokens : ITokenService
    {
        private readonly ITokenService _inner;
        public ThrowingTokens(ITokenService inner) => _inner = inner;
        public Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken ct, TokenKind kind = TokenKind.Developer) => throw new InvalidOperationException("boom");
        public Task<bool> RevokeAsync(string tokenId, CancellationToken ct) => _inner.RevokeAsync(tokenId, ct);
        public Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken ct) => _inner.RevokeAsync(tenantId, tokenId, ct);
        public Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken ct) => _inner.ListAsync(tenantId, ct);
        public Task<IssuedToken?> IssueForMemberAsync(string tenantId, string ownerTid, string ownerOid, string label, DateTimeOffset expiresAt, TokenKind kind, int maxActive, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string tenantId, string ownerTid, string ownerOid, CancellationToken ct) => _inner.ListForMemberAsync(tenantId, ownerTid, ownerOid, ct);
        public Task<bool> RevokeForMemberAsync(string tenantId, string ownerTid, string ownerOid, string tokenId, string revokedBy, CancellationToken ct) => _inner.RevokeForMemberAsync(tenantId, ownerTid, ownerOid, tokenId, revokedBy, ct);
        public Task<bool> RevokeInTenantAsync(string tenantId, string tokenId, string revokedBy, CancellationToken ct) => _inner.RevokeInTenantAsync(tenantId, tokenId, revokedBy, ct);
    }

    private sealed class ToggleAllowlist : AdminAllowlist
    {
        public bool Deny { get; set; }
        public ToggleAllowlist() : base(new[] { (Guid.Parse(Tid), Guid.Parse(Oid)) }) { }
        public override bool IsAllowed(ClaimsPrincipal? principal) => !Deny && base.IsAllowed(principal);
    }

    private static async Task<AdminBrowser> SignedIn(AdminSiteFixture f)
    {
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);
        return b;
    }

    /// <summary>A POST the way the page's own form makes it: antiforgery field from a page, same-site Origin.</summary>
    private static async Task<HttpResponseMessage> Post(AdminBrowser b, string path, params (string, string)[] fields) =>
        await PostWith(b, path, await b.TokenFromAsync("/tenants"), Origin, fields);

    private static async Task<HttpResponseMessage> PostWith(AdminBrowser b, string path, string? af, string? origin, params (string, string)[] fields)
    {
        var form = fields.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
        if (af is not null) form.Add(new(AdminSite.AntiforgeryField, af));
        return await b.SendAsync(HttpMethod.Post, path, form, req => { if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin); });
    }

    /// <summary>The fields the tenant page's "Issue token" form would send, including its single-use nonce.</summary>
    private static async Task<(string, string)[]> IssueForm(AdminBrowser b, string tenant, string label, string kind)
    {
        var html = await (await b.GetAsync("/tenants/" + tenant)).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"nonce\" value=\"([0-9a-f]+)\"");
        Assert.True(m.Success);
        return new[] { ("label", label), ("kind", kind), ("nonce", m.Groups[1].Value) };
    }

    private static string Where(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private static async Task<IReadOnlyList<AdminActionRecord>> Trail(AdminSiteFixture f) => await f.Actions.LatestAsync(500, default);

    private static async Task<Tenant> Acme(AdminSiteFixture f) => (await f.Tenants.GetAsync("acme", default))!;

    private static (string, string)[] NewTenantForm(string id = "newco", string name = "New Co", string upstream = "https://pkgs.dev.azure.com", string path = "org", string feed = "https://pkgs.dev.azure.com/org/p/_packaging/f/nuget/v3/index.json", string cooldown = "10") =>
        new[] { ("id", id), ("name", name), ("upstream_base", upstream), ("path_prefix", path), ("feed", feed), ("cooldown", cooldown) };

    // ---- 1. create tenant

    [Fact]
    public async Task Create_tenant_works_validates_like_the_cli_and_writes_the_trail()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        var page = (await b.GetTextAsync("/tenants")).Body;
        Assert.Contains("packmoat-admin identity</code> on the server.", page);   // the page says certificates and identity stay on the command line (#226)
        Assert.DoesNotContain("BEGIN", page);

        var r = await Post(b, "/tenant-create", NewTenantForm());
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/tenants/newco?r=created", Where(r));

        var t = (await f.Tenants.GetAsync("newco", default))!;
        Assert.Equal("New Co", t.Name);
        Assert.Equal("https://pkgs.dev.azure.com/", t.UpstreamBase);   // trailing slash added, as the CLI does
        Assert.Equal("org/", t.AllowedPathPrefix);
        Assert.Equal(10, t.Policy.CooldownDays);
        Assert.Null(await f.Tenants.GetUpstreamIdentityAsync("newco", default));   // no identity is touched

        var row = Assert.Single(await Trail(f));
        Assert.Equal("tenant.create", row.Action);
        Assert.Equal("newco", row.TenantId);
        Assert.Equal(Tid, row.ActorTid);
        Assert.Equal(Oid, row.ActorOid);
        Assert.Equal("Test Founder", row.ActorName);
        Assert.Equal(f.Clock.UtcNow, row.Time);

        var shown = (await b.GetTextAsync("/tenants/newco?r=created")).Body;
        Assert.Contains("Tenant created", shown);
        Assert.Contains("command line only", shown);
    }

    [Fact]
    public async Task Creating_an_existing_tenant_changes_nothing_and_says_so()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var r = await Post(b, "/tenant-create", NewTenantForm(id: "acme", name: "Hijack"));
        Assert.Equal("/tenants?r=exists", Where(r));
        Assert.Equal("Acme", (await Acme(f)).Name);
        Assert.Empty(await Trail(f));
    }

    public static IEnumerable<object[]> BadTenants() => new[]
    {
        new object[] { "bad-id", "Has Space", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-id", "UPPER", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-id", "", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-id", "-leading", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-id", new string('a', 64), "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-name", "ok", "", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-name", "ok", "two\nlines", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-name", "ok", new string('n', 101), "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-upstream", "ok", "N", "http://h/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-upstream", "ok", "N", "ftp://h/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-upstream", "ok", "N", "not a url", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-upstream", "ok", "N", "https://user:pw@h/", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-upstream", "ok", "N", "https://h/?x=1", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-upstream", "ok", "N", "", "o/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-path-prefix", "ok", "N", "https://pkgs.dev.azure.com/", "../x/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-path-prefix", "ok", "N", "https://pkgs.dev.azure.com/", "/abs/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-path-prefix", "ok", "N", "https://pkgs.dev.azure.com/", "a b/", "https://pkgs.dev.azure.com/o/f", "5" },
        new object[] { "bad-feed", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "http://h/f", "5" },
        new object[] { "bad-feed", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "", "5" },
        new object[] { "bad-feed", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f#frag", "5" },
        new object[] { "feed-outside", "ok", "N", "https://pkgs.dev.azure.com/", "o", "https://pkgs.dev.azure.com/o-secret/f", "5" },   // "o" is "o/": a sibling org is outside
        new object[] { "feed-outside", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/other/f", "5" },
        new object[] { "bad-cooldown", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "-1" },
        new object[] { "bad-cooldown", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "3651" },
        new object[] { "bad-cooldown", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "ten" },
        new object[] { "bad-cooldown", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "" },
        new object[] { "bad-cooldown", "ok", "N", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/f", "1e2" },
    };

    [Theory]
    [MemberData(nameof(BadTenants))]
    public async Task Every_create_field_is_validated_and_a_bad_form_changes_nothing(string key, string id, string name, string upstream, string path, string feed, string cooldown)
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var r = await Post(b, "/tenant-create", NewTenantForm(id, name, upstream, path, feed, cooldown));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/tenants?r=" + key, Where(r));
        Assert.Equal(2, (await f.Tenants.ListAsync(default)).Count);
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task A_path_prefix_is_optional_and_a_missing_field_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants/nopath?r=created", Where(await Post(b, "/tenant-create", NewTenantForm(id: "nopath", path: ""))));
        Assert.Equal("", (await f.Tenants.GetAsync("nopath", default))!.AllowedPathPrefix);
        Assert.Equal("/tenants?r=bad-feed", Where(await Post(b, "/tenant-create", ("id", "x1"), ("name", "X"), ("upstream_base", "https://pkgs.dev.azure.com/"), ("cooldown", "1"))));
        // a repeated field is not understood either (it could be an attempt to smuggle a second value)
        var twice = await Post(b, "/tenant-create", NewTenantForm(id: "dup1").Append(("name", "Other")).ToArray());
        Assert.Equal("/tenants?r=bad-name", Where(twice));
        Assert.Null(await f.Tenants.GetAsync("dup1", default));
    }

    // ---- 2 to 5: the per-tenant actions

    [Fact]
    public async Task Add_and_remove_an_internal_prefix()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        Assert.Equal("/tenants/acme?r=prefix-added", Where(await Post(b, "/tenants/acme/prefix-add", ("prefix", "Contoso."))));
        Assert.Contains("Contoso.", (await Acme(f)).Policy.InternalPrefixes);
        Assert.Contains("Contoso.", (await b.GetTextAsync("/tenants/acme")).Body);

        // remove asks first: the first POST changes nothing and writes nothing
        var ask = await Post(b, "/tenants/acme/prefix-remove", ("prefix", "Contoso."));
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Contains("Please confirm", await ask.Content.ReadAsStringAsync());
        Assert.Contains("Contoso.", (await Acme(f)).Policy.InternalPrefixes);
        Assert.Single(await Trail(f));

        Assert.Equal("/tenants/acme?r=prefix-removed", Where(await Post(b, "/tenants/acme/prefix-remove", ("prefix", "Contoso."), ("confirm", "yes"))));
        Assert.DoesNotContain("Contoso.", (await Acme(f)).Policy.InternalPrefixes);
        Assert.Equal(new[] { "prefix.remove", "prefix.add" }, (await Trail(f)).Select(x => x.Action));
        Assert.All(await Trail(f), x => Assert.Equal("acme", x.TenantId));
    }

    [Fact]
    public async Task Set_and_remove_a_package_override()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        Assert.Equal("/tenants/acme?r=override-set", Where(await Post(b, "/tenants/acme/override-set", ("package", "Newtonsoft.Json"), ("days", "3"))));
        var o = Assert.Single((await Acme(f)).Policy.PackageOverrides);
        Assert.Equal(("newtonsoft.json", 3), (o.PackageId, o.CooldownDays));

        Assert.Equal(HttpStatusCode.OK, (await Post(b, "/tenants/acme/override-remove", ("package", "newtonsoft.json"))).StatusCode);
        Assert.Single((await Acme(f)).Policy.PackageOverrides);
        Assert.Equal("/tenants/acme?r=override-removed", Where(await Post(b, "/tenants/acme/override-remove", ("package", "newtonsoft.json"), ("confirm", "yes"))));
        Assert.Empty((await Acme(f)).Policy.PackageOverrides);
        Assert.Equal(new[] { "override.remove", "override.set" }, (await Trail(f)).Select(x => x.Action));
    }

    [Fact]
    public async Task Add_and_revoke_a_waiver_with_the_signed_in_name_as_creator()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        // a form field cannot name the creator: only the signed-in identity does
        var add = await Post(b, "/tenants/acme/waiver-add", ("package", "Some.Pkg"), ("version", "1.2.3"), ("days", "30"), ("reason", "urgent fix"), ("created_by", "mallory"), ("actor", "mallory"));
        Assert.Equal("/tenants/acme?r=waiver-added", Where(add));
        var w = Assert.Single((await Acme(f)).Policy.Waivers);
        Assert.Equal("Test Founder", w.CreatedBy);
        Assert.Equal("some.pkg", w.PackageId);
        Assert.Equal(f.Clock.UtcNow.AddDays(30), w.ExpiresAt);
        Assert.Equal("urgent fix", w.Reason);

        Assert.Equal(HttpStatusCode.OK, (await Post(b, "/tenants/acme/waiver-revoke", ("waiver", w.Id))).StatusCode);
        Assert.Single((await Acme(f)).Policy.Waivers);
        Assert.Equal("/tenants/acme?r=waiver-revoked", Where(await Post(b, "/tenants/acme/waiver-revoke", ("waiver", w.Id), ("confirm", "yes"))));
        Assert.Empty((await Acme(f)).Policy.Waivers);

        var rows = await Trail(f);
        Assert.Equal(new[] { "waiver.revoke", "waiver.add" }, rows.Select(x => x.Action));
        Assert.Contains("reason=urgent fix", rows[1].Detail);
        Assert.DoesNotContain("mallory", string.Join("|", rows.Select(x => x.ActorName + x.Detail)));
    }

    [Fact]   // #157: one active waiver per package and version
    public async Task A_duplicate_waiver_is_refused_with_its_own_message_and_recorded_as_failed()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants/acme?r=waiver-added", Where(await Post(b, "/tenants/acme/waiver-add", ("package", "Some.Pkg"), ("version", "1.2.3"), ("days", "30"), ("reason", "urgent fix"))));
        var again = await Post(b, "/tenants/acme/waiver-add", ("package", "some.pkg"), ("version", "1.2.3.0"), ("days", "5"), ("reason", "again"));
        Assert.Equal("/tenants/acme?r=waiver-duplicate", Where(again));
        Assert.Single((await Acme(f)).Policy.Waivers);
        Assert.Equal(new[] { "waiver.add.failed", "waiver.add", "waiver.add" }, (await Trail(f)).Select(x => x.Action));
        Assert.Contains("already has an active waiver", (await b.GetTextAsync("/tenants/acme?r=waiver-duplicate")).Body);
    }

    [Fact]   // #157: revoked_by is the signed-in name
    public async Task A_revoke_from_the_site_records_the_signed_in_name()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var w = await f.Tenants.AddWaiverAsync("acme", "p.q", "1.0.0", f.Clock.UtcNow.AddDays(5), "r", "x", default);
        Assert.Equal("/tenants/acme?r=waiver-revoked", Where(await Post(b, "/tenants/acme/waiver-revoke", ("waiver", w.Id), ("confirm", "yes"))));
        var row = Assert.Single(await f.Tenants.ListWaiversAsync("acme", 10, default));
        Assert.Equal("Test Founder", row.RevokedBy);
    }

    [Fact]
    public async Task A_waiver_of_another_tenant_cannot_be_revoked_through_this_tenants_page()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var w = await f.Tenants.AddWaiverAsync("globex", "p.q", "1.0.0", f.Clock.UtcNow.AddDays(5), "r", "x", default);
        var r = await Post(b, "/tenants/acme/waiver-revoke", ("waiver", w.Id), ("confirm", "yes"));
        Assert.Equal("/tenants/acme?r=not-found", Where(r));
        Assert.Single((await f.Tenants.GetAsync("globex", default))!.Policy.Waivers);
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task Suspend_asks_first_then_suspends_and_activate_brings_it_back()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        Assert.NotNull(await f.Tokens.AuthenticateAsync(token.Plaintext, default));

        var ask = await Post(b, "/tenants/acme/suspend");
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Contains("Please confirm", await ask.Content.ReadAsStringAsync());
        Assert.True((await Acme(f)).Active);
        Assert.Empty(await Trail(f));

        Assert.Equal("/tenants/acme?r=suspended", Where(await Post(b, "/tenants/acme/suspend", ("confirm", "yes"))));
        Assert.False((await Acme(f)).Active);
        Assert.Null(await f.Tokens.AuthenticateAsync(token.Plaintext, default));
        Assert.Contains("Suspended", (await b.GetTextAsync("/tenants/acme")).Body);

        Assert.Equal(HttpStatusCode.OK, (await Post(b, "/tenants/acme/activate")).StatusCode);   // weakening: asks first
        Assert.False((await Acme(f)).Active);
        Assert.Equal("/tenants/acme?r=activated", Where(await Post(b, "/tenants/acme/activate", ("confirm", "yes"))));
        Assert.True((await Acme(f)).Active);
        Assert.Equal(new[] { "tenant.activate", "tenant.suspend" }, (await Trail(f)).Select(x => x.Action));
    }

    [Fact]
    public async Task Set_cooldown_changes_the_days_and_bumps_the_policy_revision()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var before = (await Acme(f)).Policy.Revision;
        Assert.Equal("/tenants/acme?r=cooldown-set", Where(await Post(b, "/tenants/acme/cooldown", ("days", "21"))));
        var t = await Acme(f);
        Assert.Equal(21, t.Policy.CooldownDays);
        Assert.True(t.Policy.Revision > before);
        Assert.Equal("days=21", Assert.Single(await Trail(f)).Detail);
    }

    // ---- 6. tokens

    [Fact]
    public async Task Issue_token_shows_the_plaintext_once_and_nowhere_else()
    {
        var logs = new CaptureLogs();
        await using var f = await StartAsync(services: s => s.AddSingleton<ILoggerProvider>(logs));
        var b = await SignedIn(f);

        var issue = await Post(b, "/tenants/acme/token-issue", await IssueForm(b, "acme", "build agents", "automation"));
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        Assert.Null(issue.Headers.Location);                       // not a redirect, so nothing in a URL
        var body = await issue.Content.ReadAsStringAsync();
        var plaintext = TokenPattern.Match(body).Value;
        Assert.NotEmpty(plaintext);
        Assert.Contains("cannot be shown again", body);
        Assert.True(issue.Headers.CacheControl?.NoStore);

        // the token really is the customer's key
        var auth = await f.Tokens.AuthenticateAsync(plaintext, default);
        Assert.NotNull(auth);
        Assert.Equal("acme", auth!.Tenant.Id);
        Assert.Equal(TokenKind.Automation, Assert.Single(await f.Tokens.ListAsync("acme", default)).Kind);

        // it is in no cookie, no later page, no trail row, no log line
        Assert.DoesNotContain(b.SetCookieLog, c => c.Contains(plaintext));
        Assert.DoesNotContain(b.Cookies.Values, c => c.Contains(plaintext));
        foreach (var page in new[] { "/tenants", "/tenants/acme", "/tenants/acme?r=failed", "/actions", "/audit", "/audit?tenant=acme" })
            Assert.DoesNotContain(plaintext, (await b.GetTextAsync(page)).Body);
        var trail = await Trail(f);
        Assert.DoesNotContain(trail, x => (x.Detail + x.Action + x.ActorName + x.TenantId).Contains(plaintext) || x.Detail.Contains("pmt_"));
        Assert.Equal(new[] { "token.issue.id", "token.issue" }, trail.Select(x => x.Action));
        Assert.Equal("kind=Automation; label=\"build agents\"", trail[1].Detail);
        Assert.Equal("id=" + auth.TokenId, trail[0].Detail);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(plaintext));

        // reposting the same page does not show the old token again: it makes a new, different one
        var again = TokenPattern.Match(await (await Post(b, "/tenants/acme/token-issue", await IssueForm(b, "acme", "second", "developer"))).Content.ReadAsStringAsync()).Value;
        Assert.NotEqual(plaintext, again);
    }

    [Fact]
    public async Task Revoking_a_token_asks_first_and_only_revokes_a_token_of_this_tenant()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var mine = await f.Tokens.IssueAsync("acme", "mine", default);
        var theirs = await f.Tokens.IssueAsync("globex", "theirs", default);

        Assert.Equal(HttpStatusCode.OK, (await Post(b, "/tenants/acme/token-revoke", ("token", mine.TokenId))).StatusCode);
        Assert.NotNull(await f.Tokens.AuthenticateAsync(mine.Plaintext, default));
        Assert.Empty(await Trail(f));

        Assert.Equal("/tenants/acme?r=not-found", Where(await Post(b, "/tenants/acme/token-revoke", ("token", theirs.TokenId), ("confirm", "yes"))));
        Assert.NotNull(await f.Tokens.AuthenticateAsync(theirs.Plaintext, default));

        Assert.Equal("/tenants/acme?r=token-revoked", Where(await Post(b, "/tenants/acme/token-revoke", ("token", mine.TokenId), ("confirm", "yes"))));
        Assert.Null(await f.Tokens.AuthenticateAsync(mine.Plaintext, default));
        var row = Assert.Single(await Trail(f));
        Assert.Equal("token.revoke", row.Action);
        Assert.Contains(mine.TokenId, row.Detail);
        Assert.DoesNotContain(mine.Plaintext, row.Detail);
    }

    // ---- field validation for the per-tenant actions

    public static IEnumerable<object[]> BadActions() => new[]
    {
        new object[] { "prefix-add", "bad-prefix", "prefix=", "" },
        new object[] { "prefix-add", "bad-prefix", "prefix=*", "" },
        new object[] { "prefix-add", "bad-prefix", "prefix=has space", "" },
        new object[] { "prefix-add", "bad-prefix", "prefix=a", "" },
        new object[] { "prefix-add", "bad-prefix", "prefix=a*", "" },
        new object[] { "prefix-add", "bad-prefix", "prefix=**", "" },
        new object[] { "prefix-add", "bad-prefix", "prefix=" + "a/b", "" },
        new object[] { "override-set", "bad-package", "package=a b&days=3", "" },
        new object[] { "override-set", "bad-package", "package=&days=3", "" },
        new object[] { "override-set", "bad-cooldown", "package=ok.pkg&days=-1", "" },
        new object[] { "override-set", "bad-cooldown", "package=ok.pkg&days=99999", "" },
        new object[] { "override-set", "bad-cooldown", "package=ok.pkg&days=abc", "" },
        new object[] { "waiver-add", "bad-package", "package=a%20b&version=1.0.0&days=5&reason=r", "" },
        new object[] { "waiver-add", "bad-version", "package=p&version=&days=5&reason=r", "" },
        new object[] { "waiver-add", "bad-version", "package=p&version=1.0%200&days=5&reason=r", "" },
        new object[] { "waiver-add", "bad-days", "package=p&version=1.0.0&days=0&reason=r", "" },
        new object[] { "waiver-add", "bad-days", "package=p&version=1.0.0&days=3651&reason=r", "" },
        new object[] { "waiver-add", "bad-days", "package=p&version=1.0.0&days=-5&reason=r", "" },
        new object[] { "waiver-add", "bad-days", "package=p&version=1.0.0&days=&reason=r", "" },
        new object[] { "waiver-add", "bad-reason", "package=p&version=1.0.0&days=5&reason=", "" },
        new object[] { "waiver-add", "bad-reason", "package=p&version=1.0.0&days=5&reason=%20%20%20", "" },
        new object[] { "waiver-add", "bad-reason", "package=p&version=1.0.0&days=5&reason=two%0Alines", "" },
        new object[] { "cooldown", "bad-cooldown", "days=-3", "" },
        new object[] { "cooldown", "bad-cooldown", "days=3651", "" },
        new object[] { "cooldown", "bad-cooldown", "days=", "" },
        new object[] { "token-issue", "bad-label", "label=&kind=developer", "" },
        new object[] { "token-issue", "bad-label", "label=%20&kind=developer", "" },
        new object[] { "token-issue", "bad-kind", "label=ok&kind=admin", "" },
        new object[] { "token-issue", "bad-kind", "label=ok", "" },
        new object[] { "token-revoke", "bad-token-id", "token=tok_zz", "confirm=yes" },
        new object[] { "waiver-revoke", "bad-waiver-id", "waiver=nope", "confirm=yes" },
        new object[] { "prefix-remove", "not-found", "prefix=Absent.", "confirm=yes" },
    };

    [Theory]
    [MemberData(nameof(BadActions))]
    public async Task Every_field_is_validated_on_the_server_and_a_bad_form_changes_nothing(string verb, string key, string query, string extra)
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var before = await Acme(f);
        var fields = (query + "&" + extra).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Select(p => (p[0], WebUtility.UrlDecode(p[1]))).ToArray();
        var r = await Post(b, "/tenants/acme/" + verb, fields);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/tenants/acme?r=" + key, Where(r));
        var after = await Acme(f);
        Assert.Equal(before.Policy.Revision, after.Policy.Revision);   // every real change bumps the revision, so none happened
        Assert.Equal(before.Active, after.Active);
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task An_unknown_tenant_is_refused_and_nothing_is_written()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants?r=no-tenant", Where(await Post(b, "/tenants/ghost/prefix-add", ("prefix", "X."))));
        Assert.Equal("/tenants?r=no-tenant", Where(await Post(b, "/tenants/ghost/token-issue", ("label", "x"), ("kind", "developer"))));
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task A_message_key_that_is_not_ours_shows_nothing_and_nothing_is_reflected()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var body = (await b.GetTextAsync("/tenants/acme?r=%3Cscript%3Ealert(1)%3C/script%3E")).Body;
        Assert.DoesNotContain("alert(1)", body);
        Assert.DoesNotContain("role=\"status\"", body);
    }

    // ---- request checks

    [Fact]
    public async Task A_post_without_or_with_a_bad_antiforgery_token_is_refused_and_changes_nothing()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var good = await b.TokenFromAsync("/tenants");
        foreach (var path in new[] { "/tenant-create", "/tenants/acme/prefix-add", "/tenants/acme/suspend", "/tenants/acme/token-issue", "/tenants/acme/cooldown" })
        {
            foreach (var af in new[] { null, "", "garbage", good + "x" })
            {
                var r = await PostWith(b, path, af, Origin, ("prefix", "X."), ("confirm", "yes"), ("label", "l"), ("kind", "developer"), ("days", "1"));
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            }
            // a token but no antiforgery cookie
            var noCookie = await b.SendAsync(HttpMethod.Post, path, new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, good), new("confirm", "yes") },
                req => req.Headers.TryAddWithoutValidation("Origin", Origin), sendCookies: false);
            Assert.Contains((int)noCookie.StatusCode, new[] { 400, 401 });
        }
        AssertUntouched(await Acme(f));
        Assert.Equal(2, (await f.Tenants.ListAsync(default)).Count);
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
        Assert.Empty(await Trail(f));
    }

    private static void AssertUntouched(Tenant t)
    {
        Assert.True(t.Active);
        Assert.Equal(14, t.Policy.CooldownDays);
        Assert.Empty(t.Policy.InternalPrefixes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://evil.example")]
    [InlineData("http://admin.packmoat.test")]
    [InlineData("https://admin.packmoat.test:8443")]
    [InlineData("https://admin.packmoat.test.evil.example")]
    [InlineData("https://admin.packmoat.test/")]
    public async Task A_post_from_another_origin_or_with_none_is_refused_even_with_a_valid_token(string? origin)
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var r = await PostWith(b, "/tenants/acme/suspend", await b.TokenFromAsync("/tenants"), origin, ("confirm", "yes"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.True((await Acme(f)).Active);
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task A_cross_site_fetch_marker_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var form = new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, await b.TokenFromAsync("/tenants")), new("confirm", "yes") };
        var r = await b.SendAsync(HttpMethod.Post, "/tenants/acme/suspend", form, req =>
        {
            req.Headers.TryAddWithoutValidation("Origin", Origin);
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.True((await Acme(f)).Active);
    }

    [Fact]
    public async Task Signed_out_and_refused_people_change_nothing()
    {
        await using var f = await StartAsync();
        // signed out: a valid-looking token from the landing page, but no session
        var anon = f.Browser();
        var landing = await anon.GetAsync("/");
        var af = AdminBrowser.Field(await landing.Content.ReadAsStringAsync())!;
        foreach (var path in new[] { "/tenant-create", "/tenants/acme/suspend", "/tenants/acme/token-issue" })
        {
            var r = await PostWith(anon, path, af, Origin, ("confirm", "yes"), ("label", "l"), ("kind", "developer"));
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        Assert.True((await Acme(f)).Active);

        // someone not on the list never gets a session, so cannot post
        f.SignInAs(Tid, "99999999-9999-4999-8999-999999999999");
        var intruder = f.Browser();
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.SignInAsync()).StatusCode);
        Assert.False(intruder.HasCookie(AdminSite.CookieName));
        var ownToken = AdminBrowser.Field(await (await intruder.GetAsync("/")).Content.ReadAsStringAsync())!;
        var r2 = await PostWith(intruder, "/tenants/acme/suspend", ownToken, Origin, ("confirm", "yes"));
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        Assert.True((await Acme(f)).Active);
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task Antiforgery_layer_a_token_from_another_session_state_is_refused_with_400()
    {
        var list = new ToggleAllowlist();
        await using var f = await StartAsync(services: s => s.AddSingleton<AdminAllowlist>(list));
        var b = await SignedIn(f);
        var af = await b.TokenFromAsync("/tenants");
        Assert.Equal("/tenants/acme?r=cooldown-set", Where(await PostWith(b, "/tenants/acme/cooldown", af, Origin, ("days", "5"))));
        list.Deny = true;   // the cookie is now rejected, so the request is anonymous and the signed-in user's token no longer matches
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWith(b, "/tenants/acme/cooldown", af, Origin, ("days", "99"))).StatusCode);
        Assert.Equal(5, (await Acme(f)).Policy.CooldownDays);
    }

    [Fact]
    public async Task Allowlist_layer_a_valid_antiforgery_pair_with_a_cookie_of_a_person_now_off_the_list_gets_401()
    {
        var list = new ToggleAllowlist();
        await using var f = await StartAsync(services: s => s.AddSingleton<AdminAllowlist>(list));
        var signedIn = await SignedIn(f);
        var cookie = signedIn.Cookie(AdminSite.CookieName)!;
        list.Deny = true;

        // an anonymous browser gets a perfectly valid antiforgery cookie and field; only the stolen session cookie is added
        var b = f.Browser();
        var af = AdminBrowser.Field(await (await b.GetAsync("/")).Content.ReadAsStringAsync())!;
        b.SetCookie(AdminSite.CookieName, cookie);
        var r = await PostWith(b, "/tenants/acme/cooldown", af, Origin, ("days", "99"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(14, (await Acme(f)).Policy.CooldownDays);
        Assert.Empty(await Trail(f));

    }

    [Fact]
    public async Task A_get_to_any_action_url_changes_nothing()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var urls = new[]
        {
            "/tenant-create?id=zz&name=Z&upstream_base=https://h/&feed=https://h/f&cooldown=1",
            "/tenants/acme/prefix-add?prefix=X.", "/tenants/acme/prefix-remove?prefix=X.&confirm=yes", "/tenants/acme/override-set?package=p&days=1",
            "/tenants/acme/override-remove?package=p&confirm=yes", "/tenants/acme/waiver-add?package=p&version=*&days=1&reason=r",
            "/tenants/acme/waiver-revoke?waiver=wvr_000000000000&confirm=yes", "/tenants/acme/suspend?confirm=yes", "/tenants/acme/activate",
            "/tenants/acme/cooldown?days=1", "/tenants/acme/token-issue?label=l&kind=developer", "/tenants/acme/token-revoke?token=tok_000000000000&confirm=yes",
        };
        foreach (var u in urls)
        {
            var (status, body, _) = await b.GetTextAsync(u);
            Assert.Equal(404, status);
            Assert.DoesNotContain("Please confirm", body);
        }
        var t = await Acme(f);
        AssertUntouched(t);
        Assert.Empty(t.Policy.Waivers);
        Assert.Empty(t.Policy.PackageOverrides);
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
        Assert.Null(await f.Tenants.GetAsync("zz", default));
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task A_post_to_an_unknown_path_is_404_and_a_body_over_the_admin_limit_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, "/tenants/acme/explode")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, "/tenants/acme")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, "/tenants/acme/prefix-add/extra")).StatusCode);

        var big = await Post(b, "/tenants/acme/waiver-add", ("package", "p"), ("version", "*"), ("days", "5"), ("reason", new string('x', 6000)));
        Assert.True(big.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, big.StatusCode.ToString());
        Assert.Empty((await Acme(f)).Policy.Waivers);
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public async Task The_longest_allowed_form_still_fits_the_four_kilobyte_limit()
    {
        var host = string.Join(".", Enumerable.Repeat(new string('h', 40), 2)) + ".example";
        await using var f = await StartAsync(o => o.AdminUpstreamHosts = host);
        var b = await SignedIn(f);
        // worst case: every character of the longest fields is a three-byte letter, which the browser percent-encodes to nine bytes
        var path = string.Join("/", Enumerable.Repeat(new string('p', 30), 2));
        var r = await Post(b, "/tenant-create", NewTenantForm(id: new string('a', 63), name: new string('\u00e9', AdminInput.MaxName),
            upstream: "https://" + host, path: path, feed: "https://" + host + "/" + path + "/" + new string('f', 100) + "/index.json"));
        Assert.Equal("/tenants/" + new string('a', 63) + "?r=created", Where(r));
        var w = await Post(b, "/tenants/acme/waiver-add", ("package", new string('p', 100)), ("version", new string('1', 64)), ("days", "364"), ("reason", new string('\u20ac', AdminInput.MaxReason)));
        Assert.Equal("/tenants/acme?r=waiver-added", Where(w));
    }

    [Fact]
    public async Task Write_posts_are_rate_limited_per_address()
    {
        await using var f = await StartAsync(services: s => s.AddSingleton(sp => new AdminWriteLimiter(sp.GetRequiredService<IClock>(), 3)));
        var b = await SignedIn(f);
        var af = await b.TokenFromAsync("/tenants");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.SeeOther, (await PostWith(b, "/tenants/acme/cooldown", af, Origin, ("days", "5"))).StatusCode);
        var blocked = await PostWith(b, "/tenants/acme/cooldown", af, Origin, ("days", "6"));
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.True(blocked.Headers.Contains("Retry-After"));
        Assert.Equal(5, (await Acme(f)).Policy.CooldownDays);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.SeeOther, (await PostWith(b, "/tenants/acme/cooldown", af, Origin, ("days", "7"))).StatusCode);
        Assert.Equal(7, (await Acme(f)).Policy.CooldownDays);
    }

    [Fact]
    public async Task The_confirm_step_is_itself_a_protected_post_and_carries_its_own_token()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var ask = await Post(b, "/tenants/acme/suspend");
        var html = await ask.Content.ReadAsStringAsync();
        Assert.Contains("name=\"confirm\" value=\"yes\"", html);
        var token = AdminBrowser.Field(html);
        Assert.NotNull(token);
        Assert.Contains("action=\"/tenants/acme/suspend\"", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWith(b, "/tenants/acme/suspend", null, Origin, ("confirm", "yes"))).StatusCode);
        Assert.True((await Acme(f)).Active);
        Assert.Equal(HttpStatusCode.SeeOther, (await PostWith(b, "/tenants/acme/suspend", token, Origin, ("confirm", "yes"))).StatusCode);
        Assert.False((await Acme(f)).Active);
    }

    // ---- the trail

    [Fact]
    public async Task If_the_trail_cannot_be_written_the_action_does_not_happen()
    {
        var logs = new CaptureLogs();
        await using var f = await StartAsync(services: s => { s.AddSingleton<IAdminActionLog>(new ThrowingActionLog()); s.AddSingleton<ILoggerProvider>(logs); });
        var b = await SignedIn(f);

        Assert.Equal("/tenants/acme?r=trail-failed", Where(await Post(b, "/tenants/acme/prefix-add", ("prefix", "X."))));
        Assert.Equal("/tenants/acme?r=trail-failed", Where(await Post(b, "/tenants/acme/suspend", ("confirm", "yes"))));
        Assert.Equal("/tenants/acme?r=trail-failed", Where(await Post(b, "/tenants/acme/token-issue", await IssueForm(b, "acme", "l", "developer"))));
        Assert.Equal("/tenants?r=trail-failed", Where(await Post(b, "/tenant-create", NewTenantForm())));
        var t = await Acme(f);
        Assert.Empty(t.Policy.InternalPrefixes);
        Assert.True(t.Active);
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
        Assert.Null(await f.Tenants.GetAsync("newco", default));
        Assert.Contains("NOT done", (await b.GetTextAsync("/tenants/acme?r=trail-failed")).Body);
        Assert.Contains(logs.Lines, l => l.Contains("trail write failed") && l.Contains("InvalidOperationException"));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("disk full"));   // type only, never the message
    }

    [Fact]
    public async Task A_failed_token_issue_is_recorded_as_failed_and_shows_no_token()
    {
        AdminSiteFixture? holder = null;
        await using var f = await StartAsync(services: s => s.AddSingleton<ITokenService>(sp => new ThrowingTokens(holder!.Tokens)));
        holder = f;
        var b = await SignedIn(f);
        var r = await Post(b, "/tenants/acme/token-issue", await IssueForm(b, "acme", "ci", "developer"));
        Assert.Equal("/tenants/acme?r=failed", Where(r));
        Assert.Equal(new[] { "token.issue.failed", "token.issue" }, (await Trail(f)).Select(x => x.Action));
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task The_trail_table_refuses_update_and_delete()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        await Post(b, "/tenants/acme/cooldown", ("days", "5"));
        await using var c = await f.Db.OpenAsync();
        foreach (var sql in new[] { "UPDATE admin_actions SET detail='x'", "DELETE FROM admin_actions" })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var ex = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("append-only", ex.Message);
        }
        Assert.Single(await Trail(f));
    }

    [Fact]
    public async Task The_actions_page_lists_the_latest_hundred_newest_first_and_needs_a_session()
    {
        await using var f = await StartAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await f.Browser().GetAsync("/actions")).StatusCode);

        var b = await SignedIn(f);
        for (var i = 0; i < 105; i++)
            await f.Actions.AppendAsync(new AdminActionRecord(f.Clock.UtcNow.AddSeconds(i), Tid, Oid, "Test Founder", "tenant.cooldown", "acme", "marker-" + i.ToString("D3")), default);
        var (status, body, _) = await b.GetTextAsync("/actions");
        Assert.Equal(200, status);
        Assert.Equal(100, Regex.Matches(body, "marker-\\d{3}").Count);
        Assert.Contains("marker-104", body);
        Assert.DoesNotContain("marker-004", body);
        Assert.True(body.IndexOf("marker-104", StringComparison.Ordinal) < body.IndexOf("marker-103", StringComparison.Ordinal));
        Assert.Contains(Tid, body);
        Assert.Contains(Oid, body);
    }

    // ---- hostile text

    private const string Xss1 = "<script>alert(1)</script>";
    private const string Xss2 = "\"><img src=x onerror=alert(2)>";
    private const string Xss3 = "'><svg onload=alert(3)>";

    private static void AssertEncoded(string html)
    {
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<svg onload", html);
        Assert.DoesNotContain("onerror=alert(2)>", html);
    }

    [Fact]
    public async Task Hostile_text_in_names_labels_and_reasons_is_encoded_on_every_page_including_the_trail()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        Assert.Equal("/tenants/xss?r=created", Where(await Post(b, "/tenant-create", NewTenantForm(id: "xss", name: Xss1 + Xss2))));
        Assert.Equal("/tenants/xss?r=waiver-added", Where(await Post(b, "/tenants/xss/waiver-add", ("package", "p.q"), ("version", "1.0.0"), ("days", "9"), ("reason", Xss2 + Xss3))));
        var issue = await Post(b, "/tenants/xss/token-issue", await IssueForm(b, "xss", Xss3 + Xss1, "developer"));
        var issueBody = await issue.Content.ReadAsStringAsync();
        AssertEncoded(issueBody);
        Assert.Contains("&lt;script&gt;", issueBody);

        foreach (var page in new[] { "/tenants", "/tenants/xss", "/actions", "/audit?tenant=xss" })
        {
            var body = (await b.GetTextAsync(page)).Body;
            AssertEncoded(body);
        }
        var actions = (await b.GetTextAsync("/actions")).Body;
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", actions);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", actions);
        var tenantPage = (await b.GetTextAsync("/tenants/xss")).Body;
        Assert.Contains("&lt;svg onload=alert(3)&gt;", tenantPage);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", tenantPage);

        // the confirm page echoes hostile stored text (a token label) only encoded
        var tok = (await f.Tokens.ListAsync("xss", default)).Single();
        var ask = await (await Post(b, "/tenants/xss/token-revoke", ("token", tok.TokenId))).Content.ReadAsStringAsync();
        AssertEncoded(ask);
        Assert.Contains("&lt;svg onload=alert(3)&gt;", ask);
    }

    [Fact]
    public async Task Hostile_text_in_identifier_fields_is_refused_and_never_echoed()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        foreach (var (verb, field) in new[] { ("prefix-add", "prefix"), ("override-set", "package"), ("waiver-add", "package"), ("prefix-remove", "prefix") })
        {
            var r = await Post(b, "/tenants/acme/" + verb, (field, Xss1), ("days", "5"), ("version", "*"), ("reason", "r"), ("confirm", "yes"));
            Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
            Assert.DoesNotContain("script", Where(r));
            var page = (await b.GetTextAsync(Where(r))).Body;
            Assert.DoesNotContain("alert(1)", page);
        }
        var rid = await Post(b, "/tenant-create", NewTenantForm(id: Xss1));
        Assert.DoesNotContain("script", Where(rid));
        Assert.Empty(await Trail(f));

        // a hostile display name from the identity provider is encoded in the trail too
        f.Identity.Next = FakeIdentityProvider.Person(Tid, Oid, name: Xss1);
        var b2 = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b2.SignInAsync()).StatusCode);
        await Post(b2, "/tenants/acme/cooldown", ("days", "3"));
        var actions = (await b2.GetTextAsync("/actions")).Body;
        AssertEncoded(actions);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", actions);
    }

    // ---- who may act

    [Fact]
    public async Task The_page_only_ever_acts_as_the_operator_named_after_the_signed_in_person()
    {
        var principal = FakeIdentityProvider.Person(Tid, Oid, name: "Ada");
        var actor = AdminWrites.ActorFor(principal);
        Assert.True(actor.IsOperator);
        Assert.Null(actor.TenantId);
        Assert.Equal("Ada", actor.Name);

        // an identity that claims a tenant of its own still gets the operator scope named after the person, never a tenant scope
        var sneaky = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("name", "x"), new Claim("tenant", "acme"), new Claim("tenant_id", "acme"), new Claim("oid", Oid) }, "t"));
        Assert.Null(AdminWrites.ActorFor(sneaky).TenantId);
        // no display name: the object id names the actor
        Assert.Equal(Oid, AdminWrites.ActorFor(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Oid) }, "t"))).Name);

        // and a tenant-scoped actor, which the page cannot create, is refused every command that is not its own
        await using var f = await StartAsync();
        var commands = new AdminCommands(f.Tenants, f.Tokens, f.Clock);
        var scoped = ActorScope.ForTenant("acme");
        await Assert.ThrowsAsync<AccessDeniedException>(() => commands.CreateTenantAsync(scoped, new NewTenant("zz", "Z", "https://h/", "", "https://h/f", 1, TenantOrigin.Operator), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => commands.SuspendTenantAsync(scoped, "globex", default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => commands.IssueTokenAsync(scoped, "globex", "l", default));
    }

    // ---- upstream and feed are the credential's destination

    private const string Az = "https://pkgs.dev.azure.com/";

    [Theory]
    [InlineData("https://10.0.0.1/", "upstream-host")]
    [InlineData("https://169.254.169.254/", "upstream-host")]
    [InlineData("https://127.0.0.1/", "upstream-host")]
    [InlineData("https://[::1]/", "upstream-host")]
    [InlineData("https://localhost/", "upstream-host")]
    [InlineData("https://evil.example/", "upstream-host")]
    [InlineData("https://pkgs.dev.azure.com.evil.example/", "upstream-host")]
    [InlineData("https://xpkgs.dev.azure.com/", "upstream-host")]
    [InlineData("https://user@pkgs.dev.azure.com/", "bad-upstream")]
    [InlineData("https://user:pw@pkgs.dev.azure.com/", "bad-upstream")]
    [InlineData("https://pkgs.dev.azure.com@evil.example/", "bad-upstream")]
    [InlineData("https://pkgs.dev.azure.com:8443/", "bad-upstream")]
    [InlineData("http://pkgs.dev.azure.com/", "bad-upstream")]
    [InlineData("https://pkgs.dev.azure.com\\@evil.example/", "bad-upstream")]
    [InlineData("https://pkgs.dev.azure.com/%2e%2e/", "bad-upstream")]
    [InlineData("https://pkgs.dev.azure.com/%2F/", "bad-upstream")]
    public async Task The_upstream_must_be_an_allowed_host_over_plain_https(string upstream, string key)
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants?r=" + key, Where(await Post(b, "/tenant-create", NewTenantForm(upstream: upstream, path: "", feed: Az + "f"))));
        Assert.Null(await f.Tenants.GetAsync("newco", default));
        Assert.Empty(await Trail(f));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/other/f", "feed-outside")]
    [InlineData("https://pkgs.dev.azure.com/org", "feed-outside")]
    [InlineData("https://pkgs.dev.azure.com/o/../x/f", "feed-outside")]
    [InlineData("https://evil.example/org/f", "feed-outside")]
    [InlineData("https://pkgs.dev.azure.com:8443/org/f", "bad-feed")]
    [InlineData("http://pkgs.dev.azure.com/org/f", "bad-feed")]
    [InlineData("https://user@pkgs.dev.azure.com/org/f", "bad-feed")]
    [InlineData("https://pkgs.dev.azure.com/org/%2F", "bad-feed")]
    [InlineData("https://pkgs.dev.azure.com/org/%2f", "bad-feed")]
    [InlineData("https://pkgs.dev.azure.com/org\\f", "bad-feed")]
    public async Task The_feed_must_lie_under_the_upstream_base_and_path_prefix(string feed, string key)
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants?r=" + key, Where(await Post(b, "/tenant-create", NewTenantForm(upstream: Az, path: "org", feed: feed))));
        Assert.Null(await f.Tenants.GetAsync("newco", default));
    }

    [Fact]
    public async Task The_canonical_form_of_both_urls_is_what_is_stored_and_more_hosts_can_be_allowed_by_configuration()
    {
        await using var f = await StartAsync(o => o.AdminUpstreamHosts = "pkgs.dev.azure.com, feeds.example.com");
        var b = await SignedIn(f);
        Assert.Equal("/tenants/newco?r=created", Where(await Post(b, "/tenant-create", NewTenantForm(upstream: "https://PKGS.dev.azure.com:443", path: "org", feed: "https://PKGS.Dev.Azure.com/org/p/_packaging/f/nuget/v3/index.json"))));
        var t = (await f.Tenants.GetAsync("newco", default))!;
        Assert.Equal("https://pkgs.dev.azure.com/", t.UpstreamBase);
        Assert.Equal("https://pkgs.dev.azure.com/org/p/_packaging/f/nuget/v3/index.json", t.UpstreamFeedIndexUrl);
        Assert.Equal("/tenants/other?r=created", Where(await Post(b, "/tenant-create", NewTenantForm(id: "other", upstream: "https://feeds.example.com/", path: "x", feed: "https://feeds.example.com/x/v3/index.json"))));
    }

    // ---- weakening actions ask first

    [Fact]
    public async Task Weakening_actions_ask_before_they_change_anything()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var asks = new (string Path, (string, string)[] Fields)[]
        {
            ("/tenants/acme/waiver-add", new[] { ("package", "p.q"), ("version", "*"), ("days", "5"), ("reason", "r") }),
            ("/tenants/acme/waiver-add", new[] { ("package", "p.q"), ("version", "1.*"), ("days", "5"), ("reason", "r") }),
            ("/tenants/acme/waiver-add", new[] { ("package", "p.q"), ("version", "1.0.0"), ("days", "365"), ("reason", "r") }),
            ("/tenants/acme/cooldown", new[] { ("days", "0") }),
            ("/tenants/acme/cooldown", new[] { ("days", "2") }),
            ("/tenants/acme/override-set", new[] { ("package", "p.q"), ("days", "0") }),
            ("/tenants/acme/override-set", new[] { ("package", "p.q"), ("days", "2") }),
        };
        foreach (var (path, fields) in asks)
        {
            var r = await Post(b, path, fields);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("Please confirm", await r.Content.ReadAsStringAsync());
        }
        var t = await Acme(f);
        Assert.Empty(t.Policy.Waivers);
        Assert.Empty(t.Policy.PackageOverrides);
        Assert.Equal(14, t.Policy.CooldownDays);
        Assert.Empty(await Trail(f));

        // confirming goes through, carrying every field over
        var yes = await Post(b, "/tenants/acme/waiver-add", ("package", "p.q"), ("version", "*"), ("days", "5"), ("reason", "r"), ("confirm", "yes"));
        Assert.Equal("/tenants/acme?r=waiver-added", Where(yes));
        Assert.Equal("*", Assert.Single((await Acme(f)).Policy.Waivers).Version);
        Assert.Equal("/tenants/acme?r=cooldown-set", Where(await Post(b, "/tenants/acme/cooldown", ("days", "0"), ("confirm", "yes"))));
        Assert.Equal(0, (await Acme(f)).Policy.CooldownDays);

        // and the sane values do not ask
        Assert.Equal("/tenants/acme?r=waiver-added", Where(await Post(b, "/tenants/acme/waiver-add", ("package", "p.r"), ("version", "1.0.0"), ("days", "364"), ("reason", "r"))));
        Assert.Equal("/tenants/acme?r=cooldown-set", Where(await Post(b, "/tenants/acme/cooldown", ("days", "3"))));
        Assert.Equal("/tenants/acme?r=override-set", Where(await Post(b, "/tenants/acme/override-set", ("package", "p.q"), ("days", "3"))));
    }

    // ---- token form is single use

    [Fact]
    public async Task One_rendered_issue_form_issues_one_token_and_a_resubmit_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var page = (await b.GetTextAsync("/tenants/acme")).Body;
        Assert.Contains("name=\"nonce\"", page);
        var form = await IssueForm(b, "acme", "ci", "developer");

        var first = await Post(b, "/tenants/acme/token-issue", form);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("ANOTHER live token", await first.Content.ReadAsStringAsync());

        var again = await Post(b, "/tenants/acme/token-issue", form);   // a reload or back-and-resubmit of that exact POST
        Assert.Equal("/tenants/acme?r=reload", Where(again));
        Assert.Single(await f.Tokens.ListAsync("acme", default));
        Assert.Equal(new[] { "token.issue.id", "token.issue" }, (await Trail(f)).Select(x => x.Action));
    }

    [Fact]
    public async Task A_missing_foreign_or_expired_nonce_issues_nothing()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants/acme?r=reload", Where(await Post(b, "/tenants/acme/token-issue", ("label", "ci"), ("kind", "developer"))));
        Assert.Equal("/tenants/acme?r=reload", Where(await Post(b, "/tenants/acme/token-issue", ("label", "ci"), ("kind", "developer"), ("nonce", "00"))));

        var forGlobex = await IssueForm(b, "globex", "ci", "developer");   // a nonce issued for another tenant's form
        Assert.Equal("/tenants/acme?r=reload", Where(await Post(b, "/tenants/acme/token-issue", forGlobex)));

        var stale = await IssueForm(b, "acme", "ci", "developer");
        f.Clock.Advance(TimeSpan.FromMinutes(31));
        b = await SignedIn(f);   // the session itself lapsed in the meantime
        Assert.Equal("/tenants/acme?r=reload", Where(await Post(b, "/tenants/acme/token-issue", stale)));
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
        Assert.Empty(await f.Tokens.ListAsync("globex", default));
        Assert.Empty(await Trail(f));
    }

    // ---- invisible and direction-changing characters

    [Theory]
    [InlineData("\u202e")]   // right-to-left override
    [InlineData("\u200b")]   // zero-width space
    [InlineData("\u2066")]   // left-to-right isolate
    [InlineData("\ufeff")]   // byte order mark
    [InlineData("\u2028")]   // line separator
    [InlineData("\u2029")]   // paragraph separator
    public async Task Invisible_and_direction_characters_are_refused_in_text_fields(string c)
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants?r=bad-name", Where(await Post(b, "/tenant-create", NewTenantForm(name: "Acme" + c + "Corp"))));
        Assert.Equal("/tenants/acme?r=bad-reason", Where(await Post(b, "/tenants/acme/waiver-add", ("package", "p"), ("version", "1.0.0"), ("days", "5"), ("reason", "fi" + c + "x"))));
        Assert.Equal("/tenants/acme?r=bad-label", Where(await Post(b, "/tenants/acme/token-issue", ("label", "c" + c + "i"), ("kind", "developer"), ("nonce", "x"))));
        Assert.Equal("/tenants/acme?r=bad-package", Where(await Post(b, "/tenants/acme/override-set", ("package", "pk" + c + "g"), ("days", "5"))));
        Assert.Equal("/tenants/acme?r=bad-package", Where(await Post(b, "/tenants/acme/waiver-add", ("package", "p" + c + "kg"), ("version", "1.0.0"), ("days", "5"), ("reason", "r"))));
        Assert.Null(await f.Tenants.GetAsync("newco", default));
        Assert.Empty((await Acme(f)).Policy.Waivers);
        Assert.Empty(await Trail(f));
    }

    // ---- repeated form keys

    [Fact]
    public async Task A_form_key_sent_twice_is_refused_not_resolved()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal("/tenants/acme?r=bad-prefix", Where(await Post(b, "/tenants/acme/prefix-add", ("prefix", "Good."), ("prefix", "Evil."))));
        Assert.Equal("/tenants/acme?r=bad-cooldown", Where(await Post(b, "/tenants/acme/cooldown", ("days", "20"), ("days", "30"))));
        Assert.Equal("/tenants/acme?r=bad-label", Where(await Post(b, "/tenants/acme/token-issue", ("label", "a"), ("label", "b"), ("kind", "developer"), ("nonce", "x"))));
        Assert.Equal("/tenants/acme?r=bad-kind", Where(await Post(b, "/tenants/acme/token-issue", ("label", "a"), ("kind", "developer"), ("kind", "automation"), ("nonce", "x"))));
        Assert.Equal("/tenants/acme?r=bad-package", Where(await Post(b, "/tenants/acme/override-set", ("package", "a.b"), ("package", "c.d"), ("days", "5"))));
        var t = await Acme(f);
        Assert.Empty(t.Policy.InternalPrefixes);
        Assert.Equal(14, t.Policy.CooldownDays);
        Assert.Empty(await Trail(f));
    }

    [Fact]
    public void Admin_site_code_never_builds_a_tenant_scope()
    {
        // The core's dotnet folder (Packmoat.sln), so the scan runs in core/ alone too (#197).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        var files = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "Packmoat.Host", "Admin"), "*.cs");
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.DoesNotContain("ForTenant", File.ReadAllText(file));
    }
}
