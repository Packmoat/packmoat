namespace Packmoat.Host.Auth;

/// <summary>
/// One map of one-minute windows for <see cref="TokenGuessLimiter"/>, keyed by address or by network (#153). It holds at most
/// <c>max</c> entries and sweeps expired ones at most once per window. Not thread-safe: the limiter's lock guards it.
/// </summary>
internal sealed class AddressWindows
{
    internal sealed class Window
    {
        public DateTimeOffset Start;
        public readonly HashSet<ulong> TokenKeys = new();
        public int Attempts;
        public int InFlight;

        public void Restart(DateTimeOffset now)
        {
            Start = now;
            TokenKeys.Clear();
            Attempts = 0;
        }
    }

    private readonly Dictionary<string, Window> _byKey = new();
    private readonly TimeSpan _window;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFullWarning = DateTimeOffset.MinValue;

    public AddressWindows(int max, TimeSpan window)
    {
        Max = max;
        _window = window;
    }

    public int Max { get; }
    public int Count => _byKey.Count;

    public bool TryGet(string key, out Window window) => _byKey.TryGetValue(key, out window!);

    /// <summary>Adds a fresh window for the key. False when the map is full; then nothing is added.</summary>
    public bool TryAdd(string key, DateTimeOffset now, out Window window)
    {
        if (_byKey.Count >= Max) { window = null!; return false; }
        _byKey[key] = window = new Window { Start = now };
        return true;
    }

    /// <summary>Removes the key's window if it is this one and holds nothing: no lookup in flight and no wrong attempt.</summary>
    public void RemoveIfIdle(string key, Window window)
    {
        if (window.InFlight == 0 && window.Attempts == 0 && _byKey.TryGetValue(key, out var held) && ReferenceEquals(held, window))
            _byKey.Remove(key);
    }

    public void Sweep(DateTimeOffset now)
    {
        // The second test catches the clock jumping back, which would otherwise postpone the sweep by the jump.
        if (now < _nextSweep && _nextSweep - now <= _window) return;
        foreach (var k in _byKey.Where(kv => now - kv.Value.Start >= _window && kv.Value.InFlight == 0).Select(kv => kv.Key).ToList())
            _byKey.Remove(k);
        _nextSweep = now + _window;
    }

    /// <summary>True at most once per window: the caller then logs, outside the lock, that the map is full.</summary>
    public bool NoteFull(DateTimeOffset now)
    {
        if (now < _nextFullWarning && _nextFullWarning - now <= _window) return false;
        _nextFullWarning = now + _window;
        return true;
    }
}
