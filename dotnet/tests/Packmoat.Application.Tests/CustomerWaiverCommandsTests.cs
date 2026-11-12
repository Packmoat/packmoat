using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>
/// The customer site's waiver commands and queries. The role and the fresh sign-in are enforced HERE (the core's
/// <see cref="ActorScope"/> carries neither), the tenant comes only from the actor, and every write leaves its admin_actions intent row
/// before anything changes. The core limits themselves are in CustomerWaiverTests.
/// </summary>
public class CustomerWaiverCommandsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string AcmeDir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string GlobexDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string GoodReason = "urgent security fix";
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GraceOid = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");

    private readonly MovableClock _clock = new();
    private readonly FakeStore _store = new();
    private readonly FakeDirectories _directories = new();
    private readonly FakeMembers _members = new();
    private readonly FakeActionLog _log = new();
    private readonly RecordingAlerts _alerts = new();
    private readonly CustomerWaiverCommands _commands;
    private readonly CustomerWaiverQueries _queries;

    private static readonly CustomerActor Grace = new("acme", GraceOid, CustomerRole.Admin, "Grace", AcmeDir, SignedInAt: Start);
    private static readonly CustomerActor Ada = new("acme", Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), CustomerRole.Developer, "Ada", AcmeDir, SignedInAt: Start);
    private static readonly CustomerActor Hedy = new("acme", Guid.Parse("22222222-2222-4222-8222-222222222222"), CustomerRole.Admin, "Hedy", AcmeDir, SignedInAt: Start);
    private static readonly CustomerActor Mallory = new("globex", Guid.Parse("11111111-1111-4111-8111-111111111111"), CustomerRole.Admin, "Mallory", GlobexDir, SignedInAt: Start);

    public CustomerWaiverCommandsTests()
    {
        foreach (var id in new[] { "acme", "globex" })
            _store.Tenants.Add(new Tenant(id, id.ToUpperInvariant(), "https://pkgs.dev.azure.com/", id + "/", "https://x/index.json", new TenantPolicy(14, new[] { "Contoso.*" })));
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "acme", Start, "op");
        _directories.Rows[GlobexDir] = new DirectoryBinding(GlobexDir, "globex", Start, "op");
        _commands = new CustomerWaiverCommands(_store, _store, _directories, _log, _clock, new InProcessRateCounter(_clock), _alerts);
        _queries = new CustomerWaiverQueries(_store, _directories, _members, _clock);
    }

    private static async Task<CustomerTokenRefusal> Refused(Func<Task> act) => (await Assert.ThrowsAsync<CustomerTokenRefusedException>(act)).Reason;

    private Task<Waiver> Add(CustomerActor actor, string package = "Moq", string version = "4.21.0", int days = 7, string reason = GoodReason) =>
        _commands.AddAsync(actor, package, version, days, reason, Ct);

    private void NothingWritten()
    {
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("customer-waiver", StringComparison.Ordinal) || c.StartsWith("waiver", StringComparison.Ordinal) || c.StartsWith("unwaive", StringComparison.Ordinal));
        Assert.Empty(_log.Rows);
    }

    private void GiveAcme(params Waiver[] waivers) =>
        _store.Tenants[0] = _store.Tenants[0] with { Policy = new TenantPolicy(14, new[] { "Contoso.*" }, waivers) };

    // ---------------------------------------------------------------- add

    [Fact]
    public async Task An_admin_adds_a_waiver_for_their_own_tenant_named_after_themselves_and_it_is_recorded_first()
    {
        var w = await Add(Grace, "  Moq ", "4.21", 7, "  " + GoodReason + "  ");
        Assert.Equal(("moq", "4.21.0", WaiverSource.Customer), (w.PackageId, w.Version, w.Source));
        Assert.Equal($"Grace ({GraceOid:D})", w.CreatedBy);
        Assert.Equal(new[] { $"customer-waiver acme moq 4.21.0 max={WaiverRules.MaxActiveCustomerWaivers}" }, _store.Calls);
        Assert.Equal(Start.AddDays(7), _store.LastWaiverExpiry);

        Assert.Equal(new[] { "waiver.add", "waiver.add.id" }, _log.Rows.Select(r => r.Action));
        Assert.All(_log.Rows, r =>
        {
            Assert.Equal(("app", "acme", AcmeDir, GraceOid.ToString("D"), "Grace"), (r.Site, r.TenantId, r.ActorTid, r.ActorOid, r.ActorName));
        });
        Assert.Equal($"package=moq; version=4.21.0; days=7; reason=\"{GoodReason}\"", _log.Rows[0].Detail);
        Assert.Equal("id=" + w.Id, _log.Rows[1].Detail);
    }

    [Fact]   // mutation: drop the role check in CustomerWaiverCommands (the core has none)
    public async Task A_developer_cannot_add_a_waiver_and_nothing_is_read_or_written()
    {
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => Add(Ada)));
        NothingWritten();
        Assert.Empty(_store.Reads);
    }

    [Theory]   // mutation: drop the freshness check in CustomerWaiverCommands
    [InlineData(16)]
    [InlineData(60 * 24)]
    public async Task A_stale_admin_cannot_add_a_waiver(int minutesLater)
    {
        _clock.UtcNow = Start.AddMinutes(minutesLater);
        Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => Add(Grace)));
        NothingWritten();
    }

    [Fact]
    public async Task An_admin_whose_sign_in_time_is_unknown_cannot_add_a_waiver()
    {
        Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => Add(Grace with { SignedInAt = null })));
        NothingWritten();
    }

    [Fact]
    public async Task An_admin_fourteen_minutes_after_signing_in_may_still_add()
    {
        _clock.UtcNow = Start.AddMinutes(14);
        Assert.Equal("moq", (await Add(Grace)).PackageId);
    }

    [Fact]   // mutation: take the tenant from anywhere but the actor, or skip the binding check
    public async Task The_tenant_is_only_the_actors_and_its_directory_must_still_be_bound_to_it()
    {
        // Grace claims acme, but her directory is now bound to globex: refused, nothing written to either tenant
        _directories.Rows[AcmeDir] = new DirectoryBinding(AcmeDir, "globex", Start, "op");
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => Add(Grace)));
        NothingWritten();
        // and Mallory's waivers land in globex only
        await Add(Mallory);
        Assert.Equal(new[] { $"customer-waiver globex moq 4.21.0 max={WaiverRules.MaxActiveCustomerWaivers}" }, _store.Calls);
    }

    [Fact]
    public async Task A_suspended_tenant_cannot_add_waivers()
    {
        _store.Tenants[0] = _store.Tenants[0] with { Active = false };
        Assert.Equal(CustomerTokenRefusal.TenantSuspended, await Refused(() => Add(Grace)));
        NothingWritten();
    }

    [Theory]   // bad input is refused before the intent row: nothing a person typed reaches admin_actions unless it is a valid waiver request
    [InlineData("Moq", "*", 7, GoodReason, WaiverRefusal.BadVersion)]
    [InlineData("Moq", "4.*", 7, GoodReason, WaiverRefusal.BadVersion)]
    [InlineData("<script>", "4.21.0", 7, GoodReason, WaiverRefusal.BadPackage)]
    [InlineData("Moq", "4.21.0", 0, GoodReason, WaiverRefusal.BadExpiry)]
    [InlineData("Moq", "4.21.0", 31, GoodReason, WaiverRefusal.BadExpiry)]
    [InlineData("Moq", "4.21.0", 7, "short", WaiverRefusal.BadReason)]
    [InlineData("Moq", "4.21.0", 7, "fix\nCVE-2026-1 now", WaiverRefusal.BadReason)]
    public async Task Bad_input_is_refused_and_nothing_is_recorded(string package, string version, int days, string reason, WaiverRefusal expected)
    {
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => Add(Grace, package, version, days, reason));
        Assert.Equal(expected, ex.Reason);
        NothingWritten();
    }

    [Fact]   // mutation: write the intent row after the insert
    public async Task A_waiver_that_cannot_be_recorded_is_not_made()
    {
        _log.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Add(Grace));
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("customer-waiver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_insert_leaves_a_failed_row_with_the_reason_and_rethrows()
    {
        _store.NextCustomerOutcome = WaiverInsertOutcome.Duplicate;
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => Add(Grace));
        Assert.Equal(WaiverRefusal.Duplicate, ex.Reason);
        Assert.Equal(new[] { "waiver.add", "waiver.add.failed" }, _log.Rows.Select(r => r.Action));
        Assert.StartsWith("reason=duplicate; package=moq;", _log.Rows[1].Detail);
    }

    [Fact]   // A duplicate while the tenant is AT the cap says "already waived", not "too many"
    public async Task A_duplicate_at_the_cap_says_already_waived()
    {
        _store.NextCustomerOutcome = WaiverInsertOutcome.Duplicate;
        _store.ActiveCustomerAfter = WaiverRules.MaxActiveCustomerWaivers;
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => Add(Grace));
        Assert.Equal(WaiverRefusal.Duplicate, ex.Reason);
        Assert.Contains("already has an active waiver", ex.Message);
        Assert.DoesNotContain("25", ex.Message);
    }

    [Fact]
    public async Task An_internal_package_is_refused_after_the_intent_row_and_marked_failed()
    {
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => Add(Grace, "Contoso.Core"));
        Assert.Equal(WaiverRefusal.InternalPrefix, ex.Reason);
        Assert.Equal(new[] { "waiver.add", "waiver.add.failed" }, _log.Rows.Select(r => r.Action));
        Assert.StartsWith("reason=internal-prefix;", _log.Rows[1].Detail);
    }

    [Fact]   // mutation: no quoting of the reason
    public async Task A_reason_cannot_forge_a_field_in_the_record()
    {
        await Add(Grace, reason: "fix\"; id=wvr_000000000000; x=\"y");
        Assert.EndsWith("reason=\"fix\\\"; id=wvr_000000000000; x=\\\"y\"", _log.Rows[0].Detail);
    }

    [Fact]   // mutation: no rate limit on adds
    public async Task Adds_are_rate_limited_per_person()
    {
        for (var i = 0; i < CustomerWaiverRules.MaxAddsPerWindow; i++) await Add(Grace, "pkg" + i);
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => Add(Grace, "one-more")));
        await Add(Hedy, "hedys-pkg");   // another Admin of the SAME tenant and directory is not blocked
        await Add(Mallory);   // nor someone in another tenant
        _clock.UtcNow = Start.AddMinutes(14);   // still fresh, still the same window
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => Add(Grace, "one-more")));
    }

    // ---------------------------------------------------------------- revoke

    [Fact]
    public async Task An_admin_revokes_a_waiver_of_their_tenant_operator_made_ones_too_and_it_is_recorded_first()
    {
        GiveAcme(new Waiver("wvr_0000000000a1", "moq", "4.21.0", Start.AddDays(3), "op said so", "olivia"));
        Assert.True(await _commands.RevokeAsync(Grace, "wvr_0000000000a1", Ct));
        Assert.Equal(new[] { $"unwaive acme wvr_0000000000a1 by=Grace ({GraceOid:D})" }, _store.Calls);
        var row = Assert.Single(_log.Rows);
        Assert.Equal(("waiver.revoke", "acme", "app"), (row.Action, row.TenantId, row.Site));
        Assert.Equal("waiver=wvr_0000000000a1; package=moq; version=4.21.0; source=operator", row.Detail);
    }

    [Theory]   // mutation: drop the tenant check (the policy lookup) before revoking
    [InlineData("wvr_0000000000b2")]   // globex's
    [InlineData("wvr_0000000000ff")]   // nobody's
    [InlineData("wvr_0000000000A1")]   // not the shape (upper case)
    [InlineData("wvr_0000000000a1 ")]
    [InlineData("tok_0000000000a1")]
    [InlineData("")]
    public async Task Another_tenants_waiver_an_unknown_id_and_a_bad_id_all_look_the_same_and_change_nothing(string id)
    {
        GiveAcme(new Waiver("wvr_0000000000a1", "moq", "4.21.0", Start.AddDays(3), GoodReason, "x"));
        _store.Tenants[1] = _store.Tenants[1] with { Policy = new TenantPolicy(14, null, new[] { new Waiver("wvr_0000000000b2", "moq", "4.21.0", Start.AddDays(3), GoodReason, "x") }) };
        Assert.False(await _commands.RevokeAsync(Grace, id, Ct));
        NothingWritten();
    }

    [Fact]
    public async Task A_developer_or_a_stale_admin_cannot_revoke()
    {
        GiveAcme(new Waiver("wvr_0000000000a1", "moq", "4.21.0", Start.AddDays(3), GoodReason, "x"));
        Assert.Equal(CustomerTokenRefusal.AdminOnly, await Refused(() => _commands.RevokeAsync(Ada, "wvr_0000000000a1", Ct)));
        _clock.UtcNow = Start.AddMinutes(16);
        Assert.Equal(CustomerTokenRefusal.FreshSignInRequired, await Refused(() => _commands.RevokeAsync(Grace, "wvr_0000000000a1", Ct)));
        NothingWritten();
    }

    [Fact]
    public async Task A_revoke_that_cannot_be_recorded_does_not_happen()
    {
        GiveAcme(new Waiver("wvr_0000000000a1", "moq", "4.21.0", Start.AddDays(3), GoodReason, "x"));
        _log.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _commands.RevokeAsync(Grace, "wvr_0000000000a1", Ct));
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("unwaive", StringComparison.Ordinal));
    }

    [Fact]   // mutation: no rate limit on revokes
    public async Task Revokes_are_rate_limited_per_person()
    {
        for (var i = 0; i < CustomerWaiverRules.MaxRevokesPerWindow; i++) await _commands.RevokeAsync(Grace, "wvr_0000000000ff", Ct);
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, await Refused(() => _commands.RevokeAsync(Grace, "wvr_0000000000ff", Ct)));
    }

    // ---------------------------------------------------------------- the list

    private static WaiverRecord Row(string id, DateTimeOffset expires, DateTimeOffset? revoked = null, DateTimeOffset? created = null) =>
        new(id, "moq", "4.21.0", expires, GoodReason, "x", created ?? Start.AddDays(-1), WaiverSource.Customer, revoked, revoked is null ? null : "y");

    [Fact]
    public async Task Admins_see_the_active_list_and_the_history_and_developers_see_only_the_active_list()
    {
        _store.WaiverRows.AddRange(new[]
        {
            Row("wvr_000000000001", Start.AddDays(3)),
            Row("wvr_000000000002", Start),   // expires exactly now: ended
            Row("wvr_000000000003", Start.AddDays(3), revoked: Start.AddHours(-1)),
        });
        var admin = await _queries.ListAsync(Grace, Ct);
        Assert.Equal(new[] { "wvr_000000000001" }, admin.Active.Select(w => w.Id));
        Assert.Equal(new[] { "wvr_000000000002", "wvr_000000000003" }, admin.History.Select(w => w.Id));
        Assert.True(admin.IsAdmin);

        var dev = await _queries.ListAsync(Ada, Ct);
        Assert.Equal(new[] { "wvr_000000000001" }, dev.Active.Select(w => w.Id));
        Assert.Empty(dev.History);
        Assert.False(dev.IsAdmin);
        Assert.All(_store.Reads.Where(r => r.StartsWith("waivers", StringComparison.Ordinal)), r => Assert.Equal($"waivers acme {AdminQueries.WaiverListLimit}", r));
    }

    [Fact]   // the operator's own name never reaches a customer page
    public async Task Names_are_shown_only_for_members_of_the_tenant_and_the_operator_is_always_Packmoat()
    {
        const string member = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa";
        const string stranger = "bbbbbbbb-1111-4111-8111-bbbbbbbbbbbb";
        await _members.UpsertAsync(AcmeDir, member, "acme", "Grace", Start, Ct);
        await _members.UpsertAsync(GlobexDir, stranger, "globex", "Mallory", Start, Ct);
        WaiverRecord R(string id, WaiverSource source, string createdBy, string? revokedBy) =>
            new(id, "moq", "4.21.0", Start.AddDays(3), GoodReason, createdBy, Start.AddDays(-1), source, revokedBy is null ? null : Start.AddHours(-1), revokedBy);
        _store.WaiverRows.AddRange(new[]
        {
            R("wvr_000000000001", WaiverSource.Operator, $"Olivia Operator ({member})", null),   // operator-made: Packmoat, whatever the text
            R("wvr_000000000002", WaiverSource.Customer, $"Grace ({member})", null),
            R("wvr_000000000003", WaiverSource.Customer, $"Grace ({member})", "Olivia Operator"),   // an operator revoke from the admin site or CLI
            R("wvr_000000000004", WaiverSource.Customer, $"Grace ({member})", $"Olivia ({stranger})"),   // the shape, but not a member here
            R("wvr_000000000005", WaiverSource.Customer, $"Grace ({member})", $"Grace ({member})"),
            R("wvr_000000000006", WaiverSource.Operator, "olivia", "olivia"),
            R("wvr_000000000007", WaiverSource.Customer, $"Grace ({member}) ", null),   // not exactly the shape
        });

        var list = await _queries.ListAsync(Grace, Ct);
        var all = list.Active.Concat(list.History).ToDictionary(w => w.Id);
        Assert.Equal("Packmoat", all["wvr_000000000001"].CreatedBy);
        Assert.Equal("Grace", all["wvr_000000000002"].CreatedBy);
        Assert.Equal("Packmoat", all["wvr_000000000003"].RevokedBy);
        Assert.Equal("Packmoat", all["wvr_000000000004"].RevokedBy);
        Assert.Equal("Grace", all["wvr_000000000005"].RevokedBy);
        Assert.Equal(("Packmoat", "Packmoat"), (all["wvr_000000000006"].CreatedBy, all["wvr_000000000006"].RevokedBy));
        Assert.Equal("Packmoat", all["wvr_000000000007"].CreatedBy);
        Assert.DoesNotContain(all.Values, w => (w.CreatedBy + w.RevokedBy).Contains("Olivia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_stale_admin_may_still_read_the_list()
    {
        _clock.UtcNow = Start.AddDays(1);
        Assert.True((await _queries.ListAsync(Grace, Ct)).IsAdmin);
    }

    [Fact]
    public async Task The_list_needs_a_bound_directory()
    {
        _directories.Rows.Remove(AcmeDir);
        Assert.Equal(CustomerTokenRefusal.NotLinked, await Refused(() => _queries.ListAsync(Ada, Ct)));
        Assert.DoesNotContain(_store.Reads, r => r.StartsWith("waivers", StringComparison.Ordinal));
    }
}
