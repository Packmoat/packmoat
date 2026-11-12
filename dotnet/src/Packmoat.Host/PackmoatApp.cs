using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using Packmoat.Host.Auth;
using Packmoat.Host.Customer;
using Packmoat.Host.Modules;
using Packmoat.Host.Proxy;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packmoat.Policy;

namespace Packmoat.Host;

public sealed class PackmoatOptions
{
    /// <summary>
    /// The database (environment: PACKMOAT_DB): SQLite's file, or with PACKMOAT_DB_ENGINE=postgres a PostgreSQL connection string, never logged
    /// or echoed. When set, the real tenant, token and audit stores are used (production).
    /// </summary>
    public string? Database { get; set; }

    /// <summary>Base64 of the 32-byte master key. Environment: PACKMOAT_MASTER_KEY.</summary>
    public string? MasterKeyBase64 { get; set; }

    /// <summary>
    /// Host names this service answers to, separated by spaces, commas or semicolons (environment: PACKMOAT_HOSTS).
    /// Any other Host header is refused with 400, so the URLs we write into responses can only ever name our own hostnames.
    /// Loopback names are always allowed (the container health check and the operator).
    /// </summary>
    public string? AllowedHosts { get; set; }

    /// <summary>
    /// Addresses or networks (CIDR) of reverse proxies whose X-Forwarded-* headers are believed, besides loopback, which always is (environment:
    /// PACKMOAT_TRUSTED_PROXIES). Unset means loopback only. Parsed strictly at start by <see cref="Packmoat.Host.TrustedProxies"/>.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>The largest upstream JSON document the proxy will hold in memory, enforced while reading.</summary>
    public long MaxJsonBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Set when the proxy sits behind something that hides its public address. Otherwise the request's own host is used.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Requests per token per minute before 429. Generous: it stops runaway loops, not normal restores.</summary>
    public int TokenRequestsPerMinute { get; set; } = 6000;

    /// <summary>
    /// The one hostname that serves the operator admin site (environment: PACKMOAT_ADMIN_HOST, for example admin.example.com).
    /// Empty or unset means the admin site does not exist at all: every host is then the proxy. Never put it in PACKMOAT_HOSTS; it is added to the allowed hosts automatically.
    /// </summary>
    public string? AdminHost { get; set; }

    /// <summary>The Entra (Microsoft) directory the operator signs in from (PACKMOAT_ADMIN_TENANT_ID). Issuers from any other directory are refused.</summary>
    public string? AdminTenantId { get; set; }

    /// <summary>The Entra app registration's application (client) id (PACKMOAT_ADMIN_CLIENT_ID).</summary>
    public string? AdminClientId { get; set; }

    /// <summary>The Entra app registration's client secret (PACKMOAT_ADMIN_CLIENT_SECRET). Environment only; never logged.</summary>
    public string? AdminClientSecret { get; set; }

    /// <summary>Who may sign in: comma, space or semicolon separated tid:oid pairs (PACKMOAT_ADMIN_ALLOW). Empty means nobody.</summary>
    public string? AdminAllow { get; set; }

    /// <summary>Where the admin cookie encryption keys live (PACKMOAT_ADMIN_KEYS_DIR). Default: the "admin-keys" folder next to the database file.</summary>
    public string? AdminKeysDir { get; set; }

    /// <summary>Hosts a tenant's upstream may be on, comma separated (PACKMOAT_ADMIN_UPSTREAM_HOSTS). Default: pkgs.dev.azure.com.</summary>
    public string? AdminUpstreamHosts { get; set; }

    /// <summary>
    /// A test seam: registers a fake identity provider in place of Microsoft on the admin site, so a test can sign in without
    /// Microsoft. Null by default and in production; settable only in code, never from the environment. When set, the server logs a
    /// warning naming it at start.
    /// </summary>
    public Action<Microsoft.AspNetCore.Authentication.AuthenticationBuilder>? AdminRemoteSchemeOverride { get; set; }

    /// <summary>
    /// The one hostname that serves the customer site (environment: PACKMOAT_APP_HOST, for example app.contoso.example).
    /// Empty or unset means the customer site does not exist. Never put it in PACKMOAT_HOSTS; it is added to the allowed hosts automatically.
    /// </summary>
    public string? AppHost { get; set; }

    /// <summary>The customer sign-in app registration's application (client) id (PACKMOAT_APP_CLIENT_ID). A separate multitenant app, not the admin app and not the shared upstream app.</summary>
    public string? AppClientId { get; set; }

