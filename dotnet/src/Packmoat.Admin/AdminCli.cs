using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Admin;

/// <summary>Where the database and master key come from. Tests supply their own.</summary>
/// <param name="OperatorName">The operator's name for the record; null means the OS user. Tests set it to simulate a missing user name.</param>
public sealed record CliEnvironment(string DbPath, Func<AesGcmSecretProtector> Protector, IClock Clock, string? OperatorName = null)
{
    /// <summary>Tests only: what `onboard check` calls Azure DevOps and the proxy through. Null means the real network.</summary>
    public HttpMessageHandler? OnboardingHandler { get; init; }
    /// <summary>Tests only: Packmoat's own upstream credential for `onboard check` (the real one asks Entra). Arguments: tenants, shared store, clock.</summary>
    public Func<ITenantStore, ISharedAppCredentialStore, IClock, IUpstreamCredentialSource>? OnboardingCredentials { get; init; }
    /// <summary>Tests only: adjusts the onboarding options after they are read from the command line (for example shorter retry waits).</summary>
    public Action<OnboardingOptions>? TuneOnboarding { get; init; }

    /// <summary>The database engine (PACKMOAT_DB_ENGINE, #232 P1). Only <see cref="DatabaseEngine.Sqlite"/> runs: any other stops every
    /// command that needs the environment, and backup, verify and restore point to the engine's own tools.</summary>
    public DatabaseEngine Engine { get; init; } = DatabaseEngine.Sqlite;

    public static CliEnvironment FromProcess() => FromSettings(Settings.Get);

    /// <summary>The environment from <paramref name="setting"/> (production: <see cref="Settings.Get"/>). A bad PACKMOAT_DB_ENGINE throws.</summary>
    public static CliEnvironment FromSettings(Func<string, string?> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return new(
            setting("PACKMOAT_DB") ?? "data/packmoat.db",
            () => AesGcmSecretProtector.FromEnvironment(),
            new SystemClock())
        {
            Engine = DatabaseEngines.Parse(setting(DatabaseEngines.Variable)),
        };
    }
}

/// <summary>
/// The hand-run operator tool for the first customers. Prints ids and states only. A new token is printed once, on issue.
/// Certificate files are read and stored encrypted; they are never echoed.
/// </summary>
public static class AdminCli
{
    // The core's help before the modules' commands.
    private const string UsageHead = """
        packmoat-admin <command>
          keygen                                              print a new 32-byte master key (store it outside the repository)
          tenant create <id> <name> --upstream-base URL --path-prefix org/ --feed URL [--cooldown 14]
                [--azure-tenant GUID]                         and link this customer Microsoft directory to it, in the same step
          tenant create <id> <name> --azure-tenant GUID [--cooldown 14]
                                                              no upstream: the customer connects their own feed (Azure setup)
          tenant list | show <id> | suspend <id> | activate <id> | cooldown <id> <days>
          tenant directory bind <id> --azure-tenant GUID       let people from this customer Microsoft directory sign in to the customer site as tenant <id>
          tenant directory unbind <id> --azure-tenant GUID     remove that link (their next page load ends their session)
          tenant directory show <id>                          the tenant's linked directories (and, for a shared-app tenant, a suggestion)
          tenant purge-due                                    tenants due for purge, and any to check by hand. Deletes nothing.
          tenant purge <id> --confirm <id>                    DELETE the tenant's data for good (only when due; recorded)
          prefix add|remove <id> <prefix>
          override set <id> <package> <days> | remove <id> <package>
          waiver add <id> <package> <version|*> --days N --reason "text" --by name
          waiver revoke <waiverId>
          token issue <id> [--label text] [--kind developer|automation] | list <id> | revoke <tokenId>
          token active --tenant <id> [--days 30]               developer tokens used in the last N days (automation tokens are not counted)
          identity new-cert <id> --out-dir DIR [--days 365]    make the customer's certificate here: writes <id>.cer (public, send to the customer)
                                                              and <id>.pem (private, mode 600: pass it to identity set, then delete it)
          identity set <id> --azure-tenant GUID --client-id GUID --pem-file PATH     own-app mode: the customer's own app registration
          identity bind <id> --azure-tenant GUID [--allow-shared-directory]
                                                              shared-app mode: the customer added Packmoat's app to their directory GUID.
                                                              The tenant serves nothing until 'onboard check' passes (exit 0); that run records it.
          identity shared new-cert --out-dir DIR [--days 365] make a certificate for Packmoat's shared app: packmoat-shared.cer (public, upload
                                                              to the app registration) and packmoat-shared.pem (private, mode 600)
          identity shared set --client-id GUID --pem-file PATH [--next]
                                                              store the shared app's credential as active (or as next, for a rotation)
          identity shared promote                             next becomes active (then remove the old certificate from the app)
          identity shared show                                client id, thumbprints and expiry (never key material)
          identity expiry [--warn-days 45]                    certificate expiry for every customer and for the shared app
          onboard check <id> --org O --feed F [--project P] [--principal OBJECT-ID]
                [--admin-token-env VAR [--admin-bearer]] [--developer-token-env VAR [--developer-bearer]]
                [--probe-url URL ...] [--proxy-url URL --proxy-token-env VAR] [--confirm-admin "NAME or DESCRIPTOR" ...]
                                                              verify the customer's setup; secrets are read from the named environment variables.
                                                              --confirm-admin: a feed administrator the customer has named to you (once each)
        """;

    // The core's help after the modules' commands, then the Environment lines (the modules' after the core's).
    private const string UsageTail = """
          audit tail [--tenant id] [--limit 50]
          decision show --tenant <id> --package <id> [--version <v>] [--prefix] [--limit 50]
                                                              the tenant's decision record for a package (--prefix: every id starting with it):
                                                              what was decided per version and why, newest last seen first (at most 500 rows).
                                                              "decisions=" counts decisions (listings and downloads alike), not downloads
          backup <file> [--force]                             consistent online copy, verified (safe while the server runs)
          verify <file>                                       check a backup without restoring it
          restore <file> [--force]                            replace the database with a verified backup (stop the server first)
          db check-migrations <file>                          before a deploy: run this build's migrations on a temporary copy of a backup
                                                              (never the live database); report each track, row counts and the schema
          jobs run retention                                  the server's daily retention pass, for when PACKMOAT_BACKGROUND_JOBS is off
                                                              (prints each step's count; exit 1 if a step failed; deletes no tenant)
        Environment: PACKMOAT_DB (default data/packmoat.db), PACKMOAT_DB_ENGINE (sqlite, the default; postgres is not available yet), PACKMOAT_MASTER_KEY (base64, 32 bytes);
        """;

    /// <summary>The core's own commands: a module's group may not be one of them.</summary>
    private static readonly HashSet<string> CoreGroups = new(StringComparer.Ordinal)
    {
        "-h", "--help", "keygen", "verify", "restore", "backup", "db", "tenant", "prefix", "override", "waiver", "token", "identity", "onboard", "audit", "decision",
        "jobs",
    };

    // `jobs` (#233): what `packmoat-admin jobs` with anything but `run retention` prints (the help lists it too).
    internal const string JobsUsage = "Usage: packmoat-admin jobs run retention   (the server's daily retention pass, for when PACKMOAT_BACKGROUND_JOBS is off)";

    /// <summary>The help: the core's commands, each module's, the core's tail, then each module's Environment lines.</summary>
    internal static string Usage(IReadOnlyList<IAdminCliModule> modules) => string.Join("\n",
        new[] { WithPurgeHelp(UsageHead, modules) }.Concat(modules.Select(m => m.Usage)).Append(UsageTail).Concat(modules.Select(m => m.EnvironmentUsage).Where(e => e.Length > 0)));

