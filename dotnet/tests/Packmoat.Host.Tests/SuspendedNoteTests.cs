using Packmoat.Application;
using Packmoat.Host.Customer;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// The suspended page's "what to do next" (#197 C3): the core's sentence, then the customer-site module's note when it has one. The hosted
/// module's note is pinned on a rendered page in the hosted tests.
/// </summary>
public class SuspendedNoteTests
{
    private sealed class NoteModule(string? note) : ICustomerSiteModule
    {
        public bool OffersSetup(string directoryId) => false;
        public NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin) => new("x", "x");
        public IReadOnlyList<CustomerRoute<CustomerActor>> MemberRoutes => [];
        public IReadOnlyList<CustomerRoute<Prospect>> ProspectRoutes => [];
        public string HomeBanner(CustomerActor actor, CustomerHome view) => "";
        public string HomeLinks(CustomerActor actor, CustomerHome view) => "";
        public string ConnectSentence(CustomerActor actor) => "";
        public string? SuspendedNote => note;
    }

    [Fact]   // mutations: drop the module's note; drop the space before it
    public void The_module_note_follows_the_cores_sentence_and_no_module_adds_nothing()
    {
        var support = OperatorLinks.Parse("Acme Ops", "help@example.com", null).Support;
        Assert.Equal("Email help@example.com to restore access.", AppSite.SuspendedNext(support));
        Assert.Equal(AppSite.SuspendedNext(support), AppSite.SuspendedNext(support, NoCustomerSiteModule.Instance));
        Assert.Equal(AppSite.SuspendedNext(support), AppSite.SuspendedNext(support, new NoteModule(null)));
        Assert.Equal(AppSite.SuspendedNext(support), AppSite.SuspendedNext(support, new NoteModule("")));
        Assert.Equal("Email help@example.com to restore access. Ask your account manager.",
            AppSite.SuspendedNext(support, new NoteModule("Ask your account manager.")));
        Assert.Null(((ICustomerSiteModule)NoCustomerSiteModule.Instance).SuspendedNote);
    }
}