    /// <summary>Path to a PEM holding that app's certificate and private key (PACKMOAT_APP_CERT). Certificate credential only: there is no client secret.</summary>
    public string? AppCertPath { get; set; }

    /// <summary>
    /// A test seam: registers a fake identity provider in place of Microsoft on the customer site, so a module's tests can sign
    /// people in. Null by default and in production; settable only in code, never from the environment. When set, the server logs a
    /// warning naming it at start.
    /// </summary>
    public Action<Microsoft.AspNetCore.Authentication.AuthenticationBuilder>? AppRemoteSchemeOverride { get; set; }

    /// <summary>
    /// The Azure setup wizard on the customer site (PACKMOAT_AZURE_SETUP: off, on, or a comma-separated list of directory GUIDs). Off by
    /// default: with it off, none of its pages, its sign-in callback or the home card exist and the site behaves exactly as before.
    /// </summary>
    public AzureSetupSettings AzureSetup { get; set; } = AzureSetupSettings.Off;

    /// <summary>
    /// A test seam: registers a fake for the Azure DevOps check's sign-in scheme. Null by default and in production; settable only
    /// in code, never from the environment. When set, the server logs a warning naming it at start.
    /// </summary>
    public Action<Microsoft.AspNetCore.Authentication.AuthenticationBuilder>? AppAzureDevOpsSchemeOverride { get; set; }

    /// <summary>
    /// A test seam: the transport under the Azure DevOps clients (the delegated check's, the developer probe's and the shared app
    /// probe's; a fake Azure DevOps). Null by default and in production; settable only in code, never from the environment. When set, the
    /// server logs a warning naming it at start.
    /// </summary>
    public Func<HttpMessageHandler>? AzureDevOpsHandlerOverride { get; set; }

    /// <summary>
    /// A test seam: builds the shared app probe's Entra credential, so no sign-in to Microsoft is needed. Null by default and in
    /// production; settable only in code, never from the environment. When set, the server logs a warning naming it at start.
    /// </summary>
    public Func<AppSignIn, Azure.Core.TokenCredential>? SharedAppTokenOverride { get; set; }

    /// <summary>
    /// Who runs this server: PACKMOAT_OPERATOR_NAME, PACKMOAT_SUPPORT_EMAIL and PACKMOAT_SITE_URL (the privacy policy and terms are
    /// &lt;site&gt;/privacy and &lt;site&gt;/terms). Each optional: unset, the customer pages leave the link out or use neutral words. A bad
    /// value stops the start, never echoing it.
    /// </summary>
    public Web.OperatorLinks Operator { get; set; } = Web.OperatorLinks.None;

    /// <summary>A test seam: how often the retention pass runs (production: <see cref="RetentionService.Interval"/>).</summary>
    public TimeSpan? RetentionInterval { get; set; }

    /// <summary>
    /// A test seam: how long every job lease runs before another copy may take it over (production: <see cref="JobLeases.Ttl"/>). Settable
    /// only in code, so a test can stop a copy mid-pass and see another take its job over within seconds instead of an hour.
    /// </summary>
    public TimeSpan? JobLeaseTtl { get; set; }

    /// <summary>
    /// Whether the scheduled background jobs run (environment: PACKMOAT_BACKGROUND_JOBS, on or off; unset means on, as before). Parsed
    /// strictly at start by <see cref="Packmoat.Host.BackgroundJobs"/>; a module reads it with <see cref="Packmoat.Host.BackgroundJobs.On"/>.
    /// </summary>
    public string? BackgroundJobs { get; set; }

    /// <summary>
    /// Which database engine (environment: PACKMOAT_DB_ENGINE, sqlite or postgres; unset means sqlite, as before). Parsed strictly at
    /// start by <see cref="Packmoat.Infrastructure.DatabaseEngines"/>. With postgres, the server never
    /// migrates, and refuses to start on any of the start's checks (<see cref="PostgresStartupChecks"/>).
    /// </summary>
    public string? DatabaseEngine { get; set; }

    /// <summary>
    /// PostgreSQL only (environment: PACKMOAT_DB_SINGLE_ROLE, on or off; unset means off): on lets the server's role own the tables and
    /// create in the schema, a warning instead of a refusal. Parsed strictly at start, and only on PostgreSQL: on
    /// SQLite it is not read at all.
    /// </summary>
    public string? DatabaseSingleRole { get; set; }

