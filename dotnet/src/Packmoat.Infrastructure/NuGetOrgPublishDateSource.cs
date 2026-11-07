using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

public sealed class NuGetOrgDateOptions
{
    public string RegistrationBase { get; set; } = "https://api.nuget.org/v3/registration5-gz-semver2/";
    /// <summary>How long a fetched answer is used without asking again.</summary>
    public TimeSpan FreshFor { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>A package nuget.org does not know is asked about again after this long (it may just have been published).</summary>
    public TimeSpan NotFoundFor { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>If nuget.org cannot be reached, an older answer is still used up to this age; beyond it we fail closed.</summary>
    public TimeSpan StaleLimit { get; set; } = TimeSpan.FromHours(24);
    /// <summary>After a failed fetch we do not ask again for this long, so an outage is not hammered by every request.</summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The cache holds at most this many packages; the oldest are dropped first.</summary>
    public int MaxEntries { get; set; } = 20_000;
    public int MaxConcurrentFetches { get; set; } = 8;
    /// <summary>The largest registration document we will read, enforced while reading.</summary>
    public long MaxResponseBytes { get; set; } = 32 * 1024 * 1024;
}

/// <summary>
/// Publish dates from nuget.org's registration API. The feed's own date is the save time, which is useless for a cooldown.
/// Versions nuget.org lists with a placeholder date (unlisted packages show the year 1900) are treated as unknown.
/// Concurrent requests for the same package share one fetch. Failures never return a made-up answer: callers get null and must fail closed.
/// </summary>
public sealed class NuGetOrgPublishDateSource : IPublishDateSource
{
    private sealed class Entry
    {
        public IReadOnlyDictionary<string, DateTimeOffset>? Dates;   // last good answer (empty when nuget.org said 404)
        public DateTimeOffset FetchedAt;
        public bool NotFound;
        public DateTimeOffset? FailedAt;
    }

    private static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly NuGetOrgDateOptions _options;
    private readonly Dictionary<string, Entry> _cache = new();
    private readonly Dictionary<string, Task> _inflight = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate;

    public NuGetOrgPublishDateSource(HttpClient http, IClock clock, NuGetOrgDateOptions? options = null)
    {
        _http = http;
        _clock = clock;
        _options = options ?? new NuGetOrgDateOptions();
        _gate = new SemaphoreSlim(_options.MaxConcurrentFetches);
    }

    public int CachedPackages { get { lock (_lock) return _cache.Count; } }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(
        Ecosystem ecosystem, string packageId, CancellationToken cancellationToken)
    {
        var key = packageId.Trim().ToLowerInvariant();

        if (TryAnswer(key, out var answer)) return answer;

        Task fetch;
        lock (_lock)
        {
            if (!_inflight.TryGetValue(key, out fetch!))
            {
                fetch = Task.Run(() => FetchAndStoreAsync(key));
                _inflight[key] = fetch;
            }
        }
        await fetch.WaitAsync(cancellationToken); // one caller giving up does not cancel the shared fetch

        return TryAnswer(key, out answer, afterFetch: true) ? answer : null;
    }

    /// <summary>Answers from the cache when it may: fresh data, or stale data while nuget.org is failing. Otherwise false (go and fetch).</summary>
    private bool TryAnswer(string key, out IReadOnlyDictionary<string, DateTimeOffset>? answer, bool afterFetch = false)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            answer = null;
            if (!_cache.TryGetValue(key, out var e)) return afterFetch; // nothing at all, and the fetch just failed: answer is null (fail closed)

            var usable = e.Dates is not null && now - e.FetchedAt < _options.StaleLimit;
            if (e.Dates is not null && now - e.FetchedAt < (e.NotFound ? _options.NotFoundFor : _options.FreshFor)) { answer = e.Dates; return true; }
            if (e.FailedAt is { } failed && now - failed < _options.FailureBackoff) { answer = usable ? e.Dates : null; return true; }
            if (afterFetch) { answer = usable && e.FailedAt is not null ? e.Dates : null; return true; }
            return false;
        }
    }

    private async Task FetchAndStoreAsync(string key)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                var (dates, notFound) = await FetchAsync(key);
                lock (_lock)
                {
                    _cache[key] = new Entry { Dates = dates, FetchedAt = _clock.UtcNow, NotFound = notFound };
                    Trim();
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception)
        {
            lock (_lock)
            {
                if (!_cache.TryGetValue(key, out var e)) _cache[key] = e = new Entry();
                e.FailedAt = _clock.UtcNow;
                Trim();
            }
        }
        finally
        {
            lock (_lock) _inflight.Remove(key);
        }
    }

    /// <summary>Keeps the cache bounded: drop what is too old to use, then the oldest, until it fits.</summary>
    private void Trim()
    {
        if (_cache.Count <= _options.MaxEntries) return;
        var now = _clock.UtcNow;
        foreach (var k in _cache.Where(kv => !_inflight.ContainsKey(kv.Key) && Age(kv.Value, now) >= _options.StaleLimit).Select(kv => kv.Key).ToList())
            _cache.Remove(k);
        if (_cache.Count <= _options.MaxEntries) return;
        var excess = _cache.Count - _options.MaxEntries + Math.Max(1, _options.MaxEntries / 10);
        foreach (var k in _cache.Where(kv => !_inflight.ContainsKey(kv.Key)).OrderByDescending(kv => Age(kv.Value, now)).Take(excess).Select(kv => kv.Key).ToList())
            _cache.Remove(k);
    }

    private static TimeSpan Age(Entry e, DateTimeOffset now) => now - (e.FailedAt is { } f && (e.Dates is null || f > e.FetchedAt) ? f : e.FetchedAt);

    private async Task<(IReadOnlyDictionary<string, DateTimeOffset> Dates, bool NotFound)> FetchAsync(string id)
    {
        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var root = await GetJsonAsync(new Uri(new Uri(_options.RegistrationBase), $"{Uri.EscapeDataString(id)}/index.json"));
        if (root is null) return (dates, true); // not on nuget.org: every version unknown

        foreach (var page in root["items"]?.AsArray() ?? new JsonArray())
        {
            var items = page?["items"] as JsonArray;
            if (items is null)
            {
                var pageUrl = page?["@id"]?.GetValue<string>() ?? throw new FormatException("Registration page without items or id.");
                items = (await GetJsonAsync(new Uri(pageUrl)) ?? throw new FormatException("Registration page vanished."))["items"] as JsonArray ?? new JsonArray();
            }
            foreach (var item in items)
            {
                var entry = item?["catalogEntry"];
                var version = entry?["version"]?.GetValue<string>();
                if (version is null || entry?["published"] is not JsonValue p || !p.TryGetValue<DateTimeOffset>(out var published)) continue;
                if (published < Earliest) continue; // placeholder date (unlisted): unknown
                dates[NuGetVersionKey.Normalize(version)] = published.ToUniversalTime();
            }
        }
        return (dates, false);
    }

    /// <summary>Null for 404. Throws for any other failure, and when the body is bigger than the limit (checked while reading).</summary>
    private async Task<JsonNode?> GetJsonAsync(Uri url)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync();
        var text = await BoundedText.ReadAsync(body, _options.MaxResponseBytes, CancellationToken.None);
        return JsonNode.Parse(text) ?? throw new FormatException("Empty registration.");
    }
}
