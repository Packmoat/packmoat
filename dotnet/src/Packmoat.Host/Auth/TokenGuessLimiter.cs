using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Domain;
using Window = Packmoat.Host.Auth.AddressWindows.Window;

namespace Packmoat.Host.Auth;

/// <summary>
/// Slows token guessing on the feed. Only a token that was presented and is wrong counts: NuGet sends every request
/// once without credentials and retries after the 401 (NuGet/Home#12517), so a missing header is not a guess.
/// An address is blocked for the rest of its one-minute window after <c>maxDistinctTokens</c> DIFFERENT wrong tokens
/// (the same stale CI secret repeated is not guessing), or after <c>maxWrongAttempts</c> wrong attempts of any kind,
/// a backstop that protects the token store (wrong tokens are not cached). Keyed by <see cref="ClientKey.Address"/>,
/// never globally, so one guesser cannot lock everyone out; blocks are short because a customer's developers can share one NAT address.
/// Only the first 8 bytes of the token's SHA-256 are kept, never the token.
/// </summary>
/// <remarks>
/// <para>Reserve, then settle. A presented token first reserves a lookup with <see cref="TryBeginLookup"/>, and the caller releases
/// it with <see cref="EndLookup"/> in a <c>finally</c>. An address is refused a lookup when its wrong attempts plus its lookups in flight
/// reach the backstop, so parallel guesses cannot all reach the store before the first one is counted. The distinct-token rule is not
/// reserved: a build farm behind one NAT address restoring in parallel with one stale secret must not get 429s.</para>
/// <para>Memory, and the network overflow. Expired windows are swept at most once per window, on whichever call comes next.
/// The address map holds at most <c>maxAddresses</c> addresses. When it is full, a new address is tracked by its
/// <see cref="ClientKey.Network"/> (/48 or /24) in a second map, of at most <c>maxNetworks</c> networks, under the same rules; so an attacker
/// who fills the address map still needs about <c>maxNetworks</c> networks to escape counting. When both are full a new client is not
/// tracked. One Warning per map is logged per window. Neither map fails closed: an address with a valid token never keeps an entry, so
/// failing closed would lock out every customer. Neither evicts the oldest either: finding it costs a full scan and an attacker gains no
/// less from that than from being refused tracking. A request with no token only reads: it never creates an entry in either map.</para>
/// <para>Accepted gap. An address that holds an entry in the address map is judged by that entry only, not by its network's. So a blocked
/// /48 can regain a few /64 slots as customers' entries free up, at most <c>maxDistinctTokens</c> tokens each.</para>
/// </remarks>
public sealed class TokenGuessLimiter
{
    public const int DefaultMaxDistinctTokens = 20;
    public const int DefaultMaxWrongAttempts = 300;
    public const int DefaultMaxAddresses = 50_000;
    public const int DefaultMaxNetworks = 10_000;

    /// <summary>A reserved lookup. Pass it to <see cref="EndLookup"/> exactly once.</summary>
    public readonly struct Lookup
    {
        internal Lookup(ClientKey client, AddressWindows? map, string? mapKey, Window? slot)
        {
            Client = client;
            Map = map;
            MapKey = mapKey;
            Slot = slot;
        }

        internal ClientKey Client { get; }
        /// <summary>The map the reservation was made in; null when the client was not tracked because both maps were full.</summary>
        internal AddressWindows? Map { get; }
        internal string? MapKey { get; }
        /// <summary>The window holding the reservation; null when the client was not tracked.</summary>
        internal Window? Slot { get; }
    }

    private readonly IClock _clock;
    private readonly int _maxDistinctTokens;
    private readonly int _maxWrongAttempts;
    private readonly TimeSpan _window;
    private readonly ILogger _log;
    private readonly AddressWindows _addresses;
    private readonly AddressWindows _networks;
    private readonly object _lock = new();

