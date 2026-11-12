using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Auth;
using static Packmoat.Host.Tests.ProxyFixture;
using static Packmoat.Host.Tests.LimiterKeys;

namespace Packmoat.Host.Tests;

/// <summary>Test keys: a real address gets its real address and network keys; a name like "a" is its own address and network.</summary>
internal static class LimiterKeys
{
    public static ClientKey Key(string address) => IPAddress.TryParse(address, out var ip) ? ClientKey.Of(ip) : new ClientKey(address, address);
}

/// <summary>Limiter keys are /64 for IPv6, memory is bounded, and parallel guesses cannot overshoot the backstop.</summary>
public class ClientAddressTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]                         // IPv4 mapped into IPv6 is the IPv4 address
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("fe80::1%3", "fe80::/64")]                                    // the scope id is dropped too
    public void An_address_becomes_its_key(string address, string key) => Assert.Equal(key, ClientAddress.Key(IPAddress.Parse(address)));

    [Fact]
    public void A_missing_address_is_unknown() => Assert.Equal("unknown", ClientAddress.Key((IPAddress?)null));

    [Fact]
    public void Two_addresses_in_one_slash_64_share_a_key_and_the_next_slash_64_does_not()
    {
        Assert.Equal(ClientAddress.Key(IPAddress.Parse("2001:db8:0:1::1")), ClientAddress.Key(IPAddress.Parse("2001:db8:0:1:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(ClientAddress.Key(IPAddress.Parse("2001:db8:0:1::1")), ClientAddress.Key(IPAddress.Parse("2001:db8:0:2::1")));
    }

    [Fact]
    public void A_mapped_ipv4_address_is_not_lumped_with_other_mapped_addresses()
    {
        Assert.NotEqual(ClientAddress.Key(IPAddress.Parse("::ffff:203.0.113.7")), ClientAddress.Key(IPAddress.Parse("::ffff:198.51.100.9")));
    }

    // The network an address falls back to when the guess limiter's address map is full.
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.0/24")]
    [InlineData("203.0.113.255", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.0/24")]                      // IPv4 mapped into IPv6 is the IPv4 /24
    [InlineData("2001:db8:abcd:ffff:aaaa:bbbb:cccc:dddd", "2001:db8:abcd::/48")]
    [InlineData("2001:db8:abcd:1::1", "2001:db8:abcd::/48")]
    [InlineData("fe80::1%3", "fe80::/48")]                                    // the scope id is dropped too
    public void An_address_becomes_its_network_key(string address, string key) => Assert.Equal(key, ClientAddress.NetworkKey(IPAddress.Parse(address)));

    [Fact]
    public void A_missing_address_has_an_unknown_network() => Assert.Equal(new ClientKey("unknown", "unknown"), ClientKey.Of(null));

    [Fact]
    public void A_client_key_carries_both_keys() =>
        Assert.Equal(new ClientKey("2001:db8:1:2::/64", "2001:db8:1::/48"), ClientKey.Of(IPAddress.Parse("2001:db8:1:2::5")));
}

/// <summary>Once the address map is full, new addresses are held to the same rules by their /48 or /24.</summary>
public class TokenGuessLimiterNetworkOverflowTests
{
    private readonly FakeClock _clock = new();
    private readonly ListLogger<TokenGuessLimiter> _log = new();

    private TokenGuessLimiter Limiter(int maxWrongAttempts = TokenGuessLimiter.DefaultMaxWrongAttempts, int maxAddresses = 1,
        int maxNetworks = TokenGuessLimiter.DefaultMaxNetworks) =>
        new(_clock, maxWrongAttempts: maxWrongAttempts, maxAddresses: maxAddresses, maxNetworks: maxNetworks, log: _log);

    /// <summary>Fills the one-slot address map with a guesser far from every network the tests use.</summary>
    private static void Fill(TokenGuessLimiter limiter) => limiter.RecordWrongToken(Key("100.64.0.1"), "fill");

    [Fact]
    public void A_full_map_holds_a_slash_48_to_twenty_distinct_tokens_and_another_slash_48_proceeds()
    {
        var limiter = Limiter();
        Fill(limiter);
        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++) limiter.RecordWrongToken(Key($"2001:db8:1:{i + 1:x}::1"), "t" + i);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(limiter.TryBeginLookup(Key("2001:db8:1:99::1"), out _, out var wait));
        Assert.Equal(50, wait);
        Assert.Equal(50, limiter.RetryAfterSeconds(Key("2001:db8:1:aa::1")));

        Assert.True(limiter.TryBeginLookup(Key("2001:db8:2:1::1"), out _, out _));
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void A_full_map_holds_an_ipv4_slash_24_to_twenty_distinct_tokens_and_another_slash_24_proceeds()
    {
        var limiter = Limiter();
        Fill(limiter);
        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++) limiter.RecordWrongToken(Key($"198.51.100.{i + 1}"), "t" + i);

        Assert.False(limiter.TryBeginLookup(Key("198.51.100.200"), out _, out var wait));
        Assert.Equal(60, wait);
        Assert.Null(limiter.RetryAfterSeconds(Key("198.51.101.1")));
        Assert.True(limiter.TryBeginLookup(Key("198.51.101.1"), out _, out _));
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void Fifty_concurrent_lookups_from_one_slash_48_with_a_backstop_of_ten_let_exactly_ten_through()
    {
        var limiter = Limiter(maxWrongAttempts: 10);
        Fill(limiter);
        var through = 0;
        Parallel.For(0, 50, i =>
        {
            if (limiter.TryBeginLookup(Key($"2001:db8:7:{i + 1:x}::1"), out _, out _)) Interlocked.Increment(ref through);
        });
        Assert.Equal(10, through);
        Assert.Equal(10, limiter.NetworkInFlightFor("2001:db8:7::/48"));
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void Valid_lookups_while_the_map_is_full_leave_no_network_entry()
    {
        var limiter = Limiter();
        Fill(limiter);
        var held = new List<TokenGuessLimiter.Lookup>();
        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryBeginLookup(Key($"2001:db8:{i + 10:x}:1::1"), out var l, out _));
            held.Add(l);
        }
        foreach (var l in held) limiter.EndLookup(l, null);
        Assert.Equal(0, limiter.NetworkCount);
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void A_request_with_no_token_creates_nothing_but_a_blocked_slash_48_still_waits()
    {
        var limiter = Limiter();
        Fill(limiter);
        for (var i = 0; i < 1000; i++) Assert.Null(limiter.RetryAfterSeconds(Key($"2001:db8:{i / 16 + 100:x}:{i % 16 + 1:x}::1")));
        Assert.Equal(1, limiter.AddressCount);
        Assert.Equal(0, limiter.NetworkCount);

        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++) limiter.RecordWrongToken(Key($"2001:db8:9:{i + 1:x}::1"), "t" + i);
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(40, limiter.RetryAfterSeconds(Key("2001:db8:9:ffff::1")));
    }

    [Fact]
    public void When_the_network_map_is_full_too_new_networks_are_untracked_with_one_warning_per_kind_per_window()
    {
        var limiter = Limiter(maxNetworks: 1);
        Fill(limiter);
        limiter.RecordWrongToken(Key("198.51.100.1"), "x");   // takes the one network slot
        Assert.Equal(1, limiter.NetworkCount);

        for (var i = 0; i < 30; i++) limiter.RecordWrongToken(Key($"192.0.2.{i + 1}"), "t" + i);
        Assert.Null(limiter.RetryAfterSeconds(Key("192.0.2.1")));
        Assert.True(limiter.TryBeginLookup(Key("192.0.2.1"), out _, out _));
        Assert.Equal(1, limiter.NetworkCount);
        Assert.Equal(1, limiter.AddressCount);

        _clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 5; i++) limiter.RecordWrongToken(Key($"192.0.3.{i + 1}"), "t" + i);

        Assert.Equal(2, _log.Lines.Count(l => l.Level == LogLevel.Warning));
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("tracking new addresses by network"));
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("new networks are not tracked"));
    }

    [Fact]
    public void After_the_window_both_maps_drop_expired_entries()
    {
        var limiter = Limiter();
        Fill(limiter);
        for (var i = 0; i < 50; i++) limiter.RecordWrongToken(Key($"2001:db8:{i + 1:x}:1::1"), "x");
        Assert.Equal(50, limiter.NetworkCount);

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordWrongToken(Key("192.0.2.1"), "x");
        Assert.Equal(1, limiter.AddressCount);    // the new address, in the address map again
        Assert.Equal(0, limiter.NetworkCount);
    }

    [Fact]
    public void A_new_address_gets_its_own_entry_once_there_is_room_again_even_in_a_blocked_network()
    {
        var limiter = Limiter();
        Assert.True(limiter.TryBeginLookup(Key("100.64.0.1"), out var holder, out _));   // fills the address map
        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++) limiter.RecordWrongToken(Key($"2001:db8:1:{i + 1:x}::1"), "t" + i);
        Assert.False(limiter.TryBeginLookup(Key("2001:db8:1:99::1"), out _, out _));

        limiter.EndLookup(holder, null);   // room again
        Assert.True(limiter.TryBeginLookup(Key("2001:db8:1:99::1"), out _, out _));
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void An_address_with_its_own_entry_is_judged_by_it_even_when_its_network_is_blocked()
    {
        var limiter = Limiter();
        limiter.RecordWrongToken(Key("2001:db8:1:1::1"), "own");   // takes the one address slot
        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++) limiter.RecordWrongToken(Key($"2001:db8:1:{i + 2:x}::1"), "t" + i);
        Assert.NotNull(limiter.RetryAfterSeconds(Key("2001:db8:1:99::1")));   // the network is blocked

        Assert.Null(limiter.RetryAfterSeconds(Key("2001:db8:1:1::1")));
        Assert.True(limiter.TryBeginLookup(Key("2001:db8:1:1::1"), out _, out _));
    }

    [Fact]
    public void A_request_with_no_token_never_creates_an_address_entry_while_there_is_room()
    {
        var limiter = new TokenGuessLimiter(_clock, log: _log);
        for (var i = 0; i < 100; i++) Assert.Null(limiter.RetryAfterSeconds(Key($"2001:db8:{i + 1:x}:1::1")));
        Assert.Equal(0, limiter.AddressCount);
        Assert.Equal(0, limiter.NetworkCount);
    }
}

