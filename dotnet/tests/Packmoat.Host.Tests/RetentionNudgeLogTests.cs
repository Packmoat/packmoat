using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Host.Tests;

/// <summary>
/// The steps moved into <see cref="RetentionPass"/>, but the server's scheduled pass still tells the operator, in its own line, how
/// many tenants are due for purge and what to run.
/// </summary>
public class RetentionNudgeLogTests
{
    private sealed class TwoDue : ITenantPurgeStore, IPurgeEligibility
    {
        public int Claims;
        public IPurgeEligibility? Eligibility => this;
        public Task<IReadOnlyList<PurgeCandidate>> ListAsync(DateTimeOffset now, CancellationToken ct) => Task.FromResult<IReadOnlyList<PurgeCandidate>>(
            [new("a", PurgeVerdict.Due, "test", now.AddDays(-1), "test", ""), new("b", PurgeVerdict.Due, "test", now.AddDays(-2), "test", ""),
             new("c", PurgeVerdict.NotYet, "test", now.AddDays(3), "test", "")]);
        public Task<PurgeCandidate?> AssessAsync(string tenantId, DateTimeOffset now, CancellationToken ct) => Task.FromResult<PurgeCandidate?>(null);
        public Task ConfirmAsync(PurgeCandidate candidate, CancellationToken ct) => throw new PurgeRefusedException("test");
        public Task<TenantPurgeRecord?> FindPurgeAsync(string tenantId, CancellationToken ct) => Task.FromResult<TenantPurgeRecord?>(null);
        public Task<IReadOnlyList<PurgedTable>> PurgeAsync(PurgeCandidate candidate, TenantPurgeRecord record, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TryClaimNudgeDayAsync(DateOnly day, CancellationToken ct) { Interlocked.Increment(ref Claims); return Task.FromResult(true); }
    }

    private sealed class NoFeedChecks : IFeedCheckStore
    {
        public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct) => Task.FromResult(0);
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Lines : ILogger
    {
        private readonly List<string> _lines = new();
        public string[] All { get { lock (_lines) return _lines.ToArray(); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)}"); }
    }

    [Fact]   // mutation: the scheduled pass stops passing its due-count line to the nudge
    public async Task The_scheduled_pass_logs_how_many_are_due_and_what_to_run()
    {
        var store = new TwoDue();
        var log = new Lines();
        using var service = new RetentionService(new NoFeedChecks(), new FakeClock(), log, interval: TimeSpan.FromDays(1), purges: store,
            firstPass: TimeSpan.FromMilliseconds(10));
        await service.StartAsync(default);
        for (var i = 0; i < 400 && store.Claims == 0; i++) await Task.Delay(10);
        await service.StopAsync(default);

        Assert.Equal(1, store.Claims);
        Assert.Contains("Warning: Retention: 2 tenant(s) due for purge; run packmoat-admin tenant purge-due", log.All);
    }
}
