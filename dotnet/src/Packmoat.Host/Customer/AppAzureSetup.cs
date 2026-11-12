using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;
using static Packmoat.Host.Customer.AppHtml;

namespace Packmoat.Host.Customer;

/// <summary>
/// The Azure setup wizard: GET /setup/azure, POST /setup/azure/check, the two consent return pages and the delegated Azure
/// DevOps sign-in check; and Connect: POST /setup/azure/connect, which makes Packmoat serve the checked feed, and
/// POST /setup/azure/reauth, the fresh sign-in Connect may ask for. Exists only while PACKMOAT_AZURE_SETUP allows the person's directory (otherwise the site's 404).
/// "Sign in and check" starts a second sign-in with the same customer app (scheme <see cref="Scheme"/>, callback <see cref="CallbackPath"/>),
/// asking for read-only Azure DevOps scopes. Its token-validated event checks the new ID token against the session, runs the check with
/// the access token (a local only: never saved, logged, stored or shown), records the result, and answers 303 to the page. It never
/// signs anyone in and writes no session cookie. The typed names travel in the protected sign-in state.
/// The developer proof: GET /setup/developer and POST /setup/developer/check, for Developers. The same scheme, with the scope
/// overridden to read Azure Artifacts only, and the protected-state purpose <see cref="DeveloperPurpose"/>: the callback branches on the
/// purpose, which must match the page the sign-in returns to.
/// </summary>
public static class AppAzureSetup
{
    public const string Path = "/setup/azure";
    public const string CheckPath = "/setup/azure/check";
    public const string FeedAppAddedPath = "/setup/azure/feed-app-added";
    public const string ConsentedPath = "/setup/azure/consented";
    public const string ConnectPath = "/setup/azure/connect";
    public const string ReauthPath = "/setup/azure/reauth";
    /// <summary>Step 2's button: keeps step 1 for this person, then sends the browser to Microsoft's approval page.</summary>
    public const string ApprovePath = "/setup/azure/approve";
    public const string DeveloperPath = "/setup/developer";
    public const string DeveloperCheckPath = "/setup/developer/check";
    public const string Scheme = "PackmoatAppAzureDevOps";
    /// <summary>Register in Entra on the "Packmoat" app as https://app.&lt;host&gt;/signin-azure-devops.</summary>
    public const string CallbackPath = "/signin-azure-devops";
    /// <summary>Azure DevOps' resource id in Entra.</summary>
    public const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";
    /// <summary>Read-only. No offline_access: no refresh token is ever issued.</summary>
    public static readonly string[] AzureDevOpsScopes =
    {
        AzureDevOpsResource + "/vso.packaging", AzureDevOpsResource + "/vso.graph", AzureDevOpsResource + "/vso.identity", AzureDevOpsResource + "/vso.project",
    };
    /// <summary>The developer proof's whole scope (it replaces the scheme's): sign-in, and read Azure Artifacts. Nothing else is asked for.</summary>
    public static readonly string[] DeveloperScopes = { "openid", "profile", AzureDevOpsResource + "/vso.packaging" };
    private const string LogCategory = "Packmoat.App";

    /// <summary>What a step-up sign-in is for, in the protected state. The callback refuses one whose purpose does not match its return page.</summary>
    internal const string PurposeItem = "packmoat.azure.purpose";
    internal const string SettingsPurpose = "settings";
    internal const string DeveloperPurpose = "developer";

    // Items in the protected sign-in state.
    internal const string OrgItem = "packmoat.azure.org";
    internal const string ProjectItem = "packmoat.azure.project";
    internal const string FeedItem = "packmoat.azure.feed";
    internal const string ConfirmItem = "packmoat.azure.confirm";
    /// <summary>"Confirm and check again": no prompt=login, so Microsoft can reuse a sign-in still inside max_age (auth_time is checked all the same).</summary>
    internal const string SilentItem = "packmoat.azure.silent";

    // ---------------------------------------------------------------- wording

    internal const string HomeCardSentence = "Packmoat isn't connected to your Azure Artifacts feed yet. Your developers can't use Packmoat until an Admin connects it.";
    internal const string HomeCardButton = "Connect your feed";
    internal const string Intro = "Packmoat reads public packages from one feed in your Azure DevOps organisation. That feed should hold only packages from nuget.org, and your developers mustn't be able to read it directly. Allow about 20 minutes. " + IntroAdminSentence;
    /// <summary>Moment 4 of the setup wizard.</summary>
    internal const string IntroAdminSentence = "You may need a Microsoft Entra directory administrator for steps 2 and 4.";
    internal const string OrgHelp = "Organisation: the name after dev.azure.com/.";
    internal const string ProjectHelp = "Project: leave empty for an organisation-scoped feed.";
    internal const string FeedHelp = "Feed: we recommend a new feed used only by Packmoat, such as public-packages.";
    internal const string OrgError = "Use letters, numbers and hyphens only, as in dev.azure.com/<name>.";
    internal const string FeedError = "A feed name can't contain spaces, slashes, %, ? or #.";
    internal const string ProjectError = "A project name can't contain slashes, %, ? or #, and has at most 64 characters.";
    internal const string Step2 = "A directory administrator approves Packmoat's feed access app once. The app asks for no permissions in your directory. It can reach only what you give it in Azure DevOps in step 3.";
    internal const string Step2Button = "Open Microsoft's approval page";
    /// <summary>The page the setup wizard shows when the developer must approve.</summary>
    internal const string Step2SendLink = "Not a directory administrator? Send them this link:";
    internal const string Step2Return = "Thanks. Packmoat confirms this when it checks your setup.";
    internal const string Step3 = "In Azure DevOps: add Packmoat feed access to your organisation with Stakeholder access, in the project's Readers group only. Then, in the feed's settings under Permissions, give it the Collaborator role. Don't give it a role on any other feed. Azure DevOps also makes a feed named after your organisation that everyone in it can read. Remove Project Collection Valid Users from its Permissions, or delete it if you don't use it.";
    internal const string Step4 = "Packmoat checks your feed using your own Microsoft sign-in. You'll be asked to sign in again and to let Packmoat read your feeds, groups and identities in Azure DevOps. Packmoat uses this access only during the check, can't change anything, and doesn't keep it.";
    internal const string Step4Button = "Sign in and check";
    /// <summary>Moment 3 of the setup wizard, the Approval needed page.</summary>
    internal const string ConsentBlocked = "A directory administrator (Global Administrator, Cloud Application Administrator or Application Administrator) must approve Packmoat for your whole organisation. Send them this link, then check again once they've approved it.";
    internal const string NamedAdminsSentence = "These people are administrators of the feed. Administrators can read the feed directly and skip Packmoat. Confirm that each one is someone you trust with that.";
    internal const string ConfirmCheckbox = "I confirm these administrators";
    internal const string ConfirmButton = "Confirm and check again";
    internal const string WaitingOnDeveloper = "Your feed's settings pass. One step left: a developer confirms they can't reach the feed directly.";
    /// <summary>A passed settings check when no developer is awaited: ready to connect, connected, or a check-only page.</summary>
    internal const string SettingsPassLine = "Your feed's settings pass.";
    /// <summary>Shown in place of a check that errored: the stored detail is unchanged.</summary>
    internal static string CouldNotFinishLine(SupportContact support) => "Packmoat couldn't finish this check: Azure DevOps gave an answer Packmoat couldn't read. Check again; if it keeps happening, " + support.Phrase + ".";
    // The privacy line.
    internal const string PrivacyLine = "When an Admin or a developer checks the setup, Packmoat uses a short-lived Microsoft token to read feed settings in Azure DevOps. The token is used only during the check and is never stored. Packmoat's own feed access is also tested against your other feeds. We keep the check's result, including the display names of the person who ran it and of the feed's administrators.";
    // The developer proof.
    internal const string WaitingTitle = "Waiting for a developer";
    internal const string WaitingSendLink = "Send this link to one of your developers:";
    internal const string WaitingHow = "They sign in to Packmoat with their own work account and choose Check my access. It takes about a minute. Admins can't do this step, because Admins can often read the feed directly.";
    internal const string WaitingAutoJoinOn = "Anyone in your directory joins Packmoat as a Developer the first time they sign in.";
    internal const string WaitingAutoJoinOff = "They need the Packmoat Developer role in Microsoft Entra.";
    /// <summary>Moment 5 of the setup wizard, followed by the directory's approval link.</summary>
    internal const string WaitingNeedAdmin = "If Microsoft tells them \"Need admin approval\", a directory administrator must approve Packmoat first. Send them this link:";
    internal const string WaitingSolo = "Use the account of someone who normally restores packages. A new account with no groups proves less.";
    internal const string DeveloperCardSentence = "Your Admin is connecting Packmoat to your Azure Artifacts feed. One check is left, and it needs a developer: confirm that you can't read that feed directly.";
    internal const string DeveloperCardButton = "Check my access";
    internal const string DeveloperStep = "Packmoat signs you in to Microsoft again and asks to read Azure Artifacts feeds as you. It then tries to read the feed Packmoat uses. You should be refused: that shows developers go through Packmoat. Packmoat uses this access only during the check, can't change anything, and doesn't keep it.";
    internal const string DeveloperStepButton = "Sign in and check my access";
    internal const string DeveloperPass = "Done. You can't read the feed directly, which is what Packmoat needs. Your Admin can now finish setup.";
    internal const string DeveloperFail = "You can read the feed directly, so developers could skip Packmoat. Ask your Admin to remove your access to the feed, or the group that gives it, then check again.";
    internal const string DeveloperNotMember = "Packmoat couldn't confirm this, because your account couldn't list feeds in this Azure DevOps organisation. Check that you're a member of it, then try again.";
    internal const string DevelopersOnlyTitle = "Developers only";
    internal const string DevelopersOnlyLine = "This check must be done by a developer, not an Admin. Admins can often read the feed directly, so their result proves nothing.";
    internal const string NotReadyTitle = "Not ready yet";
    internal const string NotReadyLine = "Your Admin hasn't finished checking the feed's settings yet.";
    // New wording, not in the plan's draft: listed in the PR for approval.
    internal const string DeveloperUnclear = "Packmoat couldn't confirm this yet. Check again in a few minutes. If it keeps happening, ask your Admin to check the feed's settings again.";    // Connect.
    internal const string ProofSentence = "{0} confirmed on {1} that they can't reach the feed directly.";
    internal const string ConnectButton = "Connect";
    /// <summary>The Connect sentence with no customer-site module; a module may say more (<see cref="Modules.ICustomerSiteModule.ConnectSentence"/>).</summary>
    public const string ConnectSentence = "Connecting makes Packmoat serve this feed. Your developers can create tokens.";
    internal const string ConnectedLine = "Connected. Your developers can now create a token on the Tokens page.";
    // New wording, not in the plan's draft: listed in the PR for approval.
    internal static string WithdrawFailedLine(SupportContact support) => "Packmoat couldn't stop serving your feed automatically. " + support.CapitalPhrase + ".";
    internal const string WithdrawnLine = "The last check found a problem, so Packmoat has stopped serving your feed. Fix the settings below, then check again and reconnect.";
    // New wording for Connect's refusals, not in the plan's draft: listed in the PR for approval.
    internal const string ConnectStaleLine = "Your checks changed, or are too old, since this page was shown. Check again, then connect.";
    internal static string ConnectTakenLine(SupportContact support) => "Another Packmoat account already uses Packmoat feed access in your directory. " + support.CapitalPhrase + ".";
    internal const string ConnectFreshLine = "Connecting needs a sign-in within the last 15 minutes, confirmed by Microsoft.";
    // The Connect re-sign-in's return.
    internal const string ReauthDoneLine = "You signed in again. Choose Connect to finish.";
    internal static string ReauthUnconfirmedLine(SupportContact support) => "Microsoft didn't confirm a recent sign-in. Try again. If it keeps happening, " + support.Phrase + ".";
    // New wording, not in the plan's draft: listed in the PR for approval.
    internal const string FailedLine = "Some checks failed. Fix them in Azure DevOps, then check again.";
    internal const string IncompleteLine = "Some checks didn't pass yet. Fix them, or confirm the administrators below, then check again.";
    internal const string CheckAgainButton = "Check again";
    /// <summary>The operator set the connection up: by name, or in neutral words.</summary>
    internal static string ReadOnlyLine(SupportContact support) => support.OperatorSubject + " set up the connection to your organisation's feed. To change it, " + support.Phrase + ".";
    internal static string ReadOnlyTitle(SupportContact support) => support.OperatorName is { } name ? "Set up by " + name : "Set up for you";
    internal const string CheckOnlyLine = "Check again reads your feed's settings with your own Microsoft sign-in. It changes nothing, and the result isn't kept.";
    internal static readonly string[] Checklist =
    {
        "Upstream sources: nuget.org only.",
        "Views: none shared with your organisation.",
        "Permissions: no group, such as Contributors or Readers, has a role on the feed.",
        "Project: private, if the feed is in one.",
    };

