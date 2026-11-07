using Packmoat.Application;
using Packmoat.Host.Modules;
using static Packmoat.Host.Customer.AppHtml;

namespace Packmoat.Host.Customer;

/// <summary>
/// The customer site with no module (#192): no pages beyond the core's, no banner, no extra links, and no organisation can be set up
/// from the site (so no prospect session is ever made). A directory that is not on this server is told to ask whoever runs it.
/// </summary>
public sealed class NoCustomerSiteModule : ICustomerSiteModule
{
    public static readonly NoCustomerSiteModule Instance = new();

    // The not-linked card (#192): the wording of option B on #192 (2026-10-31). Named here so it changes in one place.
    internal const string NotLinkedTitle = "Your organisation isn't on Packmoat here yet";
    internal const string NotLinkedSentence = "Ask your IT team, or whoever runs Packmoat for you, to add your organisation. Give them this directory id. Once they've added it, sign in again.";

    private NoCustomerSiteModule() { }

    public bool OffersSetup(string directoryId) => false;

    /// <summary>The directory id and "Sign in again" (to pick another account). Nothing links away from this server.</summary>
    public NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin) => Card(directoryId);

    internal static NotLinkedCard Card(string? directoryId) =>
        new(NotLinkedTitle, SignInCard(NotLinkedTitle, NotLinkedSentence, "Directory id: " + directoryId, SignInAgainLink));

    public IReadOnlyList<CustomerRoute<CustomerActor>> MemberRoutes { get; } = Array.Empty<CustomerRoute<CustomerActor>>();

    public IReadOnlyList<CustomerRoute<Prospect>> ProspectRoutes { get; } = Array.Empty<CustomerRoute<Prospect>>();

    public string HomeBanner(CustomerActor actor, CustomerHome view) => "";

    public string HomeLinks(CustomerActor actor, CustomerHome view) => "";

    public string ConnectSentence(CustomerActor actor) => AppAzureSetup.ConnectSentence;
}
