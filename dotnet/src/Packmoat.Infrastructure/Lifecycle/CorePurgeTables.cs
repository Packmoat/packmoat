namespace Packmoat.Infrastructure.Lifecycle;

/// <summary>
/// The core's tables and what a purge does to them, for every engine: deleted for the tenant, kept, or holding no
/// tenant data. The deleted ones are deleted in this order (children first; the contributors' just before tenants), then tenant_purges is
/// written and audit_events deleted (its trigger allows that only then). An engine's own catalog tables, which hold no tenant's data and
/// which its purge's first guard would otherwise find unclassified, are added by <see cref="WithEngineTables"/>: SQLite's
/// sqlite_sequence. PostgreSQL has none (its catalogs are another schema), and a classified table that does not exist refuses every
/// purge, so it takes <see cref="Tables"/> as it is.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class CorePurgeTables
{
    /// <summary>Up to and including shared_app_credentials: an engine's own tables come right after it, where SQLite's always were.</summary>
    private static readonly PurgeTable[] BeforeEngineTables =
    [
        new("token_activity", PurgeTableFate.Deleted, "which token was used on which day"),
        new("decision_records", PurgeTableFate.Deleted, "what was decided per package version, and why (no token or person)", "tenant_id"),
        new("tokens", PurgeTableFate.Deleted, "the tenant's feed tokens (hashes), owners and labels", "tenant_id"),
        new("shared_app_verifications", PurgeTableFate.Deleted, "the tenant's onboarding check records", "tenant_id"),
        new("feed_checks", PurgeTableFate.Deleted, "the tenant's feed setup checks (may hold admin names)", "tenant_id"),
        new("upstream_identities", PurgeTableFate.Deleted, "the tenant's upstream binding and certificate", "tenant_id"),
        new("sign_in_directories", PurgeTableFate.Deleted, "the directory link: freed, so the organisation may sign up again", "tenant_id"),
        new("members", PurgeTableFate.Deleted, "who signed in, with display names", "tenant_id"),
        new("auto_join_settings", PurgeTableFate.Deleted, "the tenant's auto-join switch", "tenant_id"),
        new("internal_prefixes", PurgeTableFate.Deleted, "the tenant's policy", "tenant_id"),
        new("package_overrides", PurgeTableFate.Deleted, "the tenant's policy", "tenant_id"),
        new("waivers", PurgeTableFate.Deleted, "the tenant's policy", "tenant_id"),
        new("tenants", PurgeTableFate.Deleted, "the tenant itself", "id"),
        new("audit_events", PurgeTableFate.Deleted, "the tenant's package decisions (deleted last, through the purge trigger)", "tenant_id"),
        new("admin_actions", PurgeTableFate.Kept, "the operator's evidence of what was done, including the purge itself (retention: TenantPurgePolicy.AdminActionsRetention, pruned daily by the retention pass)", "tenant_id"),
        new("tenant_purges", PurgeTableFate.Kept, "the purge record: makes a second run a no-op and blocks the id's reuse; no personal fields", "tenant_id"),
        new("meta", PurgeTableFate.NoTenantData, "the master key check and the last purge nudge day"),
        new("schema_migrations", PurgeTableFate.NoTenantData, "schema versions"),
        new("shared_app_credentials", PurgeTableFate.NoTenantData, "Packmoat's own shared app"),
    ];

    private static readonly PurgeTable[] AfterEngineTables =
    [
        new("job_leases", PurgeTableFate.NoTenantData, "which server copy runs each scheduled job, and until when"),
        new("cookie_keys", PurgeTableFate.NoTenantData, "the cookie protection keys, encrypted"),
        new("rate_counters", PurgeTableFate.NoTenantData, "rate-limit windows: pseudonymous (unsalted SHA-256) keys and counts, short-lived: deleted within one window plus about a day"),
        new("form_nonces", PurgeTableFate.NoTenantData, "single-use form nonces: hashes of nonces and of pseudonymous keys, short-lived: deleted when used or within their life plus about a day"),
        new("feed_setup_scratch", PurgeTableFate.Deleted, "what the feed setup wizard keeps for a person between pages (may hold admin names), short-lived: 30 minutes plus about a day", "tenant_id"),
    ];

    /// <summary>
    /// Deletes the tenant's feed-check lease row (its job is <c>feed-check:</c> and the tenant id, <see cref="Packmoat.Application.AzureSetupState.LeaseJob"/>),
    /// so no job_leases row naming the tenant outlives its purge. <c>@t</c> is the tenant id; the same text on every engine. Not counted:
    /// job_leases holds no tenant data but that name.
    /// </summary>
    public const string FeedCheckLeaseDelete = "DELETE FROM job_leases WHERE job = 'feed-check:' || @t";

    /// <summary>The core's tables on every engine, in order, with no engine's own.</summary>
    public static IReadOnlyList<PurgeTable> Tables { get; } = WithEngineTables();

    /// <summary>
    /// <see cref="Tables"/> with <paramref name="engineTables"/> after shared_app_credentials, in the order given. Each must hold no tenant
    /// data (<see cref="PurgeTableFate.NoTenantData"/>): an engine's own catalog table is never deleted from or kept.
    /// </summary>
    public static IReadOnlyList<PurgeTable> WithEngineTables(params PurgeTable[] engineTables)
    {
        ArgumentNullException.ThrowIfNull(engineTables);
        if (engineTables.FirstOrDefault(t => t.Fate != PurgeTableFate.NoTenantData) is { } holding)
            throw new ArgumentException($"An engine's own table holds no tenant data; {holding.Table} is declared {holding.Fate}.", nameof(engineTables));
        return [.. BeforeEngineTables, .. engineTables, .. AfterEngineTables];
    }
}
