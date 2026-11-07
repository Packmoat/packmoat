using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

[Collection("sqlite-pools")]
public class AdminCliTests
{
    private sealed class Cli : IAsyncDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("packmoat-cli-").FullName;
        private readonly CliEnvironment _env;
        public FakeClock Clock { get; } = new();
        public string PemPath => Path.Combine(_dir, "sp.pem");
        public const string Pem = "-----BEGIN CERTIFICATE-----\nCLICERT\n-----END CERTIFICATE-----\n-----BEGIN PRIVATE KEY-----\nCLI-PRIVATE-KEY-MATERIAL\n-----END PRIVATE KEY-----\n";

        public Cli(string? operatorName = null)
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            _env = new CliEnvironment(Path.Combine(_dir, "packmoat.db"), () => AesGcmSecretProtector.FromBase64(key), Clock, operatorName);
            File.WriteAllText(PemPath, Pem);
        }

        public async Task<(int Code, string Out, string Err)> Run(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var code = await AdminCli.RunAsync(args, o, e, _env, TestCli.Modules);
            return (code, o.ToString(), e.ToString());
        }

        public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch { } return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task A_customer_can_be_onboarded_end_to_end_by_hand()
    {
        await using var cli = new Cli();

        Assert.Equal(0, (await cli.Run("tenant", "create", "acme", "Acme Ltd", "--upstream-base", "https://pkgs.dev.azure.com", "--path-prefix", "acme-org/", "--feed", "https://pkgs.dev.azure.com/acme-org/p/_packaging/pub/nuget/v3/index.json", "--cooldown", "14")).Code);
        Assert.Equal(0, (await cli.Run("prefix", "add", "acme", "Acme.")).Code);
        Assert.Equal(0, (await cli.Run("override", "set", "acme", "Moq", "3")).Code);
        Assert.Equal(0, (await cli.Run("identity", "set", "acme", "--azure-tenant", "aaaaaaaa-0000-4000-8000-000000000000", "--client-id", "bbbbbbbb-0000-4000-8000-000000000000", "--pem-file", cli.PemPath)).Code);
        var issue = await cli.Run("token", "issue", "acme", "--label", "build agents");
        Assert.Equal(0, issue.Code);
        Assert.Contains("only time the token is shown", issue.Out);

        var show = (await cli.Run("tenant", "show", "acme")).Out;
        Assert.Contains("cooldown: 14 days", show);
        Assert.Contains("Acme.", show);
        Assert.Contains("moq = 3 days", show);
        Assert.Contains("own app, client bbbbbbbb-0000-4000-8000-000000000000 in aaaaaaaa-0000-4000-8000-000000000000", show);
        Assert.Contains("https://pkgs.dev.azure.com/", show); // trailing slash was added
    }

    [Fact]
    public async Task The_token_is_printed_once_and_never_again_and_the_certificate_is_never_printed()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
        var plaintext = (await cli.Run("token", "issue", "acme")).Out.Split('\n').Single(l => l.StartsWith("token: ")).Split(' ')[1].Trim();
        await cli.Run("identity", "set", "acme", "--azure-tenant", "aaaaaaaa-0000-4000-8000-000000000000", "--client-id", "bbbbbbbb-0000-4000-8000-000000000000", "--pem-file", cli.PemPath);

        var everythingElse = string.Concat(
            (await cli.Run("token", "list", "acme")).Out, (await cli.Run("tenant", "show", "acme")).Out,
            (await cli.Run("tenant", "list")).Out, (await cli.Run("audit", "tail")).Out,
            (await cli.Run("identity", "set", "acme", "--azure-tenant", "aaaaaaaa-0000-4000-8000-000000000000", "--client-id", "bbbbbbbb-0000-4000-8000-000000000000", "--pem-file", cli.PemPath)).Out);

        Assert.StartsWith("pmt_", plaintext);
        Assert.DoesNotContain(plaintext, everythingElse);
        Assert.DoesNotContain("CLI-PRIVATE-KEY-MATERIAL", everythingElse);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", everythingElse);
    }

    [Fact]
    public async Task Waivers_always_expire_need_a_reason_and_can_be_revoked()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");

        Assert.Equal(1, (await cli.Run("waiver", "add", "acme", "moq", "4.21.0", "--reason", "x")).Code);              // no --days
        Assert.Equal(1, (await cli.Run("waiver", "add", "acme", "moq", "4.21.0", "--days", "2")).Code);              // no --reason
        var ok = await cli.Run("waiver", "add", "acme", "moq", "4.21.0", "--days", "2", "--reason", "hotfix", "--by", "olivia");
        Assert.Equal(0, ok.Code);
        var id = ok.Out.Split(' ')[1];
        Assert.Contains(id, (await cli.Run("tenant", "show", "acme")).Out);

        Assert.Equal("revoked", (await cli.Run("waiver", "revoke", id)).Out.Trim());
        Assert.DoesNotContain(id, (await cli.Run("tenant", "show", "acme")).Out);
    }

    [Fact]
    public async Task A_missing_os_user_name_does_not_break_commands_and_only_waiver_add_insists_on_a_name()
    {
        await using var cli = new Cli(operatorName: "");   // a container uid with no passwd entry
        Assert.Equal(0, (await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json")).Code);
        Assert.Equal(0, (await cli.Run("tenant", "list")).Code);
        Assert.Equal(0, (await cli.Run("token", "issue", "acme")).Code);
        Assert.Equal(0, (await cli.Run("audit", "tail")).Code);

        var refused = await cli.Run("waiver", "add", "acme", "moq", "4.21.0", "--days", "2", "--reason", "hotfix");
        Assert.Equal(1, refused.Code);
        Assert.Contains("--by", refused.Err);
        Assert.Equal(0, (await cli.Run("waiver", "add", "acme", "moq", "4.21.0", "--days", "2", "--reason", "hotfix", "--by", "olivia")).Code);
    }

    [Fact]
    public async Task Revoking_a_token_and_suspending_a_tenant_are_reported()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
        var id = (await cli.Run("token", "issue", "acme")).Out.Split('\n')[0].Split(' ').Last().Trim();

        Assert.Contains("revoked", (await cli.Run("token", "revoke", id)).Out);
        Assert.Contains("revoked", (await cli.Run("token", "list", "acme")).Out);
        Assert.Contains("no active token", (await cli.Run("token", "revoke", id)).Out);
        Assert.Equal(0, (await cli.Run("tenant", "suspend", "acme")).Code);
    }

    [Fact]
    public async Task Mistakes_give_a_short_message_and_exit_code_1_not_a_stack_trace()
    {
        await using var cli = new Cli();
        var missing = await cli.Run("tenant", "show", "nobody");
        var noFeed = await cli.Run("tenant", "create", "x", "X", "--upstream-base", "https://x/");
        var emptyPrefix = await cli.Run("prefix", "add", "nobody", "Acme.");

        Assert.Equal(1, missing.Code); Assert.Contains("No tenant 'nobody'", missing.Err); Assert.DoesNotContain("   at ", missing.Err);
        Assert.Equal(1, noFeed.Code); Assert.Contains("--feed is required", noFeed.Err);
        Assert.Equal(1, emptyPrefix.Code);
        Assert.Equal(2, (await cli.Run("nonsense")).Code);
        Assert.Equal(2, (await cli.Run()).Code);
    }

    [Fact]
    public async Task Keygen_makes_a_usable_master_key()
    {
        await using var cli = new Cli();
        var key = (await cli.Run("keygen")).Out.Trim();
        Assert.Equal(32, Convert.FromBase64String(key).Length);
        _ = AesGcmSecretProtector.FromBase64(key);
    }

    [Fact]
    public async Task Token_issue_takes_a_kind_and_token_list_shows_it()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com", "--path-prefix", "o/", "--feed", "https://pkgs.dev.azure.com/o/index.json");
        Assert.Equal(0, (await cli.Run("token", "issue", "acme", "--label", "ann")).Code);
        Assert.Equal(0, (await cli.Run("token", "issue", "acme", "--label", "agents", "--kind", "automation")).Code);

        var list = (await cli.Run("token", "list", "acme")).Out;
        Assert.Contains("ann", list);
        Assert.Contains("kind=developer", list);
        Assert.Contains("kind=automation", list);

        var bad = await cli.Run("token", "issue", "acme", "--kind", "robot");
        Assert.Equal(1, bad.Code);
        Assert.Contains("--kind", bad.Err);
    }

    [Fact]
    public async Task Token_active_validates_its_arguments()
    {
        await using var cli = new Cli();
        await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com", "--path-prefix", "o/", "--feed", "https://pkgs.dev.azure.com/o/index.json");

        var empty = await cli.Run("token", "active", "--tenant", "acme", "--days", "30");
        Assert.Equal(0, empty.Code);
        Assert.Contains("0 active developer token(s) in the last 30 days", empty.Out);

        Assert.Equal(1, (await cli.Run("token", "active", "--days", "30")).Code);                          // no --tenant
        Assert.Equal(1, (await cli.Run("token", "active", "--tenant", "acme", "--days", "0")).Code);
        Assert.Equal(1, (await cli.Run("token", "active", "--tenant", "acme", "--days", "x")).Code);
        Assert.Equal(0, (await cli.Run("token", "active", "--tenant", "acme", "--days", "365")).Code);
        var tooLong = await cli.Run("token", "active", "--tenant", "acme", "--days", "366");   // token_activity is kept 365 days (#136)
        Assert.Equal(1, tooLong.Code);
        Assert.Contains("from 1 to 365", tooLong.Err);
    }
}