public class TokenGuessLimiterBoundsTests
{
    private readonly FakeClock _clock = new();
    private readonly ListLogger<TokenGuessLimiter> _log = new();

    private TokenGuessLimiter Limiter(int maxWrongAttempts = TokenGuessLimiter.DefaultMaxWrongAttempts, int maxAddresses = TokenGuessLimiter.DefaultMaxAddresses,
        int maxNetworks = TokenGuessLimiter.DefaultMaxNetworks) =>
        new(_clock, maxWrongAttempts: maxWrongAttempts, maxAddresses: maxAddresses, maxNetworks: maxNetworks, log: _log);

    private static void Block(TokenGuessLimiter limiter, string address)
    {
        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++) limiter.RecordWrongToken(Key(address), "t" + i);
    }

    [Fact]
    public void Fifty_lookups_in_flight_with_a_backstop_of_ten_let_exactly_ten_through()
    {
        var limiter = Limiter(maxWrongAttempts: 10);
        var through = 0;
        for (var i = 0; i < 50; i++)
            if (limiter.TryBeginLookup(Key("a"), out _, out var wait)) through++;
            else Assert.Equal(60, wait);
        Assert.Equal(10, through);
        Assert.Equal(10, limiter.InFlightFor("a"));
    }

    [Fact]
    public void Wrong_attempts_and_lookups_in_flight_add_up_to_the_backstop()
    {
        var limiter = Limiter(maxWrongAttempts: 10);
        for (var i = 0; i < 6; i++) limiter.RecordWrongToken(Key("a"), "stale");
        for (var i = 0; i < 4; i++) Assert.True(limiter.TryBeginLookup(Key("a"), out _, out _));
        Assert.False(limiter.TryBeginLookup(Key("a"), out _, out _));
    }

    [Fact]
    public void The_distinct_rule_reserves_nothing_so_parallel_lookups_from_one_address_all_proceed()
    {
        var limiter = Limiter();
        for (var i = 0; i < 100; i++) Assert.True(limiter.TryBeginLookup(Key("nat"), out _, out _));
    }

    [Fact]
    public void Released_lookups_for_valid_tokens_leave_no_entry()
    {
        var limiter = Limiter(maxWrongAttempts: 10);
        var lookups = new List<TokenGuessLimiter.Lookup>();
        for (var i = 0; i < 10; i++) { Assert.True(limiter.TryBeginLookup(Key("a"), out var l, out _)); lookups.Add(l); }
        foreach (var l in lookups) limiter.EndLookup(l, null);

        Assert.Equal(0, limiter.InFlightFor("a"));
        Assert.Equal(0, limiter.AddressCount);
        Assert.True(limiter.TryBeginLookup(Key("a"), out _, out _));
    }

    [Fact]
    public void With_both_maps_at_the_cap_a_new_address_is_not_tracked_and_one_warning_per_map_is_logged_per_window()
    {
        var limiter = Limiter(maxAddresses: 3, maxNetworks: 0);
        Block(limiter, "a");
        limiter.RecordWrongToken(Key("b"), "x");
        limiter.RecordWrongToken(Key("c"), "x");
        Assert.Equal(3, limiter.AddressCount);

        for (var i = 0; i < 30; i++) limiter.RecordWrongToken(Key("d"), "t" + i);
        Assert.Equal(3, limiter.AddressCount);
        Assert.Empty(limiter.TokenKeysFor("d"));
        Assert.Null(limiter.RetryAfterSeconds(Key("d")));
        Assert.NotNull(limiter.RetryAfterSeconds(Key("a")));                  // a tracked address stays blocked
        Assert.False(limiter.TryBeginLookup(Key("a"), out _, out _));
        Assert.Equal(2, _log.Lines.Count(l => l.Level == LogLevel.Warning));   // one per map

        _clock.Advance(TimeSpan.FromSeconds(30));
        limiter.RecordWrongToken(Key("e"), "x");
        Assert.Equal(2, _log.Lines.Count(l => l.Level == LogLevel.Warning));   // still the same window
    }

    [Fact]
    public void A_valid_token_proceeds_while_the_map_is_full()
    {
        var limiter = Limiter(maxAddresses: 1);
        limiter.RecordWrongToken(Key("a"), "x");

        Assert.True(limiter.TryBeginLookup(Key("b"), out var lookup, out _));
        limiter.EndLookup(lookup, null);
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void Expired_windows_are_swept_on_the_next_call()
    {
        var limiter = Limiter();
        for (var i = 0; i < 1000; i++) limiter.RecordWrongToken(Key("a" + i), "x");
        Assert.Equal(1000, limiter.AddressCount);

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordWrongToken(Key("new"), "x");
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void After_the_clock_jumps_back_the_sweep_still_trims_memory()
    {
        var limiter = Limiter();
        limiter.RecordWrongToken(Key("before"), "x");   // next sweep due a minute after this
        _clock.Advance(TimeSpan.FromMinutes(-10));
        for (var i = 0; i < 1000; i++) limiter.RecordWrongToken(Key("a" + i), "x");

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordWrongToken(Key("new"), "x");

        // "before" starts in the future, so it is not yet closed; the 1000 are.
        Assert.Equal(2, limiter.AddressCount);
    }

    [Fact]
    public void The_sweep_keeps_a_window_with_a_lookup_in_flight()
    {
        var limiter = Limiter(maxWrongAttempts: 1);
        Assert.True(limiter.TryBeginLookup(Key("a"), out var slow, out _));
        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordWrongToken(Key("other"), "x");   // the sweep runs

        Assert.Equal(1, limiter.InFlightFor("a"));
        Assert.False(limiter.TryBeginLookup(Key("a"), out _, out _));
        limiter.EndLookup(slow, "wrong");
        Assert.Equal(0, limiter.InFlightFor("a"));
        Assert.NotNull(limiter.RetryAfterSeconds(Key("a")));
    }

    [Fact]
    public void A_valid_token_between_wrong_ones_does_not_wipe_the_count()
    {
        var limiter = Limiter();
        for (var i = 0; i < 19; i++) limiter.RecordWrongToken(Key("a"), "t" + i);
        Assert.True(limiter.TryBeginLookup(Key("a"), out var valid, out _));
        limiter.EndLookup(valid, null);

        limiter.RecordWrongToken(Key("a"), "t19");
        Assert.NotNull(limiter.RetryAfterSeconds(Key("a")));
    }

    [Fact]
    public void A_valid_lookup_ending_while_a_wrong_one_is_in_flight_keeps_the_entry()
    {
        // Backstop 2, not 1: with 1, the held lookup would refuse the second one.
        var limiter = Limiter(maxWrongAttempts: 2);
        Assert.True(limiter.TryBeginLookup(Key("a"), out var wrong, out _));
        Assert.True(limiter.TryBeginLookup(Key("a"), out var valid, out _));
        limiter.EndLookup(valid, null);
        limiter.EndLookup(wrong, "wrong");

        Assert.Equal(0, limiter.InFlightFor("a"));
        Assert.Single(limiter.TokenKeysFor("a"));
        Assert.Null(limiter.RetryAfterSeconds(Key("a")));
        limiter.RecordWrongToken(Key("a"), "wrong");
        Assert.NotNull(limiter.RetryAfterSeconds(Key("a")));   // the held attempt was counted: two make the backstop
    }

    [Fact]
    public void The_sweep_never_removes_an_open_window_so_a_blocked_address_stays_blocked()
    {
        var limiter = Limiter();
        limiter.RecordWrongToken(Key("other"), "x");     // next sweep due at t60
        _clock.Advance(TimeSpan.FromSeconds(30));
        Block(limiter, "a");

        _clock.Advance(TimeSpan.FromSeconds(31));   // t61: the sweep runs on this call; a's window is 31 s old
        limiter.RecordWrongToken(Key("b"), "x");
        Assert.Equal(29, limiter.RetryAfterSeconds(Key("a")));
        Assert.False(limiter.TryBeginLookup(Key("a"), out _, out _));
    }

    [Fact]
    public void A_wrong_token_is_counted_when_both_maps_were_full_at_the_start_but_has_room_at_the_end()
    {
        var limiter = Limiter(maxAddresses: 1, maxNetworks: 0);
        Assert.True(limiter.TryBeginLookup(Key("x"), out var holder, out _));   // x fills the map
        Assert.True(limiter.TryBeginLookup(Key("a"), out var untracked, out _));
        Assert.Equal(0, limiter.InFlightFor("a"));
        limiter.EndLookup(holder, null);                                    // x was valid: its entry goes, room again

        limiter.EndLookup(untracked, "wrong");
        Assert.Single(limiter.TokenKeysFor("a"));
    }

    // ---- through the middleware: the reservation is released whatever the store does

    private sealed class Store(Func<CancellationTokenSource, ValueTask<AuthResult?>> answer, CancellationTokenSource cts) : ITenantAuthenticator
    {
        public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct) => answer(cts);
    }

    private async Task<HttpContext> Invoke(TokenGuessLimiter limiter, Func<CancellationTokenSource, ValueTask<AuthResult?>> answer)
    {
        using var cts = new CancellationTokenSource();
        var middleware = new TokenAuthMiddleware(_ => Task.CompletedTask, new Store(answer, cts), limiter, new TokenRateLimiter(_clock, 1000),
            NullLogger<TokenAuthMiddleware>.Instance);
        var ctx = new DefaultHttpContext { RequestAborted = cts.Token };
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        ctx.Request.Path = "/v3/index.json";
        ctx.Request.Headers.Authorization = "Bearer pmt_whatever";
        await middleware.InvokeAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task A_store_failure_releases_the_reservation_and_counts_nothing()
    {
        var limiter = Limiter(maxWrongAttempts: 1);
        for (var i = 0; i < 3; i++)
        {
            var ctx = await Invoke(limiter, _ => throw new InvalidOperationException("store down"));
            Assert.Equal(503, ctx.Response.StatusCode);
        }
        Assert.Equal(0, limiter.InFlightFor("203.0.113.5"));
        Assert.Null(limiter.RetryAfterSeconds(Key("203.0.113.5")));
    }

    [Fact]
    public async Task A_cancelled_request_releases_the_reservation_and_counts_nothing()
    {
        var limiter = Limiter(maxWrongAttempts: 1);
        for (var i = 0; i < 3; i++)
            await Invoke(limiter, cts => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        Assert.Equal(0, limiter.InFlightFor("203.0.113.5"));
        Assert.True(limiter.TryBeginLookup(Key("203.0.113.5"), out _, out _));
    }

    [Fact]
    public async Task A_wrong_token_through_the_middleware_is_counted_once_and_released()
    {
        var limiter = Limiter(maxWrongAttempts: 2);
        Assert.Equal(401, (await Invoke(limiter, _ => ValueTask.FromResult<AuthResult?>(null))).Response.StatusCode);
        Assert.Equal(0, limiter.InFlightFor("203.0.113.5"));
        Assert.Null(limiter.RetryAfterSeconds(Key("203.0.113.5")));
        Assert.Equal(401, (await Invoke(limiter, _ => ValueTask.FromResult<AuthResult?>(null))).Response.StatusCode);
        Assert.NotNull(limiter.RetryAfterSeconds(Key("203.0.113.5")));
    }
}

public class FailedAuthLimiterBoundsTests
{
    private readonly FakeClock _clock = new();
    private readonly ListLogger<FailedAuthLimiter> _log = new();

    [Fact]
    public void Expired_windows_are_swept_on_the_next_failure()
    {
        var limiter = new FailedAuthLimiter(_clock, log: _log);
        for (var i = 0; i < 1000; i++) limiter.RecordFailure("a" + i);
        Assert.Equal(1000, limiter.AddressCount);

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordFailure("new");
        Assert.Equal(1, limiter.AddressCount);
    }

    [Fact]
    public void After_the_clock_jumps_back_the_sweep_still_trims_memory()
    {
        var limiter = new FailedAuthLimiter(_clock, log: _log);
        limiter.RecordFailure("before");
        _clock.Advance(TimeSpan.FromMinutes(-10));
        for (var i = 0; i < 1000; i++) limiter.RecordFailure("a" + i);

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordFailure("new");
        Assert.Equal(2, limiter.AddressCount);
    }

    [Fact]
    public void At_the_cap_a_new_address_is_not_tracked_a_tracked_one_stays_blocked_and_one_warning_is_logged()
    {
        var limiter = new FailedAuthLimiter(_clock, maxFailures: 5, maxAddresses: 2, log: _log);
        for (var i = 0; i < 5; i++) limiter.RecordFailure("a");
        limiter.RecordFailure("b");

        for (var i = 0; i < 10; i++) limiter.RecordFailure("c");
        Assert.Equal(2, limiter.AddressCount);
        Assert.Null(limiter.RetryAfterSeconds("c"));
        Assert.NotNull(limiter.RetryAfterSeconds("a"));
        limiter.RecordFailure("a");
        Assert.NotNull(limiter.RetryAfterSeconds("a"));
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);
    }
}

/// <summary>Parallel wrong tokens through the real host, held at a gate in the store: no timing, only counts.</summary>
public class TokenGuessConcurrencyTests
{
    private sealed class GatedStore : ITenantAuthenticator
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Arrivals;
        public Action? Arrived;

        public async ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct)
        {
            Interlocked.Increment(ref Arrivals);
            Arrived?.Invoke();
            await Gate.Task;
            return null;
        }
    }

    [Fact]
    public async Task Fifty_parallel_wrong_tokens_with_a_backstop_of_ten_reach_the_store_exactly_ten_times()
    {
        const int requests = 50, backstop = 10;
        var store = new GatedStore();
        await using var p = await StartAsync(configure: s =>
        {
            s.AddSingleton<ITenantAuthenticator>(store);
            s.AddSingleton(sp => new TokenGuessLimiter(sp.GetRequiredService<IClock>(), maxWrongAttempts: backstop));
        });

        // Every request either reaches the store (and waits at the gate) or is refused with 429. Once all are accounted for, the gate opens.
        var refused = 0;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (Volatile.Read(ref store.Arrivals) + Volatile.Read(ref refused) == requests) settled.TrySetResult(); }
        store.Arrived = Check;

        using var http = p.Client("pmt_wrong_parallel_0000");
        var tasks = Enumerable.Range(0, requests).Select(async _ =>
        {
            var code = (await http.GetAsync("/v3/index.json")).StatusCode;
            if (code == HttpStatusCode.TooManyRequests) { Interlocked.Increment(ref refused); Check(); }
            return code;
        }).ToList();

        int arrivedWhileHeld;
        try
        {
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(60));   // a guard against a hang only; the counts decide
            arrivedWhileHeld = Volatile.Read(ref store.Arrivals);
        }
        finally
        {
            store.Gate.TrySetResult();   // never leave requests parked at the gate, even when an assertion fails
        }
        var codes = await Task.WhenAll(tasks);

        Assert.Equal(backstop, arrivedWhileHeld);

        Assert.Equal(backstop, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(requests - backstop, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(backstop, store.Arrivals);
    }

    [Fact]
    public async Task Addresses_in_one_ipv6_slash_64_share_the_limit_through_x_forwarded_for_and_another_slash_64_does_not()
    {
        await using var p = await StartAsync();
        HttpClient From(string address, string token)
        {
            var http = p.Client(token);
            http.DefaultRequestHeaders.Add("X-Forwarded-For", address);
            return http;
        }

        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++)
        {
            using var g = From("2001:db8:0:1::" + (i + 1).ToString("x"), "pmt_wrong_" + i.ToString("D4"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await g.GetAsync("/v3/index.json")).StatusCode);
        }
        using var sameNetwork = From("2001:db8:0:1:ffff::9", "pmt_wrong_next");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await sameNetwork.GetAsync("/v3/index.json")).StatusCode);

        using var otherNetwork = From("2001:db8:0:2::1", AcmeToken);
        Assert.Equal(HttpStatusCode.OK, (await otherNetwork.GetAsync("/v3/index.json")).StatusCode);
    }

    [Fact]
    public async Task A_valid_token_from_a_new_address_passes_while_the_limiter_is_full()
    {
        await using var p = await StartAsync(configure: s => s.AddSingleton(sp => new TokenGuessLimiter(sp.GetRequiredService<IClock>(), maxAddresses: 1)));
        using var guesser = p.Client("pmt_wrong_fill_0000");
        guesser.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/v3/index.json")).StatusCode);

        using var customer = p.Client(AcmeToken);
        customer.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.2");
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/v3/index.json")).StatusCode);
    }

    private static HttpClient From(ProxyFixture p, string address, string? token)
    {
        var http = p.Client(token);
        http.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return http;
    }

    [Fact]
    public async Task With_both_maps_full_a_customer_from_another_slash_48_is_served_and_challenged_never_refused()
    {
        await using var p = await StartAsync(configure: s =>
        {
            // The token cache as in production, around the fixture's counting store.
            var store = (ITenantAuthenticator)s.Last(d => d.ServiceType == typeof(ITenantAuthenticator)).ImplementationInstance!;
            s.AddSingleton<ITenantAuthenticator>(sp => new CachedTenantAuthenticator(store, sp.GetRequiredService<IClock>()));
            s.AddSingleton(sp => new TokenGuessLimiter(sp.GetRequiredService<IClock>(), maxAddresses: 1, maxNetworks: 1));
        });
        using (var fillsAddresses = From(p, "2001:db8:1:1::1", "pmt_wrong_fill_0001"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await fillsAddresses.GetAsync("/v3/index.json")).StatusCode);
        using (var fillsNetworks = From(p, "2001:db8:2:1::1", "pmt_wrong_fill_0002"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await fillsNetworks.GetAsync("/v3/index.json")).StatusCode);

        var callsBefore = p.Tenants.Calls;
        using var customer = From(p, "2001:db8:3:1::1", AcmeToken);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/v3/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/v3/index.json")).StatusCode);
        Assert.Equal(1, p.Tenants.Calls - callsBefore);

        using var probe = From(p, "2001:db8:3:1::1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await probe.GetAsync("/v3/index.json")).StatusCode);
    }

    [Fact]
    public async Task With_the_address_map_full_a_guessing_slash_48_is_refused_and_another_slash_48_is_served()
    {
        await using var p = await StartAsync(configure: s => s.AddSingleton(sp => new TokenGuessLimiter(sp.GetRequiredService<IClock>(), maxAddresses: 1)));
        using (var fills = From(p, "203.0.113.1", "pmt_wrong_fill_0000"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await fills.GetAsync("/v3/index.json")).StatusCode);

        for (var i = 0; i < TokenGuessLimiter.DefaultMaxDistinctTokens; i++)
        {
            using var g = From(p, $"2001:db8:5:{i + 1:x}::1", "pmt_wrong_" + i.ToString("D4"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await g.GetAsync("/v3/index.json")).StatusCode);
        }
        using var sameNetwork = From(p, "2001:db8:5:ffff::9", "pmt_wrong_next");
        var refused = await sameNetwork.GetAsync("/v3/index.json");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        using var sameNetworkAnonymous = From(p, "2001:db8:5:fffe::9", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await sameNetworkAnonymous.GetAsync("/v3/index.json")).StatusCode);

        using var otherNetwork = From(p, "2001:db8:6:1::1", AcmeToken);
        Assert.Equal(HttpStatusCode.OK, (await otherNetwork.GetAsync("/v3/index.json")).StatusCode);
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Enqueue((logLevel, formatter(state, exception)));
}
