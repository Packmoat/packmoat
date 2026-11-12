namespace Packmoat.Infrastructure;

/// <summary>
/// The core's migrations on PostgreSQL: plain SQL text, with no driver type, beside the SQLite migrations in
/// <see cref="SqliteDatabase"/> (<see cref="CoreSchema"/> pairs them). The track has SQLite's versions, so a version means the same on
/// either engine, but no database ever ran the early ones on PostgreSQL: versions 1 to 22 are empty, and version 23 makes every core table
/// in its shape at SQLite's 23 at once (the baseline). Each version after it is SQLite's, one for one (24: rate_counters; 25: form_nonces; 26: feed_setup_scratch). A PostgreSQL
/// database recorded at 1 to 22 was made by no Packmoat build, and is refused.
/// <para>Types: text for text, every text column COLLATE "C": byte order, as SQLite's BINARY compares and sorts, and
/// a lower() that changes only ASCII letters, as SQLite's does; timestamptz for the times (<c>PostgresSchemaComparisonTests.TimeColumns</c> pins the
/// list); date for token_activity.day; bigint for every integer, the 0/1 flags too, with SQLite's CHECK; bytea for blobs; and
/// <c>bigint GENERATED ALWAYS AS IDENTITY</c> for AUTOINCREMENT. Columns are in SQLite's final order. WITHOUT ROWID has no PostgreSQL
/// meaning and is left out. Every key column is NOT NULL, as PostgreSQL makes a primary key's columns anyway.</para>
/// <para>The schema's rules (SchemaTriggerTests.Current) are plpgsql triggers, plus three TRUNCATE guards: see
/// <c>Rules</c>. PostgreSQL is selectable (<see cref="DatabaseEngines.RequireAvailable"/>), so this text is frozen by
/// hash as SQLite's is (MigrationHashTests, PostgresFrozen): never edit an applied version; add a new one.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresCoreMigrations
{
    /// <summary>The version that makes the whole core schema: the first with any statement. Every version before it is empty.</summary>
    public const int BaselineVersion = 23;

    private static string[][] Build()
    {
        var migrations = new string[BaselineVersion][];
        for (var i = 0; i < BaselineVersion - 1; i++) migrations[i] = [];
        migrations[BaselineVersion - 1] = Baseline;
        return [.. migrations, .. AfterBaseline];
    }

    /// <summary>
    /// The advisory-lock key the schema's rules take a tenant id's lock under (with <c>hashtext(id)</c> as the second key): "pmti" in
    /// ASCII. The two-key form, so it never meets the migrator's one-key lock (<c>PostgresDatabase.MigrationLockKey</c>).
    /// </summary>
    public const int TenantIdLockKey = 0x706D7469;

    // The schema's rules: SQLite's 11 triggers (SchemaTriggerTests.Current) as plpgsql BEFORE row triggers, with SQLite's
    // messages exactly, and three BEFORE TRUNCATE guards SQLite has no need of (it has no TRUNCATE). RAISE EXCEPTION is SQLSTATE P0001;
    // RAISE(IGNORE) is RETURN NULL (the row is dropped, the statement goes on); SQLite's IS is IS NOT DISTINCT FROM. The floors are in
    // hours from statement_timestamp() (SQLite's 'now' is fixed for a statement too): days would follow the session's time zone across a
    // daylight-saving change. Every function keeps, with SET search_path FROM CURRENT, the path the migrator runs this text under: exactly
    // the schema, pg_catalog, pg_temp (PostgresDatabase.MigrateAsync sets it for the migration's transaction). So pg_temp is searched last,
    // never first, and no caller's search_path or temporary table can stand in for tenant_purges, tenants or a function here.
    //
    // Under read committed, two transactions can each pass a rule the other's uncommitted row would break (a tenant made while its purge is
    // recorded; a late row while the purge runs). So the rules that look at tenant_purges or tenants for one tenant id take that id's
    // transaction-level advisory lock: the purge record's rule exclusively, the rest shared. The waiting side then reads, in a new snapshot,
    // what the other committed. A PostgreSQL purge store must take the exclusive lock (lock_tenant_id(id, true)) before it deletes anything.
    private static readonly string[] Rules =
    [
        """
        CREATE FUNCTION refuse() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = TG_ARGV[0];
        END
        $$
        """,
        $"""
        CREATE FUNCTION lock_tenant_id(id text, exclusive boolean) RETURNS void LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            IF exclusive THEN
                PERFORM pg_catalog.pg_advisory_xact_lock({TenantIdLockKey}, pg_catalog.hashtext(id));
            ELSE
                PERFORM pg_catalog.pg_advisory_xact_lock_shared({TenantIdLockKey}, pg_catalog.hashtext(id));
            END IF;
        END
        $$
        """,
        // Migration 19's: a delete while the tenant's purge runs, or of a row more than 90 days (2160 hours) old.
        """
        CREATE FUNCTION audit_no_delete() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            IF EXISTS (SELECT 1 FROM tenant_purges p WHERE p.tenant_id = OLD.tenant_id AND p.purging = 1)
               OR OLD.time < pg_catalog.statement_timestamp() - interval '2160 hours' THEN
                RETURN OLD;
            END IF;
            RAISE EXCEPTION 'audit log is append-only: a row is deleted only once it is more than 90 days old, or by its tenant''s purge';
        END
        $$
        """,
        // Migration 18's: a row more than 365 days (8760 hours) old.
        """
        CREATE FUNCTION admin_actions_no_delete() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            IF OLD.time < pg_catalog.statement_timestamp() - interval '8760 hours' THEN
                RETURN OLD;
            END IF;
            RAISE EXCEPTION 'admin_actions is append-only: a row is deleted only once it is more than 365 days old';
        END
        $$
        """,
        """
        CREATE FUNCTION tenant_purges_only_when_gone() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            PERFORM lock_tenant_id(NEW.tenant_id, true);
            IF EXISTS (SELECT 1 FROM tenants WHERE id = NEW.tenant_id) OR EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.tenant_id) THEN
                RAISE EXCEPTION 'a tenant is recorded as purged only once, and only once its rows are gone';
            END IF;
            RETURN NEW;
        END
        $$
        """,
        """
        CREATE FUNCTION tenant_purges_no_update() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            IF NOT (OLD.purging = 1 AND NEW.purging = 0 AND NEW.tenant_id IS NOT DISTINCT FROM OLD.tenant_id
                    AND NEW.purged_at IS NOT DISTINCT FROM OLD.purged_at AND NEW.reason IS NOT DISTINCT FROM OLD.reason
                    AND NEW.actor IS NOT DISTINCT FROM OLD.actor) THEN
                RAISE EXCEPTION 'tenant_purges is append-only';
            END IF;
            RETURN NEW;
        END
        $$
        """,
        """
        CREATE FUNCTION tenants_no_purged_id() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            PERFORM lock_tenant_id(NEW.id, false);
            IF EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.id) THEN
                RAISE EXCEPTION 'this tenant id was purged and is never used again';
            END IF;
            RETURN NEW;
        END
        $$
        """,
        """
        CREATE FUNCTION tenants_no_rename_to_purged_id() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            PERFORM lock_tenant_id(NEW.id, false);
            IF EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.id) THEN
                RAISE EXCEPTION 'this tenant id was purged and is never used again';
            END IF;
            RETURN NEW;
        END
        $$
        """,
        """
        CREATE FUNCTION audit_purged_tenant() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            PERFORM lock_tenant_id(NEW.tenant_id, false);
            IF EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.tenant_id) THEN
                RETURN NULL;
            END IF;
            RETURN NEW;
        END
        $$
        """,
        """
        CREATE FUNCTION decision_records_purged_tenant() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
        BEGIN
            PERFORM lock_tenant_id(NEW.tenant_id, false);
            IF EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.tenant_id) THEN
                RETURN NULL;
            END IF;
            RETURN NEW;
        END
        $$
        """,
        "CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse('audit log is append-only')",
        "CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_no_delete()",
        "CREATE TRIGGER audit_purged_tenant BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_purged_tenant()",
        "CREATE TRIGGER admin_actions_no_update BEFORE UPDATE ON admin_actions FOR EACH ROW EXECUTE FUNCTION refuse('admin_actions is append-only')",
        "CREATE TRIGGER admin_actions_no_delete BEFORE DELETE ON admin_actions FOR EACH ROW EXECUTE FUNCTION admin_actions_no_delete()",
        "CREATE TRIGGER tenant_purges_only_when_gone BEFORE INSERT ON tenant_purges FOR EACH ROW EXECUTE FUNCTION tenant_purges_only_when_gone()",
        "CREATE TRIGGER tenant_purges_no_delete BEFORE DELETE ON tenant_purges FOR EACH ROW EXECUTE FUNCTION refuse('tenant_purges is kept forever')",
        "CREATE TRIGGER tenant_purges_no_update BEFORE UPDATE ON tenant_purges FOR EACH ROW EXECUTE FUNCTION tenant_purges_no_update()",
        "CREATE TRIGGER tenants_no_purged_id BEFORE INSERT ON tenants FOR EACH ROW EXECUTE FUNCTION tenants_no_purged_id()",
        "CREATE TRIGGER tenants_no_rename_to_purged_id BEFORE UPDATE OF id ON tenants FOR EACH ROW EXECUTE FUNCTION tenants_no_rename_to_purged_id()",
        "CREATE TRIGGER decision_records_purged_tenant BEFORE INSERT ON decision_records FOR EACH ROW EXECUTE FUNCTION decision_records_purged_tenant()",
        // PostgreSQL only: TRUNCATE skips row triggers, so each append-only table refuses it outright.
        "CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION refuse('audit log is append-only')",
        "CREATE TRIGGER admin_actions_no_truncate BEFORE TRUNCATE ON admin_actions FOR EACH STATEMENT EXECUTE FUNCTION refuse('admin_actions is append-only')",
        "CREATE TRIGGER tenant_purges_no_truncate BEFORE TRUNCATE ON tenant_purges FOR EACH STATEMENT EXECUTE FUNCTION refuse('tenant_purges is kept forever')",
    ];

    // In an order every foreign key can be made in: a table before any table that refers to it. Then the rules (above), which need the tables.
    private static readonly string[] Baseline =
    [
        """
        CREATE TABLE tenants(
            id text COLLATE "C" NOT NULL PRIMARY KEY, name text COLLATE "C" NOT NULL, upstream_base text COLLATE "C" NOT NULL, allowed_path_prefix text COLLATE "C" NOT NULL,
            feed_index_url text COLLATE "C" NOT NULL, cooldown_days bigint NOT NULL CHECK(cooldown_days >= 0),
            policy_revision bigint NOT NULL DEFAULT 1, active bigint NOT NULL DEFAULT 1, created_at timestamptz NOT NULL,
            origin text COLLATE "C" NOT NULL DEFAULT 'operator' CHECK(origin IN ('operator','self')),
            created_by_oid text COLLATE "C" NULL,
            feed_setup text COLLATE "C" NOT NULL DEFAULT 'operator' CHECK(feed_setup IN ('operator','customer')))
        """,
        """
        CREATE TABLE internal_prefixes(
            tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id), prefix text COLLATE "C" NOT NULL, PRIMARY KEY(tenant_id, prefix))
        """,
        """
        CREATE TABLE package_overrides(
            tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id), package_id text COLLATE "C" NOT NULL,
            cooldown_days bigint NOT NULL CHECK(cooldown_days >= 0), PRIMARY KEY(tenant_id, package_id))
        """,
        """
        CREATE TABLE waivers(
            id text COLLATE "C" NOT NULL PRIMARY KEY, tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id), package_id text COLLATE "C" NOT NULL, version text COLLATE "C" NOT NULL,
            expires_at timestamptz NOT NULL, reason text COLLATE "C" NOT NULL, created_by text COLLATE "C" NOT NULL, created_at timestamptz NOT NULL, revoked_at timestamptz NULL,
            revoked_by text COLLATE "C" NULL,
            source text COLLATE "C" NOT NULL DEFAULT 'operator' CHECK(source IN ('operator','customer')))
        """,
        """
        CREATE TABLE tokens(
            id text COLLATE "C" NOT NULL PRIMARY KEY, tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id), token_hash text COLLATE "C" NOT NULL UNIQUE,
            display_prefix text COLLATE "C" NOT NULL, label text COLLATE "C" NOT NULL, created_at timestamptz NOT NULL, revoked_at timestamptz NULL, last_used_at timestamptz NULL,
            kind text COLLATE "C" NOT NULL DEFAULT 'developer' CHECK(kind IN ('developer','automation')),
            owner_tid text COLLATE "C" NULL, owner_oid text COLLATE "C" NULL, expires_at timestamptz NULL,
            issued_via text COLLATE "C" NOT NULL DEFAULT 'operator' CHECK(issued_via IN ('operator','self')),
            revoked_by text COLLATE "C" NULL)
        """,
        "CREATE INDEX ix_tokens_owner ON tokens(tenant_id, owner_tid, owner_oid)",
        """
        CREATE TABLE upstream_identities(
            tenant_id text COLLATE "C" NOT NULL PRIMARY KEY REFERENCES tenants(id),
            kind text COLLATE "C" NOT NULL DEFAULT 'own' CHECK(kind IN ('own','shared')),
            azure_tenant_id text COLLATE "C" NOT NULL, client_id text COLLATE "C" NULL, cert_encrypted bytea NULL, updated_at timestamptz NOT NULL,
            CONSTRAINT upstream_identities_own_or_shared_check CHECK((kind = 'own' AND client_id IS NOT NULL AND cert_encrypted IS NOT NULL)
               OR (kind = 'shared' AND client_id IS NULL AND cert_encrypted IS NULL)))
        """,
        // The table CHECK is named so it sorts after upstream_identities_kind_check: PostgreSQL checks a row's CHECKs in name order and
        // reports the first that fails, so a kind outside the list is reported as the kind CHECK's (SchemaRulesContract's probe relies on it).
        "CREATE UNIQUE INDEX ux_shared_directory ON upstream_identities(lower(azure_tenant_id)) WHERE kind = 'shared'",
        """
        CREATE TABLE audit_events(
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, time timestamptz NOT NULL, request_id text COLLATE "C" NOT NULL, tenant_id text COLLATE "C" NOT NULL,
            token_id text COLLATE "C" NOT NULL, kind text COLLATE "C" NOT NULL, package_id text COLLATE "C" NOT NULL, version text COLLATE "C" NOT NULL,
            decision text COLLATE "C" NOT NULL, reason text COLLATE "C" NOT NULL, waiver_id text COLLATE "C" NULL)
        """,
        "CREATE INDEX ix_audit_tenant_time ON audit_events(tenant_id, time)",
        "CREATE INDEX ix_audit_time ON audit_events(time)",
        "CREATE TABLE meta(key text COLLATE \"C\" NOT NULL PRIMARY KEY, value bytea NOT NULL)",
        "CREATE TABLE token_activity(token_id text COLLATE \"C\" NOT NULL REFERENCES tokens(id), day date NOT NULL, PRIMARY KEY(token_id, day))",
        """
        CREATE TABLE admin_actions(
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, time timestamptz NOT NULL, actor_tid text COLLATE "C" NOT NULL, actor_oid text COLLATE "C" NOT NULL,
            actor_name text COLLATE "C" NOT NULL, action text COLLATE "C" NOT NULL, tenant_id text COLLATE "C" NOT NULL, detail text COLLATE "C" NOT NULL,
            site text COLLATE "C" NOT NULL DEFAULT 'admin')
        """,
        "CREATE INDEX ix_admin_actions_time ON admin_actions(time)",
        """
        CREATE TABLE shared_app_credentials(
            slot text COLLATE "C" NOT NULL PRIMARY KEY CHECK(slot IN ('active','next')), client_id text COLLATE "C" NOT NULL, cert_encrypted bytea NOT NULL,
            updated_at timestamptz NOT NULL)
        """,
        """
        CREATE TABLE feed_checks(
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id),
            checked_at timestamptz NOT NULL,
            checked_by_tid text COLLATE "C" NOT NULL, checked_by_oid text COLLATE "C" NOT NULL, checked_by_name text COLLATE "C" NOT NULL,
            azure_tenant_id text COLLATE "C" NOT NULL, feed_index_url text COLLATE "C" NOT NULL, client_id text COLLATE "C" NOT NULL,
            outcome text COLLATE "C" NOT NULL CHECK(outcome IN ('passed','failed','incomplete')),
            results text COLLATE "C" NOT NULL,
            confirmed_admins text COLLATE "C" NOT NULL DEFAULT '[]',
            kind text COLLATE "C" NOT NULL DEFAULT 'settings' CHECK(kind IN ('settings','developer')),
            feed_names text COLLATE "C" NULL)
        """,
        "CREATE INDEX ix_feed_checks_tenant ON feed_checks(tenant_id, checked_at)",
        "CREATE INDEX ix_feed_checks_tenant_kind ON feed_checks(tenant_id, kind, checked_at)",
        """
        CREATE TABLE shared_app_verifications(
            tenant_id text COLLATE "C" NOT NULL PRIMARY KEY REFERENCES tenants(id), azure_tenant_id text COLLATE "C" NOT NULL, feed_index_url text COLLATE "C" NOT NULL,
            client_id text COLLATE "C" NOT NULL, verified_at timestamptz NOT NULL, verified_by text COLLATE "C" NOT NULL,
            verified_via text COLLATE "C" NOT NULL DEFAULT 'operator' CHECK(verified_via IN ('operator','wizard')),
            feed_check_id bigint NULL REFERENCES feed_checks(id),
            developer_check_id bigint NULL REFERENCES feed_checks(id))
        """,
        """
        CREATE TABLE sign_in_directories(
            azure_tenant_id text COLLATE "C" NOT NULL PRIMARY KEY CHECK(azure_tenant_id = lower(azure_tenant_id) AND length(azure_tenant_id) = 36),
            tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id), bound_at timestamptz NOT NULL, bound_by text COLLATE "C" NOT NULL)
        """,
        "CREATE INDEX ix_sign_in_directories_tenant ON sign_in_directories(tenant_id)",
        """
        CREATE TABLE members(
            azure_tenant_id text COLLATE "C" NOT NULL CHECK(azure_tenant_id = lower(azure_tenant_id) AND length(azure_tenant_id) = 36),
            object_id text COLLATE "C" NOT NULL CHECK(object_id = lower(object_id) AND length(object_id) = 36),
            tenant_id text COLLATE "C" NOT NULL REFERENCES tenants(id), display_name text COLLATE "C" NOT NULL, first_seen_at timestamptz NOT NULL,
            last_seen_at timestamptz NOT NULL,
            PRIMARY KEY(azure_tenant_id, object_id))
        """,
        "CREATE INDEX ix_members_tenant ON members(tenant_id)",
        """
        CREATE TABLE auto_join_settings(
            tenant_id text COLLATE "C" NOT NULL PRIMARY KEY REFERENCES tenants(id),
            enabled bigint NOT NULL CHECK(enabled IN (0,1)),
            changed_at timestamptz NOT NULL, changed_by text COLLATE "C" NOT NULL)
        """,
        """
        CREATE TABLE tenant_purges(
            tenant_id text COLLATE "C" NOT NULL PRIMARY KEY, purged_at timestamptz NOT NULL, reason text COLLATE "C" NOT NULL, actor text COLLATE "C" NOT NULL,
            purging bigint NOT NULL DEFAULT 0 CHECK(purging IN (0,1)))
        """,
        """
        CREATE TABLE decision_records(
            tenant_id text COLLATE "C" NOT NULL, ecosystem text COLLATE "C" NOT NULL, package_id text COLLATE "C" NOT NULL, version text COLLATE "C" NOT NULL,
            decision text COLLATE "C" NOT NULL, reason text COLLATE "C" NOT NULL, waiver_id text COLLATE "C" NULL, cooldown_days bigint NULL, published_at timestamptz NULL,
            policy_revision bigint NOT NULL, first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
            seen_count bigint NOT NULL CHECK(seen_count >= 1),
            PRIMARY KEY(tenant_id, ecosystem, package_id, version, decision, reason))
        """,
        "CREATE INDEX ix_decision_records_tenant_seen ON decision_records(tenant_id, last_seen_at)",
        "CREATE INDEX ix_decision_records_seen ON decision_records(last_seen_at)",
        "CREATE TABLE job_leases(job text COLLATE \"C\" NOT NULL PRIMARY KEY, holder text COLLATE \"C\" NOT NULL, expires_at timestamptz NOT NULL)",
        "CREATE TABLE cookie_keys(id text COLLATE \"C\" NOT NULL PRIMARY KEY, friendly_name text COLLATE \"C\" NOT NULL, created timestamptz NOT NULL, xml_encrypted bytea NOT NULL)",
        .. Rules,
    ];

    // The versions after the baseline, SQLite's 24 onwards, one element per migration (version = BaselineVersion + 1 + index). Never edit
    // one; add a new one.
    private static readonly string[][] AfterBaseline =
    [
        // 24: the shared rate counter (PostgresRateCounter), as SQLite's 24: one new, empty table and its index.
        [
            "CREATE TABLE rate_counters(scope text COLLATE \"C\" NOT NULL, subject text COLLATE \"C\" NOT NULL, started_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, hits bigint NOT NULL CHECK(hits >= 1), PRIMARY KEY(scope, subject))",
            "CREATE INDEX ix_rate_counters_ends_at ON rate_counters(ends_at)",
        ],
        // 25: the form nonces (PostgresFormNonceStore), as SQLite's 25: one new, empty table and its two indexes.
        [
            "CREATE TABLE form_nonces(scope text COLLATE \"C\" NOT NULL, nonce_hash text COLLATE \"C\" NOT NULL, subject text COLLATE \"C\" NOT NULL, expires_at timestamptz NOT NULL, PRIMARY KEY(scope, nonce_hash))",
            "CREATE INDEX ix_form_nonces_subject ON form_nonces(scope, subject, expires_at)",
            "CREATE INDEX ix_form_nonces_expires_at ON form_nonces(expires_at)",
        ],
        // 26: the feed setup scratch (PostgresFeedSetupScratch), as SQLite's 26: one new, empty table and its index.
        [
            "CREATE TABLE feed_setup_scratch(tenant_id text COLLATE \"C\" NOT NULL, person_oid text COLLATE \"C\" NOT NULL, kind text COLLATE \"C\" NOT NULL, payload text COLLATE \"C\" NOT NULL, expires_at timestamptz NOT NULL, PRIMARY KEY(tenant_id, person_oid, kind))",
            "CREATE INDEX ix_feed_setup_scratch_expires_at ON feed_setup_scratch(expires_at)",
        ],
    ];

    // After Rules, Baseline and AfterBaseline: static fields are initialised in the order they are written.
    /// <summary>One element per migration (version = index + 1), one statement per inner element.</summary>
    public static string[][] Statements { get; } = Build();
}