    // ---------------------------------------------------------------- services and the sign-in scheme

    /// <param name="jobLease">The job lease a tenant's check runs under, made when the wizard's state is (not registered).</param>
    internal static void AddServices(IServiceCollection services, PackmoatOptions options, AppSiteSettings settings, Func<IServiceProvider, IJobLease> jobLease)
    {
        // Redirects never followed, Azure DevOps API hosts only, and no request logging (the factory's loggers would log every URL).
        services.AddHttpClient(DelegatedFeedCheck.ClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => options.AzureDevOpsHandlerOverride?.Invoke() ?? DelegatedFeedCheck.CreatePrimaryHandler())
            .AddHttpMessageHandler(() => new AzureDevOpsHostsOnlyHandler())
            .RemoveAllLoggers();
        // The developer proof's client: feeds.dev.azure.com and the exact GUID feed index only, redirects never followed, no logging.
        services.AddHttpClient(DeveloperFeedProbe.ClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => options.AzureDevOpsHandlerOverride?.Invoke() ?? DelegatedFeedCheck.CreatePrimaryHandler())
            .AddHttpMessageHandler(() => new DeveloperProbeHostsHandler())
            .RemoveAllLoggers();
        // Packmoat's own identity: pkgs.dev.azure.com GUID feed indexes only, redirects never followed, no logging.
        services.AddHttpClient(SharedAppFeedProbe.ClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => options.AzureDevOpsHandlerOverride?.Invoke() ?? DelegatedFeedCheck.CreatePrimaryHandler())
            .AddHttpMessageHandler(() => new SharedAppProbeHostsHandler())
            .RemoveAllLoggers();
        services.TryAddSingleton<ISharedAppFeedProbe>(sp => new SharedAppFeedProbe(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient(SharedAppFeedProbe.ClientName),
            () => sp.GetService<ISharedAppCredentialStore>(), options.SharedAppTokenOverride));
        services.TryAddSingleton<IDeveloperFeedProbe>(sp => new DeveloperFeedProbe(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient(DeveloperFeedProbe.ClientName),
            sp.GetRequiredService<ISharedAppFeedProbe>()));
        services.TryAddSingleton<IDelegatedFeedCheck>(sp => new DelegatedFeedCheck(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient(DelegatedFeedCheck.ClientName),
            sp.GetRequiredService<ISharedAppFeedProbe>(),
            log: sp.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory) is var checkLog ? message => checkLog.LogWarning("{Message}", message) : null,
            support: options.Operator.Support));
        // Each person's last check and draft in the shared scratch, and one check at a time per tenant under the job lease: every copy sees both.
        services.TryAddSingleton(sp => new AzureSetupState(sp.GetRequiredService<IFeedSetupScratch>(), jobLease(sp), sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(sp => new AzureSetupCommands(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ISignInDirectoryStore>(),
            sp.GetService<ISharedAppCredentialStore>(), sp.GetService<IFeedCheckStore>(), sp.GetService<IDelegatedFeedCheck>(),
            sp.GetRequiredService<AzureSetupState>(), settings.AzureSetup, sp.GetRequiredService<IClock>(), sp.GetRequiredService<IRateCounter>(), feeds: sp.GetService<ISelfServiceFeedStore>(),
            log: sp.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory) is var log
                ? (level, message) => log.Log(level == SetupLogLevel.Error ? LogLevel.Error : LogLevel.Warning, "{Message}", message)
                : null,
            developerProbe: sp.GetService<IDeveloperFeedProbe>(), autoJoin: sp.GetService<IAutoJoinSettingStore>()));
    }

