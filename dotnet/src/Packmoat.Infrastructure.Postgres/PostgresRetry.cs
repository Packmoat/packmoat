using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The one retry the PostgreSQL stores have: an attempt the server ended with a deadlock (40P01) or a
/// serialization failure (40001) is run again, at most <see cref="MaxAttempts"/> attempts in all, with a short random wait between them.
/// The last failure is then thrown as it was. Anything else (a refusal, a timeout, a lost connection, a cancellation) is thrown at once.
/// <para>Each attempt must be a whole transaction of its own (open, run, commit), since the server rolls the failed one back. Only the
/// audit batch, the decision-record chunks and the token last-used update use it; the purge never does.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresRetry
{
    /// <summary>Attempts in all, the first one included.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The wait before attempt n+1 is n times this, plus up to this again at random.</summary>
    internal static readonly TimeSpan Step = TimeSpan.FromMilliseconds(20);

    /// <summary>Whether <paramref name="sqlState"/> is one this helper retries: deadlock_detected or serialization_failure, nothing else.</summary>
    public static bool IsRetried(string? sqlState) =>
        sqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

    /// <summary>Runs <paramref name="attempt"/>, again on a deadlock or a serialization failure (see <see cref="PostgresRetry"/>).</summary>
    public static Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> attempt, CancellationToken cancellationToken) =>
        RunAsync(attempt, Task.Delay, Random.Shared, cancellationToken);

    /// <summary>Runs <paramref name="attempt"/>, again on a deadlock or a serialization failure (see <see cref="PostgresRetry"/>).</summary>
    public static Task RunAsync(Func<CancellationToken, Task> attempt, CancellationToken cancellationToken) =>
        RunAsync(async ct => { await attempt(ct); return true; }, Task.Delay, Random.Shared, cancellationToken);

    /// <summary>The loop, with the wait and the randomness given (the tests give their own).</summary>
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> attempt, Func<TimeSpan, CancellationToken, Task> wait, Random jitter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        for (var n = 1; ; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await attempt(cancellationToken);
            }
            catch (PostgresException e) when (IsRetried(e.SqlState) && n < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await wait(Delay(n, jitter), cancellationToken);
            }
        }
    }

    /// <summary>The wait after failed attempt <paramref name="n"/>: n steps, plus a random part of one more, so two losers do not meet again.</summary>
    internal static TimeSpan Delay(int n, Random jitter) => Step * n + Step * jitter.NextDouble();
}
