using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>An in-memory auto-join setting: no row means on. Records each write and how many audit rows existed at that moment.</summary>
internal sealed class FakeAutoJoinStore : IAutoJoinSettingStore
{
    private readonly FakeActionLog? _log;
    public FakeAutoJoinStore(FakeActionLog? log = null) => _log = log;
    public Dictionary<string, bool> Rows { get; } = new();
    public List<(string TenantId, bool Enabled, string By, int AuditRowsBefore)> Writes { get; } = new();
    public List<string> Reads { get; } = new();
    public bool FailWrites { get; set; }

    public Task<bool> IsOnAsync(string tenantId, CancellationToken ct)
    {
        Reads.Add(tenantId);
        return Task.FromResult(!Rows.TryGetValue(tenantId, out var on) || on);
    }

    public Task SetAsync(string tenantId, bool enabled, DateTimeOffset at, string changedBy, CancellationToken ct)
    {
        Writes.Add((tenantId, enabled, changedBy, _log?.Rows.Count ?? -1));
        if (FailWrites) throw new InvalidOperationException("store down");
        Rows[tenantId] = enabled;
        return Task.CompletedTask;
    }
}

/// <summary>Auto-join (#80): who joins as Developer without a role, and who may switch it off.</summary>
public class AutoJoinDecisionTests
{
    private const string Audience = "55555555-5555-4555-8555-555555555555";
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Other = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string Oid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Consumer = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private static readonly DirectoryBinding Acme = new(Dir, "acme", DateTimeOffset.UnixEpoch, "op");
    private static string Iss(string tid) => $"https://login.microsoftonline.com/{tid}/v2.0";

    /// <summary>A person with no Packmoat role unless given one. <paramref name="acct"/>: the acct claim values; <paramref name="idp"/>: the idp claim values.</summary>
    private static VerifiedClaims Claims(string[]? acct = null, string[]? idp = null, params string[] roles) =>
        new(new[] { Iss(Dir) }, new[] { Audience }, new[] { Dir }, new[] { Oid }, "Ada", roles, acct ?? new[] { "0" }, idp ?? Array.Empty<string>());

    private static SignInDecision.Denied Denied(SignInDecision d) => Assert.IsType<SignInDecision.Denied>(d);
    private static SignInDecision.Allowed Allowed(SignInDecision d) => Assert.IsType<SignInDecision.Allowed>(d);

    [Fact]
    public void A_member_with_no_role_joins_as_developer_when_auto_join_is_on()
    {
        var a = Allowed(CustomerSignIn.Decide(Claims(), Audience, Acme, autoJoin: true));
        Assert.Equal(CustomerRole.Developer, a.Role);
        Assert.True(a.AutoJoined);
        Assert.Equal("acme", a.TenantId);
        Assert.Equal(Guid.Parse(Oid), a.ObjectId);
    }

    [Fact]
    public void Auto_join_never_grants_admin()
    {
        // Whatever else the token says, a person let in by auto-join is a Developer.
        var a = Allowed(CustomerSignIn.Decide(Claims(roles: "Packmoat.Reader"), Audience, Acme, autoJoin: true));
        Assert.Equal(CustomerRole.Developer, a.Role);
        Assert.True(a.AutoJoined);
    }

    [Fact]
    public void A_guest_is_not_auto_joined() =>
        Assert.Equal(SignInDenial.GuestNoRole, Denied(CustomerSignIn.Decide(Claims(acct: new[] { "1" }), Audience, Acme, autoJoin: true)).Reason);