    /// <summary>
    /// The delegated sign-in: the customer app, its certificate credential and token checks, plus the read-only Azure DevOps scopes,
    /// prompt=login and max_age (15 minutes). It never signs in: the token-validated event always ends the request itself.
    /// </summary>
    public static void ConfigureOidc(OpenIdConnectOptions o, AppSiteSettings s, IClock clock)
    {
        SignedInSite.ApplyCommonOidc(o, s.Host, CallbackPath, AppSite.CookieScheme);
        o.Authority = AppSiteSettings.Authority;
        o.ClientId = s.ClientId;
        o.ClientSecret = null;
        o.TokenValidationParameters = AppSite.TokenValidation(s.ClientId!);
        o.ClaimActions.Clear();
        o.SaveTokens = false;
        foreach (var scope in AzureDevOpsScopes) o.Scope.Add(scope);
        o.Prompt = "login";
        o.MaxAge = CustomerTokenRules.FreshSignIn;
        AppSite.UseCertificateCredential(o, s, clock);
        var pinRedirect = o.Events.OnRedirectToIdentityProvider;
        o.Events.OnRedirectToIdentityProvider = async ctx =>
        {
            await pinRedirect(ctx);
            if (ctx.Properties.Items.TryGetValue(SilentItem, out var silent) && silent == "1") ctx.ProtocolMessage.Prompt = null;
        };
        o.Events.OnTokenValidated = CompleteAsync;
        // Never reached (the event above always handles the response); if it ever were, nobody is signed in.
        o.Events.OnTicketReceived = ctx => { ctx.Fail("The Azure DevOps check never signs anyone in."); return Task.CompletedTask; };
        // Microsoft's error code alone picks the page, as on the customer sign-in: a decline (access_denied, consent_required) shows
        // Approval needed even when the handler could not match the reply to a sign-in. Nothing from the reply is shown; a log line names
        // only an allowlisted code. Only a failure after Microsoft issued a code counts against the limiter.
        o.Events.OnRemoteFailure = async ctx =>
        {
            SignedInSite.CountIfCodeReceived(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<AppSignInLimiter>());
            var error = await SignInReply.ErrorAsync(ctx.HttpContext.Request);
            var kind = ctx.Failure?.GetType().Name ?? "unknown";   // never the message
            ctx.HandleResponse();
            if (SignInReply.IsDeclined(error))
            {
                Log(ctx.HttpContext).LogWarning("Azure DevOps check sign-in cancelled or not approved ({Kind}, error {Error})", kind, SignInReply.LogCode(error));
                await ApprovalNeededPage(ctx.HttpContext, ctx.Properties, s);
                return;
            }
            Log(ctx.HttpContext).LogWarning("Azure DevOps check sign-in failed ({Kind}, error {Error}, token {TokenError})", kind, SignInReply.LogCode(error),
                SignInReply.TokenLogCode(ctx.HttpContext, ctx.Failure));
            await Card(ctx.HttpContext, 400, "Sign-in failed", AppSite.FailedSentence(Support(ctx.HttpContext)), null, BackToSetup);
        };
        // error=access_denied: the person cancelled, or their organisation must approve the Azure DevOps access first. Not counted
        // against the limiter: a decline is not a guess.
        o.Events.OnAccessDenied = async ctx =>
        {
            Log(ctx.HttpContext).LogWarning("Azure DevOps check sign-in cancelled or not approved");
            ctx.HandleResponse();
            await ApprovalNeededPage(ctx.HttpContext, ctx.Properties, s);
        };
    }

    /// <summary>
    /// Approval needed (403): the approval link for the signed-in person's own directory (none without a session), and the way back
    /// chosen from the sign-in's protected purpose: the developer proof's goes to the developer page, anything else to setup.
    /// </summary>
    internal static async Task ApprovalNeededPage(HttpContext ctx, AuthenticationProperties? properties, AppSiteSettings s)
    {
        var session = AppSite.ReadSession((await ctx.AuthenticateAsync(AppSite.CookieScheme)).Principal);
        var link = session is null ? "" : $"<pre class=\"snippet\">{E(OrganisationConsentUrl(s, session.DirectoryId))}</pre>";
        var back = properties?.Items.TryGetValue(PurposeItem, out var purpose) == true && purpose == DeveloperPurpose ? BackToDeveloper : BackToSetup;
        await SignedInSite.Write(ctx, 403, Page(ctx, "Approval needed",
            $"<section class=\"card notice\"><h1>Approval needed</h1><p>{E(ConsentBlocked)}</p>{link}<div class=\"actions\">{back}</div></section>"));
    }

    /// <summary>
    /// Microsoft's v2 admin-consent page for the Packmoat sign-in app: one approval, for the whole organisation, of sign-in
    /// (openid profile) and the read-only Azure DevOps scopes (<see cref="AzureDevOpsScopes"/>, the one list), returning to
    /// <see cref="ConsentedPath"/>. <paramref name="directoryId"/> is the person's own directory when known; null gives "organizations",
    /// where Microsoft uses the approver's own directory. No .default, no prompt and no offline_access.
    /// </summary>
    internal static string OrganisationConsentUrl(string? directoryId, string clientId, string redirectUri)
    {
        var scope = string.Join(' ', new[] { "openid", "profile" }.Concat(AzureDevOpsScopes));
        return $"https://login.microsoftonline.com/{Uri.EscapeDataString(directoryId ?? "organizations")}/v2.0/adminconsent" +
            $"?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}";
    }

    /// <summary><see cref="OrganisationConsentUrl(string?, string, string)"/> for this site's Packmoat app.</summary>
    public static string OrganisationConsentUrl(AppSiteSettings s, string? directoryId) =>
        OrganisationConsentUrl(directoryId, s.ClientId!, "https://" + s.Host + ConsentedPath);

