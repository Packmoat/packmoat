using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>Every rule of customer self-service tokens: who may issue what, for how long, how many, and who may revoke whose.</summary>
public class CustomerTokenTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string AcmeDir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string GlobexDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private static readonly Guid AdaOid = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid BobOid = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private const string SharedClient = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

    private readonly MovableClock _clock = new();
    private readonly FakeStore _tenants = new();
    private readonly FakeTokens _tokens;
    private readonly FakeMembers _members = new();
    private readonly FakeDirectories _directories = new();
    private readonly FakeActionLog _log = new();
    private readonly FakeSharedStore _shared = new();
    private readonly InProcessRateCounter _rates;
    private readonly CustomerTokenCommands _commands;
    private readonly CustomerTokenQueries _queries;

    private static readonly CustomerActor Ada = new("acme", AdaOid, CustomerRole.Developer, "Ada", AcmeDir);
    private static readonly CustomerActor Bob = new("acme", BobOid, CustomerRole.Developer, "Bob", AcmeDir);
    private static readonly CustomerActor AcmeAdmin = new("acme", Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"), CustomerRole.Admin, "Grace", AcmeDir, SignedInAt: new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private static readonly CustomerActor GlobexAdmin = new("globex", Guid.Parse("11111111-1111-4111-8111-111111111111"), CustomerRole.Admin, "Mallory", GlobexDir, SignedInAt: new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public CustomerTokenTests()
    {
        _tokens = new FakeTokens(_clock);
        _rates = new InProcessRateCounter(_clock);
        foreach (var (id, dir) in new[] { ("acme", AcmeDir), ("globex", GlobexDir) })
        {
            _tenants.Tenants.Add(new Tenant(id, id.ToUpperInvariant(), "https://pkgs.dev.azure.com/", id + "/", $"https://pkgs.dev.azure.com/{id}/_packaging/f/nuget/v3/index.json", new TenantPolicy(14)));
            _tenants.Bindings[id] = new UpstreamBinding(IdentityKind.Own, dir, "99999999-9999-4999-8999-999999999999");   // set up: an own app reaches the feed
        }
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "acme", _clock.UtcNow, "op");
        _directories.Rows[GlobexDir] = new DirectoryBinding(GlobexDir, "globex", _clock.UtcNow, "op");
        _commands = new CustomerTokenCommands(_tenants, _tokens, _members, _directories, _log, _clock, _shared, ServeAllGate.Instance, _rates);
        _queries = new CustomerTokenQueries(_tenants, _tokens, _directories, _shared, ServeAllGate.Instance);
    }

    private static async Task<CustomerTokenRefusal> Refused(Func<Task> act) =>
        (await Assert.ThrowsAsync<CustomerTokenRefusedException>(act)).Reason;

    private void Suspend(string id)
    {
        var i = _tenants.Tenants.FindIndex(t => t.Id == id);
        _tenants.Tenants[i] = _tenants.Tenants[i] with { Active = false };
    }

    // ---- whether issuing would be refused (the token pages hide the create form)

    private sealed record TestNotice : ServingNotice;

    private sealed class RefusingGate(ServingNotice notice) : IServingGate
    {
        public Task<ServingAnswer> AskAsync(string tenantId, CancellationToken ct) => Task.FromResult<ServingAnswer>(new ServingAnswer.Refuse(notice));
    }

    [Fact]   // mutation: the blocker asks only the gate (not the upstream), or only the binding
    public async Task The_issue_blocker_follows_the_posts_rule_and_gives_the_same_reason_and_notice()
    {
        Assert.Null(await _queries.IssueBlockerAsync(Ada, Ct));   // set up, served: the form shows
        var notice = new TestNotice();
        var refusing = new CustomerTokenQueries(_tenants, _tokens, _directories, _shared, new RefusingGate(notice));
        var refusingCommands = new CustomerTokenCommands(_tenants, _tokens, _members, _directories, _log, _clock, _shared, new RefusingGate(notice), _rates);
        Assert.Equal(new CustomerTokenBlocker(CustomerTokenRefusal.ServingRefused, notice), await refusing.IssueBlockerAsync(Ada, Ct));
        var e = await Assert.ThrowsAsync<CustomerTokenRefusedException>(() => refusingCommands.IssueDeveloperTokenAsync(Ada, "laptop", Ct));
        Assert.Equal((CustomerTokenRefusal.ServingRefused, (ServingNotice?)notice), (e.Reason, e.Notice));

        _tenants.Bindings.Remove("acme");   // no upstream
        Assert.Equal(new CustomerTokenBlocker(CustomerTokenRefusal.TenantNotSetUp, null), await _queries.IssueBlockerAsync(Ada, Ct));
        Assert.Equal(CustomerTokenRefusal.TenantNotSetUp, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "laptop", Ct)));

        Suspend("acme");
        Assert.Equal(new CustomerTokenBlocker(CustomerTokenRefusal.TenantSuspended, null), await _queries.IssueBlockerAsync(Ada, Ct));
        Assert.Empty(_tokens.Rows);
    }

    // ---- issuing

    [Fact]
    public async Task A_developer_token_is_owned_by_the_person_issued_via_self_and_expires_in_90_days()
    {
        var issued = await _commands.IssueDeveloperTokenAsync(Ada, " laptop ", Ct);
        var row = Assert.Single(_tokens.Rows);
        Assert.Equal(issued.TokenId, row.TokenId);
        Assert.Equal(("acme", AcmeDir, AdaOid.ToString("D")), (row.TenantId, row.OwnerTid, row.OwnerOid));
        Assert.Equal(TokenIssuer.Self, row.IssuedVia);
        Assert.Equal(TokenKind.Developer, row.Kind);
        Assert.Equal("laptop", row.Label);
        Assert.Equal(_clock.UtcNow.AddDays(90), row.ExpiresAt);
        Assert.Equal(90, CustomerTokenRules.DeveloperTokenDays);
        // the person is recorded, so the owner's name is known
        Assert.Equal("Ada", _members.Rows[(AcmeDir, AdaOid.ToString("D"))].DisplayName);
    }

    [Fact]
    public async Task An_admin_may_issue_a_developer_token_too()
    {
        await _commands.IssueDeveloperTokenAsync(AcmeAdmin, "desk", Ct);
        Assert.Equal(TokenKind.Developer, Assert.Single(_tokens.Rows).Kind);
    }

    [Fact]
    public async Task At_most_five_active_self_issued_tokens_per_person()
    {
        for (var i = 0; i < 5; i++) await _commands.IssueDeveloperTokenAsync(Ada, "t" + i, Ct);
        Assert.Equal(CustomerTokenRefusal.TooManyTokens, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "sixth", Ct)));
        Assert.Equal(5, _tokens.Rows.Count);
        // another person's cap is their own
        await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);
        // a revoked token frees a place
        await _commands.RevokeOwnTokenAsync(Ada, _tokens.Rows[0].TokenId, Ct);
        await _commands.IssueDeveloperTokenAsync(Ada, "sixth", Ct);
        Assert.Equal(CustomerTokenRefusal.TooManyTokens, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "seventh", Ct)));
    }

    [Fact]
    public async Task Expired_tokens_do_not_count_towards_the_cap()
    {
        for (var i = 0; i < 5; i++) await _commands.IssueDeveloperTokenAsync(Ada, "t" + i, Ct);
        _clock.UtcNow = _clock.UtcNow.AddDays(91);
        await _commands.IssueDeveloperTokenAsync(Ada, "fresh", Ct);
        Assert.Equal(6, _tokens.Rows.Count);
    }

    [Fact]
    public async Task Operator_issued_tokens_do_not_count_towards_the_cap()
    {
        for (var i = 0; i < 3; i++) await _tokens.IssueAsync("acme", "op" + i, Ct);
        for (var i = 0; i < 5; i++) await _commands.IssueDeveloperTokenAsync(Ada, "t" + i, Ct);
        Assert.Equal(8, _tokens.Rows.Count);
    }

    [Fact]
    public async Task The_store_refusing_at_the_cap_is_a_refusal_and_is_recorded_as_failed()
    {
        // Simulates a concurrent issue winning the last place between the quick check and the insert.
        for (var i = 0; i < 5; i++)
            _tokens.Rows.Add(new TokenInfo($"tok_race{i}", "acme", "p", "x", _clock.UtcNow, null, null, TokenKind.Developer, AcmeDir, AdaOid.ToString("D"), null, _clock.UtcNow.AddDays(1), TokenIssuer.Self));
        var racing = new CustomerTokenCommands(_tenants, new CapOnlyInStore(_tokens), _members, _directories, _log, _clock, _shared, ServeAllGate.Instance, _rates);
        Assert.Equal(CustomerTokenRefusal.TooManyTokens, await Refused(() => racing.IssueDeveloperTokenAsync(Ada, "late", Ct)));
        Assert.Equal(new[] { "token.issue", "token.issue.failed" }, _log.Rows.Select(r => r.Action));
    }

    /// <summary>Hides the existing rows from the quick check, so only the store's own cap can refuse.</summary>
    private sealed class CapOnlyInStore : ITokenService
    {
        private readonly FakeTokens _inner;
        public CapOnlyInStore(FakeTokens inner) => _inner = inner;
        public Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string a, string b, string c, CancellationToken ct) => Task.FromResult<IReadOnlyList<TokenInfo>>(Array.Empty<TokenInfo>());
        public Task<IssuedToken?> IssueForMemberAsync(string a, string b, string c, string d, DateTimeOffset e, TokenKind k, int m, CancellationToken ct) => _inner.IssueForMemberAsync(a, b, c, d, e, k, m, ct);
        public Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken ct, TokenKind kind = TokenKind.Developer) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(string tokenId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RevokeForMemberAsync(string a, string b, string c, string d, string e, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RevokeInTenantAsync(string a, string b, string c, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Automation_tokens_only_by_an_admin()
    {
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => _commands.IssueAutomationTokenAsync(Ada, "ci", 30, Ct)));
        Assert.Empty(_tokens.Rows);
        Assert.Empty(_log.Rows);
        await _commands.IssueAutomationTokenAsync(AcmeAdmin, "ci", 30, Ct);
        var row = Assert.Single(_tokens.Rows);
        Assert.Equal(TokenKind.Automation, row.Kind);
        Assert.Equal(AcmeAdmin.ObjectId.ToString("D"), row.OwnerOid);
        Assert.Equal(_clock.UtcNow.AddDays(30), row.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    [InlineData(int.MaxValue)]
    public async Task Automation_expiry_is_1_to_365_days_never_none(int days)
    {
        Assert.Equal(CustomerTokenRefusal.BadExpiry, await Refused(() => _commands.IssueAutomationTokenAsync(AcmeAdmin, "ci", days, Ct)));
        Assert.Empty(_tokens.Rows);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(365)]
    public async Task Automation_expiry_bounds_are_allowed(int days)
    {
        await _commands.IssueAutomationTokenAsync(AcmeAdmin, "ci", days, Ct);
        Assert.Equal(_clock.UtcNow.AddDays(days), Assert.Single(_tokens.Rows).ExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    [InlineData("zero​width")]
    [InlineData("rtl‮override")]
    public async Task A_bad_label_is_refused(string label) =>
        Assert.Equal(CustomerTokenRefusal.BadLabel, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, label, Ct)));

    [Fact]
    public async Task A_label_over_100_characters_is_refused() =>
        Assert.Equal(CustomerTokenRefusal.BadLabel, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, new string('x', 101), Ct)));

    // ---- the tenant and the binding

    [Fact]
    public async Task A_suspended_tenant_issues_and_revokes_nothing()
    {
        var t = await _commands.IssueDeveloperTokenAsync(Ada, "laptop", Ct);
        Suspend("acme");
        _log.Rows.Clear();
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "again", Ct)));
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.IssueAutomationTokenAsync(AcmeAdmin, "ci", 30, Ct)));
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.RevokeOwnTokenAsync(Ada, t.TokenId, Ct)));
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.RevokeTenantTokenAsync(AcmeAdmin, t.TokenId, Ct)));
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _queries.MineAsync(Ada, Ct)));
        Assert.Single(_tokens.Rows);
        Assert.Null(_tokens.Rows[0].RevokedAt);
        Assert.Empty(_log.Rows);
    }

    [Fact]
    public async Task A_tenant_that_no_longer_exists_is_refused()
    {
        var ghost = Ada with { TenantId = "gone" };
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "gone", _clock.UtcNow, "op");
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.IssueDeveloperTokenAsync(ghost, "x", Ct)));
    }

    [Fact]
    public async Task An_actor_whose_directory_is_unbound_or_moved_is_refused()
    {
        _directories.Rows.Remove(AcmeDir);
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "globex", _clock.UtcNow, "op");   // now bound to another tenant
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
        Assert.Empty(_tokens.Rows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task An_actor_without_a_directory_is_refused(string? directory) =>
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada with { DirectoryId = directory }, "x", Ct)));

    [Fact]
    public async Task The_tenant_comes_only_from_the_actor_and_must_match_the_binding()
    {
        // An actor claiming globex while signed in from acme's directory gets nothing in either tenant.
        var forged = Ada with { TenantId = "globex" };
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _commands.IssueDeveloperTokenAsync(forged, "x", Ct)));
        Assert.Empty(_tokens.Rows);
    }

    [Fact]
    public async Task A_shared_app_tenant_without_a_passing_check_issues_nothing()
    {
        _tenants.Bindings["acme"] = new UpstreamBinding(IdentityKind.Shared, AcmeDir, null);
        Assert.Equal(CustomerTokenRefusal.TenantNotVerified, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));

        _shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(SharedClient, "PEM");
        Assert.Equal(CustomerTokenRefusal.TenantNotVerified, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));

        // a check for another feed does not count
        var feed = _tenants.Tenants.Single(t => t.Id == "acme").UpstreamFeedIndexUrl;
        _shared.Verifications["acme"] = new SharedAppVerification("acme", AcmeDir, feed + "x", SharedClient, _clock.UtcNow, "op");
        Assert.Equal(CustomerTokenRefusal.TenantNotVerified, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
        Assert.Empty(_tokens.Rows);

        _shared.Verifications["acme"] = new SharedAppVerification("acme", AcmeDir, feed, SharedClient, _clock.UtcNow, "op");
        await _commands.IssueDeveloperTokenAsync(Ada, "x", Ct);
        Assert.Single(_tokens.Rows);
    }

    [Fact]
    public async Task A_tenant_with_no_upstream_binding_issues_nothing_and_writes_nothing()
    {
        _tenants.Bindings.Remove("acme");
        Assert.Equal(CustomerTokenRefusal.TenantNotSetUp, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
        Assert.Equal(CustomerTokenRefusal.TenantNotSetUp, await Refused(() => _commands.IssueAutomationTokenAsync(AcmeAdmin, "ci", 30, Ct)));
        Assert.Empty(_tokens.Rows);
        Assert.Empty(_log.Rows);
        // a verification record (say, left over) does not stand in for a binding
        _shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(SharedClient, "PEM");
        var feed = _tenants.Tenants.Single(t => t.Id == "acme").UpstreamFeedIndexUrl;
        _shared.Verifications["acme"] = new SharedAppVerification("acme", AcmeDir, feed, SharedClient, _clock.UtcNow, "op");
        Assert.Equal(CustomerTokenRefusal.TenantNotSetUp, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
        // the other tenant is unaffected
        await _commands.IssueDeveloperTokenAsync(GlobexAdmin, "x", Ct);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/other/_packaging/f/nuget/v3/index.json")]
    public async Task An_own_app_tenant_with_empty_or_invalid_urls_issues_nothing(string upstreamBase, string prefix, string feed)
    {
        var i = _tenants.Tenants.FindIndex(t => t.Id == "acme");
        _tenants.Tenants[i] = _tenants.Tenants[i] with { UpstreamBase = upstreamBase, AllowedPathPrefix = prefix, UpstreamFeedIndexUrl = feed };
        Assert.Equal(CustomerTokenRefusal.TenantNotSetUp, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
        Assert.Empty(_tokens.Rows);
    }

    [Theory]
    [InlineData("http://pkgs.dev.azure.com/", "acme/", "http://pkgs.dev.azure.com/acme/_packaging/f/nuget/v3/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "", "https://pkgs.dev.azure.com/acme/_packaging/f/nuget/v3/index.json")]
    public async Task An_own_app_tenant_with_http_or_no_prefix_still_issues_but_a_shared_one_does_not(string upstreamBase, string prefix, string feed)
    {
        var i = _tenants.Tenants.FindIndex(t => t.Id == "acme");
        _tenants.Tenants[i] = _tenants.Tenants[i] with { UpstreamBase = upstreamBase, AllowedPathPrefix = prefix, UpstreamFeedIndexUrl = feed };
        await _commands.IssueDeveloperTokenAsync(Ada, "x", Ct);
        Assert.Single(_tokens.Rows);

        _tenants.Bindings["acme"] = new UpstreamBinding(IdentityKind.Shared, AcmeDir, null);
        _shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(SharedClient, "PEM");
        _shared.Verifications["acme"] = new SharedAppVerification("acme", AcmeDir, feed, SharedClient, _clock.UtcNow, "op");
        Assert.Equal(CustomerTokenRefusal.TenantNotSetUp, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "y", Ct)));
        Assert.Single(_tokens.Rows);
    }

    [Fact]
    public async Task A_tenant_that_is_not_set_up_still_lists_and_revokes_its_tokens()
    {
        var op = await _tokens.IssueAsync("acme", "operator-issued", Ct);
        var mine = await _commands.IssueDeveloperTokenAsync(Ada, "ada", Ct);
        _tenants.Bindings.Remove("acme");
        Assert.Single(await _queries.MineAsync(Ada, Ct));
        Assert.Equal(2, (await _queries.TenantAsync(AcmeAdmin, Ct)).Count);
        Assert.True(await _commands.RevokeOwnTokenAsync(Ada, mine.TokenId, Ct));
        Assert.True(await _commands.RevokeTenantTokenAsync(AcmeAdmin, op.TokenId, Ct));
    }

    [Fact]
    public async Task A_suspended_tenant_is_refused_as_suspended_before_the_upstream_is_asked()
    {
        _tenants.Bindings.Remove("acme");
        Suspend("acme");
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct)));
    }

    [Fact]
    public async Task A_shared_app_tenant_with_no_shared_store_fails_closed()
    {
        _tenants.Bindings["acme"] = new UpstreamBinding(IdentityKind.Shared, AcmeDir, null);
        var noShared = new CustomerTokenCommands(_tenants, _tokens, _members, _directories, _log, _clock, null, ServeAllGate.Instance, _rates);
        Assert.Equal(CustomerTokenRefusal.TenantNotVerified, await Refused(() => noShared.IssueDeveloperTokenAsync(Ada, "x", Ct)));
    }

    // ---- revoking

    [Fact]
    public async Task A_person_revokes_only_their_own_token()
    {
        var mine = await _commands.IssueDeveloperTokenAsync(Ada, "ada", Ct);
        var bobs = await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);
        Assert.False(await _commands.RevokeOwnTokenAsync(Ada, bobs.TokenId, Ct));
        Assert.Null(_tokens.Rows.Single(r => r.TokenId == bobs.TokenId).RevokedAt);
        Assert.True(await _commands.RevokeOwnTokenAsync(Ada, mine.TokenId, Ct));
        var row = _tokens.Rows.Single(r => r.TokenId == mine.TokenId);
        Assert.NotNull(row.RevokedAt);
        Assert.Equal($"Ada ({AdaOid:D})", row.RevokedBy);
        Assert.False(await _commands.RevokeOwnTokenAsync(Ada, mine.TokenId, Ct));   // already revoked
        Assert.False(await _commands.RevokeOwnTokenAsync(Ada, "tok_unknown", Ct));
    }

    [Fact]
    public async Task An_admin_can_not_use_the_own_token_path_on_someone_elses_token()
    {
        var bobs = await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);
        Assert.False(await _commands.RevokeOwnTokenAsync(AcmeAdmin, bobs.TokenId, Ct));
        Assert.Null(_tokens.Rows.Single().RevokedAt);
    }

    [Fact]
    public async Task A_developer_can_not_revoke_as_admin()
    {
        var bobs = await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => _commands.RevokeTenantTokenAsync(Ada, bobs.TokenId, Ct)));
        Assert.Null(_tokens.Rows.Single().RevokedAt);
    }

    [Fact]
    public async Task An_admin_revokes_any_token_in_the_tenant_including_operator_issued_and_records_who()
    {
        var bobs = await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);
        var op = await _tokens.IssueAsync("acme", "operator-issued", Ct);
        Assert.True(await _commands.RevokeTenantTokenAsync(AcmeAdmin, bobs.TokenId, Ct));
        Assert.True(await _commands.RevokeTenantTokenAsync(AcmeAdmin, op.TokenId, Ct));
        Assert.All(_tokens.Rows, r => Assert.Equal($"Grace ({AcmeAdmin.ObjectId:D})", r.RevokedBy));
    }

    [Fact]
    public async Task An_admin_never_revokes_across_tenants()
    {
        var acmeToken = await _commands.IssueDeveloperTokenAsync(Ada, "ada", Ct);
        var globexOperatorToken = await _tokens.IssueAsync("globex", "theirs", Ct);
        _log.Rows.Clear();
        Assert.False(await _commands.RevokeTenantTokenAsync(GlobexAdmin, acmeToken.TokenId, Ct));
        Assert.False(await _commands.RevokeTenantTokenAsync(AcmeAdmin, globexOperatorToken.TokenId, Ct));
        Assert.All(_tokens.Rows, r => Assert.Null(r.RevokedAt));
        Assert.Empty(_log.Rows);   // a refused id leaves no trail row naming it
        // and the admin's view of the tenant never holds another tenant's token
        Assert.DoesNotContain(await _queries.TenantAsync(AcmeAdmin, Ct), t => t.TenantId != "acme");
    }

    // ---- reads

    [Fact]
    public async Task Mine_lists_only_the_persons_own_tokens_and_tenant_view_is_admin_only()
    {
        await _commands.IssueDeveloperTokenAsync(Ada, "ada", Ct);
        await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);
        await _tokens.IssueAsync("acme", "operator", Ct);
        Assert.Equal(new[] { "ada" }, (await _queries.MineAsync(Ada, Ct)).Select(t => t.Label));
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => _queries.TenantAsync(Ada, Ct)));
        Assert.Equal(3, (await _queries.TenantAsync(AcmeAdmin, Ct)).Count);
    }

    // ---- the trail and the plaintext

    [Fact]
    public async Task Issue_and_revoke_leave_app_site_rows_naming_the_person_and_never_the_token()
    {
        var t = await _commands.IssueDeveloperTokenAsync(Ada, "laptop", Ct);
        await _commands.RevokeOwnTokenAsync(Ada, t.TokenId, Ct);
        Assert.Equal(new[] { "token.issue", "token.issue.id", "token.revoke" }, _log.Rows.Select(r => r.Action));
        Assert.All(_log.Rows, r =>
        {
            Assert.Equal("app", r.Site);
            Assert.Equal("acme", r.TenantId);
            Assert.Equal(AcmeDir, r.ActorTid);
            Assert.Equal(AdaOid.ToString("D"), r.ActorOid);
            Assert.Equal("Ada", r.ActorName);
            Assert.DoesNotContain(t.Plaintext, r.Detail);
            Assert.DoesNotContain("pmt_", r.Detail);
        });
        Assert.Contains(t.TokenId, _log.Rows[1].Detail);
        Assert.DoesNotContain(t.Plaintext, t.ToString());
    }

    [Fact]
    public async Task No_trail_row_no_token()
    {
        _log.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _commands.IssueDeveloperTokenAsync(Ada, "x", Ct));
        Assert.Empty(_tokens.Rows);
    }

    // ---- fresh sign-in, rate limits, quoting

    [Fact]
    public async Task Admin_only_writes_need_a_sign_in_within_15_minutes()
    {
        var op = await _tokens.IssueAsync("acme", "operator", Ct);
        var stale = AcmeAdmin with { SignedInAt = _clock.UtcNow.AddMinutes(-16) };
        var unknown = AcmeAdmin with { SignedInAt = null };
        var future = AcmeAdmin with { SignedInAt = _clock.UtcNow.AddHours(1) };
        foreach (var a in new[] { stale, unknown, future })
        {
            Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => _commands.IssueAutomationTokenAsync(a, "ci", 30, Ct)));
            Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => _commands.RevokeTenantTokenAsync(a, op.TokenId, Ct)));
        }
        Assert.Single(_tokens.Rows);
        Assert.Null(_tokens.Rows[0].RevokedAt);
        // a developer's own-token writes do not need it
        await _commands.IssueDeveloperTokenAsync(stale, "desk", Ct);
        var fresh = AcmeAdmin with { SignedInAt = _clock.UtcNow.AddMinutes(-14) };
        await _commands.IssueAutomationTokenAsync(fresh, "ci", 30, Ct);
        Assert.True(await _commands.RevokeTenantTokenAsync(fresh, op.TokenId, Ct));
    }

    [Fact]
    public async Task Issues_are_limited_to_ten_an_hour_per_person()
    {
        for (var i = 0; i < 10; i++)
        {
            var t = await _commands.IssueDeveloperTokenAsync(Ada, "t" + i, Ct);
            await _commands.RevokeOwnTokenAsync(Ada, t.TokenId, Ct);   // keeps her under the cap of 5
        }
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => _commands.IssueDeveloperTokenAsync(Ada, "eleventh", Ct)));
        await _commands.IssueDeveloperTokenAsync(Bob, "bob", Ct);   // another person is not affected
        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        await _commands.IssueDeveloperTokenAsync(Ada, "next hour", Ct);
    }

    [Fact]
    public async Task Revokes_are_limited_to_ten_an_hour_per_person()
    {
        for (var i = 0; i < 10; i++) Assert.False(await _commands.RevokeOwnTokenAsync(Ada, "tok_none", Ct));
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => _commands.RevokeOwnTokenAsync(Ada, "tok_none", Ct)));
    }

    [Theory]
    [InlineData("laptop; kind=Automation; owner=operator", "label=\"laptop; kind=Automation; owner=operator\"")]
    [InlineData("say \"hi\"", "label=\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash\"; x=y", "label=\"back\\\\slash\\\"; x=y\"")]
    public async Task A_label_cannot_forge_fields_in_the_trail(string label, string expectedTail)
    {
        var t = await _commands.IssueDeveloperTokenAsync(Ada, label, Ct);
        await _commands.RevokeOwnTokenAsync(Ada, t.TokenId, Ct);
        var issue = _log.Rows.Single(r => r.Action == "token.issue").Detail;
        var revoke = _log.Rows.Single(r => r.Action == "token.revoke").Detail;
        Assert.StartsWith("kind=Developer; expires=", issue);
        Assert.EndsWith("; " + expectedTail, issue);
        Assert.EndsWith("; " + expectedTail, revoke);
        Assert.StartsWith($"token={t.TokenId}; kind=Developer; owner=self; label=", revoke);
    }

    [Fact]
    public void Quote_escapes_backslash_then_quote() =>
        Assert.Equal("\"a\\\\\\\"b\"", CustomerTokenRules.Quote("a\\\"b"));

    [Fact]
    public async Task Sign_in_records_the_person_and_updates_name_and_last_seen()
    {
        await _commands.RecordSignInAsync(Ada, Ct);
        var first = _clock.UtcNow;
        _clock.UtcNow = first.AddDays(2);
        await _commands.RecordSignInAsync(Ada with { DisplayName = "Ada L." }, Ct);
        var m = _members.Rows[(AcmeDir, AdaOid.ToString("D"))];
        Assert.Equal(("acme", "Ada L.", first, first.AddDays(2)), (m.TenantId, m.DisplayName, m.FirstSeenAt, m.LastSeenAt));
        // a suspended tenant's person is still recorded; an unbound directory is not
        Suspend("acme");
        await _commands.RecordSignInAsync(Bob, Ct);
        Assert.True(_members.Rows.ContainsKey((AcmeDir, BobOid.ToString("D"))));
        _directories.Rows.Remove(AcmeDir);
        var carol = Bob with { ObjectId = Guid.NewGuid() };
        await Assert.ThrowsAsync<CustomerTokenRefusedException>(() => _commands.RecordSignInAsync(carol, Ct));
        Assert.False(_members.Rows.ContainsKey((AcmeDir, carol.ObjectId.ToString("D"))));
    }
}