    /// <summary>
    /// Where the signed-in sites keep their cookie keys (environment: PACKMOAT_COOKIE_KEYS, folder or database; unset means folder, as
    /// before). Parsed strictly at start by <see cref="Packmoat.Host.Web.CookieKeyStorages"/>; database needs PACKMOAT_DB.
    /// </summary>
    public string? CookieKeys { get; set; }

    /// <summary>
    /// SQLite only: how long a starting server waits for another server on the same database to let go of its lock file before it
    /// refuses (<see cref="SqliteInstanceLock"/>). Not read from the environment: the default covers a container being recreated, and
    /// tests shorten it.
    /// </summary>
    public TimeSpan SqliteInstanceWait { get; set; } = SqliteInstanceLock.DefaultWait;
}

/// <summary>
/// The composition root. <paramref name="modules"/> add what is not the core proxy (production: Program.cs). Tests pass
/// <paramref name="overrides"/> to swap real adapters for fakes.
/// </summary>
public static class PackmoatApp
{
    /// <summary>The log category of the start's warning about a test seam that is set.</summary>
    internal const string TestSeamCategory = "Packmoat.TestSeams";

    /// <summary>The log category of the PostgreSQL start checks' warnings.</summary>
    public const string PostgresStartupLogCategory = "Packmoat.Database";

    /// <summary>The names of the test seams set on <paramref name="o"/>, in declaration order: none in production.</summary>
    internal static IReadOnlyList<string> SetTestSeams(PackmoatOptions o)
    {
        var set = new List<string>();
        if (o.AdminRemoteSchemeOverride is not null) set.Add(nameof(PackmoatOptions.AdminRemoteSchemeOverride));
        if (o.AppRemoteSchemeOverride is not null) set.Add(nameof(PackmoatOptions.AppRemoteSchemeOverride));
        if (o.AppAzureDevOpsSchemeOverride is not null) set.Add(nameof(PackmoatOptions.AppAzureDevOpsSchemeOverride));
        if (o.AzureDevOpsHandlerOverride is not null) set.Add(nameof(PackmoatOptions.AzureDevOpsHandlerOverride));
        if (o.SharedAppTokenOverride is not null) set.Add(nameof(PackmoatOptions.SharedAppTokenOverride));
        if (o.JobLeaseTtl is not null) set.Add(nameof(PackmoatOptions.JobLeaseTtl));
        if (o.RetentionInterval is not null) set.Add(nameof(PackmoatOptions.RetentionInterval));
        return set;
    }

    /// <summary>
    /// The job lease the scheduled jobs take, the core's and the modules' alike: a registered <see cref="IJobLease"/> when there
    /// is one (a test's), else the database's, else (no database) one that is always granted. Made here, not registered, so the
    /// composition is unchanged.
    /// </summary>
    /// <summary>The log category of the shared rate counter's failures .</summary>
    public const string RateLimitLogCategory = "Packmoat.RateLimits";

    private static IJobLease JobLeaseFor(IServiceProvider sp, RegisteredStores? stores, TimeSpan? ttl)
    {
        var lease = sp.GetService<IJobLease>() ?? (stores is null ? UnsharedJobLease.Instance : stores.JobLease(sp.GetRequiredService<IClock>()));
        return ttl is { } t ? new FixedTtlJobLease(lease, t) : lease;
    }

    public static WebApplication Build(string[] args, IReadOnlyList<IPackmoatModule> modules, Action<IServiceCollection>? overrides = null, Action<PackmoatOptions>? configureOptions = null)
    {
        // A start that fails after the SQLite server took its lock lets go of it here (in production the process ends anyway; in a test,
        // the next server is then not refused). A start that succeeds hands the lock to the app, which lets go of it when disposed.
        var held = new List<IDisposable>();
        try { return Build(args, modules, overrides, configureOptions, held); }
        catch
        {
            foreach (var h in held) h.Dispose();
            throw;
        }
    }

