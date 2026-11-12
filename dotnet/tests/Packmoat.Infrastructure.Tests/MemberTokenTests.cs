using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="MemberTokenContract"/> on PostgreSQL, each test on a database of its own: the members and the sign-in
/// directory with the token service, and the members' list sorted by bytes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresMemberTokenTests : MemberTokenContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="MemberTokenContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class MemberTokenTests : MemberTokenContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Migration_8_keeps_existing_tokens_working_with_no_owner_no_expiry_and_operator_issue()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(7);
        await t.AddLegacyTenantAsync("acme");
        var old = await t.Tokens.IssueAsync("acme", "build-agent", default, TokenKind.Automation);
        var oldDev = await t.Tokens.IssueAsync("acme", "dev", default);
        await t.Db.MigrateAsync(TestTracks.Modules);

        Assert.True(SqliteDatabase.LatestVersion >= 8);
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='members'"));
        Assert.NotNull(await t.Tokens.AuthenticateAsync(old.Plaintext, default));
        Assert.NotNull(await t.Tokens.AuthenticateAsync(oldDev.Plaintext, default));
        t.Clock.Advance(TimeSpan.FromDays(3650));   // no expiry: still good ten years on
        Assert.NotNull(await t.Tokens.AuthenticateAsync(oldDev.Plaintext, default));
        foreach (var info in await t.Tokens.ListAsync("acme", default))
        {
            Assert.Null(info.ExpiresAt);
            Assert.Null(info.OwnerTid);
            Assert.Null(info.OwnerOid);
            Assert.Null(info.RevokedBy);
            Assert.Equal(TokenIssuer.Operator, info.IssuedVia);
        }
        Assert.Equal(TokenKind.Automation, (await t.Tokens.ListAsync("acme", default)).Single(x => x.TokenId == old.TokenId).Kind);
        await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(t, "UPDATE tokens SET issued_via='other'"));
    }

    [Fact]
    public async Task A_self_issued_token_is_stored_with_owner_expiry_and_issued_via_and_never_its_plaintext()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        var members = new SqliteMemberStore(t.Db);
        await members.UpsertAsync(Dir, Ada, "acme", "Ada Lovelace", t.Clock.UtcNow, default);
        var issued = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        var info = Assert.Single(await t.Tokens.ListAsync("acme", default));
        Assert.Equal((Dir, Ada, "Ada Lovelace", TokenIssuer.Self), (info.OwnerTid, info.OwnerOid, info.OwnerName, info.IssuedVia));
        Assert.Equal(t.Clock.UtcNow.AddDays(90), info.ExpiresAt);
        Assert.False(Bytes.Contains(await t.RawBytesAsync(), issued.Plaintext));
    }

    [Fact]
    public async Task Members_and_token_owners_survive_backup_and_restore()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        await new SqliteMemberStore(t.Db).UpsertAsync(Dir, Ada, "acme", "Ada", t.Clock.UtcNow, default);
        var issued = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        var backup = Path.Combine(t.Dir, "copy.db");
        var v = await SqliteBackup.BackupAsync(t.Db, backup, false, default, t.Protector);
        Assert.True(v.Ok);
        Assert.Equal(SqliteDatabase.LatestVersion, v.SchemaVersion);
        var target = Path.Combine(t.Dir, "restored.db");
        await SqliteBackup.RestoreAsync(backup, target, false, t.Clock.UtcNow);
        var restored = new SqliteDatabase(target);
        await restored.MigrateAsync(TestTracks.Modules);
        var tokens = new SqliteTokenService(restored, new SqliteTenantStore(restored, t.Clock, t.Protector, TestLifecycle.Participants), t.Clock, TestDb.Gate(restored, t.Clock));
        var info = Assert.Single(await tokens.ListAsync("acme", default));
        Assert.Equal(("Ada", TokenIssuer.Self, t.Clock.UtcNow.AddDays(90)), (info.OwnerName, info.IssuedVia, info.ExpiresAt!.Value));
        Assert.NotNull(await tokens.AuthenticateAsync(issued.Plaintext, default));
    }

    [Fact]
    public async Task The_cli_token_list_shows_owner_expiry_and_issued_via()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-tok-").FullName;
        try
        {
            var clock = new FakeClock();
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            var env = new CliEnvironment(Path.Combine(dir, "packmoat.db"), () => AesGcmSecretProtector.FromBase64(key), clock, "founder");
            async Task<string> Run(params string[] args)
            {
                var o = new StringWriter(); var e = new StringWriter();
                Assert.Equal(0, await AdminCli.RunAsync(args, o, e, env, TestCli.Modules));
                return o.ToString();
            }
            await Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "acme-org/", "--feed", "https://pkgs.dev.azure.com/acme-org/p/_packaging/f/nuget/v3/index.json");
            await Run("token", "issue", "acme", "--label", "ci");
            var db = new SqliteDatabase(env.DbPath);
            var tokens = new SqliteTokenService(db, new SqliteTenantStore(db, clock, env.Protector(), TestLifecycle.Participants), clock, TestDb.Gate(db, clock));
            await new SqliteMemberStore(db).UpsertAsync(Dir, Ada, "acme", "Ada\tLovelace", clock.UtcNow, default);
            var self = (await tokens.IssueForMemberAsync("acme", Dir, Ada, "laptop", clock.UtcNow.AddDays(90), TokenKind.Developer, 5, default))!;
            var shortLived = (await tokens.IssueForMemberAsync("acme", Dir, Ada, "short", clock.UtcNow.AddDays(1), TokenKind.Developer, 5, default))!;
            clock.Advance(TimeSpan.FromDays(2));

            var lines = (await Run("token", "list", "acme")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            var op = lines.Single(l => l.Contains("\tci\t"));
            Assert.Contains("\tactive\t", op);
            Assert.Contains("\towner=none\texpires=never\tissued via=operator", op);
            var mine = lines.Single(l => l.StartsWith(self.TokenId));
            Assert.Contains($"\towner=Ada Lovelace ({Ada})\t", mine);   // the tab in the name is blanked
            Assert.Contains($"\texpires={clock.UtcNow.AddDays(88):u}\tissued via=self", mine);
            Assert.Contains("\texpired ", lines.Single(l => l.StartsWith(shortLived.TokenId)));
            Assert.DoesNotContain(self.Plaintext, string.Join("\n", lines));
        }
        finally
        {
            TempDir.Delete(dir, clearSqlitePools: true);
        }
    }
}
