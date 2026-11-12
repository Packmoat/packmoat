using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>Shared-app identity mode: the commands are operator-only, validate GUIDs, and guard the bind.</summary>
public class SharedAppCommandsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Dir = "11111111-2222-4333-8444-555555555555";
    private const string AppId = "99999999-8888-4777-8666-555555555555";
    private readonly FakeStore _store = new();
    private readonly FakeSharedStore _shared = new();
    private readonly AdminCommands _commands;
    private readonly AdminQueries _queries;
    private static readonly ActorScope Op = ActorScope.Operator;
    private static readonly ActorScope Acme = ActorScope.ForTenant("acme");

    public SharedAppCommandsTests()
    {
        _commands = new AdminCommands(_store, _store, new FixedClock(), new InProcessRateCounter(new FixedClock()), _shared, new FakeInspector());
        _queries = new AdminQueries(_store, _store, _store, _store, _shared);
        _store.Tenants.Add(Tenant("acme", "acme-org/"));
        _store.Tenants.Add(Tenant("other", "other-org/"));
        _store.Tenants.Add(Tenant("noprefix", ""));
    }

    private static Tenant Tenant(string id, string prefix, string upstream = "https://pkgs.dev.azure.com/", string? feed = null) =>
        new(id, id, upstream, prefix, feed ?? upstream + prefix + "p/_packaging/f/nuget/v3/index.json", new TenantPolicy(14));

    [Fact]
    public async Task Bind_stores_the_directory_in_canonical_form()
    {
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir.ToUpperInvariant(), false, Ct);
        Assert.Contains($"bind acme {Dir}", _store.Calls);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("{11111111-2222-4333-8444-555555555555}")]
    [InlineData("11111111222243338444555555555555")]
    public async Task Bind_refuses_a_directory_that_is_not_a_guid(string dir)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "acme", dir, false, Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task Bind_refuses_a_tenant_without_a_path_prefix()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "noprefix", Dir, true, Ct));
        Assert.Contains("path prefix", ex.Message);
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("bind"));
    }

    [Fact]
    public async Task Two_shared_tenants_can_never_share_a_directory_not_even_with_the_override()
    {
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "other", Dir.ToUpperInvariant(), false, Ct));
        Assert.Contains("acme", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "other", Dir, true, Ct));
        Assert.DoesNotContain($"bind other {Dir}", _store.Calls);
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData(" not-a-guid ")]
    public async Task A_directory_stored_as_a_name_cannot_be_compared_and_blocks_the_bind_unless_overridden(string stored)
    {
        _store.Bindings["other"] = new UpstreamBinding(IdentityKind.Own, stored, AppId);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct));
        Assert.Contains("cannot be compared", ex.Message);
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, true, Ct);
        Assert.Contains($"bind acme {Dir}", _store.Calls);
    }

    [Fact]
    public async Task An_own_app_tenant_with_the_same_directory_written_differently_still_matches()
    {
        _store.Bindings["other"] = new UpstreamBinding(IdentityKind.Own, " " + Dir.ToUpperInvariant() + " ", AppId);
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct));
    }

    [Fact]
    public async Task Rebinding_the_same_tenant_to_its_own_directory_is_fine()
    {
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        Assert.Equal(2, _store.Calls.Count(c => c == $"bind acme {Dir}"));
    }

    [Fact]
    public async Task An_own_app_tenant_in_the_same_directory_also_blocks_the_bind()
    {
        _store.Bindings["other"] = new UpstreamBinding(IdentityKind.Own, Dir, AppId);
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct));
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, true, Ct);   // own + shared in one directory: allowed when the operator says so
        Assert.Contains($"bind acme {Dir}", _store.Calls);
    }

    [Fact]
    public async Task Bind_of_an_unknown_tenant_is_refused()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _commands.BindSharedIdentityAsync(Op, "ghost", Dir, false, Ct));
    }

    [Fact]
    public async Task Shared_commands_and_queries_are_operator_only_and_touch_nothing_when_refused()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.BindSharedIdentityAsync(Acme, "acme", Dir, false, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.SetSharedAppCredentialAsync(Acme, SharedAppSlot.Active, new SharedAppCredential(AppId, "pem"), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.PromoteSharedAppCredentialAsync(Acme, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.SharedAppCredentialsAsync(Acme, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.GetUpstreamBindingAsync(Acme, "acme", Ct));
        Assert.Empty(_store.Calls);
        Assert.Empty(_store.Reads);
        Assert.Empty(_shared.Calls);
    }

    [Fact]
    public async Task Set_validates_the_client_id_and_promote_moves_next_to_active()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential("packmoat", "pem"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId, " "), Ct));
        Assert.Empty(_shared.Calls);

        await _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId.ToUpperInvariant(), "pem-1"), Ct);
        Assert.Equal(AppId, _shared.Slots[SharedAppSlot.Active].ClientId);
        Assert.False(await _commands.PromoteSharedAppCredentialAsync(Op, Ct));   // nothing waiting

        await _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Next, new SharedAppCredential(AppId, "pem-2"), Ct);
        Assert.True(await _commands.PromoteSharedAppCredentialAsync(Op, Ct));
        Assert.Equal("pem-2", _shared.Slots[SharedAppSlot.Active].CertificatePem);
        Assert.False(_shared.Slots.ContainsKey(SharedAppSlot.Next));
        Assert.Single(await _queries.SharedAppCredentialsAsync(Op, Ct));
    }

    [Theory]
    [InlineData("a", "bbbbbbbb-0000-4000-8000-000000000000")]
    [InlineData("aaaaaaaa-0000-4000-8000-000000000000", "client")]
    public async Task Identity_set_now_validates_both_guids(string azureTenant, string clientId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.SetUpstreamIdentityAsync(Op, "acme", new UpstreamIdentity(azureTenant, clientId, "pem"), Ct));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task Show_gives_the_operator_the_binding_and_a_tenant_actor_nothing()
    {
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        Assert.Equal(IdentityKind.Shared, (await _queries.ShowTenantAsync(Op, "acme", Ct))!.Binding!.Kind);
        _store.Reads.Clear();
        var mine = await _queries.ShowTenantAsync(Acme, "acme", Ct);
        Assert.Null(mine!.Binding);
        Assert.Null(mine.Identity);
        Assert.DoesNotContain(_store.Reads, r => r.StartsWith("binding") || r.StartsWith("identity"));
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("e2e-test", true)]
    [InlineData("a_b-9", true)]
    [InlineData("shared-app:active", false)]
    [InlineData("shared-app:next", false)]
    [InlineData("-x", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    public void Tenant_ids_can_never_look_like_a_shared_app_context(string id, bool valid)
    {
        Assert.Equal(valid, AdminCommands.IsValidTenantId(id));
    }

    [Fact]
    public async Task Create_tenant_refuses_an_id_with_a_colon()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.CreateTenantAsync(Op, new NewTenant("shared-app:active", "x", "https://x/", "o/", "https://x/o/i.json", 14, TenantOrigin.Operator), Ct));
        Assert.Empty(_store.Calls);
    }

    // ---- URL rules for bind and create

    [Theory]
    [InlineData("acme-org", null, "https://pkgs.dev.azure.com/")]                                                    // no trailing slash
    [InlineData("acme-org/", "http://pkgs.dev.azure.com/acme-org/p/index.json", "http://pkgs.dev.azure.com/")]      // not https
    [InlineData("acme-org/", "https://pkgs.dev.azure.com/other-org/p/index.json", "https://pkgs.dev.azure.com/")]  // feed outside the prefix
    [InlineData("acme-org/", "https://pkgs.dev.azure.com/acme-org-secret/p/index.json", "https://pkgs.dev.azure.com/")]
    [InlineData("acme-org/", "https://evil.example/acme-org/p/index.json", "https://pkgs.dev.azure.com/")]         // another host
    [InlineData("acme-org/", "https://pkgs.dev.azure.com/acme-org/p/index.json?x=1", "https://pkgs.dev.azure.com/")]
    [InlineData("acme-org/", "https://pkgs.dev.azure.com/acme-org/p%2F/index.json", "https://pkgs.dev.azure.com/")]
    [InlineData("/acme-org/", null, "https://pkgs.dev.azure.com/")]
    [InlineData("acme-org/../", null, "https://pkgs.dev.azure.com/")]
    public async Task Bind_refuses_a_tenant_whose_urls_break_the_strict_rules(string prefix, string? feed, string upstream)
    {
        _store.Tenants.Add(Tenant("weird", prefix, upstream, feed));
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "weird", Dir, true, Ct));
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("bind"));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/", "acme-org", "https://pkgs.dev.azure.com/acme-org/p/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "acme-org/", "https://pkgs.dev.azure.com/other/p/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "acme-org/", "https://other.example/acme-org/p/index.json")]
    [InlineData("https://pkgs.dev.azure.com", "acme-org/", "https://pkgs.dev.azure.com/acme-org/p/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "", "https://other.example/p/index.json")]
    public async Task Create_refuses_a_prefix_without_a_slash_or_a_feed_outside_base_plus_prefix(string upstream, string prefix, string feed)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.CreateTenantAsync(Op, new NewTenant("newco", "N", upstream, prefix, feed, 14, TenantOrigin.Operator), Ct));
        Assert.Empty(_store.Calls);
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/", "acme-org/", "https://pkgs.dev.azure.com/acme-org/p/_packaging/f/nuget/v3/index.json")]
    [InlineData("http://127.0.0.1:5000/", "org/", "http://127.0.0.1:5000/org/index.json")]   // own-app tenants may still use an http test host
    [InlineData("https://x/", "", "https://x/i.json")]                                        // own-app: an empty prefix stays allowed
    public async Task Create_accepts_well_formed_urls(string upstream, string prefix, string feed)
    {
        await _commands.CreateTenantAsync(Op, new NewTenant("newco", "N", upstream, prefix, feed, 14, TenantOrigin.Operator), Ct);
        Assert.Contains("create newco", _store.Calls);
    }

    [Theory]
    [InlineData("acme/feed/x", "acme/", true)]
    [InlineData("acme/feed/x", "acme", true)]            // an older row without its slash is read as "acme/"
    [InlineData("acme-secret/feed/x", "acme", false)]
    [InlineData("acme-secret/feed/x", "acme/", false)]
    [InlineData("ACME/feed", "acme/", true)]
    [InlineData("acme", "acme/", false)]
    [InlineData("anything/x", "", true)]
    public void The_proxy_prefix_check_matches_whole_segments(string path, string prefix, bool allowed)
    {
        Assert.Equal(allowed, TenantUrlRules.PathIsUnderPrefix(path, prefix));
    }

    // ---- promote and set check the certificate

    [Theory]
    [InlineData("pem-expired")]
    [InlineData("pem-nokey")]
    [InlineData("garbage")]
    public async Task Set_refuses_an_expired_keyless_or_unreadable_certificate(string pem)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Next, new SharedAppCredential(AppId, pem), Ct));
        Assert.Empty(_shared.Slots);
    }

    [Theory]
    [InlineData("pem-expired")]
    [InlineData("pem-nokey")]
    public async Task Promote_rechecks_the_next_certificate_and_changes_nothing_when_it_is_unusable(string pem)
    {
        await _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId, "pem-active"), Ct);
        _shared.Slots[SharedAppSlot.Next] = new SharedAppCredential(AppId, pem);   // got there somehow (an older build, or it expired since)

        await Assert.ThrowsAsync<ArgumentException>(() => _commands.PromoteSharedAppCredentialAsync(Op, Ct));

        Assert.Equal("pem-active", _shared.Slots[SharedAppSlot.Active].CertificatePem);
        Assert.True(_shared.Slots.ContainsKey(SharedAppSlot.Next));
    }

    [Fact]
    public async Task Without_an_inspector_shared_credentials_cannot_be_set_or_promoted()
    {
        var bare = new AdminCommands(_store, _store, new FixedClock(), new InProcessRateCounter(new FixedClock()), _shared);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bare.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId, "pem"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bare.PromoteSharedAppCredentialAsync(Op, Ct));
        Assert.Empty(_shared.Slots);
    }

    // ---- the verification record

    [Fact]
    public async Task A_verification_records_the_current_directory_feed_and_client_id()
    {
        await _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId, "pem"), Ct);
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        var feed = _store.Tenants[0].UpstreamFeedIndexUrl;

        var subject = await _commands.ReadSharedCheckSubjectAsync(Op, "acme", Ct);
        var v = await _commands.RecordSharedVerificationAsync(ActorScope.OperatorAs("olivia"), subject, Ct);

        Assert.Equal(("acme", Dir, feed, AppId, "olivia"), (v.TenantId, v.AzureTenantId, v.FeedIndexUrl, v.ClientId, v.VerifiedBy));
        var binding = _store.Bindings["acme"];
        Assert.True(v.Matches(binding, feed, AppId));
        Assert.False(v.Matches(binding with { AzureTenantId = "22222222-2222-4333-8444-555555555555" }, feed, AppId));
        Assert.False(v.Matches(binding, feed + "x", AppId));
        Assert.False(v.Matches(binding, feed, "88888888-8888-4777-8666-555555555555"));
        Assert.False(v.Matches(new UpstreamBinding(IdentityKind.Own, Dir, AppId), feed, AppId));
    }

    [Fact]
    public void Directory_and_client_id_compare_ignoring_case()
    {
        var v = new SharedAppVerification("acme", Dir.ToUpperInvariant(), "https://f/", AppId.ToUpperInvariant(), DateTimeOffset.UnixEpoch, "op");
        Assert.True(v.Matches(new UpstreamBinding(IdentityKind.Shared, Dir, null), "https://f/", AppId));
        Assert.False(v.Matches(new UpstreamBinding(IdentityKind.Shared, Dir, null), "https://F/", AppId));   // the feed URL is compared exactly
    }

    [Fact]
    public async Task A_verification_needs_a_shared_binding_an_active_credential_and_the_operator()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.ReadSharedCheckSubjectAsync(Op, "acme", Ct));   // not shared
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.ReadSharedCheckSubjectAsync(Op, "acme", Ct));   // no active credential
        var subject = new AdminCommands.SharedCheckSubject("acme", Dir, "https://f/", AppId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.RecordSharedVerificationAsync(Acme, subject, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.ReadSharedCheckSubjectAsync(Acme, "acme", Ct));
        Assert.Empty(_shared.Verifications);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_what_was_checked_changed_meanwhile()
    {
        await _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId, "pem"), Ct);
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        var subject = await _commands.ReadSharedCheckSubjectAsync(Op, "acme", Ct);
        _shared.StillCurrent = false;
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _commands.RecordSharedVerificationAsync(Op, subject, Ct));
        Assert.Contains("changed while the check ran", ex.Message);
        Assert.Empty(_shared.Verifications);
    }

    [Fact]
    public async Task Withdraw_removes_only_the_record_it_made()
    {
        await _commands.SetSharedAppCredentialAsync(Op, SharedAppSlot.Active, new SharedAppCredential(AppId, "pem"), Ct);
        await _commands.BindSharedIdentityAsync(Op, "acme", Dir, false, Ct);
        var subject = await _commands.ReadSharedCheckSubjectAsync(Op, "acme", Ct);
        var mine = await _commands.RecordSharedVerificationAsync(Op, subject, Ct);
        var newer = mine with { VerifiedAt = mine.VerifiedAt.AddMinutes(1), VerifiedBy = "someone else" };
        _shared.Verifications["acme"] = newer;

        Assert.False(await _commands.WithdrawSharedVerificationAsync(Op, mine, Ct));
        Assert.Equal(newer, _shared.Verifications["acme"]);
        Assert.True(await _commands.WithdrawSharedVerificationAsync(Op, newer, Ct));
        Assert.Empty(_shared.Verifications);
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.WithdrawSharedVerificationAsync(Acme, newer, Ct));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/../other/_packaging/f/nuget/v3/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/./p/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/%2e%2e/other/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/%2E%2E/other/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/p/..")]
    [InlineData("https://pkgs.dev.azure.com/x/../", "acme/", "https://pkgs.dev.azure.com/x/../acme/p/index.json")]
    [InlineData("https://pkgs.dev.azure.com/./", "acme/", "https://pkgs.dev.azure.com/./acme/p/index.json")]
    [InlineData("https://pkgs.dev.azure.com:443/", "acme/", "https://pkgs.dev.azure.com:443/acme/p/index.json")]   // not in resolved form
    public void Dot_segments_raw_or_encoded_are_refused_in_the_base_and_the_feed(string upstream, string prefix, string feed)
    {
        Assert.NotNull(TenantUrlRules.Check(upstream, prefix, feed, requireHttps: true, requirePrefix: true));
        Assert.NotNull(TenantUrlRules.Check(upstream, prefix, feed, requireHttps: false, requirePrefix: false));
    }

    [Fact]
    public async Task Bind_refuses_a_feed_that_climbs_out_of_the_prefix()
    {
        _store.Tenants.Add(Tenant("climb", "acme/", feed: "https://pkgs.dev.azure.com/acme/../other/_packaging/f/nuget/v3/index.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.BindSharedIdentityAsync(Op, "climb", Dir, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.CreateTenantAsync(Op,
            new NewTenant("climb2", "C", "https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/../other/index.json", 14, TenantOrigin.Operator), Ct));
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("bind") || c.StartsWith("create"));
    }
}
