using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Host.Auth;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>
/// Observe only: when a token's one-minute window closes above half the ceiling, one Information line records
/// the peak, so production can show how close real build farms come to the ceiling. Enforcement is unchanged.
/// </summary>
public class TokenPeakLogTests
{
    private const int Ceiling = 10;
    private readonly FakeClock _clock = new();
    private readonly PeakLogs _logs = new();

    private TokenRateLimiter Limiter() => new(_clock, Ceiling, _logs);

    private static void Send(TokenRateLimiter limiter, string tokenId, int count)
    {
        for (var i = 0; i < count; i++) limiter.TryAcquire(tokenId);
    }

    private IEnumerable<(LogLevel Level, string Message)> Peaks() => _logs.Lines.Where(l => l.Message.Contains("peaked at"));

    [Fact]
    public void A_window_that_ends_at_six_of_ten_logs_one_peak_line_at_information()
    {
        var limiter = Limiter();
        Send(limiter, "tok_a", 6);
        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("tok_a");

        var line = Assert.Single(Peaks());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Token tok_a peaked at 6/min (60% of ceiling)", line.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]   // exactly half is not above half
    public void A_window_at_or_below_half_the_ceiling_logs_nothing(int count)
    {
        var limiter = Limiter();
        Send(limiter, "tok_a", count);
        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("tok_a");

        Assert.Empty(Peaks());
    }

    [Fact]
    public void The_eleventh_request_still_gets_429_and_the_peak_is_the_ceiling()
    {
        var limiter = Limiter();
        for (var i = 0; i < Ceiling; i++) Assert.Null(limiter.TryAcquire("tok_a"));
        Assert.Equal(60, limiter.TryAcquire("tok_a"));

        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(limiter.TryAcquire("tok_a"));
        Assert.Equal("Token tok_a peaked at 10/min (100% of ceiling)", Assert.Single(Peaks()).Message);
    }

    [Fact]
    public void Nothing_is_logged_while_the_window_is_open_and_only_one_line_after_it_closes()
    {
        var limiter = Limiter();
        Send(limiter, "tok_a", 15);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Send(limiter, "tok_a", 3);
        Assert.Empty(_logs.Lines);

        _clock.Advance(TimeSpan.FromSeconds(31));
        Send(limiter, "tok_a", 2);
        Send(limiter, "tok_b", 2);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Send(limiter, "tok_b", 1);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Send(limiter, "tok_b", 1);

        Assert.Single(Peaks());
    }

    [Fact]
    public void A_window_that_never_sees_another_request_is_logged_when_any_later_request_sweeps_it()
    {
        var limiter = Limiter();
        Send(limiter, "tok_farm", 8);
        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("tok_other");

        Assert.Equal("Token tok_farm peaked at 8/min (80% of ceiling)", Assert.Single(Peaks()).Message);
        Assert.Equal(1, limiter.WindowCount);   // the closed window is gone, not just logged
    }

    [Fact]
    public void Closed_windows_are_removed_so_memory_holds_only_recent_tokens()
    {
        var limiter = Limiter();
        for (var i = 0; i < 1000; i++) limiter.TryAcquire("tok_" + i);
        Assert.Equal(1000, limiter.WindowCount);

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("tok_new");
        Assert.Equal(1, limiter.WindowCount);
        Assert.Empty(Peaks());
    }

    [Fact]
    public void The_sweep_never_removes_an_open_window_so_a_limited_token_stays_limited()
    {
        var limiter = Limiter();
        limiter.TryAcquire("tok_other");
        _clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < Ceiling; i++) Assert.Null(limiter.TryAcquire("tok_a"));

        _clock.Advance(TimeSpan.FromSeconds(31));   // the sweep runs on this request; tok_a's window is 31 s old
        Assert.Equal(29, limiter.TryAcquire("tok_a"));
    }

    [Fact]
    public void A_window_still_open_at_the_sweep_is_logged_when_its_own_token_returns_at_exactly_sixty_seconds()
    {
        var limiter = Limiter();
        limiter.TryAcquire("tok_other");
        _clock.Advance(TimeSpan.FromSeconds(10));
        Send(limiter, "tok_a", 7);
        _clock.Advance(TimeSpan.FromSeconds(50));
        limiter.TryAcquire("tok_other");            // the sweep runs while tok_a's window is open
        Assert.Empty(Peaks());

        _clock.Advance(TimeSpan.FromSeconds(10));   // tok_a's window is exactly 60 s old: closed
        limiter.TryAcquire("tok_a");

        Assert.Equal("Token tok_a peaked at 7/min (70% of ceiling)", Assert.Single(Peaks()).Message);
    }

    [Fact]
    public void After_the_clock_jumps_back_the_sweep_still_trims_memory()
    {
        var limiter = Limiter();
        limiter.TryAcquire("tok_before");             // next sweep due a minute after this
        _clock.Advance(TimeSpan.FromMinutes(-10));
        for (var i = 0; i < 1000; i++) limiter.TryAcquire("tok_" + i);

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("tok_new");

        // tok_before's window starts in the future, so it is not yet closed; the 1000 are.
        Assert.Equal(2, limiter.WindowCount);
    }

    [Fact]
    public void Separate_tokens_peak_separately()
    {
        var limiter = Limiter();
        Send(limiter, "tok_a", 7);
        Send(limiter, "tok_b", 9);
        Send(limiter, "tok_c", 3);
        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.TryAcquire("tok_d");

        Assert.Equal(
            new[] { "Token tok_a peaked at 7/min (70% of ceiling)", "Token tok_b peaked at 9/min (90% of ceiling)" },
            Peaks().Select(p => p.Message).OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Through_the_host_the_line_names_the_token_id_and_never_the_token_or_its_hash()
    {
        var logs = new PeakLogs();
        await using var p = await StartAsync(tokenRequestsPerMinute: Ceiling, configure: s => s.AddSingleton<ILoggerProvider>(logs));
        using var acme = p.Client(AcmeToken);
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/v3/index.json")).StatusCode);

        p.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/v3/index.json")).StatusCode);

        var line = Assert.Single(logs.Lines, l => l.Message.Contains("peaked at"));
        Assert.Equal("Token tok_test_acme peaked at 6/min (60% of ceiling)", line.Message);
        Assert.DoesNotContain(logs.Lines, l => l.Message.Contains(AcmeToken) || l.Message.Contains(TokenHasher.Hash(AcmeToken)));
    }

    private sealed class PeakLogs : ILoggerProvider, ILogger<TokenRateLimiter>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