    [Fact]
    public void Without_the_acct_claim_nobody_is_auto_joined_and_the_status_is_unknown()
    {
        // The optional claim is not configured yet: fail closed.
        Assert.Equal(SignInDenial.MemberStatusUnknown, Denied(CustomerSignIn.Decide(Claims(acct: Array.Empty<string>()), Audience, Acme, autoJoin: true)).Reason);
        var legacy = new VerifiedClaims(new[] { Iss(Dir) }, new[] { Audience }, new[] { Dir }, new[] { Oid }, "Ada", Array.Empty<string>());
        Assert.Equal(SignInDenial.MemberStatusUnknown, Denied(CustomerSignIn.Decide(legacy, Audience, Acme, autoJoin: true)).Reason);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0", "1")]
    [InlineData("1", "0")]
    public void A_repeated_acct_claim_is_member_status_unknown(string first, string second) =>
        Assert.Equal(SignInDenial.MemberStatusUnknown, Denied(CustomerSignIn.Decide(Claims(acct: new[] { first, second }), Audience, Acme, autoJoin: true)).Reason);

    [Theory]
    [InlineData("")]
    [InlineData(" 0")]
    [InlineData("0 ")]
    [InlineData("00")]
    [InlineData("-0")]
    [InlineData("+0")]
    [InlineData("0.0")]
    [InlineData("false")]
    [InlineData("member")]
    [InlineData("2")]
    [InlineData("1 ")]
    [InlineData(" 1")]
    [InlineData("٠")]   // Arabic-Indic zero
    public void Any_acct_value_other_than_zero_or_one_is_member_status_unknown(string value) =>
        Assert.Equal(SignInDenial.MemberStatusUnknown, Denied(CustomerSignIn.Decide(Claims(acct: new[] { value }), Audience, Acme, autoJoin: true)).Reason);

    [Theory]
    [InlineData(null)]                                                                    // absent: a member signing in at home
    [InlineData("https://login.microsoftonline.com/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/v2.0")]   // equals the issuer
    [InlineData("https://sts.windows.net/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/")]          // this directory's own STS
    public void An_idp_of_this_directory_is_accepted(string? idp) =>
        Assert.True(Allowed(CustomerSignIn.Decide(Claims(idp: idp is null ? null : new[] { idp }), Audience, Acme, autoJoin: true)).AutoJoined);

    [Theory]
    [InlineData("live.com")]
    [InlineData("https://sts.windows.net/9188040d-6c67-4c5b-b112-36a304b66dad/")]          // the consumer directory
    [InlineData("9188040d-6c67-4c5b-b112-36a304b66dad")]
    [InlineData("https://sts.windows.net/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/")]          // another directory: an external member
    [InlineData("https://login.microsoftonline.com/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/v2.0")]
    [InlineData("https://accounts.google.com")]                                          // a federated identity provider
    [InlineData("google.com")]
    [InlineData("mail")]
    [InlineData("")]
    [InlineData("https://sts.windows.net/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]           // no trailing slash
    public void An_idp_from_anywhere_else_refuses_auto_join(string idp) =>
        Assert.Equal(SignInDenial.GuestNoRole, Denied(CustomerSignIn.Decide(Claims(idp: new[] { idp }), Audience, Acme, autoJoin: true)).Reason);

    [Fact]
    public void Two_idp_claims_refuse_auto_join() =>
        Assert.Equal(SignInDenial.GuestNoRole, Denied(CustomerSignIn.Decide(
            Claims(idp: new[] { "https://sts.windows.net/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/", "https://sts.windows.net/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/" }),
            Audience, Acme, autoJoin: true)).Reason);

    [Fact]
    public void With_auto_join_off_a_member_without_a_role_is_refused_as_before()
    {
        Assert.Equal(SignInDenial.NoRole, Denied(CustomerSignIn.Decide(Claims(), Audience, Acme, autoJoin: false)).Reason);
        Assert.Equal(SignInDenial.NoRole, Denied(CustomerSignIn.Decide(Claims(), Audience, Acme)).Reason);   // the default is off
        Assert.Equal(SignInDenial.NoRole, Denied(CustomerSignIn.Decide(Claims(acct: new[] { "1" }), Audience, Acme, autoJoin: false)).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Assigned_roles_are_unchanged_by_auto_join(bool autoJoin)
    {
        var dev = Allowed(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.DeveloperRole), Audience, Acme, autoJoin));
        Assert.Equal((CustomerRole.Developer, false), (dev.Role, dev.AutoJoined));
        var admin = Allowed(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.AdminRole), Audience, Acme, autoJoin));
        Assert.Equal((CustomerRole.Admin, false), (admin.Role, admin.AutoJoined));
        // A guest with an assigned role still gets in, as before.
        var guest = Allowed(CustomerSignIn.Decide(Claims(acct: new[] { "1" }, idp: new[] { "live.com" }, roles: CustomerSignIn.DeveloperRole), Audience, Acme, autoJoin));
        Assert.Equal((CustomerRole.Developer, false), (guest.Role, guest.AutoJoined));
    }

    [Fact]
    public void No_binding_is_still_not_linked_with_auto_join_on()
    {
        var d = Denied(CustomerSignIn.Decide(Claims(), Audience, null, autoJoin: true));
        Assert.Equal(SignInDenial.DirectoryNotLinked, d.Reason);
        Assert.Equal(Dir, d.DirectoryId);
        Assert.Equal(SignInDenial.DirectoryNotLinked, Denied(CustomerSignIn.Decide(Claims(), Audience, Acme with { AzureTenantId = Other }, autoJoin: true)).Reason);
    }

    [Fact]
    public void Every_earlier_check_still_runs_before_auto_join()
    {
        Assert.Equal(SignInDenial.IssuerMismatch, Denied(CustomerSignIn.Decide(Claims() with { Issuers = new[] { Iss(Other) } }, Audience, Acme, autoJoin: true)).Reason);
        Assert.Equal(SignInDenial.WrongAudience, Denied(CustomerSignIn.Decide(Claims() with { Audiences = new[] { Other } }, Audience, Acme, autoJoin: true)).Reason);
        Assert.Equal(SignInDenial.MissingIdentity, Denied(CustomerSignIn.Decide(Claims() with { ObjectIds = Array.Empty<string>() }, Audience, Acme, autoJoin: true)).Reason);
        var consumer = Claims() with { DirectoryIds = new[] { Consumer }, Issuers = new[] { Iss(Consumer) } };
        Assert.Equal(SignInDenial.ConsumerDirectory, Denied(CustomerSignIn.Decide(consumer, Audience, Acme with { AzureTenantId = Consumer }, autoJoin: true)).Reason);
    }
}

/// <summary>The auto-join switch on /team: Admin only, fresh sign-in, guarded, rate limited, recorded before it changes.</summary>
public class AutoJoinCommandTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string AcmeDir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string GlobexDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly MovableClock _clock = new();
    private readonly FakeStore _tenants = new();
    private readonly FakeDirectories _directories = new();
    private readonly FakeActionLog _log = new();
    private readonly FakeAutoJoinStore _store;
    private readonly AutoJoinCommands _commands;

