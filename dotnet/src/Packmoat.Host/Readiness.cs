using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host;

/// <summary>
/// The readiness check behind /readyz (#233): the database opens, every migration track (the core's and each module's) has exactly the
/// versions this build knows, and the master key in use reads the database's key marker. Read only: it records nothing (no key marker, no
/// migration, no file).
/// <para>It answers "ready" or "not ready" and never says why. Only the failure's kind (database, schema or key) is logged, and only when
/// the answer changes. An answer is kept for <see cref="CacheFor"/>, so a probe cannot make the server read the database on every request.
/// It fails closed: no database, or any error, is "not ready".</para>
/// <para>Not registered in DI: the composition root builds one and maps it.</para>
/// </summary>
public sealed class Readiness
{
    public const string Route = "/readyz";
    public const string ReadyText = "ready";
    public const string NotReadyText = "not ready";
    public const string LogCategory = "Packmoat.Readiness";

    /// <summary>How long one answer is kept.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Why the server is not ready. The only thing about a failure that is logged.</summary>
    public enum Failure { None, Database, Schema, Key }

    private readonly Func<CancellationToken, Task<IReadOnlyList<TrackVersion>>>? _tracks;
    private readonly Func<CancellationToken, Task<bool>>? _keyMatches;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, Failure Result)? _cached;
    private Failure? _logged;

    /// <param name="tracks">Reads where every migration track stands, read only (the composition root's database, against the tracks the
    /// start migrated); null (no database) is never ready.</param>
    /// <param name="keyMatches">Reads whether the master key in use reads the database's key marker, read only; null is never ready.</param>
    public Readiness(Func<CancellationToken, Task<IReadOnlyList<TrackVersion>>>? tracks, Func<CancellationToken, Task<bool>>? keyMatches, IClock clock, ILogger log)
    {
        _tracks = tracks;
        _keyMatches = keyMatches;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The answer: <see cref="Failure.None"/> when ready. Kept for <see cref="CacheFor"/>.</summary>
    public async Task<Failure> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.UtcNow;
            if (_cached is { } c && now - c.At < CacheFor && now >= c.At) return c.Result;
            // Not the request's token: one probe giving up must not leave the next with a cancelled answer.
            var result = await ProbeAsync(CancellationToken.None);
            _cached = (now, result);
            if (_logged != result)
            {
                if (result == Failure.None) _log.LogInformation("Readiness: ready");
                else _log.LogWarning("Readiness: not ready ({Kind})", result.ToString().ToLowerInvariant());
                _logged = result;
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<Failure> ProbeAsync(CancellationToken ct)
    {
        if (_tracks is null || _keyMatches is null) return Failure.Database;
        IReadOnlyList<TrackVersion> tracks;
        try { tracks = await _tracks(ct); }
        catch (Exception) { return Failure.Database; }   // fail closed; the exception (which may name the file) is not logged
        if (tracks.Count == 0 || tracks.Any(t => !t.IsCurrent)) return Failure.Schema;
        try { return await _keyMatches(ct) ? Failure.None : Failure.Key; }
        catch (Exception) { return Failure.Database; }
    }

    /// <summary>GET /readyz: 200 "ready" or 503 "not ready", nothing else.</summary>
    public static void Map(WebApplication app, Readiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        app.MapGet(Route, async (HttpContext c) => await readiness.CheckAsync(c.RequestAborted) == Failure.None
            ? Results.Text(ReadyText)
            : Results.Text(NotReadyText, statusCode: StatusCodes.Status503ServiceUnavailable));
    }
}
