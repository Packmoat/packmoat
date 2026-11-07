using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="SharedAppIdentityContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class SharedAppIdentityTests : SharedAppIdentityContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    /// <summary>A database as it was before this change (schema v5), with one own-app identity written the way the old code wrote it.</summary>
    private static async Task<(TestDb Db, byte[] Blob)> V5WithIdentityAsync(string tenantId = "e2e-test")
    {
        var t = new TestDb();
        await t.Db.MigrateAsync(5);
        await t.AddLegacyTenantAsync(tenantId);
        var blob = t.Protector.Protect("-----BEGIN PRIVATE KEY-----\nLIVE-KEY\n-----END PRIVATE KEY-----", tenantId);
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO upstream_identities(tenant_id, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@id,'az-dir','client-app',@b,'2026-01-02T03:04:05.0000000+00:00')";
        cmd.Parameters.AddWithValue("@id", tenantId);
        cmd.Parameters.AddWithValue("@b", blob);
        await cmd.ExecuteNonQueryAsync();
        return (t, blob);
    }

    private static async Task<(string Kind, string Az, string? Client, byte[]? Blob, string Updated)> RawRowAsync(TestDb t, string tenantId)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind, azure_tenant_id, client_id, cert_encrypted, updated_at FROM upstream_identities WHERE tenant_id=@id";
        cmd.Parameters.AddWithValue("@id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : (byte[])r[3], r.GetString(4));
    }

    [Fact]
    public async Task Migration_six_turns_every_existing_identity_into_an_own_app_row_unchanged()
    {
        var (t, blob) = await V5WithIdentityAsync();
        await using var _ = t;

        await t.Db.MigrateAsync(TestTracks.Modules);
        await t.Db.MigrateAsync(TestTracks.Modules);   // idempotent

        var row = await RawRowAsync(t, "e2e-test");
        Assert.Equal(("own", "az-dir", "client-app", "2026-01-02T03:04:05.0000000+00:00"), (row.Kind, row.Az, row.Client, row.Updated));
        Assert.Equal(blob, row.Blob);   // the very same encrypted bytes
        var identity = await t.Tenants.GetUpstreamIdentityAsync("e2e-test", default);
        Assert.Equal(("az-dir", "client-app"), (identity!.AzureTenantId, identity.ClientId));
        Assert.Contains("LIVE-KEY", identity.CertificatePem);
        Assert.Equal(new UpstreamBinding(IdentityKind.Own, "az-dir", "client-app"), await t.Tenants.GetUpstreamBindingAsync("e2e-test", default));
        Assert.Null(await new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector).GetAsync(SharedAppSlot.Active, default));
    }

    [Fact]
    public async Task A_v5_backup_restores_and_then_upgrades_with_its_identity_intact()
    {
        var (t, _) = await V5WithIdentityAsync("acme");
        await using var __ = t;
        var backup = Path.Combine(t.Dir, "b", "v5.db");
        var made = await SqliteBackup.BackupAsync(t.Db, backup, overwrite: false);
        Assert.Equal(5, made.SchemaVersion);

        var target = Path.Combine(t.Dir, "restored", "packmoat.db");
        await SqliteBackup.RestoreAsync(backup, target, replaceExisting: false, Now);
        var restored = new SqliteDatabase(target);
        await restored.MigrateAsync(TestTracks.Modules);
        var store = new SqliteTenantStore(restored, t.Clock, t.Protector, TestLifecycle.Participants);
        Assert.Contains("LIVE-KEY", (await store.GetUpstreamIdentityAsync("acme", default))!.CertificatePem);
        Assert.Equal(SqliteDatabase.LatestVersion, (await SqliteBackup.VerifyAsync(target)).SchemaVersion);
    }

    [Theory]
    [InlineData("INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('acme','shared','d','client',NULL,'t')")]
    [InlineData("INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('acme','shared','d',NULL,x'01','t')")]
    [InlineData("INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('acme','own','d',NULL,x'01','t')")]
    [InlineData("INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('acme','own','d','c',NULL,'t')")]
    [InlineData("INSERT INTO upstream_identities(tenant_id, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('acme','d',NULL,NULL,'t')")]
    [InlineData("INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('acme','other','d',NULL,NULL,'t')")]
    [InlineData("INSERT INTO shared_app_credentials(slot, client_id, cert_encrypted, updated_at) VALUES('old','c',x'01','t')")]
    public async Task The_schema_refuses_rows_that_mix_the_modes(string sql)
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var ex = await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Binding_to_the_shared_app_removes_the_own_app_key_and_setting_own_again_works()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c", "OWN-KEY"), default);

        await t.Tenants.BindSharedIdentityAsync("acme", Dir, default);

        var row = await RawRowAsync(t, "acme");
        Assert.Equal(("shared", Dir, (string?)null, (byte[]?)null), (row.Kind, row.Az, row.Client, row.Blob));
        Assert.Null(await t.Tenants.GetUpstreamIdentityAsync("acme", default));
        Assert.Equal(new UpstreamBinding(IdentityKind.Shared, Dir, null), await t.Tenants.GetUpstreamBindingAsync("acme", default));
        Assert.DoesNotContain("OWN-KEY", System.Text.Encoding.UTF8.GetString(await t.RawBytesAsync()));

        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c2", "OWN-KEY-2"), default);
        Assert.Equal(IdentityKind.Own, (await t.Tenants.GetUpstreamBindingAsync("acme", default))!.Kind);
    }

    [Fact]
    public async Task Shared_credentials_round_trip_encrypted_and_promote_moves_next_to_active()
    {
        await using var t = await TestDb.CreateAsync();
        var store = new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector);
        await store.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "SHARED-KEY-ONE"), default);
        await store.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId, "SHARED-KEY-TWO"), default);

        Assert.Equal("SHARED-KEY-ONE", (await store.GetAsync(SharedAppSlot.Active, default))!.CertificatePem);
        Assert.DoesNotContain("SHARED-KEY", System.Text.Encoding.UTF8.GetString(await t.RawBytesAsync()));

        Assert.True(await store.PromoteAsync(_ => { }, default));
        Assert.Equal("SHARED-KEY-TWO", (await store.GetAsync(SharedAppSlot.Active, default))!.CertificatePem);
        Assert.Null(await store.GetAsync(SharedAppSlot.Next, default));
        Assert.False(await store.PromoteAsync(_ => { }, default));
    }

    private static async Task<byte[]> BlobAsync(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (byte[])(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(TestDb t, string sql, byte[] blob)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@b", blob);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_blob_copied_between_slots_does_not_decrypt()
    {
        await using var t = await TestDb.CreateAsync();
        var store = new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector);
        await store.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "ACTIVE"), default);
        await store.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId, "NEXT"), default);

        var next = await BlobAsync(t, "SELECT cert_encrypted FROM shared_app_credentials WHERE slot='next'");
        await ExecAsync(t, "UPDATE shared_app_credentials SET cert_encrypted=@b WHERE slot='active'", next);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAsync(SharedAppSlot.Active, default));
    }

    [Fact]
    public async Task A_blob_copied_between_a_tenant_and_the_shared_app_does_not_decrypt_either_way()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        var store = new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector);
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c", "TENANT"), default);
        await store.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "SHARED"), default);

        var tenantBlob = await BlobAsync(t, "SELECT cert_encrypted FROM upstream_identities WHERE tenant_id='acme'");
        var sharedBlob = await BlobAsync(t, "SELECT cert_encrypted FROM shared_app_credentials WHERE slot='active'");
        await ExecAsync(t, "UPDATE shared_app_credentials SET cert_encrypted=@b WHERE slot='active'", tenantBlob);
        await ExecAsync(t, "UPDATE upstream_identities SET cert_encrypted=@b WHERE tenant_id='acme'", sharedBlob);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAsync(SharedAppSlot.Active, default));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => t.Tenants.GetUpstreamIdentityAsync("acme", default));
    }

    [Fact]
    public void The_shared_contexts_contain_a_character_no_tenant_id_may_hold()
    {
        foreach (var slot in new[] { SharedAppSlot.Active, SharedAppSlot.Next })
            Assert.False(AdminCommands.IsValidTenantId(SqliteSharedAppCredentialStore.Context(slot)));
        Assert.Equal("shared-app:active", SqliteSharedAppCredentialStore.Context(SharedAppSlot.Active));
        Assert.Equal("shared-app:next", SqliteSharedAppCredentialStore.Context(SharedAppSlot.Next));
        Assert.ThrowsAny<CryptographicException>(() => SqliteTenantStore.TenantContext("shared-app:active"));
    }

    private sealed class SharedWorld : IAsyncDisposable
    {
        public TestDb Db { get; private init; } = null!;
        public SqliteSharedAppCredentialStore Shared { get; private init; } = null!;
        public EntraCertificateCredentialSource Source { get; private init; } = null!;
        public List<AppSignIn> Built { get; } = new();
        public CertificateTools.Generated One { get; } = CertificateTools.Generate("one", 365, Now);
        public CertificateTools.Generated Two { get; } = CertificateTools.Generate("two", 365, Now);

        /// <param name="wrap">Wraps the shared store the credential source reads, so a test can make those reads fail.</param>
        public static async Task<SharedWorld> StartAsync(Func<ISharedAppCredentialStore, ISharedAppCredentialStore>? wrap = null)
        {
            var db = await TestDb.CreateAsync();
            await db.AddTenantAsync();   // feed under https://pkgs.dev.azure.com/example-org/
            var shared = new SqliteSharedAppCredentialStore(db.Db, db.Clock, db.Protector);
            SharedWorld? w = null;
            w = new SharedWorld
            {
                Db = db, Shared = shared,
                Source = new EntraCertificateCredentialSource(db.Tenants, db.Clock, wrap is null ? shared : wrap(shared), createCredential: s => { w!.Built.Add(s); return new FakeTokenCredential(s); }),
            };
            await shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, w.One.CombinedPem), default);
            await db.Tenants.BindSharedIdentityAsync("acme", Dir, default);
            return w;
        }

        public Task<Tenant> TenantAsync() => Db.Tenants.GetAsync("acme", default)!;
        /// <summary>Writes a record straight into the table, so a stale one (for another directory, feed or app) can be planted.</summary>
        public async Task VerifyAsync(string dir = Dir, string? feed = null, string client = AppId)
        {
            await using var c = await Db.Db.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) VALUES('acme',@a,@f,@c,'2026-10-10T12:00:00.0000000+00:00','op')
                ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@a, feed_index_url=@f, client_id=@c
                """;
            cmd.Parameters.AddWithValue("@a", dir);
            cmd.Parameters.AddWithValue("@f", feed ?? (await TenantAsync()).UpstreamFeedIndexUrl);
            cmd.Parameters.AddWithValue("@c", client);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<string> TokenAsync()
        {
            var c = await Source.GetAsync(await TenantAsync(), default);
            return System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(c.Parameter))["packmoat:".Length..];
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task A_shared_tenant_without_a_verification_gets_no_credential()
    {
        await using var w = await SharedWorld.StartAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
        Assert.Contains("onboard check", ex.Message);
        Assert.Empty(w.Built);
    }

    [Fact]
    public async Task A_verification_for_another_binding_gets_no_credential()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync(dir: Dir2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
        await w.VerifyAsync(feed: "https://pkgs.dev.azure.com/example-org/other/_packaging/g/nuget/v3/index.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
        await w.VerifyAsync(client: AppId2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
        Assert.Empty(w.Built);
    }

    [Fact]
    public async Task Rebinding_removes_the_verification()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        Assert.NotNull(await w.Shared.GetVerificationAsync("acme", default));
        await w.Db.Tenants.BindSharedIdentityAsync("acme", Dir, default);   // even to the same directory
        Assert.Null(await w.Shared.GetVerificationAsync("acme", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
    }

    [Fact]
    public async Task A_matching_verification_works_and_asks_the_customers_directory_with_our_app()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        Assert.Equal($"{Dir}/{AppId}/{w.One.Thumbprint}", await w.TokenAsync());
        Assert.Single(w.Built);
    }

    [Fact]
    public async Task After_a_promote_the_new_certificate_is_used_at_the_next_recheck()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());

        await w.Shared.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId, w.Two.CombinedPem), default);
        Assert.True(await w.Shared.PromoteAsync(_ => { }, default));

        w.Db.Clock.Advance(TimeSpan.FromSeconds(4));             // inside the re-check interval: still the old one, nothing rebuilt
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());
        Assert.Single(w.Built);

        w.Db.Clock.Advance(TimeSpan.FromSeconds(1));             // 5 s: re-checked, and the new certificate is used
        Assert.EndsWith(w.Two.Thumbprint, await w.TokenAsync());
        Assert.Equal(2, w.Built.Count);

        w.Db.Clock.Advance(TimeSpan.FromMinutes(3));             // re-checked again, nothing changed: the credential is reused, not rebuilt
        Assert.EndsWith(w.Two.Thumbprint, await w.TokenAsync());
        Assert.Equal(2, w.Built.Count);
    }

    [Fact]
    public async Task A_deleted_verification_stops_the_credential_within_five_seconds()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());   // read just now

        await using (var c = await w.Db.Db.OpenAsync())
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "DELETE FROM shared_app_verifications"; Assert.Equal(1, await cmd.ExecuteNonQueryAsync()); }

        w.Db.Clock.Advance(TimeSpan.FromSeconds(4.9));             // the window: at most 5 s after the last read
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());
        w.Db.Clock.Advance(TimeSpan.FromSeconds(0.1));             // 5 s after the last read: re-checked and refused
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
        Assert.Contains("onboard check", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());   // and it stays refused, with no clock change

        await w.VerifyAsync();                                     // a new pass is honoured at once, with the same credential
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());
        Assert.Single(w.Built);
    }

    [Fact]
    public async Task An_unchanged_verification_never_rebuilds_the_credential()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        for (var i = 0; i < 12; i++)                               // a minute of re-checks under traffic
        {
            Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());
            w.Db.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.Single(w.Built);                                    // one credential, so Entra's token cache in it is kept
    }

    /// <summary>The real shared store, except that every call throws a database error while <see cref="Broken"/> is set.</summary>
    private sealed class FlakySharedStore : ISharedAppCredentialStore
    {
        private readonly ISharedAppCredentialStore _inner;
        public FlakySharedStore(ISharedAppCredentialStore inner) => _inner = inner;
        public bool Broken { get; set; }

        private T Check<T>(T result) => Broken ? throw new SqliteException("database is locked", 5) : result;
        public Task SetAsync(SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct) => Check(_inner).SetAsync(slot, credential, ct);
        public Task<SharedAppCredential?> GetAsync(SharedAppSlot slot, CancellationToken ct) => Check(_inner).GetAsync(slot, ct);
        public Task<bool> PromoteAsync(Action<SharedAppCredential> validate, CancellationToken ct) => Check(_inner).PromoteAsync(validate, ct);
        public Task<bool> RecordVerificationAsync(SharedAppVerification verification, CancellationToken ct) => Check(_inner).RecordVerificationAsync(verification, ct);
        public Task<bool> DeleteVerificationAsync(SharedAppVerification verification, CancellationToken ct) => Check(_inner).DeleteVerificationAsync(verification, ct);
        public Task<SharedAppVerification?> GetVerificationAsync(string tenantId, CancellationToken ct) => Check(_inner).GetVerificationAsync(tenantId, ct);
    }

    [Fact]
    public async Task A_database_error_during_the_recheck_refuses_rather_than_serving_the_old_credential()
    {
        FlakySharedStore? flaky = null;
        await using var w = await SharedWorld.StartAsync(inner => flaky = new FlakySharedStore(inner));
        await w.VerifyAsync();
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());   // read just now

        flaky!.Broken = true;
        w.Db.Clock.Advance(TimeSpan.FromSeconds(4.9));             // inside the interval: no read, the cached credential is served
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());
        w.Db.Clock.Advance(TimeSpan.FromSeconds(0.1));             // 5 s: the re-check fails, and the old credential is not served
        await Assert.ThrowsAsync<SqliteException>(() => w.TokenAsync());
        await Assert.ThrowsAsync<SqliteException>(() => w.TokenAsync());   // and again, with no clock change

        flaky.Broken = false;
        Assert.EndsWith(w.One.Thumbprint, await w.TokenAsync());
        Assert.Single(w.Built);                                    // the same credential, never rebuilt
    }

    [Fact]
    public async Task A_new_directory_or_client_id_builds_a_new_credential()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        Assert.Equal($"{Dir}/{AppId}/{w.One.Thumbprint}", await w.TokenAsync());

        await w.Db.Tenants.BindSharedIdentityAsync("acme", Dir2, default);   // another directory, same app and PEM
        await w.VerifyAsync(dir: Dir2);
        w.Db.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal($"{Dir2}/{AppId}/{w.One.Thumbprint}", await w.TokenAsync());
        Assert.Equal(2, w.Built.Count);

        await w.Shared.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId2, w.One.CombinedPem), default);   // another client id, same PEM
        Assert.True(await w.Shared.PromoteAsync(_ => { }, default));
        await w.VerifyAsync(dir: Dir2, client: AppId2);
        w.Db.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal($"{Dir2}/{AppId2}/{w.One.Thumbprint}", await w.TokenAsync());
        Assert.Equal(3, w.Built.Count);

        w.Db.Clock.Advance(TimeSpan.FromSeconds(5));               // nothing changed: no rebuild
        Assert.Equal($"{Dir2}/{AppId2}/{w.One.Thumbprint}", await w.TokenAsync());
        Assert.Equal(3, w.Built.Count);
    }

    [Fact]
    public async Task A_refusal_is_not_cached_so_a_new_record_is_honoured_at_once()
    {
        await using var w = await SharedWorld.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());   // refused: nothing recorded yet
        await w.VerifyAsync();                                                       // no clock change at all
        Assert.Equal($"{Dir}/{AppId}/{w.One.Thumbprint}", await w.TokenAsync());
    }

    [Fact]
    public async Task A_record_is_written_only_while_directory_feed_and_client_id_are_still_what_was_checked()
    {
        await using var w = await SharedWorld.StartAsync();
        var feed = (await w.TenantAsync()).UpstreamFeedIndexUrl;
        SharedAppVerification Rec(string dir = Dir, string? f = null, string client = AppId) => new("acme", dir, f ?? feed, client, Now, "op");

        Assert.False(await w.Shared.RecordVerificationAsync(Rec(dir: Dir2), default));
        Assert.False(await w.Shared.RecordVerificationAsync(Rec(f: feed + "x"), default));
        Assert.False(await w.Shared.RecordVerificationAsync(Rec(client: AppId2), default));
        Assert.False(await w.Shared.RecordVerificationAsync(Rec() with { TenantId = "ghost" }, default));
        Assert.Null(await w.Shared.GetVerificationAsync("acme", default));

        Assert.True(await w.Shared.RecordVerificationAsync(Rec(dir: Dir.ToUpperInvariant(), client: AppId.ToUpperInvariant()), default));   // GUIDs ignore case
        Assert.Equal($"{Dir}/{AppId}/{w.One.Thumbprint}", await w.TokenAsync());
    }

    [Fact]
    public async Task No_record_is_written_for_a_tenant_that_is_back_in_own_mode_or_without_an_active_credential()
    {
        await using var w = await SharedWorld.StartAsync();
        var rec = new SharedAppVerification("acme", Dir, (await w.TenantAsync()).UpstreamFeedIndexUrl, AppId, Now, "op");
        await w.Db.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity(Dir, AppId, "pem"), default);
        Assert.False(await w.Shared.RecordVerificationAsync(rec, default));
        await w.Db.Tenants.BindSharedIdentityAsync("acme", Dir, default);
        await using (var c = await w.Db.Db.OpenAsync())
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "DELETE FROM shared_app_credentials"; await cmd.ExecuteNonQueryAsync(); }
        Assert.False(await w.Shared.RecordVerificationAsync(rec, default));
    }

    [Fact]
    public async Task Delete_removes_only_exactly_that_record()
    {
        await using var w = await SharedWorld.StartAsync();
        var rec = new SharedAppVerification("acme", Dir, (await w.TenantAsync()).UpstreamFeedIndexUrl, AppId, Now, "op");
        Assert.True(await w.Shared.RecordVerificationAsync(rec, default));
        Assert.False(await w.Shared.DeleteVerificationAsync(rec with { VerifiedAt = Now.AddSeconds(1) }, default));
        Assert.False(await w.Shared.DeleteVerificationAsync(rec with { VerifiedBy = "other" }, default));
        Assert.NotNull(await w.Shared.GetVerificationAsync("acme", default));
        Assert.True(await w.Shared.DeleteVerificationAsync(rec, default));
        Assert.Null(await w.Shared.GetVerificationAsync("acme", default));
    }

    [Fact]   // mutation: an unknown stored kind read as an own app again
    public async Task A_stored_identity_kind_this_build_does_not_know_fails_closed_in_both_readers()
    {
        await using var w = await SharedWorld.StartAsync();
        var rec = new SharedAppVerification("acme", Dir, (await w.TenantAsync()).UpstreamFeedIndexUrl, AppId, Now, "op");
        // Only the table's CHECK stops a third kind today; plant one past it, as a newer build (or a hand edit) could.
        await using (var c = await w.Db.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE upstream_identities SET kind='future' WHERE tenant_id='acme'";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var binding = await Assert.ThrowsAsync<InvalidDataException>(() => w.Db.Tenants.GetUpstreamBindingAsync("acme", default));
        Assert.Contains("upstream_identities.kind", binding.Message);
        Assert.DoesNotContain("future", binding.Message);
        var record = await Assert.ThrowsAsync<InvalidDataException>(() => w.Shared.RecordVerificationAsync(rec, default));
        Assert.DoesNotContain("future", record.Message);
        Assert.Null(await w.Shared.GetVerificationAsync("acme", default));
    }

    [Fact]
    public async Task A_lost_verification_stops_a_running_tenant_at_the_next_reload()
    {
        await using var w = await SharedWorld.StartAsync();
        await w.VerifyAsync();
        await w.TokenAsync();
        await w.Db.Tenants.BindSharedIdentityAsync("acme", Dir, default);
        w.Db.Clock.Advance(TimeSpan.FromMinutes(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.TokenAsync());
    }

    [Fact]
    public async Task Two_shared_rows_can_never_hold_the_same_directory_in_the_database()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("globex");
        await t.Tenants.BindSharedIdentityAsync("acme", Dir, default);
        var ex = await Assert.ThrowsAsync<SqliteException>(() => t.Tenants.BindSharedIdentityAsync("globex", Dir.ToUpperInvariant(), default));
        Assert.Contains("UNIQUE", ex.Message);
        await t.Tenants.SetUpstreamIdentityAsync("globex", new UpstreamIdentity(Dir, AppId, "pem"), default);   // an own row may
    }

    [Fact]
    public async Task A_database_from_a_newer_build_is_refused()
    {
        await using var t = await TestDb.CreateAsync();
        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO schema_migrations(version, applied_at) VALUES({SqliteDatabase.LatestVersion + 1}, 'later')";
            await cmd.ExecuteNonQueryAsync();
        }
        var ex = await Assert.ThrowsAsync<SchemaTooNewException>(() => t.Db.MigrateAsync(TestTracks.Modules));
        Assert.Contains("newer than this build", ex.Message);
    }

    [Fact]
    public async Task The_admin_tool_refuses_a_newer_database_with_a_clear_error_but_can_still_restore_an_older_backup()
    {
        await using var cli = new Cli();
        Assert.Equal(0, (await cli.Run("tenant", "list")).Code);
        var backup = Path.Combine(cli.Dir, "before.db");
        Assert.Equal(0, (await cli.Run("backup", backup)).Code);
        var db = new SqliteDatabase(Path.Combine(cli.Dir, "packmoat.db"));
        await using (var c = await db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO schema_migrations(version, applied_at) VALUES({SqliteDatabase.LatestVersion + 1}, 'later')";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var refused = await cli.Run("tenant", "list");
        Assert.Equal(1, refused.Code);
        Assert.Contains("newer than this build", refused.Err);

        SqliteConnection.ClearAllPools();   // the server is stopped before a restore; here, the tool's own pooled connections stand in for it
        var restored = await cli.Run("restore", backup, "--force");   // the rollback path: restore does not migrate, so it still works
        Assert.True(restored.Code == 0, restored.Err);
        var after = await cli.Run("tenant", "list");
        Assert.True(after.Code == 0, after.Err);
    }

    private sealed class Cli : IAsyncDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("packmoat-shared-cli-").FullName;
        private readonly CliEnvironment _env;
        public FakeClock Clock { get; } = new();

        public Cli()
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            _env = new CliEnvironment(Path.Combine(Dir, "packmoat.db"), () => AesGcmSecretProtector.FromBase64(key), Clock, "tester");
        }

        public async Task<(int Code, string Out, string Err)> Run(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var code = await AdminCli.RunAsync(args, o, e, _env, TestCli.Modules);
            return (code, o.ToString(), e.ToString());
        }

        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Dir, true); } catch { } return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Shared_new_cert_writes_a_private_bundle_with_mode_600_and_refuses_to_overwrite()
    {
        await using var cli = new Cli();
        var outDir = Path.Combine(cli.Dir, "shared-certs");
        var r = await cli.Run("identity", "shared", "new-cert", "--out-dir", outDir, "--days", "365");

        Assert.Equal(0, r.Code);
        var pem = Path.Combine(outDir, "packmoat-shared.pem");
        Assert.True(File.Exists(Path.Combine(outDir, "packmoat-shared.cer")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pem));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(outDir));
        }
        Assert.DoesNotContain("PRIVATE KEY", r.Out);

        var again = await cli.Run("identity", "shared", "new-cert", "--out-dir", outDir);
        Assert.Equal(1, again.Code);
        Assert.Contains("refusing to overwrite", again.Err);
    }

    [Fact]
    public async Task Shared_set_show_promote_and_expiry_never_print_key_material()
    {
        await using var cli = new Cli();
        var one = Path.Combine(cli.Dir, "one"); var two = Path.Combine(cli.Dir, "two");
        await cli.Run("identity", "shared", "new-cert", "--out-dir", one);
        await cli.Run("identity", "shared", "new-cert", "--out-dir", two, "--days", "60");
        var pemOne = Path.Combine(one, "packmoat-shared.pem"); var pemTwo = Path.Combine(two, "packmoat-shared.pem");

        Assert.Equal(0, (await cli.Run("identity", "shared", "set", "--client-id", AppId, "--pem-file", pemOne)).Code);
        Assert.Equal(0, (await cli.Run("identity", "shared", "set", "--client-id", AppId, "--pem-file", pemTwo, "--next")).Code);

        var show = await cli.Run("identity", "shared", "show");
        var keyBody = File.ReadAllText(pemOne).Split('\n')[^3];   // a line from inside the private key
        Assert.Contains("active\tclient " + AppId, show.Out);
        Assert.Contains("next\tclient " + AppId, show.Out);
        Assert.Contains("thumbprint", show.Out);
        Assert.DoesNotContain("PRIVATE", show.Out);
        Assert.DoesNotContain(keyBody, show.Out);

        var expiry = (await cli.Run("identity", "expiry")).Out;
        Assert.Contains("[shared app active]\texpires 2027-", expiry);
        Assert.Contains("[shared app next]", expiry);
        Assert.DoesNotContain("RENEW SOON", expiry);
        Assert.DoesNotContain("PRIVATE", expiry);
        Assert.Contains("[shared app next]\texpires", (await cli.Run("identity", "expiry", "--warn-days", "90")).Out.Split('\n').Single(l => l.Contains("RENEW SOON")));   // next has 60 days

        Assert.Equal(0, (await cli.Run("identity", "shared", "promote")).Code);
        var after = (await cli.Run("identity", "shared", "show")).Out;
        Assert.DoesNotContain("next\t", after);
        Assert.Equal(1, (await cli.Run("identity", "shared", "promote")).Code);
    }

    [Fact]
    public async Task Shared_set_refuses_a_bad_client_id_or_a_pem_without_a_key()
    {
        await using var cli = new Cli();
        var dir = Path.Combine(cli.Dir, "c");
        await cli.Run("identity", "shared", "new-cert", "--out-dir", dir);
        var pem = Path.Combine(dir, "packmoat-shared.pem");
        var certOnly = Path.Combine(cli.Dir, "cert-only.pem");
        File.WriteAllText(certOnly, File.ReadAllText(pem)[..(File.ReadAllText(pem).IndexOf("-----END CERTIFICATE-----") + "-----END CERTIFICATE-----".Length)]);

        var badId = await cli.Run("identity", "shared", "set", "--client-id", "packmoat", "--pem-file", pem);
        var noKey = await cli.Run("identity", "shared", "set", "--client-id", AppId, "--pem-file", certOnly);

        Assert.Equal(1, badId.Code); Assert.Contains("must be a GUID", badId.Err);
        Assert.Equal(1, noKey.Code); Assert.Contains("no private key", noKey.Err);
        Assert.Contains("no shared app credential set", (await cli.Run("identity", "shared", "show")).Out);
    }

    [Fact]
    public async Task Bind_shows_the_mode_in_list_and_show_and_guards_the_directory()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "acme-org/", "--feed", "https://pkgs.dev.azure.com/acme-org/p/i.json");
        await cli.Run("tenant", "create", "globex", "Globex", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "globex-org/", "--feed", "https://pkgs.dev.azure.com/globex-org/p/i.json");
        await cli.Run("tenant", "create", "bare", "Bare", "--upstream-base", "https://pkgs.dev.azure.com/", "--feed", "https://pkgs.dev.azure.com/p/i.json");

        Assert.Equal(0, (await cli.Run("identity", "bind", "acme", "--azure-tenant", Dir)).Code);
        var dup = await cli.Run("identity", "bind", "globex", "--azure-tenant", Dir);
        Assert.Equal(1, dup.Code); Assert.Contains("already bound to the shared app by tenant 'acme'", dup.Err);
        Assert.Equal(1, (await cli.Run("identity", "bind", "globex", "--azure-tenant", Dir, "--allow-shared-directory")).Code);   // never for two shared tenants
        Assert.Equal(0, (await cli.Run("identity", "bind", "globex", "--azure-tenant", "44444444-2222-4333-8444-555555555555")).Code);
        var bare = await cli.Run("identity", "bind", "bare", "--azure-tenant", "33333333-2222-4333-8444-555555555555");
        Assert.Equal(1, bare.Code); Assert.Contains("path prefix is required", bare.Err);
        Assert.Equal(1, (await cli.Run("identity", "bind", "acme", "--azure-tenant", "nope")).Code);

        var list = (await cli.Run("tenant", "list")).Out;
        Assert.Contains("acme\tAcme", list); Assert.Contains("identity=shared", list); Assert.Contains("identity=none", list);
        Assert.Contains($"shared (Packmoat's app) in directory {Dir}", (await cli.Run("tenant", "show", "acme")).Out);
        Assert.Contains("acme\tuses the shared app", (await cli.Run("identity", "expiry")).Out);
    }

    [Fact]
    public async Task Onboard_check_for_a_shared_tenant_needs_an_active_shared_credential()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "acme-org/", "--feed", "https://pkgs.dev.azure.com/acme-org/p/i.json");
        await cli.Run("identity", "bind", "acme", "--azure-tenant", Dir);
        var r = await cli.Run("onboard", "check", "acme", "--org", "acme-org", "--feed", "f");
        Assert.Equal(1, r.Code);
        Assert.Contains("no active shared credential", r.Err);
    }
}
