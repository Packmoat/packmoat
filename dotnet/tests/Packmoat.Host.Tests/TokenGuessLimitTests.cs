using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Host.Auth;
using static Packmoat.Host.Tests.ProxyFixture;
using static Packmoat.Host.Tests.LimiterKeys;

namespace Packmoat.Host.Tests;

/// <summary>
/// NuGet sends every request once without credentials, gets 401, then retries with them (NuGet/Home#12517). Those
/// challenges are not guesses: only a token that was presented and is wrong counts, and only distinct wrong tokens
/// per address count towards the block (a stale CI secret repeated is not guessing), with a higher backstop on all
/// wrong attempts to protect the token store.
/// </summary>
public class TokenGuessLimitTests
{
    private static string Wrong(int i) => "pmt_wrong_" + i.ToString("D4") + new string('x', 40);

    private static HttpClient Client(ProxyFixture p, string? token, string? address = null)
    {
        var http = p.Client(token);
        if (address is not null) http.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return http;
    }

    private static async Task<List<HttpStatusCode>> SendWrongTokens(ProxyFixture p, IEnumerable<string> tokens, string? address = null)
    {
        var codes = new List<HttpStatusCode>();
        foreach (var t in tokens)
        {
            using var http = Client(p, t, address);
            codes.Add((await http.GetAsync("/v3/index.json")).StatusCode);
        }
        return codes;
    }

    [Fact]
    public async Task Fifty_restores_of_ten_urls_each_challenged_first_from_one_address_are_never_throttled_and_count_nothing()
    {
        await using var p = await StartAsync();
        var ids = new[] { "Acme.A", "Acme.B", "Acme.C" };
        foreach (var id in ids) p.Publish(id, "1.0.0", 100);
        const string feed = "/v3/u/org/proj/_packaging/feed/nuget/v3/";
        var urls = new List<string> { "/v3/index.json" };
        foreach (var id in ids.Select(i => i.ToLowerInvariant()))
            urls.AddRange(new[] { feed + "flat2/" + id + "/index.json", feed + "registrations2/" + id + "/index.json", feed + "flat2/" + id + "/1.0.0/" + id + ".1.0.0.nupkg" });
        Assert.Equal(10, urls.Count);

        using var anonymous = p.Client(null);
        using var authed = p.Client(AcmeToken);
        var challenges = new List<HttpStatusCode>();
        var answers = new List<HttpStatusCode>();
        for (var restore = 0; restore < 50; restore++)
            foreach (var url in urls)
            {
                challenges.Add((await anonymous.GetAsync(url)).StatusCode);
                answers.Add((await authed.GetAsync(url)).StatusCode);
            }

        Assert.All(challenges, c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.All(answers, c => Assert.NotEqual(HttpStatusCode.TooManyRequests, c));
        Assert.All(answers, c => Assert.NotEqual(HttpStatusCode.Unauthorized, c));
        Assert.Contains(HttpStatusCode.OK, answers);

        // Nothing was counted: the full allowance of 20 distinct wrong tokens is still there.
        var codes = await SendWrongTokens(p, Enumerable.Range(0, 21).Select(Wrong));
        Assert.Equal(20, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    }

    [Fact]
    public async Task The_21st_distinct_wrong_token_gets_429_with_retry_after_and_401_returns_after_the_window()
    {
        await using var p = await StartAsync();

        var codes = await SendWrongTokens(p, Enumerable.Range(0, 20).Select(Wrong));
        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Unauthorized, c));

        using var twentyFirst = p.Client(Wrong(20));
        var blocked = await twentyFirst.GetAsync("/v3/index.json");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);