    public TokenGuessLimiter(IClock clock, int maxDistinctTokens = DefaultMaxDistinctTokens, int maxWrongAttempts = DefaultMaxWrongAttempts, TimeSpan? window = null,
        int maxAddresses = DefaultMaxAddresses, ILogger<TokenGuessLimiter>? log = null, int maxNetworks = DefaultMaxNetworks)
    {
        _clock = clock;
        _maxDistinctTokens = maxDistinctTokens;
        _maxWrongAttempts = maxWrongAttempts;
        _window = window ?? TimeSpan.FromMinutes(1);
        _addresses = new AddressWindows(maxAddresses, _window);
        _networks = new AddressWindows(maxNetworks, _window);
        _log = log ?? NullLogger<TokenGuessLimiter>.Instance;
    }

    /// <summary>
    /// Seconds the client must wait, or null if it may proceed. For a request with no token, which makes no lookup: only settled wrong
    /// attempts count here, not lookups in flight. Only reads: it never creates an entry.
    /// </summary>
    /// <remarks>
    /// Accepted asymmetry: when a /48 or /24 is blocked in the network map and the address map has room again, a new address in that
    /// network gets 429 here (from the network entry), while a token request from the same address gets a fresh address entry. It is
    /// bounded by the network entry's window.
    /// </remarks>
    public int? RetryAfterSeconds(ClientKey client)
    {
        var now = _clock.UtcNow;
        var full = Full.None;
        lock (_lock)
        {
            if (!Resolve(client, now, create: false, ref full, out _, out _, out var w) || now - w.Start >= _window) return null;
            return Blocked(w, inFlight: 0) ? Wait(w, now) : null;
        }
    }

    /// <summary>
    /// Reserves a token lookup for the client. False, with the seconds to wait, when it is blocked; then nothing is reserved.
    /// True otherwise; the caller must then call <see cref="EndLookup"/> once, in a <c>finally</c>.
    /// </summary>
    public bool TryBeginLookup(ClientKey client, out Lookup lookup, out int retryAfterSeconds)
    {
        var now = _clock.UtcNow;
        var full = Full.None;
        try
        {
            lock (_lock)
            {
                Sweep(now);
                if (!Resolve(client, now, create: true, ref full, out var map, out var mapKey, out var w))
                {
                    lookup = new Lookup(client, null, null, null);
                    retryAfterSeconds = 0;
                    return true;
                }
                if (now - w.Start >= _window) w.Restart(now);

                if (Blocked(w, w.InFlight))
                {
                    lookup = default;
                    retryAfterSeconds = Wait(w, now);
                    return false;
                }
                w.InFlight++;
                lookup = new Lookup(client, map, mapKey, w);
                retryAfterSeconds = 0;
                return true;
            }
        }
        finally
        {
            WarnFull(full);
        }
    }

    /// <summary>
    /// Releases a lookup reserved by <see cref="TryBeginLookup"/>. Pass the token when it was presented and matched no one, so it counts;
    /// pass null when it matched, or when the lookup failed or was cancelled. The token is hashed at once and not kept.
    /// </summary>
    public void EndLookup(Lookup lookup, string? wrongToken)
    {
        ulong? key = wrongToken is null ? null : KeyOf(wrongToken);
        var now = _clock.UtcNow;
        var full = Full.None;
        lock (_lock)
        {
            var map = lookup.Map;
            var mapKey = lookup.MapKey;
            var w = lookup.Slot;
            if (w is not null) w.InFlight--;
            if (key is { } k)
            {
                // Not tracked when it began (both maps were full): count it now if there is room.
                if (w is null && Resolve(lookup.Client, now, create: true, ref full, out var m, out var mk, out var found))
                    (map, mapKey, w) = (m, mk, found);
                if (w is not null)
                {
                    if (now - w.Start >= _window) w.Restart(now);
                    if (w.TokenKeys.Count < _maxDistinctTokens) w.TokenKeys.Add(k);   // enough to block; no more kept
                    w.Attempts++;
                }
            }
            // A client whose tokens were all valid keeps no entry, in either map.
            if (w is not null && map is not null && mapKey is not null) map.RemoveIfIdle(mapKey, w);
            Sweep(now);
        }
        WarnFull(full);
    }

