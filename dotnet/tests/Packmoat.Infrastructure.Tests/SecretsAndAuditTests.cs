using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

[Collection("sqlite-pools")]
public class SecretProtectionTests
{
    private const string Pem = "-----BEGIN CERTIFICATE-----\nMIIFAKECERT\n-----END CERTIFICATE-----\n-----BEGIN PRIVATE KEY-----\nTOPSECRETKEYMATERIAL1234567890\n-----END PRIVATE KEY-----\n";

    [Fact]
    public async Task The_identity_round_trips_and_the_key_material_is_not_readable_on_disk()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("az-tenant", "client-id", Pem), default);

        var back = await t.Tenants.GetUpstreamIdentityAsync("acme", default);
        var disk = await t.RawBytesAsync();

        Assert.Equal(Pem, back!.CertificatePem);
        Assert.False(Bytes.Contains(disk, "TOPSECRETKEYMATERIAL"), "key material found on disk");
        Assert.False(Bytes.Contains(disk, "BEGIN PRIVATE KEY"), "PEM header found on disk");
    }

    [Fact]
    public async Task An_identity_can_be_replaced()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c1", Pem), default);
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c2", Pem + "x"), default);
        Assert.Equal("c2", (await t.Tenants.GetUpstreamIdentityAsync("acme", default))!.ClientId);
    }

    [Fact]
    public async Task A_blob_copied_onto_another_tenant_will_not_decrypt()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("globex");
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c", Pem), default);
        await t.Tenants.SetUpstreamIdentityAsync("globex", new UpstreamIdentity("a", "c", "something else"), default);

        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE upstream_identities SET cert_encrypted=(SELECT cert_encrypted FROM upstream_identities WHERE tenant_id='acme') WHERE tenant_id='globex'";
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => t.Tenants.GetUpstreamIdentityAsync("globex", default));
    }

    [Fact]
    public async Task A_tampered_blob_and_a_wrong_master_key_are_both_detected()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("a", "c", Pem), default);

        var wrongKey = new SqliteTenantStore(t.Db, t.Clock, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()), TestLifecycle.Participants);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrongKey.GetUpstreamIdentityAsync("acme", default));

        byte[] blob;
        await using (var c = await t.Db.OpenAsync())
        {
            await using var read = c.CreateCommand();
            read.CommandText = "SELECT cert_encrypted FROM upstream_identities WHERE tenant_id='acme'";
            blob = (byte[])(await read.ExecuteScalarAsync())!;
            blob[blob.Length / 2] ^= 0x01; // flip one bit of the ciphertext
            await using var write = c.CreateCommand();
            write.CommandText = "UPDATE upstream_identities SET cert_encrypted=@b WHERE tenant_id='acme'";
            write.Parameters.AddWithValue("@b", blob);
            await write.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAnyAsync<CryptographicException>(() => t.Tenants.GetUpstreamIdentityAsync("acme", default));
    }

    [Fact]
    public void The_master_key_must_be_exactly_32_bytes_and_the_text_form_hides_the_pem()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16]));
        var text = new UpstreamIdentity("a", "c", "TOPSECRET").ToString();
        Assert.DoesNotContain("TOPSECRET", text);
    }

    [Fact]
    public void Encrypting_the_same_secret_twice_gives_different_bytes()
    {
        var p = AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());
        Assert.NotEqual(p.Protect("x", "acme"), p.Protect("x", "acme"));
    }
}

/// <summary><see cref="AuditContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class AuditTests : AuditContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task An_audit_row_holds_no_token_or_secret_fields()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        var issued = await t.Tokens.IssueAsync("acme", "ci", default);
        await t.Audit.AppendAsync(Event(t.Clock.UtcNow) with { TokenId = issued.TokenId }, default);

        var disk = await t.RawBytesAsync();

        Assert.False(Bytes.Contains(disk, issued.Plaintext));
    }
}
