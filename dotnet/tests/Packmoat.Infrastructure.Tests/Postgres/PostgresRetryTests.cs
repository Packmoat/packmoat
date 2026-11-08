using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// <see cref="PostgresRetry"/> (#303 decision "Retry"), with no server: each attempt is a function that throws what the server would, and
/// the waits are recorded, not slept.
/// </summary>
public class PostgresRetryTests
{
    private static PostgresException Failure(string sqlState) => new("test failure", "ERROR", "ERROR", sqlState);

    /// <summary>Runs <paramref name="outcomes"/> in turn, one per attempt (null: the attempt succeeds and returns its number).</summary>
    private static async Task<(int Attempts, List<TimeSpan> Waits, Exception? Thrown, int Result)> RunAsync(params string?[] outcomes)
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        try
        {
            var result = await PostgresRetry.RunAsync(_ =>
            {
                var outcome = outcomes[attempts++];
                return outcome is null ? Task.FromResult(attempts) : throw Failure(outcome);
            }, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, new Random(7), CancellationToken.None);
            return (attempts, waits, null, result);
        }
        catch (Exception e)
        {
            return (attempts, waits, e, 0);
        }
    }

    [Fact]
    public async Task A_first_attempt_that_succeeds_runs_once_and_never_waits()
    {
        var run = await RunAsync([null]);
        Assert.Equal((1, 1), (run.Attempts, run.Result));
        Assert.Empty(run.Waits);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]       // 40P01
    [InlineData(PostgresErrorCodes.SerializationFailure)]   // 40001
    public async Task A_deadlock_or_serialization_failure_is_run_again_up_to_three_attempts(string sqlState)
    {
        var run = await RunAsync(sqlState, sqlState, null);
        Assert.Null(run.Thrown);
        Assert.Equal((3, 3), (run.Attempts, run.Result));
        Assert.Equal(2, run.Waits.Count);
    }

    // mutation: a fourth attempt (MaxAttempts 4, or n <= MaxAttempts); the failure wrapped or swallowed
    [Fact]
    public async Task After_three_failed_attempts_the_last_failure_is_thrown_as_it_was()
    {
        var run = await RunAsync(PostgresErrorCodes.DeadlockDetected, PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected, null);
        Assert.Equal(3, run.Attempts);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, Assert.IsType<PostgresException>(run.Thrown).SqlState);
        Assert.Equal(2, run.Waits.Count);
        Assert.Equal(3, PostgresRetry.MaxAttempts);
    }

    // mutation (#303): the retry also retries 23505 (a unique violation is a refusal: running it again cannot help and hides it)
    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]        // 23505
    [InlineData(PostgresErrorCodes.CheckViolation)]         // 23514
    [InlineData(PostgresErrorCodes.RaiseException)]         // P0001, a rule's refusal
    [InlineData(PostgresErrorCodes.LockNotAvailable)]       // 55P03
    [InlineData(PostgresErrorCodes.QueryCanceled)]          // 57014, a statement timeout
    [InlineData(PostgresErrorCodes.AdminShutdown)]          // 57P01, which Npgsql calls transient
    public async Task Any_other_failure_is_thrown_at_once(string sqlState)
    {
        var run = await RunAsync(sqlState, null);
        Assert.Equal(1, run.Attempts);
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(run.Thrown).SqlState);
        Assert.Empty(run.Waits);
    }

    [Fact]
    public async Task An_exception_that_is_not_the_servers_is_thrown_at_once()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => PostgresRetry.RunAsync<int>(_ => { attempts++; throw new TimeoutException(); },
            (_, _) => Task.CompletedTask, new Random(1), CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_cancelled_call_does_not_run_again()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAsync<PostgresException>(() => PostgresRetry.RunAsync<int>(_ =>
        {
            attempts++;
            cts.Cancel();
            throw Failure(PostgresErrorCodes.DeadlockDetected);
        }, (_, _) => Task.CompletedTask, new Random(1), cts.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void The_codes_retried_are_exactly_deadlock_and_serialization_failure()
    {
        Assert.True(PostgresRetry.IsRetried("40P01"));
        Assert.True(PostgresRetry.IsRetried("40001"));
        foreach (var other in new[] { "40000", "40002", "40003", "23505", "P0001", "08006", "57P01", "", null })
            Assert.False(PostgresRetry.IsRetried(other));
    }

    // mutation: no jitter (two losers would wait the same and meet again), or a wait that does not grow
    [Fact]
    public void Each_wait_grows_and_has_a_random_part()
    {
        var random = new Random(42);
        for (var n = 1; n < PostgresRetry.MaxAttempts; n++)
        {
            var waits = Enumerable.Range(0, 50).Select(_ => PostgresRetry.Delay(n, random)).ToList();
            Assert.All(waits, w => Assert.InRange(w, PostgresRetry.Step * n, PostgresRetry.Step * (n + 1)));
            Assert.True(waits.Distinct().Count() > 1, "the wait has no random part");
        }
    }

    [Fact]
    public async Task The_public_overloads_run_the_attempt()
    {
        Assert.Equal(5, await PostgresRetry.RunAsync(_ => Task.FromResult(5), CancellationToken.None));
        var ran = false;
        await PostgresRetry.RunAsync(_ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(ran);
    }
}