    // The tenant purge line's words, where a module's last check before a purge is named (#197 C3).
    private const string PurgeHelp = "(only when due; recorded)";

    /// <summary>The modules' purge checks, each in the order given, joined by "; " (empty: none).</summary>
    private static string PurgeChecks(IReadOnlyList<IAdminCliModule> modules, Func<PurgeCheckNote, string> words) =>
        string.Join("; ", modules.Select(m => m.PurgeCheck).OfType<PurgeCheckNote>().Select(words));

    // The tenant purge-due line's words, which a module's DueLines replace (#226), and the help's column for a line's later lines.
    private const string PurgeDueHelp = "tenants due for purge, and any to check by hand. Deletes nothing.";
    private const int HelpColumn = 54;

    /// <summary>The one module's purge-due lines, or null when none gives them. Two that do stop the tool (exit 2). An empty list counts as not given.</summary>
    private static IReadOnlyList<string>? DueLines(IReadOnlyList<IAdminCliModule> modules) =>
        modules.Select(m => m.PurgeCheck?.DueLines).OfType<IReadOnlyList<string>>().Where(lines => lines.Count > 0).SingleOrDefault();

    private static string WithPurgeHelp(string head, IReadOnlyList<IAdminCliModule> modules)
    {
        if (DueLines(modules) is { Count: > 0 } due)
            head = head.Replace(PurgeDueHelp, string.Join("\n" + new string(' ', HelpColumn), due), StringComparison.Ordinal);
        return PurgeChecks(modules, n => n.Help) is { Length: > 0 } checks ? head.Replace(PurgeHelp, "(only when due; " + checks + "; recorded)", StringComparison.Ordinal) : head;
    }

