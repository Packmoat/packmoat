using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Host.Customer;
using Packmoat.Host.Modules;

namespace Packmoat.Host.Tests;

/// <summary>
/// The developer step-up's not-linked race: the session was still linked when the callback read it, and the directory was
/// unlinked before the step-up's own sign-in was decided. The page is the customer-site module's not-linked card, the same one sign-in shows.
/// </summary>
public partial class AzureSetupSiteTests
{
    /// <summary>Answers <see cref="PassesLeft"/> more lookups from the database once armed, then none: the directory is unlinked mid-request.</summary>
    private sealed class UnlinkingDirectoryStore : ISignInDirectoryStore
    {
        public ISignInDirectoryStore Inner { get; set; } = null!;
        public int? PassesLeft { get; set; }
        public Task<bool> BindAsync(DirectoryBinding binding, CancellationToken ct) => Inner.BindAsync(binding, ct);
        public Task<int?> UnbindAsync(string tenantId, string azureTenantId, DateTimeOffset at, string revokedBy, CancellationToken ct) => Inner.UnbindAsync(tenantId, azureTenantId, at, revokedBy, ct);
        public Task<IReadOnlyList<DirectoryBinding>> ListForTenantAsync(string tenantId, CancellationToken ct) => Inner.ListForTenantAsync(tenantId, ct);

        public Task<DirectoryBinding?> GetAsync(string azureTenantId, CancellationToken ct)
        {
            if (PassesLeft is { } left)
            {
                if (left <= 0) return Task.FromResult<DirectoryBinding?>(null);
                PassesLeft = left - 1;
            }
            return Inner.GetAsync(azureTenantId, ct);
        }
    }

    private sealed class CardModule : ICustomerSiteModule
    {
        public List<(string DirectoryId, bool NeedsAdmin)> Asked { get; } = [];
        public bool OffersSetup(string directoryId) => false;
        public NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin)
        {
            Asked.Add((directoryId, needsAdmin));
            return Card(directoryId, needsAdmin);
        }
        public static NotLinkedCard Card(string directoryId, bool needsAdmin) =>
            new("Module not-linked title", "<p>module not-linked card for " + directoryId + (needsAdmin ? ", ask an admin" : ", early access") + "</p>");
        public IReadOnlyList<CustomerRoute<CustomerActor>> MemberRoutes { get; } = [];
        public IReadOnlyList<CustomerRoute<Prospect>> ProspectRoutes { get; } = [];
        public string HomeBanner(CustomerActor actor, CustomerHome view) => "";
        public string HomeLinks(CustomerActor actor, CustomerHome view) => "";
        public string ConnectSentence(CustomerActor actor) => AppAzureSetup.ConnectSentence;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_directory_unlinked_during_the_developer_step_up_gets_the_modules_not_linked_card(bool withModule)
    {
        var store = new UnlinkingDirectoryStore();
        var module = new CardModule();
        await using var site = await StartAsync(services: s =>
        {
            s.AddSingleton<ISignInDirectoryStore>(store);
            if (withModule) s.AddSingleton<ICustomerSiteModule>(module);
        });
        store.Inner = site.F.Directories;
        await AdminCheckPassedAsync(site);
        var dev = await SignInDeveloper(site.F);
        var (_, page, _) = await dev.GetTextAsync(AppAzureSetup.DeveloperPath);
        var start = await Post(dev, page, AppAzureSetup.DeveloperCheckPath, true);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        site.SignIn.Next = Developer(authTime: site.F.Clock.UtcNow);
        site.SignIn.AccessToken = FakeAzureDevOps.DeveloperToken;
        store.PassesLeft = 1;   // the session's own check passes; the step-up's decision finds no link
        var r = await dev.GetAsync(start.Headers.Location!.OriginalString);
        var body = await r.Content.ReadAsStringAsync();
        var asked = module.Asked.ToList();   // before anything below could ask the module itself

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(withModule ? [(DirS, false)] : [], asked);   // the not-linked card, never the needs-an-admin one
        var expected = withModule ? CardModule.Card(DirS, needsAdmin: false) : NoCustomerSiteModule.Instance.NotLinkedCard(DirS, needsAdmin: false);
        Assert.Contains(expected.Html, body);
        Assert.Contains("<title>" + AppHtml.E(expected.Title), body);
        Assert.False(WroteSessionCookie(r));
        Assert.Empty(WithDeveloperToken(site));
        Assert.Null(await LatestDeveloperRow(site.F));
    }
}
