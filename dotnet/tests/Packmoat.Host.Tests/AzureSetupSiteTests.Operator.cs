using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// #207: an operator-made tenant made for its customer to connect (feed_setup 'customer'), on a self-hosted install: no hosted module
/// at all. Its Admin gets the home card and the wizard; until Connect it has no upstream, so it serves nothing and
/// the customer site issues no token.
/// </summary>
public partial class AzureSetupSiteTests
{
    /// <summary>selfco as `tenant create selfco Selfco --azure-tenant DirS` leaves it: operator-made, no URLs, feed_setup 'customer'.</summary>
    private static async Task MakeFlaggedOperatorTenantAsync(AdminSiteFixture f, string feedSetup = "customer")
    {
        await using var c = await f.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE tenants SET origin='operator', created_by_oid=NULL, feed_setup='{feedSetup}' WHERE id='selfco'";
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    [Fact]   // mutations: the wizard (or the card) still needs a module's store; ignore the flag in the setup kind
    public async Task Self_hosted_with_no_module_a_flagged_operator_tenant_gets_the_card_and_the_wizard()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await MakeFlaggedOperatorTenantAsync(f);
        var b = await SignIn(f);

        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.HomeCardSentence), home);
        Assert.Contains($"<a class=\"btn\" href=\"/setup/azure\">{AppAzureSetup.HomeCardButton}</a>", home);
        var (status, page, _) = await b.GetTextAsync(AppAzureSetup.Path);
        Assert.Equal(200, status);
        Assert.Contains(AppHtml.E(AppAzureSetup.OrgHelp), page);   // step 1 of the wizard: the names to type

        // The check runs and is recorded, as a self-made tenant's is.
        var callback = await RunCheck(site, b);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        Assert.Equal(1L, await FeedCheckRows(f));
    }

    [Fact]   // mutation: drop the flag from the rule (the same tenant unflagged would get the wizard)
    public async Task Self_hosted_the_same_operator_tenant_unflagged_gets_no_card_and_no_wizard()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await MakeFlaggedOperatorTenantAsync(f, feedSetup: "operator");
        var b = await SignIn(f);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.HomeCardSentence), (await b.GetTextAsync("/home")).Body);
        var (_, page, _) = await b.GetTextAsync(AppAzureSetup.Path);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.OrgHelp), page);
        Assert.Equal(0L, await FeedCheckRows(f));
    }

    [Fact]
    public async Task A_flagged_tenant_with_no_upstream_fails_closed_and_the_customer_site_issues_no_token()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await MakeFlaggedOperatorTenantAsync(f);

        var tenant = (await f.Tenants.GetAsync("selfco", default))!;
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(tenant, await f.Tenants.GetUpstreamBindingAsync("selfco", default), null, FakeAzureDevOps.SharedApp));

        var b = await SignIn(f);
        var (page, r, body, token) = await CreateTokenAnyway(f, b);
        Assert.Contains("<p><strong>Not connected yet</strong></p>", page);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Not connected yet", body);
        Assert.Null(token);
        Assert.Empty(await f.Tokens.ListAsync("selfco", default));
    }

    [Fact]   // mutation: drop "OR feed_setup='customer'" from Connect's first step (the page offers Connect, the store refuses)
    public async Task Self_hosted_the_customer_connects_their_own_feed_and_then_gets_a_token()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await MakeFlaggedOperatorTenantAsync(f);
        var ids = await SeedChecksAsync(f);
        var b = await SignIn(f);

        Assert.Equal(HttpStatusCode.SeeOther, (await ConnectFrom(b, ids)).StatusCode);
        var tenant = (await f.Tenants.GetAsync("selfco", default))!;
        Assert.Equal(("https://pkgs.dev.azure.com/", CheckedFeed), (tenant.UpstreamBase, tenant.UpstreamFeedIndexUrl));
        Assert.Equal(new UpstreamBinding(IdentityKind.Shared, DirS, null), await f.Tenants.GetUpstreamBindingAsync("selfco", default));
        Assert.Equal("wizard", await Scalar(f, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal("operator|customer", await Scalar(f, "SELECT origin || '|' || feed_setup FROM tenants WHERE id='selfco'"));
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectedLine), (await b.GetTextAsync(AppAzureSetup.Path)).Body);

        var (r, _, token) = await CreateToken(b);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(token);
    }
}