    private static WebApplication Build(string[] args, IReadOnlyList<IPackmoatModule> modules, Action<IServiceCollection>? overrides,
        Action<PackmoatOptions>? configureOptions, List<IDisposable> held)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        // ASP.NET's request lines (Information) carry the full URL, including the one-time sign-in code and state Microsoft returns on
        // /signin-oidc and /signin-microsoft. Keep them out of the logs; Caddy's access logs (with those values removed) cover requests.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        // The OpenID Connect handler logs Microsoft's error reply, error_description included (event 12, Error), and other protocol
        // values at Information. The sites log their own sign-in failures (type and an allowlisted code only), so drop the handler's
        // lines. In code, not in a settings file: the host loads none, so tests and production log alike.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.Critical);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024;                     // this service only ever receives GETs
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15); // slow-request protection
            k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            k.Limits.MaxConcurrentConnections = 5000;
        });

        var options = new PackmoatOptions
        {
            PublicBaseUrl = Settings.Get("PACKMOAT_PUBLIC_BASE_URL"),
            TokenRequestsPerMinute = int.TryParse(Settings.Get("PACKMOAT_TOKEN_RPM"), out var rpm) && rpm > 0 ? rpm : 6000,
            Database = Settings.Get("PACKMOAT_DB"),
            MasterKeyBase64 = Settings.Get("PACKMOAT_MASTER_KEY"),
            AllowedHosts = Settings.Get("PACKMOAT_HOSTS"),
            TrustedProxies = Settings.Get(Packmoat.Host.TrustedProxies.Variable),
            BackgroundJobs = Settings.Get(Packmoat.Host.BackgroundJobs.Variable),
            DatabaseEngine = Settings.Get(DatabaseEngines.Variable),
            DatabaseSingleRole = Settings.Get(PostgresStartupOptions.SingleRoleVariable),
            CookieKeys = Settings.Get(Web.CookieKeyStorages.Variable),
            AdminHost = Settings.Get("PACKMOAT_ADMIN_HOST"),
            AdminTenantId = Settings.Get("PACKMOAT_ADMIN_TENANT_ID"),
            AdminClientId = Settings.Get("PACKMOAT_ADMIN_CLIENT_ID"),
            AdminClientSecret = Settings.Get("PACKMOAT_ADMIN_CLIENT_SECRET"),
            AdminAllow = Settings.Get("PACKMOAT_ADMIN_ALLOW"),
            AdminKeysDir = Settings.Get("PACKMOAT_ADMIN_KEYS_DIR"),
            AdminUpstreamHosts = Settings.Get("PACKMOAT_ADMIN_UPSTREAM_HOSTS"),
            AppHost = Settings.Get("PACKMOAT_APP_HOST"),
            AppClientId = Settings.Get("PACKMOAT_APP_CLIENT_ID"),
            AppCertPath = Settings.Get("PACKMOAT_APP_CERT"),
            // Strict, like the other switches: an unknown value stops the start.
            AzureSetup = AzureSetupSettings.Parse(Settings.Get("PACKMOAT_AZURE_SETUP")),
            // Strict: a malformed name, address or website stops the start.
            Operator = Web.OperatorLinks.Parse(Settings.Get(Web.OperatorLinks.NameVariable),
                Settings.Get(Web.OperatorLinks.EmailVariable), Settings.Get(Web.OperatorLinks.SiteVariable)),
        };
        configureOptions?.Invoke(options);
        // Strict: a malformed entry, or one that trusts every address, stops the start (here, before anything is written; the options
        // are built below, once the host names are known).
        _ = Packmoat.Host.TrustedProxies.Parse(options.TrustedProxies);
        // Strict, like the other switches: anything but on or off stops the start, before the modules read it.
        var backgroundJobs = Packmoat.Host.BackgroundJobs.On(options);
        // Strict too: an unknown engine stops the start before anything is written. Postgres is selectable.
        var engine = DatabaseEngines.RequireAvailable(options.DatabaseEngine);
        // PostgreSQL only: PACKMOAT_DB_SINGLE_ROLE, strict (anything but on or off stops the start). Not read on SQLite.
        var singleRole = engine == DatabaseEngine.Postgres && PostgresStartupOptions.ParseSingleRole(options.DatabaseSingleRole);
        // Strict too: anything but folder or database stops the start, and so does a storage the engine and database cannot
        // have, before anything is written.
        var cookieKeys = Web.CookieKeyStorages.Parse(options.CookieKeys);
        Web.CookieKeyStorages.Require(cookieKeys, engine, databaseConfigured: options.Database is not null);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IPolicyEngine>(sp => PolicyEngine.CreateDefault(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<PackageGate>();
        // With a decision record store (the database), every verdict is also noted for the per-version decision record: in
        // memory on the request path, written in the background. Without one, the plain gate. Either way the answers are the same.
        builder.Services.AddSingleton<IPackageGate>(sp => sp.GetService<IDecisionRecordLog>() is { } decisions
            ? new RecordingPackageGate(sp.GetRequiredService<PackageGate>(), decisions, sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.DecisionRecord") is var log ? (m => log.LogWarning("{Message}", m)) : null)
            : sp.GetRequiredService<PackageGate>());
        builder.Services.AddSingleton<NuGetProxy>();
        builder.Services.AddSingleton<TokenRateLimiter>(sp => new TokenRateLimiter(sp.GetRequiredService<IClock>(), sp.GetRequiredService<PackmoatOptions>().TokenRequestsPerMinute,
            sp.GetRequiredService<ILogger<TokenRateLimiter>>()));
        builder.Services.AddSingleton<TokenGuessLimiter>(sp => new TokenGuessLimiter(sp.GetRequiredService<IClock>(), log: sp.GetRequiredService<ILogger<TokenGuessLimiter>>()));

        builder.Services.AddSingleton<IPublishDateSource>(sp => new NuGetOrgPublishDateSource(
            new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) })
            { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<IClock>()));

        builder.Services.AddSingleton<IUpstreamClient>(sp => new UpstreamHttpClient(
            new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }) { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IUpstreamCredentialSource>()));

        // The database (SQLite on local disk, or PostgreSQL), with the master key from the environment. Tests and tools register fakes instead.
        var dbPath = options.Database;
        RegisteredStores? stores = null;
        // PostgreSQL only: the database the start's checks run on, once the app is built and before anything is served.
        PostgresDatabase? postgres = null;
        if (dbPath is not null)
        {
            // The stores, from the one list the command line and the hosted modules also build from. The engine is
            // picked here and only here; past this block the root sees the stores only as ports.
            RegisteredStores registered;
            if (engine == DatabaseEngine.Sqlite)
            {
                var db = new SqliteDatabase(dbPath);   // before the key is read, as before: it makes the file and its folder
                // The single-instance guard, before anything reads or writes the database: one server per SQLite file. A second waits for
                // the first to let go (a container being recreated), then refuses. The command line never takes it. The app holds it.
                var instance = SqliteInstanceLock.AcquireAsync(dbPath, options.SqliteInstanceWait,
                    Settings.Get(SqliteInstanceLock.DisableFileLockingVariable)).GetAwaiter().GetResult();
                held.Add(instance);
                builder.Services.AddSingleton(_ => instance);   // made by a factory, so the app's container disposes it
                var protector = AesGcmSecretProtector.FromBase64(options.MasterKeyBase64 ?? throw new InvalidOperationException("PACKMOAT_MASTER_KEY is not set."));
                registered = stores = builder.Services.AddSqliteStores(new SqliteStores(db, protector), cookieKeys);
            }
            else
            {
                // A connection string, checked (never echoed) before the key is read; nothing connects until the start's checks below.
                // The cookie keys are the database's: CookieKeyStorages.Require refused folder mode above.
                var connection = PostgresConnectionString.Parse(dbPath);
                var protector = AesGcmSecretProtector.FromBase64(options.MasterKeyBase64 ?? throw new InvalidOperationException("PACKMOAT_MASTER_KEY is not set."));
                postgres = PostgresDatabase.Create(connection);
                registered = stores = builder.Services.AddPostgresStores(new PostgresStores(postgres, protector));
            }
            // The shared rate counter: the database's, each failure logged as one warning naming the scope and the error's type
            // (never the key), and the attempt refused.
            builder.Services.AddSingleton<IRateCounter>(sp => new ReportingRateCounter(registered.RateCounter(sp.GetRequiredService<IClock>()),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger(RateLimitLogCategory) is var rateLog ? (m => rateLog.LogWarning("{Message}", m)) : _ => { }));
            // The form nonces: the database's, so a form shown by one copy can be posted to another. Each site's FormNonces reports
            // a failure and refuses the form.
            builder.Services.AddSingleton<IFormNonceStore>(sp => registered.FormNonces(sp.GetRequiredService<IClock>()));
            // The feed setup scratch: the database's, so a person's last check and draft are found by whichever copy their next page,
            // or their sign-in's return, reaches. The setup wizard reports a failure and never takes it as a result.
            builder.Services.AddSingleton<IFeedSetupScratch>(sp => registered.FeedSetupScratch(sp.GetRequiredService<IClock>()));
            // Outermost, so a cache hit still notes the day's use (one in-memory check; one tiny insert per token per day; failures are swallowed).
            builder.Services.AddSingleton<ITenantAuthenticator>(sp => new ActivityRecordingAuthenticator(
                new CachedTenantAuthenticator(registered.TokenAuthenticator(sp), sp.GetRequiredService<IClock>()),
                registered.TokenActivityLog(sp), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.TokenActivity") is var log ? (m => log.LogWarning("{Message}", m)) : null));
            // Customer self-service tokens: the customer site's /tokens and /team pages (Customer/AppTokens.cs) and its sign-in record. Issuing,
            // and the token pages' create forms, ask the serving gate (the core's ServeAllGate, or a module's). The customer site registers the same with TryAdd for a test host.
            builder.Services.AddSingleton(sp => new CustomerTokenCommands(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ITokenService>(),
                sp.GetRequiredService<IMemberStore>(), sp.GetRequiredService<ISignInDirectoryStore>(), sp.GetRequiredService<IAdminActionLog>(),
                sp.GetRequiredService<IClock>(), sp.GetRequiredService<ISharedAppCredentialStore>(), sp.GetRequiredService<IServingGate>(), sp.GetRequiredService<IRateCounter>()));
            builder.Services.AddSingleton(sp => new CustomerTokenQueries(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ITokenService>(),
                sp.GetRequiredService<ISignInDirectoryStore>(), sp.GetRequiredService<ISharedAppCredentialStore>(), sp.GetRequiredService<IServingGate>()));
            builder.Services.AddSingleton<IUpstreamCredentialSource>(sp => new EntraCertificateCredentialSource(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ISharedAppCredentialStore>()));
        }

        overrides?.Invoke(builder.Services);
        // The shared rate counter: the database's, registered above. With no database (a test host with fakes) and none from
        // the test, one in this process.
        if (stores is null) builder.Services.TryAddSingleton<IRateCounter>(sp => new InProcessRateCounter(sp.GetRequiredService<IClock>()));
        // The form nonces likewise: the database's, registered above; with no database and none from the test, one in this process.
        if (stores is null) builder.Services.TryAddSingleton<IFormNonceStore>(sp => new InProcessFormNonceStore(sp.GetRequiredService<IClock>()));
        // The feed setup scratch likewise.
        if (stores is null) builder.Services.TryAddSingleton<IFeedSetupScratch>(sp => new InProcessFeedSetupScratch(sp.GetRequiredService<IClock>()));
        // After the overrides, so a test's fakes win (a module registers its ports with TryAdd).
        var context = new PackmoatModuleContext(builder, options, stores?.Database, engine) { JobLease = sp => JobLeaseFor(sp, stores, options.JobLeaseTtl) };
        foreach (var module in modules) module.ConfigureServices(context);
        // The core's alert sink: a warning in the log. A module's own sink replaces it.
        builder.Services.TryAddSingleton<IOpsAlerts>(sp => new LogOpsAlerts(sp.GetRequiredService<ILoggerFactory>().CreateLogger(LogOpsAlerts.Category)));
        // The tenant lifecycle: at most one module's; with none, a tenant gets only its own row and nobody is purged.
        var lifecycles = builder.Services.Count(d => d.ServiceType == typeof(TenantLifecycleFactory));
        if (lifecycles > 1) throw new InvalidOperationException($"{lifecycles} tenant lifecycles are registered. Refusing to start: at most one may take part in making and purging tenants.");
        builder.Services.TryAddSingleton(TenantLifecycle.None);
        // The serving gate: at most one, from a module or a test; after them, so the core's serve-everyone gate is only the default.
        var gates = builder.Services.Count(d => d.ServiceType == typeof(IServingGate));
        if (gates > 1) throw new InvalidOperationException($"{gates} serving gates are registered. Refusing to start: exactly one may decide who is served.");
        builder.Services.TryAddSingleton<IServingGate>(ServeAllGate.Instance);
        // The customer-site module: at most one; with none, the core's adds nothing. The core reads only its own settings: one that
        // only a module acts on is not read at all when no module would act on it.
        var customerModules = builder.Services.Count(d => d.ServiceType == typeof(ICustomerSiteModule));
        if (customerModules > 1) throw new InvalidOperationException($"{customerModules} customer-site modules are registered. Refusing to start: at most one may add to the customer site.");
        builder.Services.TryAddSingleton<ICustomerSiteModule>(NoCustomerSiteModule.Instance);
        // The admin-site module: at most one; with none, the core's adds nothing. After the modules, so it is only the default.
        var adminModules = builder.Services.Count(d => d.ServiceType == typeof(IAdminSiteModule));
        if (adminModules > 1) throw new InvalidOperationException($"{adminModules} admin-site modules are registered. Refusing to start: at most one may add to the admin site.");
        builder.Services.TryAddSingleton<IAdminSiteModule>(NoAdminSiteModule.Instance);

        // Downloads only, off the request path: the proxy queues, one background writer stores batches in the audit store.
        // Fail-open: a full queue or a failed write loses audit rows (counted, logged without token, package or version), never a download.
        if (builder.Services.Any(d => d.ServiceType == typeof(IAuditBatchLog)))
            builder.Services.TryAddSingleton<IAuditLog>(sp => new QueuedAuditLog(sp.GetRequiredService<IAuditBatchLog>(), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Audit") is var log ? (m => log.LogWarning("{Message}", m)) : null));

        // The daily retention pass over feed checks, the purge nudge, the decision record prune, the
        // admin_actions prune and the audit_events and token_activity prunes, wherever there is a feed check store. (A module's
        // own sweeps are the module's.) Only with the background jobs on; off, `packmoat-admin jobs run retention` runs the same steps.
        // Each pass takes the database's job lease, resolved here and not registered, so the composition is unchanged.
        if (backgroundJobs && builder.Services.Any(d => d.ServiceType == typeof(IFeedCheckStore)))
            builder.Services.AddHostedService(sp => new RetentionService(sp.GetRequiredService<IFeedCheckStore>(), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Retention"), options.RetentionInterval,
                sp.GetService<ITenantPurgeStore>(), sp.GetRequiredService<IOpsAlerts>(),
                decisions: sp.GetService<IDecisionRecordLog>(), actions: sp.GetService<IAdminActionLog>(),
                audit: sp.GetService<IAuditRetention>(), tokenActivity: sp.GetService<ITokenActivityRetention>(),
                lease: JobLeaseFor(sp, stores, options.JobLeaseTtl), rates: sp.GetService<IRateCounter>(), nonces: sp.GetService<IFormNonceStore>(),
                scratch: sp.GetService<IFeedSetupScratch>()));

        var admin = AdminSite.Configure(builder, options, engine);
        var customer = AppSite.Configure(builder, options, engine, admin?.Host, sp => JobLeaseFor(sp, stores, options.JobLeaseTtl));
        // Each module's site, on a hostname none of the above (nor an earlier module) answers.
        var proxyHosts = (options.AllowedHosts ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var siteHosts = new List<string>();
        if (admin is not null) siteHosts.Add(admin.Host);
        if (customer is not null) siteHosts.Add(customer.Host);
        var moduleSites = new List<ModuleSite>();
        foreach (var module in modules)
        {
            if (module.ConfigureSite(context, proxyHosts.Concat(siteHosts).ToArray()) is not { } site) continue;
            moduleSites.Add(site);
            siteHosts.Add(site.Host);
        }

        if (ProductionMode.On(options))
        {
            // Production: only our own hostnames are answered. (Caddy forwards the original Host; loopback names serve the health check.)
            if (proxyHosts.Length == 0) throw new InvalidOperationException("PACKMOAT_HOSTS is not set. Refusing to start without a list of the hostnames this service answers to.");
            builder.Configuration["AllowedHosts"] = string.Join(';', proxyHosts.Concat(siteHosts).Concat(new[] { "127.0.0.1", "localhost" }));
        }
        // With PACKMOAT_TRUSTED_PROXIES set, a forwarded host must be one of the same names the host filter allows.
        var forwarded = Packmoat.Host.TrustedProxies.Options(options.TrustedProxies, proxyHosts.Concat(siteHosts).Concat(new[] { "127.0.0.1", "localhost" }));

        if (!builder.Services.Any(d => d.ServiceType == typeof(ITenantAuthenticator)))
            throw new InvalidOperationException("PACKMOAT_DB is not set, so there is no tenant store. Refusing to start without one.");

        var app = builder.Build();
        // The SQLite lock, made now, so the app's container holds it and lets go of it when the app is disposed.
        _ = app.Services.GetService<SqliteInstanceLock>();
        // A test seam is never set in production: if one is, say so at start, by name.
        if (SetTestSeams(options) is { Count: > 0 } seams)
        {
            var seamLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(TestSeamCategory);
            foreach (var seam in seams) seamLog.LogWarning("Test seam set: {Seam}. A test seam replaces a real part of the server and is never set in production.", seam);
        }
        // A module's admin actions and notices may not shadow the core's.
        AdminWrites.Validate(app.Services.GetRequiredService<IAdminSiteModule>());
        // A customer-site module's pages are paths on this site, and its prospect pages have one to land on.
        AppSite.Validate(app.Services.GetRequiredService<ICustomerSiteModule>());
        // Which gate decides who is served: the deploy check reads this line.
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Serving")
            .LogInformation("Serving gate: {Type}", app.Services.GetRequiredService<IServingGate>().GetType().Name);
        // The core's migrations, then each module's track. A refusal (an undeclared or newer track, a baseline that cannot adopt)
        // stops the start before anything is written. Then the master-key check. Both ports are made here, not
        // registered, so the composition is unchanged; with no database there is neither.
        ISchemaMigrator? migrator = stores?.Migrator;
        IMasterKeyCheck? keyCheck = stores?.MasterKeyCheck;
        // PostgreSQL: the start's checks, awaited here, before the schema port, the key check and anything served. Any
        // refusal stops the start (PostgresStartupException, one line per refusal, never the connection string); each warning is logged.
        if (postgres is not null)
        {
            var databaseLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(PostgresStartupLogCategory);
            PostgresStartupChecks.RequireAsync(postgres, modules.Select(m => m.Migrations).OfType<MigrationTrack>().ToList(),
                app.Services.GetRequiredService<IClock>(), new PostgresStartupOptions(singleRole),
                w => databaseLog.LogWarning("{Warning}", w)).GetAwaiter().GetResult();
        }
        migrator?.MigrateAsync(modules.Select(m => m.Migrations).OfType<MigrationTrack>().ToList()).GetAwaiter().GetResult();
        keyCheck?.EnsureAsync(default).GetAwaiter().GetResult(); // wrong master key: refuse to start
        // Database mode only: the one-time import of the key folder, then one read of every key, so a key the master key cannot
        // read stops the start. The folder is never created or changed.
        if (cookieKeys == Web.CookieKeyStorage.Database)
            Web.CookieKeyImport.RunAsync(app.Services.GetRequiredService<ICookieKeyStore>(), Web.SignedInSite.KeysDir(options, engine),
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Web.CookieKeyImport.LogCategory)).GetAwaiter().GetResult();
        // The 30-day retention of feed checks' personal data: at every start, and again whenever the wizard records a check.
        // A failure is logged and the server starts anyway (RetentionService.BlankAtStartup).
        RetentionService.BlankAtStartup(app.Services.GetService<IFeedCheckStore>(), app.Services.GetRequiredService<IClock>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Retention"));

        // Caddy runs on the same machine and sets these; trust only loopback (and any proxy PACKMOAT_TRUSTED_PROXIES names) so a client
        // cannot spoof its address.
        app.UseForwardedHeaders(forwarded);
        // The admin site, if configured, is a separate branch chosen by the Host header. Everything below this line is the proxy, unchanged.
        if (admin is not null) AdminSite.MapBranch(app, admin);
        // The customer site, likewise its own branch on its own hostname, with its own cookie and schemes.
        if (customer is not null) AppSite.MapBranch(app, customer);
        // Each module's site, its own branch on its own hostname. A module may take POSTs (the webhook receiver does), so these come before
        // the GET-only rule below.
        foreach (var site in moduleSites) site.MapBranch(app);
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET";
                return;
            }
            // A GET never needs a body. Refuse one rather than quietly drain it.
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next();
        });
        app.UseMiddleware<TokenAuthMiddleware>();
        // Built here, not registered: it reads the database the start migrated, against the same tracks, through the same two
        // ports, and only reads: TrackVersionsAsync and MatchesAsync, never MigrateAsync or EnsureAsync.
        var readyTracks = modules.Select(m => m.Migrations).OfType<MigrationTrack>().ToList();
        var readiness = new Readiness(migrator is null ? null : ct => migrator.TrackVersionsAsync(readyTracks, ct),
            keyCheck is null ? null : keyCheck.MatchesAsync, app.Services.GetRequiredService<IClock>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Readiness.LogCategory), newerSchemaReady: engine == DatabaseEngine.Postgres);
        NuGetProxy.Map(app, readiness);
        return app;
    }
}