    /// <param name="modules">The command groups beyond the core's (#193; production: Program.cs). Empty: the core's commands only.</param>
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CliEnvironment env, IReadOnlyList<IAdminCliModule> modules) =>
        RunAsync(args, output, error, () => env, modules);

    /// <param name="environment">Builds the environment inside the error handling (#238), so a bad setting (a database file that cannot be read, say) is the usual
    /// "error:" line and exit 1, not a crash. Production: <see cref="CliEnvironment.FromProcess"/>. The help and keygen never build it.</param>
    /// <param name="modules">The command groups beyond the core's (#193; production: Program.cs). Empty: the core's commands only.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<CliEnvironment> environment, IReadOnlyList<IAdminCliModule> modules)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Select(m => m.Group).FirstOrDefault(CoreGroups.Contains) is { } core)
        {
            await error.WriteLineAsync($"The module command group \"{core}\" is one of the core's commands. Refusing to run.");
            return 2;
        }
        if (modules.GroupBy(m => m.Group, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            await error.WriteLineAsync($"Two modules add the command group \"{twice.Key}\". Refusing to run.");
            return 2;
        }
        if (modules.Count(m => m.PurgeCheck?.DueLines is { Count: > 0 }) > 1)
        {
            await error.WriteLineAsync("Two modules give the tenant purge-due help lines. Refusing to run.");
            return 2;
        }
        var usage = Usage(modules);
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help") { await output.WriteLineAsync(usage); return args.Length == 0 ? 2 : 0; }
            if (args[0] == "keygen") { await output.WriteLineAsync(AesGcmSecretProtector.GenerateKeyBase64()); return 0; }
            var env = environment();
            // Only SQLite in this build (#232 P1). Backup, verify and restore are SQLite's own: on another engine, its own tools do that job.
            if (env.Engine != DatabaseEngine.Sqlite)
                throw new SettingsException(args[0] is "backup" or "verify" or "restore"
                    ? $"{args[0]} works on SQLite only, and {DatabaseEngines.Variable} is {DatabaseEngines.Name(env.Engine)}. Use pg_dump to back up a Postgres database, and pg_restore to restore it."
                    : DatabaseEngines.NotAvailable(env.Engine));

            if (args[0] is "verify" or "restore")
            {
                var file = args.Length > 1 ? args[1] : throw new ArgumentException("A backup file is required.");
                var key = TryProtector(env);   // null when no master key is set at all: then the key is simply not checked
                if (args[0] == "verify")
                {
                    var v = await SqliteBackup.VerifyAsync(file, default, key);
                    await output.WriteLineAsync(Describe(v));
                    return v.Ok ? 0 : 1;
                }
                var restored = await SqliteBackup.RestoreAsync(file, env.DbPath, args.Contains("--force"), env.Clock.UtcNow, default, key);
                await output.WriteLineAsync("restored. " + Describe(restored));
                return 0;
            }

            // The core's migrations, then each module's track (#196). The dry run works on a copy and never opens the live database.
            var tracks = modules.Select(m => m.Migrations).OfType<MigrationTrack>().ToList();
            if (args[0] == "db") return await CheckMigrationsAsync(args, env, tracks, output, error);
            // jobs (#233): exactly `jobs run retention`. Anything more (--dry-run, a word) is refused here, before the database is opened
            // or the arguments are parsed, so it can never run the real pass.
            if (args[0] == "jobs" && args is not ["jobs", "run", "retention"])
            {
                await error.WriteLineAsync("Unknown jobs command. " + JobsUsage);
                return 2;
            }

            var db = new SqliteDatabase(env.DbPath);
            await db.MigrateAsync(tracks);
            if (args[0] == "backup")
            {
                var done = await SqliteBackup.BackupAsync(db, args.Length > 1 ? args[1] : throw new ArgumentException("A destination file is required."), args.Contains("--force"), default, TryProtector(env));
                await output.WriteLineAsync("backup written and verified. " + Describe(done));
                return 0;
            }
            var protector = env.Protector();
            // The tenant lifecycle (#194): at most one module's (creation participants, purge contributors and eligibility), else the
            // core's none. Notes are what its payment provider logs; a refused purge prints them.
            var notes = new List<string>();
            var lifecycles = modules.Select(m => m.Lifecycle(db, env, notes.Add)).OfType<TenantLifecycle>().ToList();
            if (lifecycles.Count > 1)
            {
                await error.WriteLineAsync($"{lifecycles.Count} modules give a tenant lifecycle. Refusing to run: at most one may.");
                return 2;
            }
            var lifecycle = lifecycles.SingleOrDefault() ?? TenantLifecycle.None;
            var tenants = new SqliteTenantStore(db, env.Clock, protector, lifecycle.Participants);
            var shared = new SqliteSharedAppCredentialStore(db, env.Clock, protector);
            await tenants.EnsureKeyCheckAsync(default);
            // The serving gate: at most one module's, else every tenant serves.
            var gates = modules.Select(m => m.Gate(db, env, error.WriteLine)).OfType<IServingGate>().ToList();
            if (gates.Count > 1)
            {
                await error.WriteLineAsync($"{gates.Count} modules give a serving gate. Refusing to run: at most one may decide who is served.");
                return 2;
            }
            var tokens = new SqliteTokenService(db, tenants, env.Clock, gates.SingleOrDefault() ?? ServeAllGate.Instance);
            var audit = new SqliteAuditLog(db);
            var a = new AdminCliArgs(args);
            // The CLI is the operator's tool: all tenants. --by or the OS user names the operator. A container uid with no passwd entry
            // has no user name; that must not break every command, so the name falls back to "operator" and only `waiver add` insists on a real one.
            var operatorName = a.Opt("by") ?? env.OperatorName ?? Environment.UserName;
            var actor = ActorScope.OperatorAs(string.IsNullOrWhiteSpace(operatorName) ? "operator" : operatorName);
            var commands = new AdminCommands(tenants, tokens, env.Clock, shared, new CertificateInspector());
            var activity = new SqliteTokenActivity(db, env.Clock);
            var queries = new AdminQueries(tenants, audit, tokens, activity, shared, new SqliteDecisionRecordQuery(db));
            var ct = CancellationToken.None;

            // A module's group (#193): the collision check above keeps the core's commands the core's.
            if (modules.FirstOrDefault(m => m.Group == a.Cmd.Item1) is { } module)
                return await module.RunAsync(new AdminCliContext(a, actor, db, env, output, error, ct));

            switch (a.Cmd)
            {
                case ("tenant", "create"):
                {
                    // --azure-tenant with NO upstream option: the customer connects their own feed (#207). Any upstream option: all are
                    // required as before (a partial set is refused), and the operator sets up the feed.
                    var upstreamGiven = a.Opt("upstream-base") is not null || a.Opt("path-prefix") is not null || a.Opt("feed") is not null;
                    var tenant = a.Opt("azure-tenant") is not null && !upstreamGiven
                        ? new NewTenant(a.Pos(0), a.Pos(1), "", "", "", int.Parse(a.Opt("cooldown") ?? "14"), TenantOrigin.Operator) { FeedSetup = FeedSetup.Customer }
                        : new NewTenant(a.Pos(0), a.Pos(1), Ensure(a.Opt("upstream-base")), a.Opt("path-prefix") ?? "", a.Opt("feed") ?? throw new ArgumentException("--feed is required."),
                            int.Parse(a.Opt("cooldown") ?? "14"), TenantOrigin.Operator);
                    if (a.Opt("azure-tenant") is { } directory)
                        return await CreateAndLinkAsync(tenant, directory, actor, db, lifecycle, env.Clock, output, ct);
                    await commands.CreateTenantAsync(actor, tenant, ct);
                    await output.WriteLineAsync($"created tenant {a.Pos(0)}");
                    break;
                }
                case ("tenant", "list"):
                    foreach (var t in await queries.ListTenantsAsync(actor, ct))
                        await output.WriteLineAsync($"{t.Id}\t{t.Name}\tcooldown={t.Policy.CooldownDays}d\trevision={t.Policy.Revision}\tidentity={Mode(await queries.GetUpstreamBindingAsync(actor, t.Id, ct))}");
                    break;
                case ("tenant", "show"):
                {
                    var detail = await queries.ShowTenantAsync(actor, a.Pos(0), ct) ?? throw new KeyNotFoundException($"No tenant '{a.Pos(0)}'.");
                    await Show(output, detail);
                    if (detail.Binding is { Kind: IdentityKind.Shared } sb)
                    {
                        var v = await queries.SharedVerificationAsync(actor, detail.Tenant.Id, ct);
                        var active = (await queries.SharedAppCredentialsAsync(actor, ct)).FirstOrDefault(x => x.Slot == SharedAppSlot.Active).Credential;
                        await output.WriteLineAsync(v is not null && active is not null && v.Matches(sb, detail.Tenant.UpstreamFeedIndexUrl, active.ClientId)
                            ? $"  onboard check: PASSED {v.VerifiedAt:u} by {v.VerifiedBy} (serving)"
                            : "  onboard check: NOT PASSED for the current binding (NOT SERVING: run onboard check)");
                    }
                }
                    break;
                case ("tenant", "directory"):
                    return await DirectoryAsync(a, actor, new DirectoryBindingCommands(tenants, new SqliteSignInDirectoryStore(db), env.Clock, shared), new SqliteAdminActionLog(db), env.Clock, output, ct);
                case ("tenant", "purge-due"):
                case ("tenant", "purge"):
                    return await PurgeAsync(a, actor, db, lifecycle, notes, env, PurgeChecks(modules, n => n.Due), output, ct);
                case ("tenant", "suspend"): await commands.SuspendTenantAsync(actor, a.Pos(0), ct); await output.WriteLineAsync("suspended"); break;
                case ("tenant", "activate"): await commands.ActivateTenantAsync(actor, a.Pos(0), ct); await output.WriteLineAsync("activated"); break;
                case ("tenant", "cooldown"): await commands.SetCooldownAsync(actor, a.Pos(0), int.Parse(a.Pos(1)), ct); await output.WriteLineAsync("ok"); break;
                case ("prefix", "add"): await commands.AddPrefixAsync(actor, a.Pos(0), a.Pos(1), ct); await output.WriteLineAsync("ok"); break;
                case ("prefix", "remove"): await commands.RemovePrefixAsync(actor, a.Pos(0), a.Pos(1), ct); await output.WriteLineAsync("ok"); break;
                case ("override", "set"): await commands.AddOverrideAsync(actor, a.Pos(0), a.Pos(1), int.Parse(a.Pos(2)), ct); await output.WriteLineAsync("ok"); break;
                case ("override", "remove"): await commands.RemoveOverrideAsync(actor, a.Pos(0), a.Pos(1), ct); await output.WriteLineAsync("ok"); break;
                case ("waiver", "add"):
                {
                    if (string.IsNullOrWhiteSpace(operatorName)) throw new ArgumentException("No user name is available for the waiver's created-by; pass --by name.");
                    var days = int.TryParse(a.Opt("days") ?? throw new ArgumentException("--days is required."), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var d)
                        ? d : throw new ArgumentException($"--days must be a whole number from {AdminCommands.MinWaiverDays} to {AdminCommands.MaxWaiverDays}.");   // the range itself is checked once, in AdminCommands
                    var w = await commands.AddWaiverAsync(actor, a.Pos(0), a.Pos(1), a.Pos(2), days,
                        a.Opt("reason") ?? throw new ArgumentException("--reason is required."), ct);
                    await output.WriteLineAsync($"waiver {w.Id} allows {w.PackageId} {w.Version} until {w.ExpiresAt:u}");
                    break;
                }
                case ("waiver", "revoke"):
                    await output.WriteLineAsync(await commands.RevokeWaiverAsync(actor, a.Pos(0), ct) ? "revoked" : "no active waiver with that id");
                    break;
                case ("token", "issue"):
                {
                    var t = await commands.IssueTokenAsync(actor, a.Pos(0), a.Opt("label") ?? "unlabelled", ct, ParseKind(a.Opt("kind")));
                    await output.WriteLineAsync($"token id: {t.TokenId}");
                    await output.WriteLineAsync($"token: {t.Plaintext}");
                    await output.WriteLineAsync("This is the only time the token is shown. Store it in the customer's secret store now.");
                    break;
                }
                case ("token", "list"):
                    foreach (var t in await queries.ListTokensAsync(actor, a.Pos(0), ct))
                        await output.WriteLineAsync(TokenLine(t, env.Clock.UtcNow));
                    break;
                case ("token", "active"):
                {
                    var days = int.TryParse(a.Opt("days") ?? "30", System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
                        ? n : throw new ArgumentException($"--days must be a whole number from {AdminQueries.MinActiveDays} to {AdminQueries.MaxActiveDays}.");   // the range is checked in AdminQueries
                    var active = await queries.ActiveDevelopersAsync(actor, a.Opt("tenant") ?? throw new ArgumentException("--tenant is required."), days, ct);
                    await output.WriteLineAsync($"{active.Count} active developer token(s) in the last {active.Days} days (automation tokens not counted)");
                    foreach (var t in active.Tokens) await output.WriteLineAsync($"{t.TokenId}\t{t.Label}\tlast active={t.LastActiveDay:yyyy-MM-dd}");
                    break;
                }
                case ("token", "revoke"):
                    await output.WriteLineAsync(await commands.RevokeTokenAsync(actor, a.Pos(0), ct) ? "revoked (running servers notice within 5 seconds)" : "no active token with that id");
                    break;
                case ("identity", "new-cert"):
                {
                    var id = a.Pos(0);
                    AdminCommands.RequireOperator(actor);
                    if (await queries.GetTenantAsync(actor, id, ct) is null) throw new KeyNotFoundException($"No tenant '{id}'.");
                    var days = IntOpt(a, "days", 365, CertificateTools.MinDays, CertificateTools.MaxDays);
                    var dir = Path.GetFullPath(a.Opt("out-dir") ?? throw new ArgumentException("--out-dir is required."));
                    var cer = Path.Combine(dir, id + ".cer");
                    var pem = Path.Combine(dir, id + ".pem");
                    foreach (var existing in new[] { cer, pem })
                        if (File.Exists(existing)) throw new IOException($"{existing} already exists; refusing to overwrite a certificate. Move it away or choose another --out-dir.");
                    CertificateTools.EnsurePrivateDirectory(dir);
                    var gen = CertificateTools.Generate($"packmoat-{id}", days, env.Clock.UtcNow);
                    await CertificateTools.WriteNewAsync(pem, System.Text.Encoding.ASCII.GetBytes(gen.CombinedPem), privateFile: true, ct); // the secret first, born with mode 600
                    await CertificateTools.WriteNewAsync(cer, gen.PublicCer, privateFile: false, ct);
                    await output.WriteLineAsync($"public certificate (send to the customer to upload): {cer}");
                    await output.WriteLineAsync($"private bundle (mode 600; give to 'identity set' then delete it): {pem}");
                    await output.WriteLineAsync($"thumbprint {gen.Thumbprint}, expires {gen.NotAfter:u}");
                    break;
                }
                case ("identity", "expiry"):
                {
                    var warnDays = IntOpt(a, "warn-days", 45, 0, 3650);
                    string Left(DateTimeOffset expires)
                    {
                        var left = (int)Math.Floor((expires - env.Clock.UtcNow).TotalDays);
                        return $"expires {expires:u} ({left} days){(left < 0 ? "  EXPIRED" : left < warnDays ? "  RENEW SOON" : "")}";
                    }
                    foreach (var (slot, credential) in await queries.SharedAppCredentialsAsync(actor, ct))
                        await output.WriteLineAsync(TryExpiry(credential.CertificatePem, out var sharedExpiry)
                            ? $"[shared app {SqliteSharedAppCredentialStore.SlotName(slot)}]\t{Left(sharedExpiry)}"
                            : $"[shared app {SqliteSharedAppCredentialStore.SlotName(slot)}]\tcertificate unreadable: re-run identity shared set");
                    foreach (var (t, identity, binding) in await queries.ListIdentitiesAsync(actor, ct))
                    {
                        if (binding?.Kind == IdentityKind.Shared) { await output.WriteLineAsync($"{t.Id}\tuses the shared app (see [shared app] lines)"); continue; }
                        if (identity is null) { await output.WriteLineAsync($"{t.Id}\tno identity set"); continue; }
                        if (!TryExpiry(identity.CertificatePem, out var expires)) { await output.WriteLineAsync($"{t.Id}\tcertificate unreadable: re-run identity set"); continue; }
                        await output.WriteLineAsync($"{t.Id}\t{Left(expires)}");
                    }
                    break;
                }
                case ("identity", "bind"):
                    await commands.BindSharedIdentityAsync(actor, a.Pos(0), a.Opt("azure-tenant") ?? throw new ArgumentException("--azure-tenant is required."), a.Flag("allow-shared-directory"), ct);
                    await output.WriteLineAsync($"tenant {a.Pos(0)} now uses Packmoat's shared app in its directory (any own-app certificate it had was removed)");
                    break;
                case ("identity", "shared"):
                    return await SharedAsync(a, actor, commands, queries, env, output, ct);
                case ("onboard", "check"):
                {
                    AdminCommands.RequireOperator(actor);
                    var tenant = await queries.GetTenantAsync(actor, a.Pos(0), ct) ?? throw new KeyNotFoundException($"No tenant '{a.Pos(0)}'.");
                    var options = new OnboardingOptions
                    {
                        Organization = a.Opt("org") ?? throw new ArgumentException("--org is required."),
                        Project = a.Opt("project"),
                        Feed = a.Opt("feed") ?? throw new ArgumentException("--feed is required."),
                        PackmoatPrincipal = a.Opt("principal") ?? "",
                        AdminToken = FromEnv(a.Opt("admin-token-env")),
                        AdminTokenIsBearer = a.Flag("admin-bearer"),
                        DeveloperToken = FromEnv(a.Opt("developer-token-env")),
                        DeveloperTokenIsBearer = a.Flag("developer-bearer"),
                        ProxyUrl = a.Opt("proxy-url"),
                        ProxyToken = FromEnv(a.Opt("proxy-token-env")),
                    };
                    options.PrivateFeedProbeUrls.AddRange(a.Opts("probe-url"));
                    options.ConfirmedAdmins.AddRange(a.Opts("confirm-admin"));

                    // A shared tenant: read what is being checked ONCE, now; the record (if any) vouches for exactly this.
                    AdminCommands.SharedCheckSubject? subject = null;
                    if (await queries.GetUpstreamBindingAsync(actor, tenant.Id, ct) is { Kind: IdentityKind.Shared })
                    {
                        subject = await commands.ReadSharedCheckSubjectAsync(actor, tenant.Id, ct);
                        if (subject.FeedIndexUrl != tenant.UpstreamFeedIndexUrl) throw new ArgumentException("The tenant's feed URL changed just now; run the check again.");
                        options.SharedAppClientId = subject.ClientId;
                        options.SharedAppDirectoryId = subject.AzureTenantId;
                        options.ProxyRetryFor = TimeSpan.FromSeconds(150);   // running servers re-read it within 5 s; generous, for builds before #170 (2 minutes)
                    }
                    env.TuneOnboarding?.Invoke(options);

                    using var http = env.OnboardingHandler is { } handler
                        ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) }
                        : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
                    // The check itself may use a shared identity that has not passed yet: it is what produces the pass.
                    var source = env.OnboardingCredentials?.Invoke(tenants, shared, env.Clock)
                                 ?? new EntraCertificateCredentialSource(tenants, env.Clock, shared, requireSharedVerification: false);
                    var checks = new OnboardingChecks(http, source, tenant, options);

                    async Task<int> Report(IReadOnlyList<CheckResult> all, string? after = null)
                    {
                        var verdict = OnboardingSummary.Evaluate(all);
                        await output.WriteLineAsync("\n" + verdict.Text);
                        if (after is not null) await output.WriteLineAsync(after);
                        return verdict.ExitCode;
                    }
                    Task Print(CheckResult r) => output.WriteLineAsync($"[{OnboardingSummary.Label(r.Status)}] {r.Title}\n       {r.Detail}");

                    if (subject is null)
                    {
                        var results = await checks.RunAsync(ct);   // own app: one pass, as always
                        foreach (var r in results) await Print(r);
                        return await Report(results);
                    }

                    // Shared app, phase 1: everything but the proxy (the server refuses this tenant until a pass is recorded).
                    var phaseOne = await checks.RunWithoutProxyAsync(ct);
                    foreach (var r in phaseOne) await Print(r);
                    if (!OnboardingSummary.PhaseOnePassed(phaseOne))
                    {
                        var notRun = new CheckResult("proxy", "Packmoat answers: 401 without a token, 200 with one", CheckStatus.Skip,
                            "NOT VERIFIED: not run, because the checks above did not all pass (the server serves a shared tenant only after they do).");
                        await Print(notRun);
                        return await Report(phaseOne.Append(notRun).ToList(), "Not recorded: this shared tenant serves nothing until a run passes (exit 0).");
                    }

                    // Phase 1 passed: record it, so the server will serve the tenant; then phase 2, the proxy check. Withdraw unless it passes.
                    var record = await commands.RecordSharedVerificationAsync(actor, subject, ct);
                    await output.WriteLineAsync($"Phase 1 passed: recorded for directory {record.AzureTenantId} and app {record.ClientId}. Checking that Packmoat now serves it (up to {options.ProxyRetryFor.TotalSeconds:0} seconds)...");
                    var kept = false;
                    try
                    {
                        var proxy = await checks.RunProxyAsync(ct);
                        await Print(proxy);
                        var all = phaseOne.Append(proxy).ToList();
                        kept = OnboardingSummary.Evaluate(all).ExitCode == 0;
                        return await Report(all, kept
                            ? $"Recorded: shared tenant {record.TenantId} is verified and served."
                            : "Withdrawn: the proxy check did not pass, so the record made above was removed. This shared tenant serves nothing until a run passes (exit 0).");
                    }
                    finally
                    {
                        // Only the record made above; a newer one (another run) is left alone.
                        if (!kept) await commands.WithdrawSharedVerificationAsync(actor, record, CancellationToken.None);
                    }
                }
                case ("identity", "set"):
                {
                    var pem = await File.ReadAllTextAsync(a.Opt("pem-file") ?? throw new ArgumentException("--pem-file is required."), ct);
                    await commands.SetUpstreamIdentityAsync(actor, a.Pos(0), new UpstreamIdentity(
                        a.Opt("azure-tenant") ?? throw new ArgumentException("--azure-tenant is required."),
                        a.Opt("client-id") ?? throw new ArgumentException("--client-id is required."), pem), ct);
                    await output.WriteLineAsync("identity stored (encrypted)");
                    break;
                }
                case ("audit", "tail"):
                    foreach (var e in await queries.AuditTailAsync(actor, a.Opt("tenant"), IntOpt(a, "limit", 50, 1, 10000), ct))
                        await output.WriteLineAsync($"{e.Time:u}\t{e.TenantId}\t{e.Kind}\t{e.PackageId} {e.Version}\t{e.Decision}\t{e.Reason}\t{e.WaiverId}\t{e.TokenId}\t{e.RequestId}");
                    break;
                case ("decision", _):
                    await DecisionAsync(a, actor, queries, output, ct);
                    break;
                case ("jobs", _):
                    return await JobsAsync(db, lifecycle, env, audit, activity, output, error, ct);
                default:
                    await error.WriteLineAsync("Unknown command.\n" + usage);
                    return 2;
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or SettingsException or TenantCreationRefusedException or SchemaTooNewException or SchemaTrackException or SchemaBaselineException or KeyNotFoundException or FormatException or OverflowException or IOException or UnauthorizedAccessException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException or ExternalServiceException)
        {
            await error.WriteLineAsync("error: " + ex.Message); // messages never contain secrets: they name the problem, not the value
            return 1;
        }
    }

    /// <summary>
    /// jobs run retention (#233): the server's daily retention pass, every step, by hand or from a scheduler (for a server whose
    /// PACKMOAT_BACKGROUND_JOBS is off). It prints each step's count, or the kind of its failure only, and exits 1 when any step failed.
    /// It counts the tenants due for purge but never claims the day's nudge nor alerts, so the server's own nudge is unaffected; it deletes
    /// no tenant. It takes the job lease the server's retention pass takes (#232 M2): while a server copy (or this command elsewhere) is
    /// running the pass, it changes nothing, says so and exits 0. A pass that runs prints its duration.
    /// </summary>
    private static async Task<int> JobsAsync(SqliteDatabase db, TenantLifecycle lifecycle, CliEnvironment env, SqliteAuditLog audit,
        SqliteTokenActivity activity, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var pass = new RetentionPass(new SqliteFeedCheckStore(db), env.Clock,
            new SqliteTenantPurgeStore(db, lifecycle.Eligibility?.Invoke(), lifecycle.Contributors),
            alerts: null, new SqliteDecisionRecordLog(db), new SqliteAdminActionLog(db), audit, activity);
        // The server's lease (#232 M2): while a server copy runs its pass, this one is skipped, and the other way round.
        IReadOnlyList<RetentionStep> steps = [];
        var run = await LeasedJob.RunAsync(new SqliteJobLease(db, env.Clock), JobLeases.Retention, JobLeases.ProcessHolder,
            async c => steps = await pass.RunAsync(nudge: false, report: null, c), ct);
        if (run.AcquireFailure is { } acquire) throw acquire;
        if (!run.Ran)
        {
            await output.WriteLineAsync("retention pass skipped: another copy (a server, or this command elsewhere) is running it now. Nothing was changed; run it again later.");
            return 0;
        }
        await output.WriteLineAsync($"retention pass took {(long)run.Duration.TotalMilliseconds} ms");
        if (run.ReleaseFailure is { } release)
            await error.WriteLineAsync($"warning: ending the job lease failed ({release.GetType().Name}); it runs out by itself within {(int)JobLeases.Ttl.TotalMinutes} minutes");
        foreach (var step in steps)
            await output.WriteLineAsync($"{StepName(step.Kind)}\t" + (step.Failure is { } ex ? $"FAILED ({ex.GetType().Name})" : step.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var failed = steps.Count(s => s.Failed);
        if (failed > 0)
        {
            await error.WriteLineAsync($"error: {failed} retention step(s) failed; the others ran. Run it again later.");
            return 1;
        }
        if (steps.Single(s => s.Kind == RetentionStepKind.TenantsDueForPurge).Count > 0)
            await output.WriteLineAsync("Tenants are due for purge: run packmoat-admin tenant purge-due");
        await output.WriteLineAsync("retention pass done");
        return 0;
    }

    private static string StepName(RetentionStepKind kind) => kind switch
    {
        RetentionStepKind.FeedChecksBlanked => "feed checks blanked",
        RetentionStepKind.TenantsDueForPurge => "tenants due for purge (counted, nothing deleted)",
        RetentionStepKind.DecisionRecordsPruned => "decision records deleted",
        RetentionStepKind.AdminActionsPruned => "admin action records deleted",
        RetentionStepKind.AuditEventsPruned => "audit events deleted",
        RetentionStepKind.TokenActivityPruned => "per-day activity rows deleted",
        _ => kind.ToString(),
    };

    /// <summary>
    /// db check-migrations FILE (#196): the pre-deploy dry run. Never opens PACKMOAT_DB (refuses a file that resolves to it); the runner
    /// runs on a temporary copy, which is deleted. Prints names and counts only. Exit 0 only when the runner ran, every table's row count
    /// is unchanged and the schema equals a fresh build's; 1 otherwise (a missing file: an error line, exit 1).
    /// </summary>
    private static async Task<int> CheckMigrationsAsync(string[] args, CliEnvironment env, IReadOnlyList<MigrationTrack> tracks, TextWriter output, TextWriter error)
    {
        if (args.Length < 2 || args[1] != "check-migrations")
        {
            await error.WriteLineAsync("Unknown db command. Usage: packmoat-admin db check-migrations <file>");
            return 2;
        }
        var file = args.Length > 2 ? args[2] : throw new ArgumentException("A database file (a backup) is required.");
        // A missing file is a typo, not a refused deploy: nothing was copied or checked (#251 item 24).
        if (!File.Exists(file)) throw new FileNotFoundException($"{file} was not found. Nothing was checked; give the path of a backup file.");
        var report = await MigrationDryRun.RunAsync(file, env.DbPath, tracks, TryProtector(env));
        await output.WriteLineAsync($"checked a temporary copy of {file} (the file itself is only read; the copy is deleted)");
        await output.WriteLineAsync("source: " + Describe(report.Source));
        foreach (var t in report.Tracks)
            await output.WriteLineAsync($"track {t.Name}: before {Version(t.Before)}, after {Version(t.After)}: {t.Verdict}");
        if (report.Refusal is not null)
        {
            await output.WriteLineAsync("REFUSED: " + report.Refusal);
            await output.WriteLineAsync("result: REFUSED. The deploy would not start on this database.");
            return 1;
        }
        foreach (var r in report.Rows)
            await output.WriteLineAsync($"rows {r.Table}: {Count(r.Before)} -> {Count(r.After)}{(r.NewAndEmpty ? "  (new, empty)" : r.Equal ? "" : "  CHANGED")}");
        await output.WriteLineAsync(report.Rows.All(r => r.Equal) ? $"rows: equal in all {report.Rows.Count} tables" : $"rows: CHANGED in {report.Rows.Count(r => !r.Equal)} table(s)");
        foreach (var d in report.SchemaDifferences) await output.WriteLineAsync("schema: " + d);
        await output.WriteLineAsync(report.SchemaDifferences.Count == 0 ? "schema: equal to a fresh build" : $"schema: {report.SchemaDifferences.Count} difference(s) from a fresh build");
        if (report.WhitespaceOnly.Count > 0)
            await output.WriteLineAsync($"note: the stored SQL text of {report.WhitespaceOnly.Count} object(s) differs from a fresh build's in whitespace only: {string.Join(", ", report.WhitespaceOnly)}");
        await output.WriteLineAsync(report.Ok ? "result: OK" : "result: DIFFERENCES (see above). Do not deploy until they are understood.");
        return report.Ok ? 0 : 1;

        static string Version(int? v) => v is null ? "none" : $"v{v}";
        static string Count(long? n) => n is null ? "none" : n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// tenant create ... --azure-tenant GUID (#195): the self-hoster's "add your organisation". The tenant, its directory link and a
    /// tenant.create record (site "cli") are written in one transaction through the core's organisation store, with the lifecycle's
    /// participants; a directory already linked writes nothing (exit 1). No member is made: the directory's first Admin to sign in becomes
    /// one. With no upstream options (#207) the tenant has no URLs and feed_setup 'customer': its Admins connect the feed with the Azure
    /// setup wizard, and the record says <c>feed=customer</c> instead of the URLs.
    /// </summary>
    private static async Task<int> CreateAndLinkAsync(NewTenant tenant, string rawDirectory, ActorScope actor, SqliteDatabase db, TenantLifecycle lifecycle, IClock clock,
        TextWriter output, CancellationToken ct)
    {
        AdminCommands.CheckNewTenant(actor, tenant);
        var directory = DirectoryBindingCommands.LinkableDirectory(rawDirectory);
        var now = clock.UtcNow;
        var urls = tenant.FeedSetup == FeedSetup.Customer ? "feed=customer" : $"upstream={tenant.UpstreamBase}; path_prefix={tenant.AllowedPathPrefix}; feed={tenant.FeedIndexUrl}";
        var detail = $"name={tenant.Name}; {urls}; cooldown_days={tenant.CooldownDays}; directory={directory}";
        var record = new AdminActionRecord(now, "", "", actor.Name, "tenant.create", tenant.Id, detail, "cli");
        var result = await new SqliteOrganisationStore(db, lifecycle.Participants).CreateAsync(new NewOrganisation(tenant, directory, now, actor.Name, Record: record), [], ct);
        if (result == OrganisationCreateResult.DirectoryTaken)
            throw new ArgumentException($"Directory {directory} is already linked to another tenant. Unbind it there first. Tenant {tenant.Id} was not created.");
        await output.WriteLineAsync($"created tenant {tenant.Id}");
        await output.WriteLineAsync($"directory {directory} is linked to tenant {tenant.Id}: people from it with a Packmoat app role can sign in to the customer site");
        if (tenant.FeedSetup == FeedSetup.Customer)
            await output.WriteLineAsync("no upstream yet: an Admin from that directory connects the feed on the customer site (Azure setup)");
        return 0;
    }

    /// <summary>
    /// tenant directory bind|unbind|show. Bind and unbind are recorded in admin_actions (site "cli") BEFORE the change, and again with
    /// ".failed" when the change did not happen, the same way the admin site records its writes.
    /// </summary>
    private static async Task<int> DirectoryAsync(AdminCliArgs a, ActorScope actor, DirectoryBindingCommands commands, IAdminActionLog log, IClock clock, TextWriter output, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        var verb = a.Pos(0);
        var tenantId = a.Pos(1);
        switch (verb)
        {
            case "bind":
            case "unbind":
            {
                var raw = a.Opt("azure-tenant") ?? throw new ArgumentException("--azure-tenant is required.");
                var directory = CustomerSignIn.CanonicalDirectory(raw) ?? throw new ArgumentException("--azure-tenant must be a directory (tenant) id: a GUID.");
                var action = "directory." + verb;
                Task Record(string suffix) => log.AppendAsync(new AdminActionRecord(clock.UtcNow, "", "", actor.Name, action + suffix, tenantId, "directory " + directory, "cli"), CancellationToken.None).AsTask();
                await Record("");
                var done = false;
                try
                {
                    if (verb == "bind")
                    {
                        await commands.BindAsync(actor, tenantId, directory, ct);
                        await output.WriteLineAsync($"directory {directory} is linked to tenant {tenantId}: people from it with a Packmoat app role can sign in to the customer site");
                    }
                    else if (await commands.UnbindAsync(actor, tenantId, directory, ct) is { } revoked)
                    {
                        await output.WriteLineAsync($"directory {directory} is no longer linked to tenant {tenantId}");
                        await output.WriteLineAsync($"{revoked} self-issued token(s) of people from that directory were revoked with it (running servers notice within 5 seconds)");
                        // Best effort, after the change: the intent row above already stands.
                        try { await log.AppendAsync(new AdminActionRecord(clock.UtcNow, "", "", actor.Name, "directory.unbind.tokens-revoked", tenantId, $"directory {directory}; revoked={revoked}", "cli"), CancellationToken.None); }
                        catch (Exception) { await output.WriteLineAsync("warning: the revoked-token count could not be recorded in admin_actions"); }
                    }
                    else
                        throw new KeyNotFoundException($"Directory {directory} is not linked to tenant {tenantId}.");
                    done = true;
                }
                finally
                {
                    if (!done) await Record(".failed");
                }
                return 0;
            }
            case "show":
            {
                var (bindings, suggestion) = await commands.ShowAsync(actor, tenantId, ct);
                await output.WriteLineAsync($"{tenantId}: {bindings.Count} linked director{(bindings.Count == 1 ? "y" : "ies")}");
                foreach (var b in bindings) await output.WriteLineAsync($"  {b.AzureTenantId}\tbound {b.BoundAt:u} by {b.BoundBy}");
                if (suggestion is not null)
                    await output.WriteLineAsync($"  suggestion: this shared-app tenant passed its onboarding check for directory {suggestion}. If its people should sign in, run: tenant directory bind {tenantId} --azure-tenant {suggestion}");
                return 0;
            }
            default:
                throw new ArgumentException("Use: tenant directory bind|unbind|show <id>.");
        }
    }

    /// <summary>
    /// tenant purge-due | tenant purge &lt;id&gt; --confirm &lt;id&gt;. Operator only. purge-due only reads. purge is recorded in admin_actions
    /// (site "cli") before anything happens, and again with ".failed" when it did not complete; the module's eligibility has the last
    /// word (it may ask a payment provider) and then it deletes in one transaction. No operator alerts from here.
    /// With no module's lifecycle nobody is due and every purge is refused (#194).
    /// </summary>
    private static async Task<int> PurgeAsync(AdminCliArgs a, ActorScope actor, SqliteDatabase db, TenantLifecycle lifecycle, List<string> notes, CliEnvironment env,
        string dueCheck, TextWriter output, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        var store = new SqliteTenantPurgeStore(db, lifecycle.Eligibility?.Invoke(), lifecycle.Contributors);
        var purges = new TenantPurgeCommands(store, new SqliteAdminActionLog(db), env.Clock);
        var now = env.Clock.UtcNow;
        if (a.Cmd.Item2 == "purge-due")
        {
            var listed = await purges.ListDueAsync(actor, ct);
            await output.WriteLineAsync($"{listed.Count(l => l.Verdict == PurgeVerdict.Due)} tenant(s) due for purge, " +
                                        $"{listed.Count(l => l.Verdict == PurgeVerdict.Unknown)} to check by hand");
            foreach (var l in listed)
                await output.WriteLineAsync(l.Verdict == PurgeVerdict.Due
                    ? $"{l.TenantId}\tDUE since {l.DueAt:u} ({(int)Math.Floor((now - l.DueAt!.Value).TotalDays)} day(s))\t{l.Reason}"
                    : $"{l.TenantId}\tCHECK BY HAND\t{l.Reason}");
            if (listed.Any(l => l.Verdict == PurgeVerdict.Due))
                await output.WriteLineAsync("Purge each with: packmoat-admin tenant purge <id> --confirm <id>   (take a backup first" + (dueCheck.Length > 0 ? "; " + dueCheck : "") + ")");
            return 0;
        }

        var tenantId = a.Pos(0);
        PurgeOutcome done;
        try { done = await purges.PurgeAsync(actor, tenantId, a.Opt("confirm"), ct); }
        catch (PurgeRefusedException ex)
        {
            foreach (var n in notes) await output.WriteLineAsync("  " + n);
            throw new ArgumentException("REFUSED: " + ex.Message);
        }
        if (done.AlreadyPurged)
        {
            await output.WriteLineAsync($"tenant {tenantId} was already purged {done.PurgedAt:u}; nothing to do");
            return 0;
        }
        await output.WriteLineAsync($"tenant {tenantId} purged {done.PurgedAt:u}. Rows deleted:");
        foreach (var t in done.Counts) await output.WriteLineAsync($"  {t.Table}\t{t.Rows}");
        await output.WriteLineAsync($"Kept: {string.Join(", ", store.Kept)}. Not reached: {string.Join(", ", store.NotReached)}.");
        return 0;
    }

    public const string SharedCertName = "packmoat-shared";

    private static async Task<int> SharedAsync(AdminCliArgs a, ActorScope actor, AdminCommands commands, AdminQueries queries, CliEnvironment env, TextWriter output, CancellationToken ct)
    {
        switch (a.Pos(0))
        {
            case "new-cert":
            {
                AdminCommands.RequireOperator(actor);
                var days = IntOpt(a, "days", 365, CertificateTools.MinDays, CertificateTools.MaxDays);
                var dir = Path.GetFullPath(a.Opt("out-dir") ?? throw new ArgumentException("--out-dir is required."));
                var cer = Path.Combine(dir, SharedCertName + ".cer");
                var pem = Path.Combine(dir, SharedCertName + ".pem");
                foreach (var existing in new[] { cer, pem })
                    if (File.Exists(existing)) throw new IOException($"{existing} already exists; refusing to overwrite a certificate. Move it away or choose another --out-dir.");
                CertificateTools.EnsurePrivateDirectory(dir);
                var gen = CertificateTools.Generate("packmoat-shared-app", days, env.Clock.UtcNow);
                await CertificateTools.WriteNewAsync(pem, System.Text.Encoding.ASCII.GetBytes(gen.CombinedPem), privateFile: true, ct); // the secret first, born with mode 600
                await CertificateTools.WriteNewAsync(cer, gen.PublicCer, privateFile: false, ct);
                await output.WriteLineAsync($"public certificate (upload to Packmoat's shared app registration): {cer}");
                await output.WriteLineAsync($"private bundle (mode 600; give to 'identity shared set' then delete it): {pem}");
                await output.WriteLineAsync($"thumbprint {gen.Thumbprint}, expires {gen.NotAfter:u}");
                return 0;
            }
            case "set":
            {
                var pemText = await File.ReadAllTextAsync(a.Opt("pem-file") ?? throw new ArgumentException("--pem-file is required."), ct);
                var slot = a.Flag("next") ? SharedAppSlot.Next : SharedAppSlot.Active;
                // The command checks the certificate (readable, has its key, not expired); the same checks run again on promote.
                await commands.SetSharedAppCredentialAsync(actor, slot, new SharedAppCredential(a.Opt("client-id") ?? throw new ArgumentException("--client-id is required."), pemText), ct);
                var facts = DescribeOrThrow(pemText);
                await output.WriteLineAsync($"shared app credential stored as {SqliteSharedAppCredentialStore.SlotName(slot)} (encrypted): thumbprint {facts.Thumbprint}, expires {facts.NotAfter:u}");
                return 0;
            }
            case "promote":
                if (!await commands.PromoteSharedAppCredentialAsync(actor, ct)) { await output.WriteLineAsync("no next credential to promote"); return 1; }
                await output.WriteLineAsync("next is now active. Running servers switch within 5 seconds. Run the live test, then remove the old certificate from the app.");
                return 0;
            case "show":
            {
                var all = await queries.SharedAppCredentialsAsync(actor, ct);
                if (all.Count == 0) await output.WriteLineAsync("no shared app credential set");
                foreach (var (slot, c) in all)
                {
                    var name = SqliteSharedAppCredentialStore.SlotName(slot);
                    try
                    {
                        var f = CertificateTools.Describe(c.CertificatePem);
                        await output.WriteLineAsync($"{name}\tclient {c.ClientId}\tthumbprint {f.Thumbprint}\texpires {f.NotAfter:u}{(f.HasPrivateKey ? "" : "\tNO PRIVATE KEY")}");
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        await output.WriteLineAsync($"{name}\tclient {c.ClientId}\tcertificate unreadable");
                    }
                }
                return 0;
            }
            default:
                throw new ArgumentException("identity shared needs new-cert, set, promote or show.");
        }
    }

    private static CertificateTools.Facts DescribeOrThrow(string pem)
    {
        try { return CertificateTools.Describe(pem); }
        catch (System.Security.Cryptography.CryptographicException) { throw new ArgumentException("The PEM file does not hold a readable certificate."); }
    }

    /// <summary>
    /// decision show --tenant &lt;id&gt; --package &lt;id&gt; [--version &lt;v&gt;] [--prefix] [--limit 50]. Operator only, read only. One line per
    /// row, tab-separated, newest last seen first. Package ids and versions came from clients (and the search from the command line):
    /// every one is passed through <see cref="Terminal"/>, so none can move the cursor, forge a line or a column, or reorder the text.
    /// </summary>
    private static async Task DecisionAsync(AdminCliArgs a, ActorScope actor, AdminQueries queries, TextWriter output, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        if (a.Cmd.Item2 != "show") throw new ArgumentException("Use: decision show --tenant <id> --package <id> [--version <v>] [--prefix] [--limit 50].");
        var tenantId = a.Opt("tenant") ?? throw new ArgumentException("--tenant is required.");
        var package = a.Opt("package") ?? throw new ArgumentException("--package is required (with --prefix, the start of the package ids).");
        var limit = IntOpt(a, "limit", DecisionRecordQuery.DefaultLimit, 1, DecisionRecordQuery.MaxLimit);
        if (await queries.GetTenantAsync(actor, tenantId, ct) is null) throw new KeyNotFoundException($"No tenant '{Terminal(tenantId)}'.");
        var prefix = a.Flag("prefix");
        var page = await queries.DecisionsAsync(actor, new DecisionRecordQuery(tenantId, package, prefix, a.Opt("version"), limit), ct);

        // What was searched for, as the store was asked (lower-case id, normalised version).
        var searched = (prefix ? "package prefix " : "package ") + Terminal(package.Trim().ToLowerInvariant()) +
                       (string.IsNullOrWhiteSpace(a.Opt("version")) ? "" : ", version " + Terminal(NuGetVersionKey.Normalize(a.Opt("version")!)));
        await output.WriteLineAsync($"{page.Rows.Count} row(s) for tenant {Terminal(tenantId)}, {searched}, newest last seen first");
        foreach (var r in page.Rows)
            await output.WriteLineAsync($"{Terminal(r.PackageId)}\t{Terminal(r.Version)}\t{r.Decision}\t{r.Reason}" +
                $"\tcooldown={(r.CooldownDays is { } d ? d + " days" : "none")}\tpublished={(r.PublishedAt is { } p ? p.ToString("u") : "unknown")}" +
                $"\tfirst seen={r.FirstSeenAt:u}\tlast seen={r.LastSeenAt:u}\tdecisions={r.SeenCount}\tpolicy revision={r.PolicyRevision}\twaiver={(r.WaiverId is { } w ? Terminal(w) : "none")}");
        if (page.More) await output.WriteLineAsync($"More rows match: raise --limit (at most {DecisionRecordQuery.MaxLimit}) or narrow the search.");
    }

    /// <summary>
    /// Untrusted text made safe for a terminal line: every control character (C0, DEL and C1, so escape sequences, new lines and tabs),
    /// format character (the bidirectional overrides among them) and line or paragraph separator becomes '?'.
    /// </summary>
    internal static string Terminal(string s) => new(s.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
        ? '?' : c).ToArray());

    /// <summary>One `token list` line. Never the token value. The owner's display name came from Microsoft: control characters are blanked so it cannot break the line.</summary>
    internal static string TokenLine(TokenInfo t, DateTimeOffset now)
    {
        var state = t.RevokedAt is { } r ? "revoked " + r.ToString("u") + (t.RevokedBy is { } by ? " by " + OneLine(by) : "")
            : t.ExpiresAt is { } x && x <= now ? "expired " + x.ToString("u")
            : "active";
        var owner = t.OwnerOid is null ? "none" : $"{OneLine(t.OwnerName ?? "unknown")} ({t.OwnerOid})";
        return $"{t.TokenId}\t{t.DisplayPrefix}\t{t.Label}\tcreated={t.CreatedAt:u}\t{state}\tlast used={(t.LastUsedAt?.ToString("u") ?? "never")}\tkind={(t.Kind == TokenKind.Automation ? "automation" : "developer")}" +
               $"\towner={owner}\texpires={(t.ExpiresAt?.ToString("u") ?? "never")}\tissued via={(t.IssuedVia == TokenIssuer.Self ? "self" : "operator")}";
    }

    private static string OneLine(string s) => new(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static string Mode(UpstreamBinding? b) => b switch { null => "none", { Kind: IdentityKind.Shared } => "shared", _ => "own" };

    /// <summary>A whole number within limits, or an error that names the option and the range (never a stack trace).</summary>
    private static int IntOpt(AdminCliArgs a, string name, int? fallback, int min, int max)
    {
        var text = a.Opt(name);
        if (text is null) return fallback ?? throw new ArgumentException($"--{name} is required.");
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : throw new ArgumentException($"--{name} must be a whole number from {min} to {max}.");
    }

    private static TokenKind ParseKind(string? text) => text?.ToLowerInvariant() switch
    {
        null or "developer" => TokenKind.Developer,
        "automation" => TokenKind.Automation,
        _ => throw new ArgumentException("--kind must be developer or automation."),
    };

    /// <summary>The master key, or null only when it is not set at all (neither the variable nor its file). A bad key or an unreadable file
    /// is an error (#238): it must not silently skip the key check in verify, restore, backup and db check-migrations.</summary>
    private static AesGcmSecretProtector? TryProtector(CliEnvironment env)
    {
        try { return env.Protector(); }
        catch (SettingsException ex) when (ex.NotSet) { return null; }
    }

    private static string Describe(SqliteBackup.Verification v) =>
        !v.Ok ? "NOT OK: " + v.Detail
        : $"integrity ok, schema v{v.SchemaVersion}{string.Concat((v.Tracks ?? []).Select(t => $", {t.Track} schema v{t.Version}"))}, {v.Tenants} tenants, {v.Tokens} tokens, {v.AuditEvents} audit events, master key: " + v.Key switch
        {
            SqliteBackup.KeyStatus.Matches => "matches",
            SqliteBackup.KeyStatus.NotRecorded => "no key marker in this older database (not checked)",
            _ => "NOT CHECKED (PACKMOAT_MASTER_KEY is not set)",
        };

    private static bool TryExpiry(string pem, out DateTimeOffset expires)
    {
        try { expires = CertificateTools.ExpiryOf(pem); return true; }
        catch (System.Security.Cryptography.CryptographicException) { expires = default; return false; }
    }

    private static string ExpiryText(string pem) =>
        TryExpiry(pem, out var e) ? $"expires {e:yyyy-MM-dd}" : "certificate unreadable";

    private static string? FromEnv(string? variable) =>
        variable is null ? null : Settings.Get(variable) is { Length: > 0 } v ? v : throw new ArgumentException($"Environment variable {variable} is not set.");

    private static string Ensure(string? upstreamBase) =>
        upstreamBase is null ? throw new ArgumentException("--upstream-base is required.") : upstreamBase.EndsWith('/') ? upstreamBase : upstreamBase + "/";

    private static async Task Show(TextWriter output, TenantDetail detail)
    {
        var t = detail.Tenant;
        await output.WriteLineAsync($"{t.Id} ({t.Name})");
        // A self-made organisation has no upstream until it is set up.
        await output.WriteLineAsync(t.UpstreamBase.Length == 0 ? "  upstream: not set" : $"  upstream: {t.UpstreamBase}  path prefix: {t.AllowedPathPrefix}");
        await output.WriteLineAsync(t.UpstreamFeedIndexUrl.Length == 0 ? "  feed: not set" : $"  feed: {t.UpstreamFeedIndexUrl}");
        if (detail.Setup is { } setup)
            await output.WriteLineAsync(setup.Origin == TenantOrigin.Self ? "  feed set up by: the customer (self-made)"
                : setup.FeedSetup == FeedSetup.Customer ? "  feed set up by: the customer (Azure setup on the customer site)"
                : "  feed set up by: the operator");
        await output.WriteLineAsync($"  cooldown: {t.Policy.CooldownDays} days   policy revision: {t.Policy.Revision}");
        await output.WriteLineAsync($"  internal prefixes: {(t.Policy.InternalPrefixes.Count == 0 ? "(none)" : string.Join(", ", t.Policy.InternalPrefixes))}");
        foreach (var o in t.Policy.PackageOverrides) await output.WriteLineAsync($"  override: {o.PackageId} = {o.CooldownDays} days");
        foreach (var w in t.Policy.Waivers) await output.WriteLineAsync($"  waiver {w.Id}: {w.PackageId} {w.Version} until {w.ExpiresAt:u} ({w.Reason}, by {w.CreatedBy})");
        var identity = detail.Identity;
        await output.WriteLineAsync(detail.Binding is { Kind: IdentityKind.Shared } b
            ? $"  upstream identity: shared (Packmoat's app) in directory {b.AzureTenantId}"
            : identity is null ? "  upstream identity: NOT SET"
            : $"  upstream identity: own app, client {identity.ClientId} in {identity.AzureTenantId} (certificate stored encrypted, {ExpiryText(identity.CertificatePem)})");
    }
}
