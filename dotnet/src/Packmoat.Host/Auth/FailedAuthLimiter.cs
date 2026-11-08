using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Domain;

namespace Packmoat.Host.Auth;

/// <summary>
/// Limits failed sign-ins per address: after too many from one address in a window, that address gets 429 until the
/// window passes. Short blocks only, because many developers can share one address behind a customer's NAT.
/// </summary>
/// <remarks>
/// Memory, as in <see cref="TokenGuessLimiter"/> (#127): expired windows are swept at most once per window, and at most
/// <c>maxAddresses</c> addresses are held. When full, a new address is not tracked and one Warning is logged per window; it does not fail
/// closed, which would lock out every person signing in. Parallel failures from one address are counted only after the check, so they
/// can overshoot the limit; that is accepted here (#127).
/// </remarks>
public sealed class FailedAuthLimiter
{
    public const int DefaultMaxAddresses = 50_000;

    private readonly IClock _clock;
    private readonly int _maxFailures;
    private readonly int _maxAddresses;
    private readonly TimeSpan _window;
    private readonly ILogger _log;
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Failures)> _byAddress = new();
    private readonly object _lock = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFullWarning = DateTimeOffset.MinValue;

    public FailedAuthLimiter(IClock clock, int maxFailures = 20, TimeSpan? window = null, int maxAddresses = DefaultMaxAddresses, ILogger? log = null)
    {
        _clock = clock;
        _maxFailures = maxFailures;
        _maxAddresses = maxAddresses;
        _window = window ?? TimeSpan.FromMinutes(1);
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Seconds the caller must wait, or null if it may proceed.</summary>
    public int? RetryAfterSeconds(string address)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            if (!_byAddress.TryGetValue(address, out var e) || now - e.WindowStart >= _window) return null;
            return e.Failures >= _maxFailures ? Math.Max(1, (int)Math.Ceiling((e.WindowStart + _window - now).TotalSeconds)) : null;
        }
    }

    public void RecordFailure(string address)
    {
        var now = _clock.UtcNow;
        var warn = false;
        lock (_lock)
        {
            // The second test catches the clock jumping back, which would otherwise postpone the sweep by the jump.
            if (now >= _nextSweep || _nextSweep - now > _window)
            {
                foreach (var k in _byAddress.Where(kv => now - kv.Value.WindowStart >= _window).Select(kv => kv.Key).ToList()) _byAddress.Remove(k);
                _nextSweep = now + _window;
            }

            if (!_byAddress.TryGetValue(address, out var e))
            {
                if (_byAddress.Count >= _maxAddresses)
                {
                    if (now >= _nextFullWarning || _nextFullWarning - now > _window) { _nextFullWarning = now + _window; warn = true; }
                }
                else _byAddress[address] = (now, 1);
            }
            else _byAddress[address] = now - e.WindowStart >= _window ? (now, 1) : (e.WindowStart, e.Failures + 1);
        }
        // Logged outside the lock so a slow log sink never holds up other requests.
        if (warn) _log.LogWarning("Failed sign-in limiter is full ({Max} addresses): new addresses are not tracked until old windows expire", _maxAddresses);
    }

    /// <summary>For tests: how many addresses are held in memory.</summary>
    internal int AddressCount { get { lock (_lock) return _byAddress.Count; } }
}