    /// <summary>Microsoft's v1 admin-consent page for <paramref name="clientId"/> in the person's own directory, returning to a fixed page of this site. Used for the feed-access app, which asks for no permissions.</summary>
    internal static string AdminConsentUrl(string directoryId, string clientId, string redirectUri) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(directoryId)}/adminconsent?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

    // ---------------------------------------------------------------- the callback

    /// <summary>
    /// The delegated sign-in's token-validated event. Always handles the response: nobody is signed in and no session cookie is written.
    /// 1. The session must be a member session of a still-linked directory. 2. The new ID token: same tid and oid as the session,
    /// Packmoat.Admin, auth_time fresh. 3. The check, with the access token as a local only. 4. Recorded (self-made tenants) or held. 5. 303.
    /// </summary>
    internal static async Task CompleteAsync(TokenValidatedContext ctx)
    {
        ctx.HandleResponse();
        // The purpose (protected state) must match the page this sign-in returns to: a settings sign-in is never a developer proof, nor
        // the other way round. A sign-in that carries neither pair is refused before anything is read.
        var purpose = ctx.Properties?.Items.TryGetValue(PurposeItem, out var p) == true ? p : null;
        switch (purpose, ctx.Properties?.RedirectUri)
        {
            case (SettingsPurpose, Path):
                await CompleteSettingsAsync(ctx);
                return;
            case (DeveloperPurpose, DeveloperPath):
                await CompleteDeveloperAsync(ctx);
                return;
            default:
                // For a directory the switch does not allow, the site's 404 as everywhere else: the wizard does not exist for it.
                if (await AppSite.ReadActorAsync(ctx.HttpContext, allowAutoJoined: true) is { } someone
                    && !ctx.HttpContext.RequestServices.GetRequiredService<AppSiteSettings>().AzureSetup.Allows(someone.DirectoryId))
                {
                    await Refused(ctx.HttpContext, AzureSetupRefusal.Disabled);
                    return;
                }
                ctx.HttpContext.RequestServices.GetRequiredService<AppSignInLimiter>().RecordFailure(ctx.HttpContext);
                Log(ctx.HttpContext).LogWarning("Azure DevOps check refused (purpose does not match the return page)");
                await Card(ctx.HttpContext, 400, "Sign-in failed", AppSite.FailedSentence(Support(ctx.HttpContext)), null, HomeLink);
                return;
        }
    }

    /// <summary>The settings check's callback (an Admin's step-up): see <see cref="CompleteAsync"/>.</summary>
    private static async Task CompleteSettingsAsync(TokenValidatedContext ctx)
    {
        var http = ctx.HttpContext;
        var services = http.RequestServices;
        var limiter = services.GetRequiredService<AppSignInLimiter>();
        var actor = await AppSite.ReadActorAsync(http);
        if (actor is null)
        {
            limiter.RecordFailure(http);
            await Card(http, 401, "Signed out", "Your Packmoat session has ended. Sign in again, then check your setup.", null, SignInAgainLink);
            return;
        }
        var p = ctx.Principal;
        string[] All(string type) => p?.FindAll(type).Select(c => c.Value).ToArray() ?? Array.Empty<string>();
        var now = services.GetRequiredService<IClock>().UtcNow;
        var refusal = AzureSetupRules.CheckStepUp(actor, All("tid"), All("oid"), All("roles"), AppSite.SignInTime(p, now), now);
        if (refusal is not null)
        {
            if (refusal == StepUpRefusal.DifferentPerson) limiter.RecordFailure(http);
            Log(http).LogWarning("Azure DevOps check refused ({Reason})", refusal.Value.ToString());   // the reason only
            await StepUpRefused(http, refusal.Value);
            return;
        }
        var token = ctx.TokenEndpointResponse?.AccessToken;   // the only copy is this local; SaveTokens is off
        if (string.IsNullOrEmpty(token))
        {
            await Card(http, 400, "Sign-in failed", AppSite.FailedSentence(Support(http)), null, BackToSetup);
            return;
        }
        var request = new AzureSetupRequest(NamesFrom(ctx.Properties), ConfirmedFrom(ctx.Properties));
        try
        {
            var held = await services.GetRequiredService<AzureSetupCommands>().RunAsync(actor, request, token, http.RequestAborted);
            Log(http).LogInformation("Azure DevOps check for tenant {TenantId}: {Outcome}{Recorded}", actor.TenantId, held.Outcome.ToString(), held.Recorded ? "" : " (not recorded)");
        }
        catch (AzureSetupRefusedException refused)
        {
            await Refused(http, refused.Reason);
            return;
        }
        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = Path;
    }

    private const string BackToDeveloper = "<a class=\"btn\" href=\"" + DeveloperPath + "\">Back to the check</a>";
    /// <summary>Marks a request of the developer proof's callback, so a refusal page links back to the developer page.</summary>
    private const string DeveloperFlowItem = "packmoat.azure.developer-flow";

    /// <summary>
    /// The developer proof's callback. 1. The session must be a member session (auto-joined allowed while auto-join is on) of a
    /// still-linked directory. 2. <see cref="CustomerSignIn.Decide"/> on the step-up's own claims (the same binding, and auto-join as at
    /// sign-in), then <see cref="AzureSetupRules.CheckDeveloperStepUp"/>: the same person, a Developer (never an Admin), auth_time fresh.
    /// 3. The probe, with the access token as a local only. 4. Recorded as a developer row. 5. 303 to the page.
    /// </summary>
    private static async Task CompleteDeveloperAsync(TokenValidatedContext ctx)
    {
        var http = ctx.HttpContext;
        http.Items[DeveloperFlowItem] = true;
        var services = http.RequestServices;
        var limiter = services.GetRequiredService<AppSignInLimiter>();
        var actor = await AppSite.ReadActorAsync(http, allowAutoJoined: true);
        if (actor is null)
        {
            limiter.RecordFailure(http);
            await Card(http, 401, "Signed out", "Your Packmoat session has ended. Sign in again, then check your access.", null, SignInAgainLink);
            return;
        }
        var p = ctx.Principal;
        string[] All(string type) => p?.FindAll(type).Select(c => c.Value).ToArray() ?? Array.Empty<string>();
        var claims = AppSite.ToVerifiedClaims(p);
        if (claims.Issuers.Count == 0 && ctx.SecurityToken is { } idToken)
            claims = claims with { Issuers = new[] { idToken.Issuer }, Audiences = AppSite.Audiences(idToken).ToArray() };
        var settings = services.GetRequiredService<AppSiteSettings>();
        var binding = await services.GetRequiredService<ISignInDirectoryStore>().GetAsync(actor.DirectoryId!, http.RequestAborted);
        var decision = CustomerSignIn.Decide(claims, settings.ClientId ?? "", binding, autoJoin: false);
        // Auto-join exactly as at sign-in: read now, and only for a person who passed every other check but has no role.
        if (decision is SignInDecision.Denied { Reason: SignInDenial.NoRole } && binding is not null
            && await services.GetRequiredService<IAutoJoinSettingStore>().IsOnAsync(binding.TenantId, http.RequestAborted))
            decision = CustomerSignIn.Decide(claims, settings.ClientId ?? "", binding, autoJoin: true);
        var now = services.GetRequiredService<IClock>().UtcNow;
        var refusal = AzureSetupRules.CheckDeveloperStepUp(actor, All("tid"), All("oid"), decision, AppSite.SignInTime(p, now), now);
        if (refusal is not null)
        {
            if (refusal is StepUpRefusal.DifferentPerson or StepUpRefusal.SignInRefused) limiter.RecordFailure(http);
            Log(http).LogWarning("Developer check refused ({Reason})", refusal.Value.ToString());   // the reason only
            await DeveloperStepUpRefused(http, refusal.Value, decision);
            return;
        }
        var token = ctx.TokenEndpointResponse?.AccessToken;   // the only copy is this local; SaveTokens is off
        if (string.IsNullOrEmpty(token))
        {
            await Card(http, 400, "Sign-in failed", AppSite.FailedSentence(Support(http)), null, BackToDeveloper);
            return;
        }
        try
        {
            var held = await services.GetRequiredService<AzureSetupCommands>().RunDeveloperAsync(actor, token, http.RequestAborted);
            Log(http).LogInformation("Developer check for tenant {TenantId}: {Outcome}", actor.TenantId, held.Outcome.ToString());
        }
        catch (AzureSetupRefusedException refused)
        {
            await Refused(http, refused.Reason);
            return;
        }
        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = DeveloperPath;
    }

    private static Task DeveloperStepUpRefused(HttpContext ctx, StepUpRefusal reason, SignInDecision decision) => reason switch
    {
        StepUpRefusal.DifferentPerson => Card(ctx, 403, "Different account",
            "You signed in to Microsoft with a different account from the one signed in to Packmoat.",
            "Check your access with the account you use for Packmoat.", BackToDeveloper),
        StepUpRefusal.NotDeveloper => Card(ctx, 403, DevelopersOnlyTitle, DevelopersOnlyLine, null, HomeLink),
        StepUpRefusal.SignInRefused => decision is SignInDecision.Denied denied
            ? SignInRefusedCard(ctx, denied)
            : Card(ctx, 403, "Sign-in failed", AppSite.FailedSentence(Support(ctx)), null, HomeLink),
        _ => Card(ctx, 403, "Sign in again first",
            ReauthUnconfirmedLine(Support(ctx)),
            "Go back and choose " + DeveloperStepButton + ".", BackToDeveloper),
    };

    /// <summary>
    /// The step-up's own sign-in was refused. A directory unlinked while the person was at Microsoft (a race) gets the customer-site
    /// module's not-linked card, the same one sign-in shows; every other refusal its fixed words.
    /// </summary>
    private static Task SignInRefusedCard(HttpContext ctx, SignInDecision.Denied denied)
    {
        if (denied is { Reason: SignInDenial.DirectoryNotLinked, DirectoryId: { } directoryId })
        {
            var card = ctx.RequestServices.GetRequiredService<ICustomerSiteModule>().NotLinkedCard(directoryId, needsAdmin: false);
            return SignedInSite.Write(ctx, 403, Page(ctx, card.Title, card.Html));
        }
        var e = new CustomerSignInDeniedException(denied.Reason, denied.DirectoryId);
        return Card(ctx, 403, e.Title, e.MessageFor(Support(ctx)), null, HomeLink);
    }

    private static FeedNames? NamesFrom(AuthenticationProperties? props)
    {
        if (props is null || !props.Items.TryGetValue(OrgItem, out var org) || !props.Items.TryGetValue(FeedItem, out var feed) || org is null || feed is null) return null;
        props.Items.TryGetValue(ProjectItem, out var project);
        return new FeedNames(org, string.IsNullOrEmpty(project) ? null : project, feed);
    }

    private static IReadOnlyList<string> ConfirmedFrom(AuthenticationProperties? props)
    {
        if (props is null || !props.Items.TryGetValue(ConfirmItem, out var json) || string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    // ---------------------------------------------------------------- routes

    /// <summary>
    /// The two consent return pages: no sign-in needed (the approver may not use Packmoat), no state changed. The Packmoat app's
    /// (<see cref="ConsentedPath"/>) always exists, because sign-up and the sign-in cancelled card link to it; the feed-access
    /// app's only while the switch is not off. Of Microsoft's reply only whether it carries error is read: no value from it (error,
    /// error_description, tenant) is ever shown or trusted.
    /// </summary>
    internal static async Task<bool> RoutePublicAsync(HttpContext ctx, AppSiteSettings s, string path, bool get)
    {
        if (!get) return false;
        if (path == ConsentedPath)
        {
            var refused = ctx.Request.Query.ContainsKey("error");
            var (title, line) = refused ? (NotApprovedTitle, NotApprovedLine) : (ConsentedTitle, ConsentedLine);
            await SignedInSite.Write(ctx, 200, Page(ctx, title,
                $"<section class=\"card{(refused ? " notice" : "")}\"><h1>{E(title)}</h1><p>{E(line)}</p><div class=\"actions\">{GoToPackmoatLink}</div></section>"));
            return true;
        }
        if (s.AzureSetup.Mode == AzureSetupMode.Off || path != FeedAppAddedPath) return false;
        await SignedInSite.Write(ctx, 200, Page(ctx, "Thanks",
            $"<section class=\"card\"><h1>Thanks</h1><p>{E(Step2Return)}</p><div class=\"actions\">{BackToSetup}</div></section>"));
        return true;
    }

    // The Packmoat app's consent return page. New wording, not in the plan's draft: listed in the PR for approval.
    internal const string ConsentedTitle = "Thanks";
    internal const string ConsentedLine = "Once a directory administrator has approved Packmoat for your organisation, your developers can sign in.";
    internal const string NotApprovedTitle = "Not approved";
    internal const string NotApprovedLine = "Packmoat wasn't approved for your organisation. A directory administrator can open the same link again to approve it.";
    private const string GoToPackmoatLink = "<a class=\"btn\" href=\"/home\">Go to Packmoat</a>";

    /// <summary>True when the path was one of the wizard's (answered here, whatever the outcome). Called only while the switch allows the directory.</summary>
    internal static async Task<bool> RouteAsync(HttpContext ctx, CustomerActor actor, AppSiteSettings s, string path, bool get)
    {
        Func<Task>? handler = (path, get) switch
        {
            (Path, true) => () => SetupPage(ctx, actor, s),
            (CheckPath, false) => () => StartCheck(ctx, actor, s),
            (ConnectPath, false) => () => Connect(ctx, actor, s),
            (ReauthPath, false) => () => AppSite.ReauthAsync(ctx, Path),
            (ApprovePath, false) => () => Approve(ctx, actor, s),
            (DeveloperPath, true) => () => DeveloperPage(ctx, actor),
            (DeveloperCheckPath, false) => () => StartDeveloperCheck(ctx, actor),
            _ => null,
        };
        if (handler is null) return false;
        if (!get && !SignedInSite.OriginOk(ctx, s.Host))
        {
            await SignedInSite.ErrorPage(ctx, 403, "Request refused", "The request did not come from this site.", next: "Go back to the page and try again.");
            return true;
        }
        try { await handler(); }
        catch (AzureSetupRefusedException refused) { await Refused(ctx, refused.Reason); }
        return true;
    }

    private static AzureSetupCommands Commands(HttpContext ctx) => ctx.RequestServices.GetRequiredService<AzureSetupCommands>();
    private static ILogger Log(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
    private static string Af(HttpContext ctx) => Hidden(SignedInSite.AntiforgeryField, SignedInSite.AntiforgeryToken(ctx));
    private static string? Form(IFormCollection f, string name) => f.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;
    private static string? QueryOne(HttpContext ctx, string name) => ctx.Request.Query.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;
    private const string BackToSetup = "<a class=\"btn\" href=\"" + Path + "\">Back to setup</a>";
    private const string HomeLink = "<a class=\"btn ghost\" href=\"/home\">Back to home</a>";

    private static Task Show(HttpContext ctx, int status, string title, string body) => SignedInSite.Write(ctx, status, Page(ctx, title, body, SignedInSite.SignOutForm(ctx)));

    private static Task Card(HttpContext ctx, int status, string title, string message, string? next, string extra) =>
        SignedInSite.Write(ctx, status, Page(ctx, title, ErrorCard(ctx, title, message, next, extra)));

    /// <summary>POST /setup/azure/check: the rules, then the step-up sign-in carrying the names (and the admins being confirmed).</summary>
    private static async Task StartCheck(HttpContext ctx, CustomerActor actor, AppSiteSettings s)
    {
        SignedInSite.UseLandingCsp(ctx);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var confirm = Form(form, "confirmed") == "yes";
        // The sign-in limits first, so a refused start does not use up one of the hour's checks.
        if (ctx.RequestServices.GetRequiredService<AppSignInLimiter>().RetryAfterSeconds(ctx) is { } wait)
        {
            ctx.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Show(ctx, 429, "Too many attempts", ErrorCard(ctx, "Too many attempts", "Too many failed sign-ins. Wait a few minutes.", "Wait a few minutes, then check again.", BackToSetup));
            return;
        }
        if (AppSite.PendingSignIns(ctx) > AppSite.MaxPendingSignIns)
        {
            await Show(ctx, 429, "Too many attempts", ErrorCard(ctx, "Too many attempts", "Too many unfinished sign-ins in this browser.", "Wait 15 minutes, then check again.", BackToSetup));
            return;
        }
        AzureSetupRequest request;
        try { request = await Commands(ctx).StartAsync(actor, Form(form, "org"), Form(form, "project"), Form(form, "feed"), confirm, ctx.RequestAborted); }
        catch (AzureSetupRefusedException refused) when (refused.Reason == AzureSetupRefusal.BadNames)
        {
            var (_, problems) = AzureFeedNames.Validate(Form(form, "org"), Form(form, "project"), Form(form, "feed"));
            var view = await Commands(ctx).ViewAsync(actor, ctx.RequestAborted);
            await WizardPage(ctx, actor, s, view, 400, problems, Form(form, "org"), Form(form, "project"), Form(form, "feed"));
            return;
        }
        var properties = new OpenIdConnectChallengeProperties { RedirectUri = Path, Prompt = "login", MaxAge = CustomerTokenRules.FreshSignIn };
        properties.Items[PurposeItem] = SettingsPurpose;
        if (request.Names is { } n)
        {
            properties.Items[OrgItem] = n.Organization;
            properties.Items[ProjectItem] = n.Project ?? "";
            properties.Items[FeedItem] = n.Feed;
        }
        if (request.ConfirmedAdmins.Count > 0)
        {
            properties.Items[ConfirmItem] = JsonSerializer.Serialize(request.ConfirmedAdmins);
            properties.Items[SilentItem] = "1";
        }
        await ctx.ChallengeAsync(Scheme, properties);
    }

    // ---------------------------------------------------------------- the developer proof

    /// <summary>
    /// POST /setup/developer/check: the sign-in limits, the developer proof's rules, then the step-up sign-in: the scope replaced by
    /// <see cref="DeveloperScopes"/>, prompt=login, max_age 15 minutes, the purpose <see cref="DeveloperPurpose"/>, back to the page.
    /// </summary>
    private static async Task StartDeveloperCheck(HttpContext ctx, CustomerActor actor)
    {
        SignedInSite.UseLandingCsp(ctx);
        if (ctx.RequestServices.GetRequiredService<AppSignInLimiter>().RetryAfterSeconds(ctx) is { } wait)
        {
            ctx.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Show(ctx, 429, "Too many attempts", ErrorCard(ctx, "Too many attempts", "Too many failed sign-ins. Wait a few minutes.", "Wait a few minutes, then check again.", BackToDeveloper));
            return;
        }
        if (AppSite.PendingSignIns(ctx) > AppSite.MaxPendingSignIns)
        {
            await Show(ctx, 429, "Too many attempts", ErrorCard(ctx, "Too many attempts", "Too many unfinished sign-ins in this browser.", "Wait 15 minutes, then check again.", BackToDeveloper));
            return;
        }
        await Commands(ctx).StartDeveloperAsync(actor, ctx.RequestAborted);   // refuses an Admin, or a tenant not ready
        var properties = new OpenIdConnectChallengeProperties
        {
            RedirectUri = DeveloperPath, Prompt = "login", MaxAge = CustomerTokenRules.FreshSignIn, Scope = DeveloperScopes.ToList(),
        };
        properties.Items[PurposeItem] = DeveloperPurpose;
        await ctx.ChallengeAsync(Scheme, properties);
    }

    /// <summary>GET /setup/developer: what is checked (the latest settings check's feed; nothing to type), the button, and this person's last result.</summary>
    private static async Task DeveloperPage(HttpContext ctx, CustomerActor actor)
    {
        var view = await Commands(ctx).DeveloperViewAsync(actor, ctx.RequestAborted);   // refuses an Admin ("Developers only") or "Not ready yet"
        SignedInSite.UseLandingCsp(ctx);   // the form posts, then redirects to Microsoft's sign-in
        var sb = new StringBuilder();
        sb.Append("<section class=\"card wide\" aria-labelledby=\"dev-h\"><p class=\"eyebrow\">Setup</p><h1 id=\"dev-h\">").Append(E(DeveloperCardButton)).Append("</h1>");
        sb.Append("<p class=\"lede\">").Append(E(DeveloperCardSentence)).Append("</p>");
        if (view.Held is { } held) sb.Append(DeveloperResults(held, Support(ctx)));
        // The feed by name, with its URL under Details; an older check without names shows the URL as before.
        sb.Append("<dl class=\"facts\"><div><dt>Feed</dt><dd>");
        if (view.Settings.Names is { } names)
            sb.Append(E(FeedLabel(names))).Append("<details><summary>Details</summary><p>").Append(E(view.Settings.FeedIndexUrl)).Append("</p></details>");
        else sb.Append(E(view.Settings.FeedIndexUrl));
        sb.Append("</dd></div></dl>");
        sb.Append("<p>").Append(E(DeveloperStep)).Append("</p>");
        sb.Append("<form method=\"post\" action=\"").Append(DeveloperCheckPath).Append("\" class=\"inline\">").Append(Af(ctx))
          .Append("<button type=\"submit\" class=\"btn\">").Append(E(DeveloperStepButton)).Append("</button></form>");
        sb.Append("<p class=\"fine\">").Append(E(PrivacyLine)).Append("</p>");
        sb.Append("<div class=\"actions\">").Append(HomeLink).Append("</div></section>");
        await Show(ctx, 200, DeveloperCardButton, sb.ToString());
    }

    /// <summary>"organisation / project / feed", or "organisation / feed" for an organisation-scoped feed.</summary>
    internal static string FeedLabel(FeedNames n) => n.Project is { } p ? $"{n.Organization} / {p} / {n.Feed}" : $"{n.Organization} / {n.Feed}";

    /// <summary>The developer's last result: the sentence for what was found, the bypass advisory when there is one, and each result's detail.</summary>
    private static string DeveloperResults(HeldFeedCheck held, SupportContact support)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"results\" aria-labelledby=\"dev-results-h\"><h2 id=\"dev-results-h\">Result of your last check</h2>");
        sb.Append("<p class=\"fine\">").Append(E(held.At.UtcDateTime.ToString("d MMMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture))).Append("</p>");
        sb.Append(held.Finding switch
        {
            DeveloperFinding.Refused => $"<p class=\"flash\" role=\"status\">{E(DeveloperPass)}</p>",
            DeveloperFinding.CanRead => $"<p class=\"flash warn\" role=\"alert\">{E(DeveloperFail)}</p>",
            DeveloperFinding.NotMember => $"<p class=\"flash warn\" role=\"status\">{E(DeveloperNotMember)}</p>",
            _ => $"<p class=\"flash warn\" role=\"status\">{E(DeveloperUnclear)}</p>",
        });
        var bypass = Bypass(held.Results);
        if (bypass is not null) sb.Append(BypassLine(bypass.Detail));
        // The bypass sentence is shown once: its result stays listed, without repeating it under Details.
        sb.Append(ResultList(held.Results, support, noDetails: bypass)).Append("</section>");
        return sb.ToString();
    }

    /// <summary>The bypass advisory when it found something (a warning) or could not check (never shown as a pass); null otherwise.</summary>
    private static CheckResult? Bypass(IReadOnlyList<CheckResult> results) =>
        results.FirstOrDefault(r => r.Id == BypassAdvisory.Id) is { Status: CheckStatus.Warn or CheckStatus.Skip } b ? b : null;

    private static string BypassLine(string sentence) => $"<p class=\"flash warn\" role=\"status\">{E(sentence)}</p>";

    /// <summary>
    /// A result's detail as the page shows it. A check that errored keeps its stored "could not complete: {type}: {message}" text (the
    /// withdraw and timeout rules read it), but the page shows one fixed sentence instead.
    /// </summary>
    internal static string Shown(CheckResult r, SupportContact support) =>
        r.Status == CheckStatus.Fail && r.Detail.StartsWith(ConnectRules.CouldNotComplete, StringComparison.Ordinal) ? CouldNotFinishLine(support) : r.Detail;

    /// <param name="noDetails">A result whose detail the page already shows: listed without its Details.</param>
    private static string ResultList(IReadOnlyList<CheckResult> results, SupportContact support, CheckResult? noDetails = null)
    {
        var sb = new StringBuilder("<ul class=\"checks\">");
        foreach (var r in results)
        {
            var kind = r.Status switch { CheckStatus.Pass => "active", CheckStatus.Fail => "fail", _ => "expired" };
            sb.Append("<li><span class=\"status ").Append(kind).Append("\">").Append(E(OnboardingSummary.Label(r.Status))).Append("</span> ").Append(E(r.Title));
            if (!ReferenceEquals(r, noDetails)) sb.Append("<details><summary>Details</summary><p>").Append(E(Shown(r, support))).Append("</p></details>");
            sb.Append("</li>");
        }
        return sb.Append("</ul>").ToString();
    }

    /// <summary>The Admin's "Waiting for a developer" card: the link to send, how they get in (the auto-join setting), and the solo hint.</summary>
    private static string WaitingCard(AzureSetupView view, AppSiteSettings s, string? directoryId)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"results\" aria-labelledby=\"waiting-h\"><h2 id=\"waiting-h\">").Append(E(WaitingTitle)).Append("</h2>");
        sb.Append("<p>").Append(E(WaitingSendLink)).Append("</p><pre class=\"snippet\">").Append(E("https://" + s.Host + DeveloperPath)).Append("</pre>");
        sb.Append("<p>").Append(E(WaitingHow)).Append("</p>");
        sb.Append("<p>").Append(E(WaitingNeedAdmin)).Append("</p><pre class=\"snippet\">").Append(E(OrganisationConsentUrl(s, directoryId))).Append("</pre>");
        sb.Append("<p>").Append(E(view.AutoJoinOn == true ? WaitingAutoJoinOn : WaitingAutoJoinOff)).Append("</p>");
        sb.Append("<p class=\"fine\">").Append(E(WaitingSolo)).Append("</p></section>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- POST /setup/azure/approve

    /// <summary>
    /// Step 2: a submit button in step 1's form, so the typed names come along. The names that pass their rules are kept
    /// for this person (30 minutes, in the shared scratch, names only), then 303 to Microsoft's approval page for the shared app in the person's own
    /// directory, returning to the fixed thanks page. A name that fails its rule is dropped, never echoed.
    /// </summary>
    private static async Task Approve(HttpContext ctx, CustomerActor actor, AppSiteSettings s)
    {
        SignedInSite.UseLandingCsp(ctx);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var client = await Commands(ctx).KeepDraftAsync(actor, Form(form, "org"), Form(form, "project"), Form(form, "feed"), ctx.RequestAborted);
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = AdminConsentUrl(actor.DirectoryId!, client, "https://" + s.Host + FeedAppAddedPath);
    }

    // ---------------------------------------------------------------- POST /setup/azure/connect

    /// <summary>
    /// Connect: the person's checks (switch, Admin, fresh sign-in) in <see cref="ConnectFeedCommands"/>, then the store's one transaction.
    /// The form carries only the two check ids and what the page showed (feed URL, client id); the tenant comes from the session.
    /// </summary>
    private static async Task Connect(HttpContext ctx, CustomerActor actor, AppSiteSettings s)
    {
        SignedInSite.UseLandingCsp(ctx);   // a refusal may offer the fresh sign-in form, which redirects to Microsoft
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        static long Id(string? v) => long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
        if (ctx.RequestServices.GetService<ISelfServiceFeedStore>() is not { } store)
        {
            await Refused(ctx, AzureSetupRefusal.NotAvailable);
            return;
        }
        var result = await new ConnectFeedCommands(store, s.AzureSetup, ctx.RequestServices.GetRequiredService<IClock>())
            .ConnectAsync(actor, Id(Form(form, "settings")), Id(Form(form, "developer")), Form(form, "feed"), Form(form, "client"), ctx.RequestAborted);
        Log(ctx).LogInformation("Connect for tenant {TenantId}: {Result}", actor.TenantId, result.ToString());   // the result only
        switch (result)
        {
            case ConnectResult.Connected:
                ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
                ctx.Response.Headers.Location = Path;
                return;
            case ConnectResult.Disabled: await Refused(ctx, AzureSetupRefusal.Disabled); return;
            case ConnectResult.NotAdmin: await Refused(ctx, AzureSetupRefusal.NotAdmin); return;
            case ConnectResult.NotLinked: await Refused(ctx, AzureSetupRefusal.NotLinked); return;
            case ConnectResult.NotSelfMade or ConnectResult.OwnApp: await Refused(ctx, AzureSetupRefusal.ReadOnly); return;
            case ConnectResult.FreshSignInRequired:
                await Show(ctx, 403, "Sign in again first", ErrorCard(ctx, "Sign in again first", ConnectFreshLine, "Sign in again with Microsoft, then connect.",
                    $"<form method=\"post\" action=\"{ReauthPath}\" class=\"inline\">{Af(ctx)}<button type=\"submit\" class=\"btn\">Sign in again</button></form>" + BackToSetup));
                return;
            case ConnectResult.DirectoryTaken:
                await Show(ctx, 409, "Couldn't connect", ErrorCard(ctx, "Couldn't connect", ConnectTakenLine(Support(ctx)), null, BackToSetup));
                return;
            default:
                await Show(ctx, 409, "Couldn't connect", ErrorCard(ctx, "Couldn't connect", ConnectStaleLine, null, BackToSetup));
                return;
        }
    }

    /// <summary>The Connect card: who proved the developer step and when, the sentence, and the button (only when both checks qualify now).</summary>
    private static string ConnectCard(HttpContext ctx, CustomerActor actor, ConnectOffer offer)
    {
        var d = offer.Developer;
        var date = d.CheckedAt.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("<section class=\"results\" aria-labelledby=\"connect-h\"><h2 id=\"connect-h\">").Append(E(ConnectButton)).Append("</h2>");
        sb.Append("<p>").Append(E(string.Format(System.Globalization.CultureInfo.InvariantCulture, ProofSentence, d.CheckedByName, date))).Append("</p>");
        // advisory: shown, never blocking, and addressed to the Admin
        if (Bypass(d.Results) is { } bypass && BypassAdvisory.AdminSentence(bypass) is { } sentence) sb.Append(BypassLine(sentence));
        sb.Append("<p>").Append(E(ctx.RequestServices.GetRequiredService<Modules.ICustomerSiteModule>().ConnectSentence(actor))).Append("</p>");
        sb.Append("<form method=\"post\" action=\"").Append(ConnectPath).Append("\" class=\"inline\">").Append(Af(ctx))
          .Append(Hidden("settings", offer.Settings.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
          .Append(Hidden("developer", d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
          .Append(Hidden("feed", offer.Settings.FeedIndexUrl)).Append(Hidden("client", offer.Settings.ClientId))
          .Append("<button type=\"submit\" class=\"btn\">").Append(E(ConnectButton)).Append("</button></form></section>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- GET /setup/azure

    private static async Task SetupPage(HttpContext ctx, CustomerActor actor, AppSiteSettings s)
    {
        var view = await Commands(ctx).ViewAsync(actor, ctx.RequestAborted);   // refuses a Developer
        if (view.Kind == AzureSetupKind.Wizard) await WizardPage(ctx, actor, s, view, 200, Array.Empty<FeedNameProblem>(), null, null, null);
        else await StatusPage(ctx, view);
    }

    private static async Task WizardPage(HttpContext ctx, CustomerActor actor, AppSiteSettings s, AzureSetupView view, int status,
        IReadOnlyList<FeedNameProblem> problems, string? typedOrg, string? typedProject, string? typedFeed)
    {
        SignedInSite.UseLandingCsp(ctx);   // the forms post, then redirect to Microsoft's sign-in
        // Prefilled (after a refusal) with the values that passed, else from this person's last check, else from what they had typed when
        // they opened the approval page, else from the tenant's latest recorded check. A refused value is never echoed.
        var held = view.Held;
        // Of this person's held check and draft, the newer one wins.
        var heldNames = held?.Names;
        var draftFirst = view.Draft is { } dr && (heldNames is null || dr.At > held!.At);
        var last = draftFirst && view.Draft is { } d ? (d.Organization, d.Project, d.Feed)
            : heldNames is { } h ? (h.Organization, h.Project, h.Feed)
            : view.Latest?.Names is { } l ? (l.Organization, l.Project, l.Feed)
            : ((string?)null, (string?)null, (string?)null);
        string Value(string? typed, bool bad, string? from) => bad ? "" : typed ?? from ?? "";
        var org = Value(typedOrg, problems.Contains(FeedNameProblem.Organization), last.Item1);
        var project = Value(typedProject, problems.Contains(FeedNameProblem.Project), last.Item2);
        var feed = Value(typedFeed, problems.Contains(FeedNameProblem.Feed), last.Item3);
        static string Error(bool show, string sentence) => show ? $"<p class=\"flash warn\" role=\"alert\">{E(sentence)}</p>" : "";

        var sb = new StringBuilder();
        sb.Append("<section class=\"card wide\" aria-labelledby=\"setup-h\"><p class=\"eyebrow\">Setup</p><h1 id=\"setup-h\">").Append(E(HomeCardButton)).Append("</h1>");
        sb.Append("<p class=\"lede\">").Append(E(Intro)).Append("</p>");
        if (!view.CanCheck)
            sb.Append("<p class=\"flash warn\" role=\"status\">").Append(E("Feed setup isn't available yet. " + Support(ctx).CapitalPhrase + ".")).Append("</p>");
        if (held?.WithdrawFailed == true) sb.Append("<p class=\"flash warn\" role=\"alert\">").Append(E(WithdrawFailedLine(Support(ctx)))).Append("</p>");
        else if (view.Readiness == UpstreamReadiness.Ready) sb.Append("<p class=\"flash\" role=\"status\">").Append(E(ConnectedLine)).Append("</p>");
        else if (held?.Withdrawn == true) sb.Append("<p class=\"flash warn\" role=\"alert\">").Append(E(WithdrawnLine)).Append("</p>");
        // Back from Connect's fresh sign-in: say whether Microsoft confirmed it, rather than showing the same page silently.
        if (QueryOne(ctx, "m") == "reauth" && view.Readiness != UpstreamReadiness.Ready)
        {
            if (!CustomerTokenRules.IsFresh(actor.SignedInAt, ctx.RequestServices.GetRequiredService<IClock>().UtcNow))
                sb.Append("<p class=\"flash warn\" role=\"alert\">").Append(E(ReauthUnconfirmedLine(Support(ctx)))).Append("</p>");
            else if (view.Connect is not null)
                sb.Append("<p class=\"flash\" role=\"status\">").Append(E(ReauthDoneLine)).Append("</p>");
            else sb.Append("<p class=\"flash warn\" role=\"status\">").Append(E(ConnectStaleLine)).Append("</p>");   // signed in, but nothing to connect now
        }
        if (view.Connect is { } offer) sb.Append(ConnectCard(ctx, actor, offer));
        else if (view.WaitingForDeveloper) sb.Append(WaitingCard(view, s, actor.DirectoryId));
        if (held is not null || view.Latest is not null) sb.Append(Results(ctx, view, held, view.Latest, wizard: true));

        sb.Append("<form method=\"post\" action=\"").Append(CheckPath).Append("\" class=\"setup\">").Append(Af(ctx));
        // The form's default button comes first, so Enter in step 1 checks (or does nothing) and never opens step 2's approval page.
        // Off-screen rather than display:none, which some browsers skip for the default button.
        sb.Append(view.CanCheck
            ? "<button type=\"submit\" class=\"sr-only\" tabindex=\"-1\" aria-hidden=\"true\">Check your setup</button>"
            : "<button type=\"submit\" disabled hidden aria-hidden=\"true\"></button>");
        sb.Append("<ol class=\"steps\">");
        sb.Append("<li><h2>Your feed</h2><div class=\"create\">");
        sb.Append("<label for=\"org\">Organisation</label>").Append(Input("org", org, required: true, max: 50));
        sb.Append("<p class=\"fine\">").Append(E(OrgHelp)).Append("</p>").Append(Error(problems.Contains(FeedNameProblem.Organization), OrgError));
        sb.Append("<label for=\"project\">Project</label>").Append(Input("project", project, required: false, max: AzureFeedNames.MaxProjectLength));
        sb.Append("<p class=\"fine\">").Append(E(ProjectHelp)).Append("</p>").Append(Error(problems.Contains(FeedNameProblem.Project), ProjectError));
        sb.Append("<label for=\"feed\">Feed</label>").Append(Input("feed", feed, required: true, max: AzureFeedNames.MaxFeedLength));
        sb.Append("<p class=\"fine\">").Append(E(FeedHelp)).Append("</p>").Append(Error(problems.Contains(FeedNameProblem.Feed), FeedError));
        sb.Append("</div><h3>Feed settings</h3><ul class=\"disclose\">");
        foreach (var line in Checklist) sb.Append("<li>").Append(E(line)).Append("</li>");
        sb.Append("</ul></li>");

        sb.Append("<li><h2>Approve Packmoat feed access</h2><p>").Append(E(Step2)).Append("</p>");
        if (view.SharedClientId is { } feedApp)
        {
            // A submit in step 1's form: the names come along and are kept while the person is at Microsoft. No browser
            // validation, so it works before step 1 is filled in.
            sb.Append("<p><button type=\"submit\" class=\"btn ghost\" formaction=\"").Append(ApprovePath).Append("\" formnovalidate>")
              .Append(E(Step2Button)).Append("</button></p>");
            // For an Admin who isn't the directory administrator: the same page, to pass on (text, not a link).
            sb.Append("<p class=\"fine\">").Append(E(Step2SendLink)).Append("</p><pre class=\"snippet\">")
              .Append(E(AdminConsentUrl(actor.DirectoryId!, feedApp, "https://" + s.Host + FeedAppAddedPath))).Append("</pre>");
            sb.Append("<p>Or run:</p><pre class=\"snippet\">").Append(E("az ad sp create --id " + feedApp)).Append("</pre>");
        }
        sb.Append("</li>");
        sb.Append("<li><h2>Give it access in Azure DevOps</h2><p>").Append(E(Step3)).Append("</p></li>");
        sb.Append("<li><h2>Check your setup</h2><p>").Append(E(Step4)).Append("</p>");
        if (view.CanCheck) sb.Append("<button type=\"submit\" class=\"btn\">").Append(E(Step4Button)).Append("</button>");
        sb.Append("<p class=\"fine\">").Append(E(PrivacyLine)).Append("</p></li>");
        sb.Append("</ol></form>");
        sb.Append("<div class=\"actions\">").Append(HomeLink).Append("</div></section>");
        await Show(ctx, status, HomeCardButton, sb.ToString());
    }

    private static string Input(string name, string value, bool required, int max) =>
        $"<input id=\"{name}\" name=\"{name}\" type=\"text\" maxlength=\"{max}\" autocomplete=\"off\" spellcheck=\"false\"{(required ? " required" : "")} value=\"{E(value)}\">";

    /// <summary>Own-app and operator-made tenants: a status card. An operator-made shared tenant also gets "Check again" (check only: nothing recorded).</summary>
    private static async Task StatusPage(HttpContext ctx, AzureSetupView view)
    {
        SignedInSite.UseLandingCsp(ctx);
        var connected = view.Readiness == UpstreamReadiness.Ready;
        var sb = new StringBuilder();
        sb.Append("<section class=\"card wide\" aria-labelledby=\"setup-h\"><p class=\"eyebrow\">Setup</p><h1 id=\"setup-h\">Your feed</h1>");
        sb.Append("<p>").Append(E(ReadOnlyLine(Support(ctx)))).Append("</p>");
        sb.Append("<dl class=\"facts\">");
        sb.Append("<div><dt>Feed</dt><dd>").Append(E(view.Tenant.UpstreamFeedIndexUrl.Length > 0 ? view.Tenant.UpstreamFeedIndexUrl : "None")).Append("</dd></div>");
        sb.Append("<div><dt>Status</dt><dd>").Append(connected ? "Connected" : "Not connected yet").Append("</dd></div></dl>");
        if (view.Kind == AzureSetupKind.CheckOnly)
        {
            if (view.Held is { } held) sb.Append(Results(ctx, view, held, null, wizard: false));
            sb.Append("<p class=\"fine\">").Append(E(CheckOnlyLine)).Append("</p>");
            if (view.CanCheck)
                sb.Append("<form method=\"post\" action=\"").Append(CheckPath).Append("\" class=\"inline\">").Append(Af(ctx))
                  .Append("<button type=\"submit\" class=\"btn\">").Append(E(CheckAgainButton)).Append("</button></form>");
            sb.Append("<p class=\"fine\">").Append(E(PrivacyLine)).Append("</p>");
        }
        sb.Append("<div class=\"actions\">").Append(HomeLink).Append("</div></section>");
        await Show(ctx, 200, "Your feed", sb.ToString());
    }

    /// <summary>The last check: what it was about, each result, what to do next, and the named-admin confirmation when there is one to make.</summary>
    private static string Results(HttpContext ctx, AzureSetupView view, HeldFeedCheck? held, FeedCheck? latest, bool wizard)
    {
        var results = held?.Results ?? latest!.Results;
        var outcome = held?.Outcome ?? latest!.Outcome;
        var at = held?.At ?? latest!.CheckedAt;
        var sb = new StringBuilder();
        sb.Append("<section class=\"results\" aria-labelledby=\"results-h\"><h2 id=\"results-h\">Results of the last check</h2>");
        sb.Append("<p class=\"fine\">").Append(E(at.UtcDateTime.ToString("d MMMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture))).Append("</p>");
        sb.Append(outcome switch
        {
            FeedCheckOutcome.Passed => $"<p class=\"flash\" role=\"status\">{E(view.WaitingForDeveloper ? WaitingOnDeveloper : SettingsPassLine)}</p>",
            FeedCheckOutcome.Failed => $"<p class=\"flash warn\" role=\"status\">{E(FailedLine)}</p>",
            _ => $"<p class=\"flash warn\" role=\"status\">{E(IncompleteLine)}</p>",
        });
        sb.Append(ResultList(results, Support(ctx)));
        // The confirmation: only for this person's own check held in the shared scratch, when the permissions check is waiting on named admins.
        if (held is not null && held.NamedAdmins.Count is > 0 and <= AzureSetupRules.MaxConfirmableAdmins
            && held.Results.FirstOrDefault(r => r.Id == "permissions")?.Status == CheckStatus.Warn && view.CanCheck)
        {
            sb.Append("<div class=\"reauth\"><p>").Append(E(NamedAdminsSentence)).Append("</p><ul>");
            foreach (var a in held.NamedAdmins) sb.Append("<li>").Append(E(a.Label)).Append("</li>");
            sb.Append("</ul><form method=\"post\" action=\"").Append(CheckPath).Append("\" class=\"checkout\">").Append(Af(ctx)).Append(NameFields(held.Names));
            sb.Append("<label class=\"terms\"><input type=\"checkbox\" name=\"confirmed\" value=\"yes\" required><span>").Append(E(ConfirmCheckbox)).Append("</span></label>");
            sb.Append("<button type=\"submit\" class=\"btn\">").Append(E(ConfirmButton)).Append("</button></form></div>");
        }
        else if (view.CanCheck && wizard && held?.Names is not null)
        {
            sb.Append("<form method=\"post\" action=\"").Append(CheckPath).Append("\" class=\"inline\">").Append(Af(ctx)).Append(NameFields(held?.Names))
              .Append("<button type=\"submit\" class=\"btn\">").Append(E(CheckAgainButton)).Append("</button></form>");
        }
        sb.Append("</section>");
        return sb.ToString();
    }

    private static string NameFields(FeedNames? n) => n is null ? "" : Hidden("org", n.Organization) + Hidden("project", n.Project ?? "") + Hidden("feed", n.Feed);

    // ---------------------------------------------------------------- the home card

    /// <summary>
    /// The home cards, while the switch allows the directory; "" otherwise. A self-made tenant with no feed yet: the "not connected" card
    /// (only Admins get the button). A Developer whose proof is open: the developer card with "Check my access".
    /// </summary>
    internal static async Task<string> HomeCardAsync(HttpContext ctx, CustomerActor actor, AppSiteSettings s)
    {
        if (!s.AzureSetup.Allows(actor.DirectoryId) || ctx.RequestServices.GetService<AzureSetupCommands>() is not { } commands) return "";
        var cards = "";
        if (await commands.ShowConnectCardAsync(actor, ctx.RequestAborted))
        {
            var button = actor.Role == CustomerRole.Admin ? $"<a class=\"btn\" href=\"{Path}\">{E(HomeCardButton)}</a>" : "";
            cards += $"<div class=\"callout warn\" role=\"status\">{E(HomeCardSentence)}{button}</div>";
        }
        if (await commands.ShowDeveloperCardAsync(actor, ctx.RequestAborted))
            cards += $"<div class=\"callout warn\" role=\"status\">{E(DeveloperCardSentence)}<a class=\"btn\" href=\"{DeveloperPath}\">{E(DeveloperCardButton)}</a></div>";
        return cards;
    }

    // ---------------------------------------------------------------- refusals

    private static Task StepUpRefused(HttpContext ctx, StepUpRefusal reason) => reason switch
    {
        StepUpRefusal.DifferentPerson => Card(ctx, 403, "Different account",
            "You signed in to Microsoft with a different account from the one signed in to Packmoat.",
            "Sign in and check with the account you use for Packmoat.", BackToSetup),
        StepUpRefusal.NotAdmin => Card(ctx, 403, "Admins only",
            "Only a Packmoat admin in your organisation can connect the feed.",
            "Ask someone with the Packmoat Admin role in your organisation.", BackToSetup),
        _ => Card(ctx, 403, "Sign in again first",
            ReauthUnconfirmedLine(Support(ctx)),
            "Go back to setup and choose " + Step4Button + ".", BackToSetup),
    };

    /// <summary>A fixed page for each refusal. Nothing a person typed is echoed.</summary>
    private static async Task Refused(HttpContext ctx, AzureSetupRefusal reason)
    {
        Log(ctx).LogWarning("Azure setup refused ({Reason})", reason.ToString());   // the reason only
        if (reason == AzureSetupRefusal.Disabled)
        {
            await SignedInSite.Write(ctx, 404, Page(ctx, "Not found", ErrorCard(ctx, "Not found", "There is no page at this address.", "Go to your home page.",
                "<a class=\"btn\" href=\"/home\">Go to your home page</a>"), SignedInSite.SignOutForm(ctx)));
            return;
        }
        var support = Support(ctx);
        var (status, title, message, next) = reason switch
        {
            AzureSetupRefusal.NotAdmin => (403, "Admins only", "Only a Packmoat admin in your organisation can connect the feed.", "Ask someone with the Packmoat Admin role in your organisation."),
            AzureSetupRefusal.NotLinked => (403, "Not linked", "Your organisation's directory is no longer linked to this Packmoat account.", "Sign in again. If it keeps happening, " + support.Phrase + "."),
            AzureSetupRefusal.ReadOnly => (409, ReadOnlyTitle(support), ReadOnlyLine(support), "Go back to your home page."),
            AzureSetupRefusal.TooManyRequests => (429, "Too many checks", "Too many checks from your account in the last hour.", "Wait a while, then try again."),
            AzureSetupRefusal.CheckRunning => (409, "A check is running", "A check for your organisation is already running.", "Wait a minute, then check again."),
            AzureSetupRefusal.BadNames => (400, "Check the names", OrgError + " " + FeedError, "Go back and try again."),
            AzureSetupRefusal.DevelopersOnly => (403, DevelopersOnlyTitle, DevelopersOnlyLine, "Send the link on the setup page to one of your developers."),
            AzureSetupRefusal.NotReady => (409, NotReadyTitle, NotReadyLine, "Ask your Admin to check the feed's settings, then try again."),
            _ => (503, "Not available yet", "Feed setup isn't available yet.", support.CapitalPhrase + "."),
        };
        if (status == 429) ctx.Response.Headers.RetryAfter = "600";
        await Show(ctx, status, title, ErrorCard(ctx, title, message, next,
            reason is AzureSetupRefusal.NotAdmin or AzureSetupRefusal.ReadOnly or AzureSetupRefusal.NotReady ? HomeLink
            : reason == AzureSetupRefusal.DevelopersOnly ? BackToSetup
            : ctx.Request.Path.StartsWithSegments(DeveloperPath) || ctx.Items.ContainsKey(DeveloperFlowItem) ? BackToDeveloper
            : BackToSetup));
    }
}