        p.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await twentyFirst.GetAsync("/v3/index.json")).StatusCode);
        Assert.Equal(0, p.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task The_same_wrong_token_a_hundred_times_is_always_401_until_the_backstop()
    {
        await using var p = await StartAsync();
        using var stale = p.Client(Wrong(0));

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 100; i++) codes.Add((await stale.GetAsync("/v3/index.json")).StatusCode);
        Assert.All(codes, c => Assert.Equal(HttpStatusCode.Unauthorized, c));

        for (var i = 100; i < TokenGuessLimiter.DefaultMaxWrongAttempts; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/v3/index.json")).StatusCode);
        var lookups = p.Tenants.Calls;
        var backstopped = await stale.GetAsync("/v3/index.json");
        Assert.Equal(HttpStatusCode.TooManyRequests, backstopped.StatusCode);
        Assert.NotNull(backstopped.Headers.RetryAfter);
        Assert.Equal(lookups, p.Tenants.Calls); // the backstop protects the token store: no lookup was made
    }

    [Fact]
    public async Task One_address_guessing_does_not_block_another()
    {
        await using var p = await StartAsync();

        var a = await SendWrongTokens(p, Enumerable.Range(0, 21).Select(Wrong), "203.0.113.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, a[^1]);

        var b = await SendWrongTokens(p, new[] { Wrong(0) }, "203.0.113.20");
        Assert.Equal(HttpStatusCode.Unauthorized, b.Single());
        using var bValid = Client(p, AcmeToken, "203.0.113.20");
        Assert.Equal(HttpStatusCode.OK, (await bValid.GetAsync("/v3/index.json")).StatusCode);
    }

    [Fact]
    public async Task A_blocked_address_gets_429_even_with_a_valid_token_and_no_token_is_looked_up()
    {
        await using var p = await StartAsync();
        await SendWrongTokens(p, Enumerable.Range(0, 20).Select(Wrong));
        var lookups = p.Tenants.Calls;

        using var valid = p.Client(AcmeToken);
        var r = await valid.GetAsync("/v3/index.json");
        var guess = await SendWrongTokens(p, new[] { Wrong(99) });

        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.NotNull(r.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, guess.Single());
        Assert.Equal(lookups, p.Tenants.Calls);
        Assert.Equal(0, p.Upstream.FeedRequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic ")]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Basic ZGV2Og==")]    // "dev:" (a user name, no password)
    [InlineData("Bearer ")]
    [InlineData("Digest abc")]
    public async Task A_missing_empty_or_malformed_header_is_401_and_never_counted(string? header)
    {
        await using var p = await StartAsync();
        using var http = p.Client(null);
        for (var i = 0; i < 200; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/v3/index.json");
            if (header is not null) request.Headers.TryAddWithoutValidation("Authorization", header);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
        }
        Assert.Equal(0, p.Tenants.Calls);

        var codes = await SendWrongTokens(p, Enumerable.Range(0, 21).Select(Wrong));
        Assert.Equal(20, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    }

    private sealed class LevelLogs : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }
        private sealed class L(LevelLogs o, string c) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => o.Lines.Enqueue((c, logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task A_challenge_logs_no_warning_and_a_wrong_token_logs_one_warning_without_the_token()
    {
        var logs = new LevelLogs();
        var category = typeof(TokenAuthMiddleware).FullName!;
        await using var p = await StartAsync(configure: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.AddLogging(b => b.AddFilter(category, LogLevel.Debug));
        });
        IEnumerable<(string Category, LogLevel Level, string Message)> Auth() => logs.Lines.Where(l => l.Category == category);

        using var anonymous = p.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v3/index.json")).StatusCode);
        Assert.DoesNotContain(Auth(), l => l.Level >= LogLevel.Warning);
        Assert.Contains(Auth(), l => l.Level == LogLevel.Debug && l.Message.Contains("no credentials"));

        var wrong = Wrong(7);
        using var guesser = p.Client(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/v3/index.json")).StatusCode);
        var warning = Assert.Single(Auth(), l => l.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("wrong token", warning.Message);
        Assert.DoesNotContain(logs.Lines, l => l.Message.Contains(wrong) || l.Message.Contains(TokenHasher.Hash(wrong)));
    }

    [Fact]
    public async Task A_real_restore_of_thirty_packages_from_one_address_succeeds()
    {
        await using var p = await StartAsync();
        var packages = Enumerable.Range(0, 30).Select(i => ("Acme.Pkg" + i.ToString("D2"), "1.0.0")).ToList();
        foreach (var (id, version) in packages) p.Publish(id, version, 100);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, packages);

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal(30, packages.Count(pkg => r.Libraries.TryGetValue(pkg.Item1.ToLowerInvariant(), out var v) && v == "1.0.0"));
        Assert.DoesNotContain("429", r.Output);
    }
}

/// <summary>The limiter on its own, on a fake clock.</summary>
public class TokenGuessLimiterTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void Twenty_distinct_wrong_tokens_block_the_address_for_the_rest_of_the_minute()
    {
        var limiter = new TokenGuessLimiter(_clock);
        for (var i = 0; i < 19; i++) limiter.RecordWrongToken(Key("a"), "t" + i);
        Assert.Null(limiter.RetryAfterSeconds(Key("a")));

        limiter.RecordWrongToken(Key("a"), "t19");
        Assert.Equal(60, limiter.RetryAfterSeconds(Key("a")));

        _clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(15, limiter.RetryAfterSeconds(Key("a")));
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Null(limiter.RetryAfterSeconds(Key("a")));
    }

    [Fact]
    public void A_repeated_token_counts_once_towards_the_distinct_limit_and_every_time_towards_the_backstop()
    {
        var limiter = new TokenGuessLimiter(_clock, maxDistinctTokens: 20, maxWrongAttempts: 50);
        for (var i = 0; i < 49; i++) limiter.RecordWrongToken(Key("a"), "stale");
        Assert.Null(limiter.RetryAfterSeconds(Key("a")));

        limiter.RecordWrongToken(Key("a"), "stale");
        Assert.NotNull(limiter.RetryAfterSeconds(Key("a")));
    }

    [Fact]
    public void Addresses_are_counted_separately()
    {
        var limiter = new TokenGuessLimiter(_clock);
        for (var i = 0; i < 20; i++) limiter.RecordWrongToken(Key("a"), "t" + i);
        for (var i = 0; i < 19; i++) limiter.RecordWrongToken(Key("b"), "t" + i);

        Assert.NotNull(limiter.RetryAfterSeconds(Key("a")));
        Assert.Null(limiter.RetryAfterSeconds(Key("b")));
    }

    [Fact]
    public void Only_the_first_eight_bytes_of_the_token_hash_are_kept_never_the_plaintext()
    {
        var limiter = new TokenGuessLimiter(_clock);
        const string token = "pmt_secret_guess_0123456789abcdef";   // gitleaks:allow (a made-up wrong guess)
        limiter.RecordWrongToken(Key("a"), token);

        var sha = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        Assert.Equal(new[] { System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sha) }, limiter.TokenKeysFor("a"));
    }

    [Fact]
    public void An_address_keeps_at_most_the_distinct_limit_of_hashes()
    {
        var limiter = new TokenGuessLimiter(_clock);
        for (var i = 0; i < 100; i++) limiter.RecordWrongToken(Key("a"), "t" + i);
        Assert.Equal(TokenGuessLimiter.DefaultMaxDistinctTokens, limiter.TokenKeysFor("a").Count);
    }
}
