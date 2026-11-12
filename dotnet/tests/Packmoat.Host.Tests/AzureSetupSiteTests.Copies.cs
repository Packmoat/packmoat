using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Customer;

namespace Packmoat.Host.Tests;

/// <summary>
/// The wizard with more than one copy of the server: two copies over one database, one browser whose requests land on either. A
/// person's last check and draft, kept by one copy, are found by the other; a check running on one copy refuses one on the other; and
/// when the store fails nothing is confirmed.
/// </summary>
public partial class AzureSetupSiteTests
{
    /// <summary>The same browser (its cookies) on another copy of the server.</summary>
    private static AdminBrowser OnCopy(AdminSiteFixture.ServerCopy copy, AdminBrowser from)
    {
        var b = copy.Browser(AppHost);
        CopyCookies(from, b);
        return b;
    }

    private static void CopyCookies(AdminBrowser from, AdminBrowser to)
    {
        foreach (var (name, value) in from.Cookies) to.SetCookie(name, value);
    }

    /// <summary>
    /// The bug the shared scratch fixes: the check runs and is held on copy one; the page and "Confirm and check again" are served by copy
    /// two, which must find the held check to put its named admins in the step-up; and Microsoft's return lands on copy one again, which
    /// records them as confirmed. With each copy's own memory, copy two confirmed nobody.
    /// </summary>
    [Fact]   // mutations: the in-process scratch over a database (each copy its own); the held check keyed without the person or the kind
    public async Task A_check_held_by_one_copy_has_its_admins_confirmed_when_the_step_up_goes_through_another()
    {
        await using var site = await StartAsync(lease: true);
        site.Ado.NamedAdmin = true;
        var f = site.F;
        await using var two = await f.StartCopyAsync();
        var one = await SignIn(f);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, one)).StatusCode);   // run, recorded and held on copy one
        var first = await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(FeedCheckOutcome.Incomplete, first!.Outcome);

        var other = OnCopy(two, one);
        var page = (await other.GetTextAsync(AppAzureSetup.Path)).Body;   // copy two's page shows copy one's check
        Assert.Contains("<li>Ada Lovelace</li>", page);
        Assert.Contains(AppHtml.E(AppAzureSetup.ConfirmCheckbox), page);
        var confirm = await Post(other, page, AppAzureSetup.CheckPath, true, GoodNames.Append(("confirmed", "yes")).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        var challenge = site.SignIn.LastChallenge!;
        Assert.Equal("1", challenge.Items[AppAzureSetup.SilentItem]);
        Assert.Contains(FakeAzureDevOps.AdaDescriptor.Replace("\\", "\\\\"), challenge.Items[AppAzureSetup.ConfirmItem]);

        // Microsoft's return lands on copy one.
        CopyCookies(other, one);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        site.SignIn.Next = Person(DirS, authTime: f.Clock.UtcNow - TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.SeeOther, (await one.GetAsync(confirm.Headers.Location!.OriginalString)).StatusCode);
        var second = await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(FeedCheckOutcome.Passed, second!.Outcome);
        Assert.Equal(new[] { "Ada Lovelace" }, second.ConfirmedAdmins);
        // and either copy's page shows the new result
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ConfirmCheckbox), (await OnCopy(two, one).GetTextAsync(AppAzureSetup.Path)).Body);
    }

    [Fact]   // mutation: the in-process scratch over a database (step 1 kept by one copy is empty on the other)
    public async Task Step_one_kept_by_one_copy_is_filled_in_by_another()
    {
        await using var site = await StartAsync();
        await using var two = await site.F.StartCopyAsync();
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await Approve(b, GoodNames)).StatusCode);   // kept on copy one
        Assert.Equal(Typed, Step1((await OnCopy(two, b).GetTextAsync(AppAzureSetup.Path)).Body));
    }

    [Fact]   // mutations: the wizard's state given a lease of its own copy; the lease job not per tenant
    public async Task A_check_running_on_one_copy_refuses_a_check_on_another()
    {
        await using var site = await StartAsync(lease: true);
        await using var two = await site.F.StartCopyAsync();
        var running = await site.F.App.Services.GetRequiredService<AzureSetupState>().TryBeginAsync("selfco");   // copy one's check
        Assert.NotNull(running);
        Assert.NotNull(await site.F.App.Services.GetRequiredService<AzureSetupState>().TryBeginAsync("acme"));   // another tenant's is no matter

        var r = await RunCheck(site, OnCopy(two, await SignIn(site.F)));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("A check is running", await r.Content.ReadAsStringAsync());
        Assert.Empty(site.Ado.Seen);
    }

    /// <summary>A scratch that fails on demand, over the in-process one.</summary>
    private sealed class FlakyScratch : IFeedSetupScratch
    {
        public const string Secret = "SECRET-STORE-DETAIL-4f1c";
        private IFeedSetupScratch? _inner;
        public bool Fail { get; set; }
        public IFeedSetupScratch Over(IFeedSetupScratch inner) { _inner = inner; return this; }
        private IFeedSetupScratch Inner => Fail ? throw new TimeoutException(Secret) : _inner!;
        public Task PutAsync(string tenantId, Guid personId, string kind, string payload, TimeSpan life, CancellationToken ct = default) => Inner.PutAsync(tenantId, personId, kind, payload, life, ct);
        public Task<string?> GetAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default) => Inner.GetAsync(tenantId, personId, kind, ct);
        public Task DeleteAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default) => Inner.DeleteAsync(tenantId, personId, kind, ct);
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Inner.PruneAsync(now, ct);
    }

    /// <summary>
    /// Fail-closed: with the store failing, "Confirm and check again" is refused (no step-up, no check, nothing confirmed), the page still
    /// opens without the held result, and each failure is one warning naming what failed, the kind and the error's type only. Once the
    /// store is back, the same confirm goes through.
    /// </summary>
    [Fact]   // mutations: the confirm's read failure let through (a check confirming nobody); a failure logged with the tenant, the person or the message
    public async Task When_the_store_fails_the_confirm_is_refused_and_nothing_is_confirmed()
    {
        var flaky = new FlakyScratch();
        await using var site = await StartAsync(services: s => s.AddSingleton<IFeedSetupScratch>(sp => flaky.Over(new InProcessFeedSetupScratch(sp.GetRequiredService<IClock>()))));
        site.Ado.NamedAdmin = true;
        var f = site.F;
        var b = await SignIn(f);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains("<li>Ada Lovelace</li>", page);

        flaky.Fail = true;
        var refused = await Post(b, page, AppAzureSetup.CheckPath, true, GoodNames.Append(("confirmed", "yes")).ToArray());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("Not available yet", await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, site.SignIn.Challenges);   // the first check's only
        Assert.Equal(1L, await FeedCheckRows(f));
        var during = await b.GetTextAsync(AppAzureSetup.Path);
        Assert.Equal(200, during.Status);
        Assert.DoesNotContain("<li>Ada Lovelace</li>", during.Body);   // the recorded result, as after a restart before

        var lines = site.Logs.Lines.Where(l => l.Contains("scratch failed", StringComparison.Ordinal)).ToList();
        Assert.Contains(lines, l => l.EndsWith("Feed setup: reading the settings scratch failed (TimeoutException); no admin is confirmed and the check is refused", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("Feed setup: reading the settings scratch failed (TimeoutException); the page shows the recorded result", StringComparison.Ordinal));
        Assert.All(lines, l =>
        {
            Assert.DoesNotContain("selfco", l, StringComparison.Ordinal);
            Assert.DoesNotContain(Ada, l, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ada Lovelace", l, StringComparison.Ordinal);
            Assert.DoesNotContain(FlakyScratch.Secret, l, StringComparison.Ordinal);
        });

        flaky.Fail = false;
        page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        var confirm = await Post(b, page, AppAzureSetup.CheckPath, true, GoodNames.Append(("confirmed", "yes")).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        site.SignIn.Next = Person(DirS, authTime: f.Clock.UtcNow - TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.SeeOther, (await b.GetAsync(confirm.Headers.Location!.OriginalString)).StatusCode);
        var second = await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(new[] { "Ada Lovelace" }, second!.ConfirmedAdmins);
    }
}
