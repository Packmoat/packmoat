using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

/// <summary>
/// The read-only "Decisions" panel on the admin site's tenant page (#123 PR 2): the operator only, one tenant, newest last seen first,
/// bounded, a package search that is exact or a prefix, and every client-supplied id and version HTML-encoded.
/// </summary>
[Collection("sqlite-pools")]
public class AdminDecisionsPanelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static DecisionRecord Rec(string package, string version = "1.0.0", string tenant = "acme", int minutes = 0, Decision decision = Decision.Hide,
        VerdictReason reason = VerdictReason.Cooldown, int? cooldown = 14, DateTimeOffset? published = null, long revision = 3, long count = 1, string? waiver = null) =>
        new(tenant, Ecosystem.NuGet, package, version, decision, reason, waiver, cooldown, published, revision, T0, T0.AddMinutes(minutes), count);

    private static async Task<AdminBrowser> SignedIn(AdminSiteFixture f)
    {
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);
        return b;
    }

    /// <summary>The panel: from its heading to the end of the page.</summary>
    private static string Panel(string body)
    {
        var at = body.IndexOf("<h2 id=\"decisions\">", StringComparison.Ordinal);
        Assert.True(at >= 0, "no Decisions panel on the page");
        return body[at..];
    }

    /// <summary>The panel's data rows (its header row excluded).</summary>
    private static string[] Rows(string panel) =>
        Regex.Matches(panel, "<tr>(<td>.*?)</tr>").Select(m => m.Groups[1].Value).ToArray();

    [Fact]
    public async Task The_tenant_page_links_to_the_panel_next_to_the_audit_trail_and_shows_every_field()
    {
        await using var f = await StartAsync();
        await f.Decisions.UpsertAsync(new[]
        {
            Rec("moq", "4.21.0", minutes: 9, decision: Decision.Allow, reason: VerdictReason.CooldownElapsed, cooldown: 7, published: T0.AddDays(-30), revision: 4, count: 2),
            Rec("moq", "4.20.0", minutes: 5, decision: Decision.Allow, reason: VerdictReason.Waiver, cooldown: null, waiver: "w-9"),
            Rec("theirs", tenant: "globex", minutes: 60),
        }, default);
        var b = await SignedIn(f);

        var (status, body, _) = await b.GetTextAsync("/tenants/acme");
        Assert.Equal(200, status);
        Assert.Contains("Audit trail</a> <a href=\"#decisions\">Decisions</a>", body);
        var panel = Panel(body);
        Assert.Contains("<th>Package</th><th>Version</th><th>Decision</th><th>Reason</th><th>Cooldown days</th><th>Published</th><th>First seen</th><th>Last seen</th><th>Decisions</th><th>Policy revision</th><th>Waiver</th>", panel);
        Assert.DoesNotContain("download", panel.Replace("listings and downloads alike, not downloads", ""), StringComparison.OrdinalIgnoreCase);
        var rows = Rows(panel);
        Assert.Equal(2, rows.Length);
        Assert.Equal("<td>moq</td><td>4.21.0</td><td>Allow</td><td>CooldownElapsed</td><td>7</td><td>2026-09-10 12:00:00Z</td><td>2026-10-10 12:00:00Z</td><td>2026-10-10 12:09:00Z</td><td>2</td><td>4</td><td></td>", rows[0]);
        Assert.Equal("<td>moq</td><td>4.20.0</td><td>Allow</td><td>Waiver</td><td>none</td><td>unknown</td><td>2026-10-10 12:00:00Z</td><td>2026-10-10 12:05:00Z</td><td>1</td><td>3</td><td>w-9</td>", rows[1]);
        Assert.DoesNotContain("theirs", body);
    }

    [Fact]
    public async Task Client_supplied_ids_and_versions_are_html_encoded_everywhere()
    {
        await using var f = await StartAsync();
        const string evilId = "<script>alert(1)</script>";
        const string evilVersion = "\"><img src=x onerror=alert(2)>";
        await f.Decisions.UpsertAsync(new[] { Rec(evilId, evilVersion) }, default);
        var b = await SignedIn(f);

        // Also searched for, so the search box echoes the same text back.
        var (status, body, _) = await b.GetTextAsync("/tenants/acme?package=" + Uri.EscapeDataString("<script>") + "&match=prefix&version=" + Uri.EscapeDataString("\"><b>"));
        Assert.Equal(200, status);
        Assert.DoesNotContain("<script>", body);
        Assert.DoesNotContain("<img", body);
        Assert.DoesNotContain("\"><b>", body);
        Assert.Contains("&lt;script&gt;", body);

        var all = (await b.GetTextAsync("/tenants/acme")).Body;
        Assert.DoesNotContain("<script>", all);
        Assert.DoesNotContain("<img", all);
        Assert.Single(Rows(Panel(all)));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", all);
    }

    [Fact]
    public async Task The_search_is_exact_by_default_or_a_prefix_and_a_version_narrows_it()
    {
        await using var f = await StartAsync();
        await f.Decisions.UpsertAsync(new[] { Rec("moq", "4.21.0", minutes: 3), Rec("moq", "4.20.0", minutes: 2), Rec("moq.extra", minutes: 1), Rec("a_b"), Rec("axb") }, default);
        var b = await SignedIn(f);

        string[] Packages(string body) => Rows(Panel(body)).Select(r => Regex.Match(r, "^<td>(.*?)</td><td>(.*?)</td>").Groups is var g ? g[1].Value + "@" + g[2].Value : "").ToArray();

        Assert.Equal(new[] { "moq@4.21.0", "moq@4.20.0" }, Packages((await b.GetTextAsync("/tenants/acme?package=MOQ")).Body));
        Assert.Equal(new[] { "moq@4.21.0", "moq@4.20.0", "moq.extra@1.0.0" }, Packages((await b.GetTextAsync("/tenants/acme?package=moq&match=prefix")).Body));
        Assert.Equal(new[] { "moq@4.20.0" }, Packages((await b.GetTextAsync("/tenants/acme?package=moq&version=4.20")).Body));
        Assert.Equal(new[] { "a_b@1.0.0" }, Packages((await b.GetTextAsync("/tenants/acme?package=a_&match=prefix")).Body));      // '_' is not a wildcard
        Assert.Empty(Packages((await b.GetTextAsync("/tenants/acme?package=%25&match=prefix")).Body));                            // nor is '%'
        // The search box keeps what was searched, encoded.
        var kept = (await b.GetTextAsync("/tenants/acme?package=moq&match=prefix")).Body;
        Assert.Contains("name=\"package\" value=\"moq\"", kept);
        Assert.Contains("<option value=\"prefix\" selected>", kept);
    }

    [Fact]
    public async Task The_panel_is_bounded_and_says_when_more_rows_match()
    {
        await using var f = await StartAsync();
        await f.Decisions.UpsertAsync(Enumerable.Range(0, AdminSite.DecisionsLimit + 1).Select(i => Rec("pkg", $"1.0.{i}", minutes: i)).ToList(), default);
        var b = await SignedIn(f);

        var panel = Panel((await b.GetTextAsync("/tenants/acme")).Body);
        var rows = Rows(panel);
        Assert.Equal(AdminSite.DecisionsLimit, rows.Length);
        Assert.StartsWith($"<td>pkg</td><td>1.0.{AdminSite.DecisionsLimit}</td>", rows[0]);   // newest last seen first
        Assert.Contains("More rows match", panel);
        Assert.Contains($"Newest {AdminSite.DecisionsLimit} rows", panel);
    }

    [Theory]
    [InlineData("?version=1.0.0")]                     // a version needs a package
    [InlineData("?match=prefix")]                      // an empty prefix
    [InlineData("?match=other&package=moq")]           // not a match kind
    public async Task A_search_that_is_not_valid_shows_a_fixed_message_and_no_rows(string query)
    {
        await using var f = await StartAsync();
        await f.Decisions.UpsertAsync(new[] { Rec("moq") }, default);
        var b = await SignedIn(f);
        var (status, body, _) = await b.GetTextAsync("/tenants/acme" + query);
        Assert.Equal(200, status);
        var panel = Panel(body);
        Assert.Contains("That search is not valid", panel);
        Assert.Empty(Rows(panel));
    }

    [Fact]
    public async Task A_search_term_longer_than_the_record_keeps_is_refused_without_echoing_it_in_the_message()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var (status, body, _) = await b.GetTextAsync("/tenants/acme?package=" + new string('q', RecordingPackageGate.MaxFieldLength + 1));
        Assert.Equal(200, status);
        Assert.Contains("That search is not valid", Panel(body));
    }

    [Fact]
    public async Task Signed_out_the_panel_and_its_search_send_you_to_the_landing_page()
    {
        await using var f = await StartAsync();
        await f.Decisions.UpsertAsync(new[] { Rec("moq") }, default);
        var r = await f.Browser().GetAsync("/tenants/acme?package=moq");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.OriginalString);
        Assert.DoesNotContain("moq", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Someone_not_on_the_allowlist_never_sees_the_panel()
    {
        await using var f = await StartAsync();
        await f.Decisions.UpsertAsync(new[] { Rec("secret.pkg") }, default);
        f.SignInAs(Tid, "99999999-9999-4999-8999-999999999999");
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SignInAsync()).StatusCode);
        var r = await b.GetAsync("/tenants/acme");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.DoesNotContain("secret.pkg", await r.Content.ReadAsStringAsync());
    }

    private sealed class BrokenDecisionQuery : IDecisionRecordQuery
    {
        public int Calls;
        public Task<DecisionRecordPage> QueryAsync(DecisionRecordQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("SQL logic error: no such index: ix_decision_records_tenant_seen SECRET-DETAIL");
        }
    }

    [Fact]
    public async Task A_storage_failure_costs_only_the_panel_and_the_rest_of_the_tenant_page_still_shows()
    {
        var broken = new BrokenDecisionQuery();
        await using var f = await StartAsync(services: s => { s.RemoveAll<IDecisionRecordQuery>(); s.AddSingleton<IDecisionRecordQuery>(broken); });
        var b = await SignedIn(f);

        foreach (var path in new[] { "/tenants/acme", "/tenants/acme?package=moq&match=prefix" })
        {
            var (status, body, _) = await b.GetTextAsync(path);
            Assert.Equal(200, status);
            Assert.Contains("<h2>Tokens</h2>", body);
            Assert.Contains("<h2>Waivers</h2>", body);
            var panel = Panel(body);
            Assert.Contains("The decision record could not be read.", panel);
            Assert.Empty(Rows(panel));
            Assert.DoesNotContain("SECRET-DETAIL", body);
            Assert.DoesNotContain("no such index", body);
        }
        Assert.Equal(2, broken.Calls);
    }

    [Fact]
    public async Task Without_a_decision_record_on_this_server_the_panel_says_so()
    {
        await using var f = await StartAsync(services: s => s.RemoveAll<IDecisionRecordQuery>());
        var b = await SignedIn(f);
        var (status, body, _) = await b.GetTextAsync("/tenants/acme");
        Assert.Equal(200, status);
        Assert.Contains("The decision record is not available on this server.", Panel(body));
    }
}
