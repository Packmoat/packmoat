using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Domain;

namespace Packmoat.Host.Auth;

/// <summary>
/// A generous per-token ceiling per minute. It exists to stop a runaway CI loop or a stolen token from hammering
/// the service, not to meter normal restores (a large solution restore makes hundreds of requests).
/// </summary>
/// <remarks>
/// Observe only: when a token's window closes above half the ceiling, one Information line records its peak,
/// so production data can show how close real build farms come. Only the token id is logged, never the token.
/// A window closes when its token's next request replaces it, or when the sweep removes it. The sweep runs at most
/// once a minute, on whichever request comes next from any token, so a token that never returns is still logged and
/// its memory freed; on a server with no requests at all the line waits for the next request. No timer is needed,
/// and the sweep also bounds the map to the tokens seen in the last two minutes.
/// </remarks>
public sealed class TokenRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly IClock _clock;
    private readonly int _perMinute;
    private readonly ILogger _log;
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = new();
    private readonly object _lock = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public TokenRateLimiter(IClock clock, int perMinute, ILogger<TokenRateLimiter>? log = null)
    {
        _clock = clock;
        _perMinute = perMinute;
        _log = log ?? NullLogger<TokenRateLimiter>.Instance;
    }

    /// <summary>Returns null if the request may proceed, otherwise the seconds to wait.</summary>
    public int? TryAcquire(string tokenId)
    {
        var now = _clock.UtcNow;
        List<(string TokenId, int Count)>? peaks = null;
        int? wait;
        lock (_lock)
        {
            // The second test catches the clock jumping back, which would otherwise postpone the sweep by the jump.
            if (now >= _nextSweep || _nextSweep - now > Window)
            {
                foreach (var (id, closed) in _windows.Where(kv => now - kv.Value.Start >= Window).ToList())
                {
                    _windows.Remove(id);
                    NotePeak(ref peaks, id, closed.Count);
                }
                _nextSweep = now + Window;
            }

            if (!_windows.TryGetValue(tokenId, out var w)) w = (now, 0);
            else if (now - w.Start >= Window)
            {
                NotePeak(ref peaks, tokenId, w.Count);
                w = (now, 0);
            }

            if (w.Count >= _perMinute)
            {
                _windows[tokenId] = w;
                wait = Math.Max(1, (int)Math.Ceiling((w.Start + Window - now).TotalSeconds));
            }
            else
            {
                _windows[tokenId] = (w.Start, w.Count + 1);
                wait = null;
            }
        }

        // Logged outside the lock so a slow log sink never holds up other requests.
        if (peaks is not null)
            foreach (var (id, count) in peaks)
                _log.LogInformation("Token {TokenId} peaked at {Count}/min ({Percent}% of ceiling)", id, count, (int)(count * 100L / _perMinute));
        return wait;
    }

    private void NotePeak(ref List<(string, int)>? peaks, string tokenId, int count)
    {
        if (count * 2L > _perMinute) (peaks ??= new()).Add((tokenId, count));
    }

    /// <summary>How many token windows are held in memory (for tests).</summary>
    internal int WindowCount { get { lock (_lock) return _windows.Count; } }
}