    [Flags]
    private enum Full { None = 0, Addresses = 1, Networks = 2 }

    /// <summary>
    /// Finds the window a client is counted in: its address entry; else a new address entry, if there is room; else its network entry,
    /// created if there is room; else none (not tracked, failing open). With <paramref name="create"/> false it only reads, and falls back
    /// to the network entry.
    /// </summary>
    private bool Resolve(ClientKey client, DateTimeOffset now, bool create, ref Full full, out AddressWindows map, out string mapKey, out Window window)
    {
        if (_addresses.TryGet(client.Address, out window) || (create && _addresses.TryAdd(client.Address, now, out window)))
        {
            (map, mapKey) = (_addresses, client.Address);
            return true;
        }
        if (create && _addresses.NoteFull(now)) full |= Full.Addresses;

        if (_networks.TryGet(client.Network, out window) || (create && _networks.TryAdd(client.Network, now, out window)))
        {
            (map, mapKey) = (_networks, client.Network);
            return true;
        }
        if (create && _networks.NoteFull(now)) full |= Full.Networks;

        (map, mapKey) = (null!, null!);
        return false;
    }

    private bool Blocked(Window w, int inFlight) => w.TokenKeys.Count >= _maxDistinctTokens || w.Attempts + inFlight >= _maxWrongAttempts;

    private void Sweep(DateTimeOffset now)
    {
        _addresses.Sweep(now);
        _networks.Sweep(now);
    }

    // Logged outside the lock so a slow log sink never holds up other requests.
    private void WarnFull(Full full)
    {
        if (full.HasFlag(Full.Addresses))
            _log.LogWarning("Token guess limiter address map is full ({Max} addresses): tracking new addresses by network until old windows expire", _addresses.Max);
        if (full.HasFlag(Full.Networks))
            _log.LogWarning("Token guess limiter network map is full ({Max} networks): new networks are not tracked until old windows expire", _networks.Max);
    }

    private int Wait(Window w, DateTimeOffset now) => Math.Max(1, (int)Math.Ceiling((w.Start + _window - now).TotalSeconds));

    /// <summary>The first 8 bytes of the token's SHA-256: about 0.6 KB per address at the distinct limit, instead of 20 hex strings.</summary>
    internal static ulong KeyOf(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>For tests: records a wrong token as one complete lookup would.</summary>
    internal void RecordWrongToken(ClientKey client, string token)
    {
        if (TryBeginLookup(client, out var lookup, out _)) EndLookup(lookup, token);
        else EndLookup(new Lookup(client, null, null, null), token);
    }

    /// <summary>For tests: the token keys kept in the address map for an address in its current window.</summary>
    internal IReadOnlyCollection<ulong> TokenKeysFor(string address)
    {
        lock (_lock) return _addresses.TryGet(address, out var w) ? w.TokenKeys.ToList() : Array.Empty<ulong>();
    }

    /// <summary>For tests: lookups in flight in the address map for an address.</summary>
    internal int InFlightFor(string address)
    {
        lock (_lock) return _addresses.TryGet(address, out var w) ? w.InFlight : 0;
    }

    /// <summary>For tests: lookups in flight in the network map for a network.</summary>
    internal int NetworkInFlightFor(string network)
    {
        lock (_lock) return _networks.TryGet(network, out var w) ? w.InFlight : 0;
    }

    /// <summary>For tests: how many addresses are held in memory.</summary>
    internal int AddressCount { get { lock (_lock) return _addresses.Count; } }

    /// <summary>For tests: how many networks are held in memory.</summary>
    internal int NetworkCount { get { lock (_lock) return _networks.Count; } }
}
