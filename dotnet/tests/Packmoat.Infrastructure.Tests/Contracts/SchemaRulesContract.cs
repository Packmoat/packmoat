using System.Data.Common;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The rules the schema enforces itself (#288 P2, PR 6), checked by what they do, on every engine: each of SQLite's triggers
/// (<see cref="SchemaTriggerTests.Current"/>) has tests named <c>&lt;trigger&gt;__&lt;behaviour&gt;</c> (SchemaRulesTests checks the names
/// match), and every CHECK constraint refuses a probe row and accepts one at its boundary (<see cref="CheckProbes"/>). A trigger with the
/// right name and a body that does nothing, or a CHECK with its operator changed, passes the schema comparison but fails here. Raw SQL
/// only, through the engine: PostgreSQL has the schema before it has the stores. Fixtures: SchemaRulesTests (SQLite) and
/// PostgresSchemaRulesTests.
/// </summary>
public abstract class SchemaRulesContract : SqlContract<ISqlEngine>
{
    /// <summary>A time both engines read, for columns no rule looks at.</summary>
    internal const string T = "'2026-01-01T00:00:00.0000000+00:00'";

    internal const string AuditAppendOnly = "audit log is append-only";
    internal const string AuditFloor = "audit log is append-only: a row is deleted only once it is more than 90 days old, or by its tenant's purge";
    internal const string AdminAppendOnly = "admin_actions is append-only";
    internal const string AdminFloor = "admin_actions is append-only: a row is deleted only once it is more than 365 days old";
    internal const string PurgedOnce = "a tenant is recorded as purged only once, and only once its rows are gone";
    internal const string PurgesKept = "tenant_purges is kept forever";
    internal const string PurgesAppendOnly = "tenant_purges is append-only";
    internal const string PurgedId = "this tenant id was purged and is never used again";

    internal static string Tenant(string id) =>
        "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) " +
        $"VALUES('{id}', 'Acme', 'https://upstream.example', '/', 'https://feed.example', 14, {T})";

    internal static string Purge(string tenant, int purging, string reason = "ended") =>
        $"INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor, purging) VALUES('{tenant}', {T}, '{reason}', 'olivia', {purging})";

    /// <summary>An audit row's VALUES tuple.</summary>
    internal static string AuditRow(string time, string request, string tenant = "acme") =>
        $"({time}, '{request}', '{tenant}', 'tok_1', 'Download', 'moq', '4.20.72', 'Allow', 'CooldownElapsed')";

    internal static string Audit(params string[] rows) =>
        "INSERT INTO audit_events(time, request_id, tenant_id, token_id, kind, package_id, version, decision, reason) VALUES " + string.Join(", ", rows);

    internal static string AdminAction(string time, string action) =>
        "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail) " +
        $"VALUES({time}, 'tid', 'oid', 'Olivia', '{action}', 'acme', 'none')";

    internal static string DecisionRow(string tenant, string package) =>
        $"('{tenant}', 'nuget', '{package}', '1.0.0', 'allowed', 'CooldownElapsed', 1, {T}, {T}, 1)";

    internal static string Decisions(params string[] rows) =>
        "INSERT INTO decision_records(tenant_id, ecosystem, package_id, version, decision, reason, policy_revision, first_seen_at, last_seen_at, seen_count) VALUES " +
        string.Join(", ", rows);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// <paramref name="refusal"/> carries <paramref name="message"/> as the rule's whole message: either engine's form of it (SQLite's
    /// <c>…: 'message'.</c>, PostgreSQL's <c>P0001: message</c>), so a longer or shorter message that contains it does not pass.
    /// </summary>
    protected static void AssertMessage(DbException refusal, string message) =>
        Assert.True(refusal.Message.EndsWith($": '{message}'.", StringComparison.Ordinal) || refusal.Message.EndsWith($": {message}", StringComparison.Ordinal),
            $"Expected the refusal \"{message}\", got: {refusal.Message}");

    protected async Task RefusedWithAsync(string message, Func<Task> write) => AssertMessage(await RefusedAsync(write), message);

    protected Task<List<string>> RequestsAsync(IEngineDb t) => ColumnAsync(t, "SELECT request_id FROM audit_events ORDER BY request_id");

    // ---------------------------------------------------------------- audit_events

    [EngineFact]   // mutation: the trigger dropped, or a do-nothing body
    public async Task audit_no_update__refuses_any_update_however_old()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Audit(AuditRow(Engine.TimeAgoSql(TimeSpan.FromDays(400)), "old"), AuditRow(Engine.TimeAgoSql(Minute), "young")));

        await RefusedWithAsync(AuditAppendOnly, () => ExecAsync(t, "UPDATE audit_events SET reason='Waiver' WHERE request_id='old'"));
        await RefusedWithAsync(AuditAppendOnly, () => ExecAsync(t, "UPDATE audit_events SET reason='Waiver'"));

        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM audit_events WHERE reason='Waiver'"));
    }

    [EngineFact]   // mutations: 89 days; 91 days; ">" for "<"; the floor in days (PostgresTriggerGuardTests: a daylight-saving session)
    public async Task audit_no_delete__refuses_a_minute_younger_than_90_days_allows_a_minute_older()
    {
        await using var t = await CreateDbAsync();
        var floor = TimeSpan.FromDays(90);
        await ExecAsync(t, Audit(AuditRow(Engine.TimeAgoSql(floor - Minute), "a minute young"), AuditRow(Engine.TimeAgoSql(floor + Minute), "a minute old")));

        await RefusedWithAsync(AuditFloor, () => ExecAsync(t, "DELETE FROM audit_events WHERE request_id='a minute young'"));
        await ExecAsync(t, "DELETE FROM audit_events WHERE request_id='a minute old'");

        Assert.Equal(["a minute young"], await RequestsAsync(t));
    }

    [EngineFact]   // mutations: the purge branch dropped; purging=1 not required; another tenant's purge allows the delete
    public async Task audit_no_delete__a_running_purge_deletes_only_its_own_tenants_young_rows()
    {
        await using var t = await CreateDbAsync();
        var young = Engine.TimeAgoSql(TimeSpan.FromDays(1));
        await ExecAsync(t, Audit(AuditRow(young, "gone-1", "gone"), AuditRow(young, "gone-2", "gone"), AuditRow(young, "live", "acme")));
        await ExecAsync(t, Purge("done", 0));

        await RefusedWithAsync(AuditFloor, () => ExecAsync(t, "DELETE FROM audit_events WHERE tenant_id='gone'"));   // no purge running yet
        await ExecAsync(t, Purge("gone", 1));
        await RefusedWithAsync(AuditFloor, () => ExecAsync(t, "DELETE FROM audit_events"));   // one row is another tenant's: nothing goes
        Assert.Equal(["gone-1", "gone-2", "live"], await RequestsAsync(t));

        await ExecAsync(t, "DELETE FROM audit_events WHERE tenant_id='gone'");
        await ExecAsync(t, "UPDATE tenant_purges SET purging=0 WHERE tenant_id='gone'");

        Assert.Equal(["live"], await RequestsAsync(t));
        await RefusedWithAsync(AuditFloor, () => ExecAsync(t, "DELETE FROM audit_events"));
    }

    [EngineFact]   // mutations: RETURN NEW (the late row kept); RAISE (the whole statement refused)
    public async Task audit_purged_tenant__drops_the_late_row_and_keeps_the_rest_of_the_statement()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Purge("gone", 0));
        var now = Engine.TimeAgoSql(TimeSpan.Zero);

        await ExecAsync(t, Audit(AuditRow(now, "a"), AuditRow(now, "late", "gone"), AuditRow(now, "b")));

        Assert.Equal(["a", "b"], await RequestsAsync(t));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM audit_events WHERE tenant_id='gone'"));
    }

    // ---------------------------------------------------------------- admin_actions

    [EngineFact]   // mutation: the trigger dropped, or a do-nothing body
    public async Task admin_actions_no_update__refuses_any_update()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, AdminAction(Engine.TimeAgoSql(TimeSpan.FromDays(400)), "old"));
        await ExecAsync(t, AdminAction(Engine.TimeAgoSql(Minute), "young"));

        await RefusedWithAsync(AdminAppendOnly, () => ExecAsync(t, "UPDATE admin_actions SET detail='changed' WHERE action='old'"));
        await RefusedWithAsync(AdminAppendOnly, () => ExecAsync(t, "UPDATE admin_actions SET detail='changed'"));

        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM admin_actions WHERE detail='changed'"));
    }

    [EngineFact]   // mutations: 364 days; 366 days; ">" for "<"; a do-nothing body (nothing tested this floor before PR 6, on either engine)
    public async Task admin_actions_no_delete__refuses_a_minute_younger_than_365_days_allows_a_minute_older()
    {
        await using var t = await CreateDbAsync();
        var floor = TimeSpan.FromDays(365);
        await ExecAsync(t, AdminAction(Engine.TimeAgoSql(floor - Minute), "a minute young"));
        await ExecAsync(t, AdminAction(Engine.TimeAgoSql(floor + Minute), "a minute old"));

        await RefusedWithAsync(AdminFloor, () => ExecAsync(t, "DELETE FROM admin_actions WHERE action='a minute young'"));
        await ExecAsync(t, "DELETE FROM admin_actions WHERE action='a minute old'");

        Assert.Equal(["a minute young"], await ColumnAsync(t, "SELECT action FROM admin_actions"));
    }

    // ---------------------------------------------------------------- tenant_purges

    [EngineFact]   // mutations: either EXISTS dropped
    public async Task tenant_purges_only_when_gone__refuses_while_the_tenant_exists_and_a_second_record()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Tenant("acme"));

        await RefusedWithAsync(PurgedOnce, () => ExecAsync(t, Purge("acme", 1)));
        await ExecAsync(t, "DELETE FROM tenants WHERE id='acme'");
        await ExecAsync(t, Purge("acme", 1));
        await RefusedWithAsync(PurgedOnce, () => ExecAsync(t, Purge("acme", 0, "again")));   // the rule's message, not the key's

        Assert.Equal(["ended"], await ColumnAsync(t, "SELECT reason FROM tenant_purges"));
    }

    [EngineFact]   // mutation: the trigger dropped, or a do-nothing body
    public async Task tenant_purges_no_delete__refuses_every_delete()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Purge("done", 0));
        await ExecAsync(t, Purge("running", 1));

        await RefusedWithAsync(PurgesKept, () => ExecAsync(t, "DELETE FROM tenant_purges WHERE tenant_id='done'"));
        await RefusedWithAsync(PurgesKept, () => ExecAsync(t, "DELETE FROM tenant_purges WHERE tenant_id='running'"));
        await RefusedWithAsync(PurgesKept, () => ExecAsync(t, "DELETE FROM tenant_purges"));

        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenant_purges"));
    }

    // mutations: "=" for IS NOT DISTINCT FROM (reason=NULL then passes the rule and meets NOT NULL's message instead); a column's
    // comparison dropped; 0 to 1 allowed
    [EngineFact]
    public async Task tenant_purges_no_update__allows_only_clearing_purging()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Purge("running", 1));
        await ExecAsync(t, Purge("done", 0));

        string[] refused =
        [
            "UPDATE tenant_purges SET purging=1 WHERE tenant_id='done'",
            "UPDATE tenant_purges SET purging=0 WHERE tenant_id='done'",
            "UPDATE tenant_purges SET reason='changed' WHERE tenant_id='running'",
            "UPDATE tenant_purges SET purging=0, reason='changed' WHERE tenant_id='running'",
            "UPDATE tenant_purges SET purging=0, reason=NULL WHERE tenant_id='running'",
            "UPDATE tenant_purges SET purging=0, actor='mallory' WHERE tenant_id='running'",
            "UPDATE tenant_purges SET purging=0, purged_at='2025-01-01T00:00:00.0000000+00:00' WHERE tenant_id='running'",
            "UPDATE tenant_purges SET purging=0, tenant_id='other' WHERE tenant_id='running'",
        ];
        foreach (var sql in refused) await RefusedWithAsync(PurgesAppendOnly, () => ExecAsync(t, sql));

        await ExecAsync(t, "UPDATE tenant_purges SET purging=0 WHERE tenant_id='running'");

        Assert.Equal(["done 0 ended olivia", "running 0 ended olivia"],
            await ColumnAsync(t, "SELECT tenant_id || ' ' || purging || ' ' || reason || ' ' || actor FROM tenant_purges ORDER BY tenant_id"));
        Assert.Equal(2L, await ScalarAsync(t, $"SELECT COUNT(*) FROM tenant_purges WHERE purged_at = {T}"));
    }

    // ---------------------------------------------------------------- tenants

    [EngineFact]   // mutation: the trigger dropped, or a do-nothing body
    public async Task tenants_no_purged_id__refuses_a_purged_id()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Purge("gone", 0));

        await RefusedWithAsync(PurgedId, () => ExecAsync(t, Tenant("gone")));
        await ExecAsync(t, Tenant("other"));

        Assert.Equal(["other"], await ColumnAsync(t, "SELECT id FROM tenants"));
    }

    [EngineFact]   // mutations: the trigger dropped; OLD.id looked up for NEW.id
    public async Task tenants_no_rename_to_purged_id__refuses_it_and_allows_another_id()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Tenant("acme"));
        await ExecAsync(t, Purge("gone", 0));

        await RefusedWithAsync(PurgedId, () => ExecAsync(t, "UPDATE tenants SET id='gone' WHERE id='acme'"));
        await ExecAsync(t, "UPDATE tenants SET id='acme2' WHERE id='acme'");

        Assert.Equal(["acme2"], await ColumnAsync(t, "SELECT id FROM tenants"));
    }

    // ---------------------------------------------------------------- decision_records

    [EngineFact]   // mutations: RETURN NEW (the late row kept); RAISE (the whole batch refused)
    public async Task decision_records_purged_tenant__drops_the_late_row_and_keeps_the_rest_of_the_statement()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, Purge("gone", 0));

        await ExecAsync(t, Decisions(DecisionRow("acme", "a"), DecisionRow("gone", "late"), DecisionRow("acme", "b")));

        Assert.Equal(["acme a", "acme b"], await ColumnAsync(t, "SELECT tenant_id || ' ' || package_id FROM decision_records ORDER BY package_id"));
    }

    // ---------------------------------------------------------------- CHECK constraints

    /// <summary>
    /// One CHECK constraint's probe: an INSERT into <see cref="Table"/> with <c>{v}</c> for the values under test, the rows it must
    /// accept and the rows it must refuse, at the boundary (a <c>&gt;= 0</c> accepts 0 and refuses -1, so <c>&gt;</c> for <c>&gt;=</c>
    /// fails) and one for each branch. In the INSERT and the values: <c>{t}</c> is a tenant made for the row, <c>{n}</c> a number of the
    /// row's own, <c>{guid}</c> a lower-case 36-character id of its own (<c>{GUID}</c> upper-case, <c>{guid35}</c> one character short)
    /// and <c>{blob}</c> a blob. Each refused row must be refused by this very CHECK (#298 review L2), named by <see cref="Name"/>
    /// where the engine names constraints (PostgreSQL) and by <see cref="Check"/>, its text, where it does not (SQLite): a row another
    /// CHECK on the table also refuses would otherwise pass with this one weakened.
    /// </summary>
    public sealed record CheckProbe(string Table, string Name, string Check, string Insert, string[] Accepted, string[] Refused);

    private const string NewTenant = "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at";

    /// <summary>
    /// A probe for every CHECK in the schema, by table and body as SQLite's CREATE TABLE text writes it (SchemaRulesTests checks there is
    /// exactly one per CHECK).
    /// </summary>
    public static readonly CheckProbe[] CheckProbes =
    [
        new("tenants", "tenants_cooldown_days_check", "cooldown_days >= 0", NewTenant + $") VALUES('p{{n}}', 'n', 'u', '/', 'f', {{v}}, {T})", ["0"], ["-1"]),
        new("tenants", "tenants_origin_check", "origin IN ('operator','self')", NewTenant + $", origin) VALUES('p{{n}}', 'n', 'u', '/', 'f', 0, {T}, {{v}})",
            ["'operator'", "'self'"], ["'Self'", "'customer'"]),
        new("tenants", "tenants_feed_setup_check", "feed_setup IN ('operator','customer')", NewTenant + $", feed_setup) VALUES('p{{n}}', 'n', 'u', '/', 'f', 0, {T}, {{v}})",
            ["'operator'", "'customer'"], ["'Customer'", "'self'"]),
        new("package_overrides", "package_overrides_cooldown_days_check", "cooldown_days >= 0", "INSERT INTO package_overrides(tenant_id, package_id, cooldown_days) VALUES('{t}', 'pkg', {v})", ["0"], ["-1"]),
        new("waivers", "waivers_source_check", "source IN ('operator','customer')",
            $"INSERT INTO waivers(id, tenant_id, package_id, version, expires_at, reason, created_by, created_at, source) VALUES('w{{n}}', '{{t}}', 'p', '1.0.0', {T}, 'r', 'op', {T}, {{v}})",
            ["'operator'", "'customer'"], ["'Operator'", "'self'"]),
        new("tokens", "tokens_kind_check", "kind IN ('developer','automation')",
            $"INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at, kind) VALUES('k{{n}}', '{{t}}', 'h{{n}}', 'pm_', 'l', {T}, {{v}})",
            ["'developer'", "'automation'"], ["'Developer'", "'member'"]),
        new("tokens", "tokens_issued_via_check", "issued_via IN ('operator','self')",
            $"INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at, issued_via) VALUES('k{{n}}', '{{t}}', 'h{{n}}', 'pm_', 'l', {T}, {{v}})",
            ["'operator'", "'self'"], ["'Self'", "'customer'"]),
        new("upstream_identities", "upstream_identities_kind_check", "kind IN ('own','shared')",
            $"INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('{{t}}', {{v}}, {T})",
            ["'own', 'dir{n}', 'cid', {blob}", "'shared', 'dir{n}', NULL, NULL"], ["'Own', 'dir{n}', 'cid', {blob}", "'Shared', 'dir{n}', NULL, NULL"]),
        new("upstream_identities", "upstream_identities_own_or_shared_check",
            "(kind = 'own' AND client_id IS NOT NULL AND cert_encrypted IS NOT NULL) OR (kind = 'shared' AND client_id IS NULL AND cert_encrypted IS NULL)",
            $"INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('{{t}}', {{v}}, {T})",
            ["'own', 'dir{n}', 'cid', {blob}", "'shared', 'dir{n}', NULL, NULL"],
            ["'own', 'dir{n}', NULL, {blob}", "'own', 'dir{n}', 'cid', NULL", "'shared', 'dir{n}', 'cid', NULL", "'shared', 'dir{n}', NULL, {blob}"]),
        new("shared_app_credentials", "shared_app_credentials_slot_check", "slot IN ('active','next')",
            $"INSERT INTO shared_app_credentials(slot, client_id, cert_encrypted, updated_at) VALUES({{v}}, 'cid', {{blob}}, {T})",
            ["'active'", "'next'"], ["'Active'", "'previous'"]),
        new("feed_checks", "feed_checks_outcome_check", "outcome IN ('passed','failed','incomplete')",
            "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results) " +
            $"VALUES('{{t}}', {T}, 'tid', 'oid', 'n', 'dir', 'f', 'cid', {{v}}, '[]')",
            ["'passed'", "'failed'", "'incomplete'"], ["'Passed'", "'skipped'"]),
        new("feed_checks", "feed_checks_kind_check", "kind IN ('settings','developer')",
            "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, kind) " +
            $"VALUES('{{t}}', {T}, 'tid', 'oid', 'n', 'dir', 'f', 'cid', 'passed', '[]', {{v}})",
            ["'settings'", "'developer'"], ["'Settings'", "'automation'"]),
        new("shared_app_verifications", "shared_app_verifications_verified_via_check", "verified_via IN ('operator','wizard')",
            "INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by, verified_via) " +
            $"VALUES('{{t}}', 'dir', 'f', 'cid', {T}, 'op', {{v}})",
            ["'operator'", "'wizard'"], ["'Wizard'", "'customer'"]),
        new("sign_in_directories", "sign_in_directories_azure_tenant_id_check", "azure_tenant_id = lower(azure_tenant_id) AND length(azure_tenant_id) = 36",
            $"INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES({{v}}, '{{t}}', {T}, 'op')",
            ["'{guid}'"], ["'{GUID}'", "'{guid35}'", "'{guid}a'"]),
        new("members", "members_azure_tenant_id_check", "azure_tenant_id = lower(azure_tenant_id) AND length(azure_tenant_id) = 36",
            $"INSERT INTO members(azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at) VALUES({{v}}, '{{t}}', 'n', {T}, {T})",
            ["'{guid}', '{guid}'"], ["'{GUID}', '{guid}'", "'{guid35}', '{guid}'", "'{guid}a', '{guid}'"]),
        new("members", "members_object_id_check", "object_id = lower(object_id) AND length(object_id) = 36",
            $"INSERT INTO members(azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at) VALUES({{v}}, '{{t}}', 'n', {T}, {T})",
            ["'{guid}', '{guid}'"], ["'{guid}', '{GUID}'", "'{guid}', '{guid35}'", "'{guid}', '{guid}a'"]),
        new("auto_join_settings", "auto_join_settings_enabled_check", "enabled IN (0,1)",
            $"INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('{{t}}', {{v}}, {T}, 'op')", ["0", "1"], ["2", "-1"]),
        new("tenant_purges", "tenant_purges_purging_check", "purging IN (0,1)",
            $"INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor, purging) VALUES('gone{{n}}', {T}, 'r', 'a', {{v}})", ["0", "1"], ["2", "-1"]),
        new("decision_records", "decision_records_seen_count_check", "seen_count >= 1",
            "INSERT INTO decision_records(tenant_id, ecosystem, package_id, version, decision, reason, policy_revision, first_seen_at, last_seen_at, seen_count) " +
            $"VALUES('{{t}}', 'nuget', 'p', '1.0.0', 'allowed', 'r', 1, {T}, {T}, {{v}})", ["1"], ["0"]),
    ];

    // mutations (N1): an operator changed (>= to >, = 36 to >= 36); a CHECK dropped on one engine; a branch of upstream_identities' dropped
    [EngineFact]
    public async Task Every_check_refuses_its_probe_and_accepts_its_row()
    {
        await using var t = await CreateDbAsync();
        var n = 0;
        foreach (var probe in CheckProbes)
        {
            foreach (var (value, accepted) in probe.Accepted.Select(v => (v, true)).Concat(probe.Refused.Select(v => (v, false))))
            {
                n++;
                var tenant = $"t{n}";
                await ExecAsync(t, Tenant(tenant));
                var guid = $"abcdef00-0000-0000-0000-{n:D12}";
                var sql = probe.Insert.Replace("{v}", value, StringComparison.Ordinal)
                    .Replace("{t}", tenant, StringComparison.Ordinal).Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{guid35}", guid[..35], StringComparison.Ordinal).Replace("{guid}", guid, StringComparison.Ordinal)
                    .Replace("{GUID}", guid.ToUpperInvariant(), StringComparison.Ordinal).Replace("{blob}", Engine.BlobSql("01"), StringComparison.Ordinal);
                var what = $"{probe.Table} CHECK({probe.Check}) with {value}";
                if (accepted)
                {
                    try { await ExecAsync(t, sql); }
                    catch (DbException e) { Assert.Fail($"{what} was refused, but must be accepted: {e.Message}"); }
                }
                else
                {
                    var refusal = await RefusedAsync(() => ExecAsync(t, sql));
                    Assert.True(Engine.IsCheckViolation(refusal, probe.Name, probe.Check), $"{what} was refused, but not by this CHECK: {refusal.Message}");
                }
            }
        }
    }
}