/// <summary>The cached authenticator never keeps a token alive past its expiry.</summary>
public class CachedExpiryTests
{
    private sealed class Counting : ITenantAuthenticator
    {
        public AuthResult? Result { get; set; }
        public int Calls { get; private set; }
        public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct) { Calls++; return ValueTask.FromResult(Result); }
    }

    private static readonly Tenant Acme = new("acme", "Acme", "https://x/", "o/", "https://x/o/index.json", new TenantPolicy(14));

    [Fact]
    public async Task A_cached_token_stops_at_its_expiry_not_at_the_end_of_the_cache_window()
    {
        var clock = new MovableClock();
        var inner = new Counting { Result = new AuthResult(Acme, "tok_1", clock.UtcNow.AddSeconds(2)) };
        var cached = new CachedTenantAuthenticator(inner, clock);
        Assert.NotNull(await cached.AuthenticateAsync("pmt_x", default));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        Assert.NotNull(await cached.AuthenticateAsync("pmt_x", default));
        Assert.Equal(1, inner.Calls);   // served from the cache
        clock.UtcNow = clock.UtcNow.AddSeconds(1);   // exactly at expiry, well inside the 5-second window
        inner.Result = null;            // the database now says no (expired)
        Assert.Null(await cached.AuthenticateAsync("pmt_x", default));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task An_already_expired_result_is_never_returned_or_cached()
    {
        var clock = new MovableClock();
        var inner = new Counting { Result = new AuthResult(Acme, "tok_1", clock.UtcNow) };
        var cached = new CachedTenantAuthenticator(inner, clock);
        Assert.Null(await cached.AuthenticateAsync("pmt_x", default));
        Assert.Null(await cached.AuthenticateAsync("pmt_x", default));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_token_without_expiry_keeps_the_usual_window()
    {
        var clock = new MovableClock();
        var inner = new Counting { Result = new AuthResult(Acme, "tok_1") };
        var cached = new CachedTenantAuthenticator(inner, clock);
        await cached.AuthenticateAsync("pmt_x", default);
        clock.UtcNow = clock.UtcNow.AddSeconds(4);
        await cached.AuthenticateAsync("pmt_x", default);
        Assert.Equal(1, inner.Calls);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await cached.AuthenticateAsync("pmt_x", default);
        Assert.Equal(2, inner.Calls);
    }
}
