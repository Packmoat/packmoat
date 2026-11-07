using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

public class AdminCommandsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeStore _store = new();
    private readonly AdminCommands _commands;
    private readonly AdminQueries _queries;
    private static readonly ActorScope Acme = ActorScope.ForTenant("acme");

    public AdminCommandsTests()
    {
        _commands = new AdminCommands(_store, _store, new FixedClock());
        _queries = new AdminQueries(_store, _store, _store, _store);
        _store.TokenRows.Add(new TokenInfo("t-acme", "acme", "pmt_a", "ci", DateTimeOffset.UnixEpoch, null, null));
        _store.TokenRows.Add(new TokenInfo("t-other", "other", "pmt_o", "ci", DateTimeOffset.UnixEpoch, null, null));
        _store.Tenants.Add(Tenant("acme"));
        _store.Tenants.Add(Tenant("other"));
    }

    private static Tenant Tenant(string id) => new(id, id, "https://pkgs.dev.azure.com/", "o/", "https://x/index.json", new TenantPolicy(14));

    // ---- Operator: everything works and reaches the store.

    [Fact]
    public async Task Operator_can_run_every_command_on_any_tenant()
    {
        var op = ActorScope.Operator;
        await _commands.CreateTenantAsync(op, new NewTenant("new", "New", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/p/index.json", 14, TenantOrigin.Operator), Ct);
        await _commands.AddPrefixAsync(op, "acme", "Contoso.", Ct);
        await _commands.AddOverrideAsync(op, "other", "left-pad", 3, Ct);
        await _commands.AddWaiverAsync(op, "acme", "pkg", "1.0.0", 7, "because", Ct);
        await _commands.IssueTokenAsync(op, "other", "ci", Ct);
        Assert.True(await _commands.RevokeTokenAsync(op, "t-other", Ct));
        Assert.Equal(new[] { "create new", "prefix acme Contoso.", "override other left-pad 3", "waiver acme pkg", "issue other", "revoke t-other" }, _store.Calls);
    }

    [Fact]
    public async Task Waiver_expiry_is_the_clock_plus_the_days()
    {
        var w = await _commands.AddWaiverAsync(ActorScope.Operator, "acme", "pkg", "*", 10, "r", Ct);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), w.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task Waiver_days_out_of_range_are_rejected(int days)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.AddWaiverAsync(ActorScope.Operator, "acme", "p", "*", days, "r", Ct));
        Assert.Empty(_store.Calls);
    }

    // ---- Tenant actor: own tenant works.

    [Fact]
    public async Task Tenant_actor_can_act_on_its_own_tenant()
    {
        await _commands.AddPrefixAsync(Acme, "acme", "Contoso.", Ct);
        await _commands.AddOverrideAsync(Acme, "acme", "p", 1, Ct);
        await _commands.AddWaiverAsync(Acme, "acme", "p", "1.0.0", 1, "urgent security fix", Ct);
        await _commands.IssueTokenAsync(Acme, "acme", "ci", Ct);
        Assert.True(await _commands.RevokeTokenAsync(Acme, "t-acme", Ct));
        Assert.Equal(5, _store.Calls.Count);
    }

    // ---- Negative tests: one per command. A refusal must also mean nothing was read or written.

    [Fact]
    public async Task CreateTenant_is_refused_for_a_tenant_actor_even_for_its_own_id()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _commands.CreateTenantAsync(Acme, new NewTenant("acme", "Acme", "https://pkgs.dev.azure.com/", "o/", "https://x/index.json", 14, TenantOrigin.Operator), Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task AddPrefix_is_refused_for_another_tenant()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddPrefixAsync(Acme, "other", "Contoso.", Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task AddOverride_is_refused_for_another_tenant()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddOverrideAsync(Acme, "other", "p", 0, Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task AddWaiver_is_refused_for_another_tenant()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddWaiverAsync(Acme, "other", "p", "*", 5, "r", Ct));
        Assert.Empty(_store.Calls);
        Assert.Null(_store.LastWaiverExpiry);
    }

    [Fact]
    public async Task IssueToken_is_refused_for_another_tenant()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.IssueTokenAsync(Acme, "other", "ci", Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task RevokeToken_is_refused_for_another_tenants_token_and_for_an_unknown_one_alike()
    {
        var other = await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RevokeTokenAsync(Acme, "t-other", Ct));
        var unknown = await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RevokeTokenAsync(Acme, "t-nope", Ct));
        Assert.Equal(other.Message, unknown.Message);   // no oracle for probing token ids
        Assert.Empty(_store.Calls);                                      // nothing was written
        Assert.All(_store.Reads, r => Assert.Equal("tokens acme", r));   // and the only read was the actor's own tenant
    }

    [Fact]
    public async Task Tenant_ids_are_matched_exactly_not_by_case_or_prefix()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddPrefixAsync(Acme, "ACME", "x.", Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddPrefixAsync(Acme, "acme2", "x.", Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddPrefixAsync(Acme, "", "x.", Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public void A_tenant_scope_needs_a_tenant_id()
    {
        Assert.Throws<ArgumentException>(() => ActorScope.ForTenant(" "));
    }

    // ---- Queries.

    [Fact]
    public async Task ListTenants_gives_the_operator_all_and_a_tenant_actor_only_its_own()
    {
        Assert.Equal(new[] { "acme", "other" }, (await _queries.ListTenantsAsync(ActorScope.Operator, Ct)).Select(t => t.Id));
        Assert.Equal(new[] { "acme" }, (await _queries.ListTenantsAsync(Acme, Ct)).Select(t => t.Id));
        Assert.Empty(await _queries.ListTenantsAsync(ActorScope.ForTenant("ghost"), Ct));
    }

    [Fact]
    public async Task AuditTail_operator_may_choose_a_tenant_or_all()
    {
        await _queries.AuditTailAsync(ActorScope.Operator, null, 10, Ct);
        Assert.Null(_store.LastAuditTenant);
        await _queries.AuditTailAsync(ActorScope.Operator, "other", 10, Ct);
        Assert.Equal("other", _store.LastAuditTenant);
    }

    [Fact]
    public async Task AuditTail_for_a_tenant_actor_defaults_to_its_own_tenant_and_never_all()
    {
        await _queries.AuditTailAsync(Acme, null, 10, Ct);
        Assert.Equal("acme", _store.LastAuditTenant);
    }

    [Fact]
    public async Task AuditTail_is_refused_for_another_tenant()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.AuditTailAsync(Acme, "other", 10, Ct));
        Assert.Empty(_store.Calls);
    }

    // ---- Issue #22: the commands that used to bypass the scope layer. Each refusal asserts the store saw nothing.

    private void AssertUntouched() { Assert.Empty(_store.Calls); Assert.Empty(_store.Reads); }

    [Fact]
    public async Task Operator_can_run_the_moved_commands()
    {
        var op = ActorScope.Operator;
        await _commands.SuspendTenantAsync(op, "acme", Ct);
        await _commands.ActivateTenantAsync(op, "acme", Ct);
        await _commands.SetCooldownAsync(op, "other", 3, Ct);
        await _commands.RemovePrefixAsync(op, "acme", "Contoso.", Ct);
        await _commands.RemoveOverrideAsync(op, "acme", "p", Ct);
        Assert.True(await _commands.RevokeWaiverAsync(op, "w9", Ct));
        await _commands.SetUpstreamIdentityAsync(op, "acme", new UpstreamIdentity("aaaaaaaa-0000-4000-8000-000000000000", "bbbbbbbb-0000-4000-8000-000000000000", "pem"), Ct);
        Assert.Single(await _queries.ListTokensAsync(op, "acme", Ct));
        Assert.NotNull((await _queries.ShowTenantAsync(op, "acme", Ct))?.Identity);
        Assert.Equal(2, (await _queries.ListIdentitiesAsync(op, Ct)).Count);
        Assert.Equal(new[] { "active acme False", "active acme True", "cooldown other 3", "unprefix acme Contoso.", "unoverride acme p", "unwaive * w9 by=operator", "identity-set acme", "list" }, _store.Calls);
    }

    [Fact] public async Task Suspend_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.SuspendTenantAsync(Acme, "other", Ct)); AssertUntouched(); }
    [Fact] public async Task Activate_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.ActivateTenantAsync(Acme, "other", Ct)); AssertUntouched(); }
    [Fact] public async Task Cooldown_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.SetCooldownAsync(Acme, "other", 1, Ct)); AssertUntouched(); }
    [Fact] public async Task RemovePrefix_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RemovePrefixAsync(Acme, "other", "x.", Ct)); AssertUntouched(); }
    [Fact] public async Task RemoveOverride_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RemoveOverrideAsync(Acme, "other", "p", Ct)); AssertUntouched(); }
    [Fact] public async Task TenantShow_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.ShowTenantAsync(Acme, "other", Ct)); AssertUntouched(); }
    [Fact] public async Task GetTenant_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.GetTenantAsync(Acme, "other", Ct)); AssertUntouched(); }
    [Fact] public async Task TokenList_is_refused_for_another_tenant() { await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.ListTokensAsync(Acme, "other", Ct)); AssertUntouched(); }

    [Fact]
    public async Task Identity_commands_are_operator_only()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.SetUpstreamIdentityAsync(Acme, "acme", new UpstreamIdentity("aaaaaaaa-0000-4000-8000-000000000000", "bbbbbbbb-0000-4000-8000-000000000000", "pem"), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.ListIdentitiesAsync(Acme, Ct));
        Assert.Throws<AccessDeniedException>(() => AdminCommands.RequireOperator(Acme));
        AdminCommands.RequireOperator(ActorScope.Operator);
        AssertUntouched();
    }

    [Fact]
    public async Task A_tenant_actor_never_gets_its_private_key_back_from_show()
    {
        var detail = await _queries.ShowTenantAsync(Acme, "acme", Ct);
        Assert.NotNull(detail);
        Assert.Null(detail!.Identity);
        Assert.DoesNotContain(_store.Reads, r => r.StartsWith("identity"));
    }

    private static Tenant WithWaiver(string id, string waiverId) =>
        Tenant(id) with { Policy = new TenantPolicy(14, null, new[] { new Waiver(waiverId, "p", "*", DateTimeOffset.MaxValue, "r", "x") }) };

    [Fact]
    public async Task RevokeWaiver_for_a_tenant_actor_needs_a_waiver_of_its_own_tenant()
    {
        _store.Tenants[0] = WithWaiver("acme", "w-acme");
        _store.Tenants[1] = WithWaiver("other", "w-other");
        var other = await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RevokeWaiverAsync(Acme, "w-other", Ct));
        var unknown = await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RevokeWaiverAsync(Acme, "w-nope", Ct));
        Assert.Equal(other.Message, unknown.Message);
        Assert.Empty(_store.Calls);                                   // nothing was written
        Assert.All(_store.Reads, r => Assert.Equal("get acme", r));   // and only the actor's own tenant was read
        Assert.True(await _commands.RevokeWaiverAsync(Acme, "w-acme", Ct));
        Assert.Equal(new[] { "unwaive acme w-acme by=tenant:acme" }, _store.Calls);
    }

    // ---- createdBy comes from the actor.

    [Fact]
    public async Task Waiver_created_by_is_the_actors_name()
    {
        await _commands.AddWaiverAsync(ActorScope.OperatorAs("olivia"), "acme", "p", "*", 1, "r", Ct);
        Assert.Equal("olivia", _store.LastWaiverCreatedBy);
        await _commands.AddWaiverAsync(ActorScope.ForTenant("acme", "alice@acme"), "acme", "p", "1.0.0", 1, "urgent security fix", Ct);
        Assert.Equal("alice@acme", _store.LastWaiverCreatedBy);
        await _commands.AddWaiverAsync(Acme, "acme", "p", "1.0.0", 1, "urgent security fix", Ct);
        Assert.Equal("tenant:acme", _store.LastWaiverCreatedBy);
    }

    // ---- Nits from the review.

    [Fact]
    public async Task A_null_tenant_id_is_refused_for_a_tenant_actor_and_rejected_for_the_operator()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddPrefixAsync(Acme, null!, "x.", Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.ListTokensAsync(Acme, null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.AddPrefixAsync(ActorScope.Operator, null!, "x.", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ListTokensAsync(ActorScope.Operator, " ", Ct));
        AssertUntouched();
    }

    [Fact]
    public async Task AuditTail_with_an_empty_tenant_is_refused_for_a_tenant_actor_and_never_means_all()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.AuditTailAsync(Acme, "", 10, Ct));
        Assert.Empty(_store.Calls);
        Assert.Equal("(not called)", _store.LastAuditTenant);
    }

    // ---- Developer counting.

    [Fact]
    public async Task IssueToken_defaults_to_a_developer_token_and_passes_the_kind_on()
    {
        await _commands.IssueTokenAsync(ActorScope.Operator, "acme", "ann", Ct);
        Assert.Equal(TokenKind.Developer, _store.LastKind);
        await _commands.IssueTokenAsync(Acme, "acme", "agents", Ct, TokenKind.Automation);
        Assert.Equal(TokenKind.Automation, _store.LastKind);
    }

    [Fact]
    public async Task ActiveDevelopers_is_scoped_and_checks_the_days_range_before_reading()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.ActiveDevelopersAsync(Acme, "other", 30, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.ActiveDevelopersAsync(Acme, null!, 30, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ActiveDevelopersAsync(ActorScope.Operator, "acme", 0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.ActiveDevelopersAsync(ActorScope.Operator, "acme", 3651, Ct));
        AssertUntouched();
        await _queries.ActiveDevelopersAsync(Acme, "acme", 30, Ct);
        await _queries.ActiveDevelopersAsync(ActorScope.Operator, "other", 1, Ct);
        Assert.Equal(new[] { "active acme 30", "active other 1" }, _store.Calls);
    }
}