    private static readonly CustomerActor Grace = new("acme", Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"), CustomerRole.Admin, "Grace", AcmeDir, SignedInAt: Now);
    private static readonly CustomerActor Ada = new("acme", Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), CustomerRole.Developer, "Ada", AcmeDir, SignedInAt: Now);

    public AutoJoinCommandTests()
    {
        _store = new FakeAutoJoinStore(_log);
        foreach (var id in new[] { "acme", "globex" })
            _tenants.Tenants.Add(new Tenant(id, id.ToUpperInvariant(), "https://pkgs.dev.azure.com/", id + "/", $"https://pkgs.dev.azure.com/{id}/_packaging/f/nuget/v3/index.json", new TenantPolicy(14)));
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "acme", _clock.UtcNow, "op");
        _directories.Rows[GlobexDir] = new DirectoryBinding(GlobexDir, "globex", _clock.UtcNow, "op");
        _commands = new AutoJoinCommands(_tenants, _directories, _store, _log, _clock);
    }

    private static async Task<CustomerTokenRefusal> Refused(Func<Task> act) => (await Assert.ThrowsAsync<CustomerTokenRefusedException>(act)).Reason;

    [Fact]
    public async Task A_fresh_admin_turns_it_off_and_the_intent_row_is_written_before_the_change()
    {
        Assert.True(await _commands.IsOnAsync(Grace, Ct));   // no row: on
        Assert.True(await _commands.SetAutoJoinAsync(Grace, enabled: false, Ct));
        var write = Assert.Single(_store.Writes);
        Assert.Equal(("acme", false), (write.TenantId, write.Enabled));
        Assert.Equal(1, write.AuditRowsBefore);   // the intent row already stood when the store was written
        Assert.Contains("Grace", write.By);
        var row = Assert.Single(_log.Rows);
        Assert.Equal(("team.autojoin.set", "acme", "enabled=off; was=on", "app"), (row.Action, row.TenantId, row.Detail, row.Site));
        Assert.Equal((AcmeDir, Grace.ObjectId.ToString("D"), "Grace"), (row.ActorTid, row.ActorOid, row.ActorName));
        Assert.False(await _commands.IsOnAsync(Grace, Ct));

        Assert.True(await _commands.SetAutoJoinAsync(Grace, enabled: true, Ct));
        Assert.Equal("enabled=on; was=off", _log.Rows[^1].Detail);
        Assert.True(await _commands.IsOnAsync(Grace, Ct));
    }

    [Fact]
    public async Task No_change_is_a_no_op_with_no_row_and_no_write()
    {
        Assert.False(await _commands.SetAutoJoinAsync(Grace, enabled: true, Ct));
        Assert.Empty(_store.Writes);
        Assert.Empty(_log.Rows);
    }

    [Fact]
    public async Task A_developer_is_refused()
    {
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => _commands.SetAutoJoinAsync(Ada, false, Ct)));
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => _commands.IsOnAsync(Ada, Ct)));
        Assert.Empty(_store.Writes);
        Assert.Empty(_log.Rows);
    }

    [Fact]
    public async Task A_stale_or_unknown_sign_in_is_refused()
    {
        _clock.UtcNow = Now.AddMinutes(16);
        Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => _commands.SetAutoJoinAsync(Grace, false, Ct)));
        _clock.UtcNow = Now;
        Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => _commands.SetAutoJoinAsync(Grace with { SignedInAt = null }, false, Ct)));
        Assert.Empty(_store.Writes);
        Assert.Empty(_log.Rows);
        Assert.Empty(_store.Reads);   // refused before anything is read
    }

    [Fact]
    public async Task An_unlinked_directory_or_a_suspended_tenant_is_refused()
    {
        _directories.Rows.Remove(AcmeDir);
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.SetAutoJoinAsync(Grace, false, Ct)));
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "globex", Now, "op");   // the directory now belongs to another tenant
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.SetAutoJoinAsync(Grace, false, Ct)));
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "acme", Now, "op");
        var i = _tenants.Tenants.FindIndex(t => t.Id == "acme");
        _tenants.Tenants[i] = _tenants.Tenants[i] with { Active = false };
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.SetAutoJoinAsync(Grace, false, Ct)));
        Assert.Empty(_store.Writes);
        Assert.Empty(_log.Rows);
    }

    [Fact]
    public async Task Changes_are_rate_limited_per_person()
    {
        for (var i = 0; i < AutoJoinCommands.MaxChangesPerWindow; i++) await _commands.SetAutoJoinAsync(Grace, i % 2 == 1, Ct);
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => _commands.SetAutoJoinAsync(Grace, true, Ct)));
        Assert.Equal(AutoJoinCommands.MaxChangesPerWindow, _store.Writes.Count);
        _clock.UtcNow = Now.AddMinutes(10);
        var later = Grace with { SignedInAt = _clock.UtcNow };
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => _commands.SetAutoJoinAsync(later, true, Ct)));
        _clock.UtcNow = Now.AddHours(1);
        Assert.True(await _commands.SetAutoJoinAsync(later with { SignedInAt = _clock.UtcNow }, false, Ct));   // a new window
    }

    [Fact]
    public async Task No_audit_row_means_no_change()
    {
        _log.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _commands.SetAutoJoinAsync(Grace, false, Ct));
        Assert.Empty(_store.Writes);
        _log.Fail = false;
        Assert.True(await _commands.IsOnAsync(Grace, Ct));
    }

    [Fact]
    public async Task A_store_failure_leaves_a_failed_row_and_is_rethrown()
    {
        _store.FailWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _commands.SetAutoJoinAsync(Grace, false, Ct));
        Assert.Equal(new[] { "team.autojoin.set", "team.autojoin.set.failed" }, _log.Rows.Select(r => r.Action).ToArray());
        Assert.All(_log.Rows, r => Assert.Equal("enabled=off; was=on", r.Detail));
    }

    [Fact]
    public async Task The_tenant_comes_only_from_the_actor()
    {
        var mallory = new CustomerActor("globex", Guid.Parse("11111111-1111-4111-8111-111111111111"), CustomerRole.Admin, "Mallory", GlobexDir, SignedInAt: Now);
        await _commands.SetAutoJoinAsync(mallory, false, Ct);
        Assert.False(_store.Rows["globex"]);
        Assert.False(_store.Rows.ContainsKey("acme"));
        Assert.True(await _commands.IsOnAsync(Grace, Ct));
        // An actor claiming acme from globex's directory is refused.
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.SetAutoJoinAsync(mallory with { TenantId = "acme" }, false, Ct)));
    }
}
