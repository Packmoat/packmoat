using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>How long the per-version decision record is kept. The figure is a product decision: change it here and nowhere else.</summary>
public static class DecisionRecordPolicy
{
    /// <summary>A row is deleted once it has not been seen for this many days (the retention pass, daily).</summary>
    public const int RetentionDays = 365;

    public static TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
}

/// <summary>
/// What Packmoat decided about one version for one tenant, and why: one row per (tenant, ecosystem, package, version, decision, reason),
/// so a version that moves from Cooldown to CooldownElapsed has two rows, which is its history. The package id is lower-case and the
/// version normalised (as in <see cref="PackageVersionFacts"/>). It holds no token, person, name or network address.
/// <paramref name="SeenCount"/> counts decisions (listings and downloads alike), not downloads. <paramref name="PublishedAt"/> is null
/// when the date was unknown or not needed.
/// </summary>
public sealed record DecisionRecord(
    string TenantId,
    Ecosystem Ecosystem,
    string PackageId,
    string Version,
    Decision Decision,
    VerdictReason Reason,
    string? WaiverId,
    int? CooldownDays,
    DateTimeOffset? PublishedAt,
    long PolicyRevision,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    long SeenCount);

/// <summary>Where decision records are kept.</summary>
public interface IDecisionRecordLog
{
    /// <summary>
    /// Adds a batch, in short transactions of a bounded number of rows each. A row already there keeps the earlier first-seen time,
    /// takes the later last-seen time, adds the count,
    /// and takes the batch's waiver, cooldown, publish date and policy revision only when the batch is newer. A purged tenant's rows are dropped.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken cancellationToken);

    /// <summary>Deletes rows last seen before <paramref name="lastSeenBefore"/>. Returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken cancellationToken);
}

/// <summary>
/// Which decision records to read, for the support view (the admin command line and the admin site). Always one tenant, newest
/// last-seen first, at most <see cref="Limit"/> rows. <see cref="PackageId"/> is matched exactly, or as a prefix when
/// <see cref="PackagePrefix"/> is set; it is never a pattern (no LIKE: '%', '_' and '\' are ordinary characters). A
/// <see cref="Version"/> needs a package. Values reach the store already normalised by <see cref="AdminQueries.DecisionsAsync"/>:
/// the package id lower-case, the version as in <see cref="NuGetVersionKey"/>.
/// </summary>
public sealed record DecisionRecordQuery(string TenantId, string? PackageId = null, bool PackagePrefix = false, string? Version = null,
    int Limit = DecisionRecordQuery.DefaultLimit, Ecosystem Ecosystem = Ecosystem.NuGet)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
}

/// <summary>One bounded read: the rows, and whether more matched than the limit let through.</summary>
public sealed record DecisionRecordPage(IReadOnlyList<DecisionRecord> Rows, bool More);

/// <summary>Reads the decision record. The support view reaches the table only through this port.</summary>
public interface IDecisionRecordQuery
{
    /// <summary>The newest rows by last-seen time (ties in package, version, decision and reason order), at most the query's limit.</summary>
    Task<DecisionRecordPage> QueryAsync(DecisionRecordQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the package gate and notes every verdict it gives (single versions and every version of a listing) in a bounded in-memory map,
/// coalesced per tenant, package, version, decision and reason. A background writer flushes the map to <see cref="IDecisionRecordLog"/>
/// every <see cref="FlushInterval"/>. The request path takes one short lock per gate call and never waits for storage.
/// Logging fails open, serving does not change: the gate's answer (or its exception) reaches the caller exactly as the inner gate gave it.
/// A full map drops new keys (counted); a failed write loses that batch (counted). Both are logged through <c>warn</c> by exception type
/// or count only, at most once per <see cref="WarnInterval"/>, never with a package or version (a tenant over its share
/// <see cref="MaxKeysPerTenant"/> is named by its id only). A crash loses at most one interval.
/// </summary>
public sealed class RecordingPackageGate : IPackageGate, IDisposable
{
    /// <summary>The most distinct keys held between flushes. About 50k rows of a few hundred bytes each: a few tens of MB at most.</summary>
    public const int DefaultCapacity = 50_000;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);
    /// <summary>
    /// The most new keys one tenant may add between flushes, so one tenant (any of its tokens can make up versions in download URLs)
    /// cannot starve the others. Its keys already held are still counted.
    /// </summary>
    public const int MaxKeysPerTenant = 10_000;
    /// <summary>Longer ids or versions are not recorded (counted as dropped): real NuGet ids are at most 100 characters.</summary>
    public const int MaxFieldLength = 256;

    private readonly record struct Key(string TenantId, Ecosystem Ecosystem, string PackageId, string Version, Decision Decision, VerdictReason Reason);

    private sealed class Entry
    {
        public DateTimeOffset First, Last;
        public long Count;
        public string? WaiverId;
        public int? CooldownDays;
        public DateTimeOffset? PublishedAt;
        public long Revision;
    }

    private readonly IPackageGate _inner;
    private readonly IDecisionRecordLog _log;
    private readonly IClock _clock;
    private readonly Action<string>? _warn;
    private readonly int _capacity;
    private readonly int _perTenantCapacity;
    private Dictionary<string, int> _tenantKeys = new(StringComparer.Ordinal);   // keys each tenant added since the last flush
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _droppedByTenant = new(StringComparer.Ordinal);
    private readonly TimeSpan _flushInterval;
    private readonly object _sync = new();
    private Dictionary<Key, Entry> _map = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly Task _writer;
    private long _dropped, _failedRecords, _failedWrites, _nextWarnTicks, _nextShareWarnTicks;
    private volatile bool _closed;

    /// <param name="capacity">A test seam: production uses <see cref="DefaultCapacity"/>.</param>
    /// <param name="flushInterval">A test seam: production uses <see cref="FlushInterval"/>.</param>
    /// <param name="perTenantCapacity">A test seam: production uses <see cref="MaxKeysPerTenant"/>.</param>
    public RecordingPackageGate(IPackageGate inner, IDecisionRecordLog log, IClock clock, Action<string>? warn = null,
        int capacity = DefaultCapacity, TimeSpan? flushInterval = null, int perTenantCapacity = MaxKeysPerTenant)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perTenantCapacity, 1);
        _perTenantCapacity = perTenantCapacity;
        _inner = inner;
        _log = log;
        _clock = clock;
        _warn = warn;
        _capacity = capacity;
        _flushInterval = flushInterval ?? FlushInterval;
        _stopToken = _stop.Token;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Decisions not recorded: the map was full, a tenant was over its share, a field was too long, or shutting down.</summary>
    public long DroppedRecords => Interlocked.Read(ref _dropped);

    /// <summary>Decisions of one tenant not recorded because it was over its share (<see cref="MaxKeysPerTenant"/>).</summary>
    public long DroppedFor(string tenantId) => _droppedByTenant.TryGetValue(tenantId, out var n) ? n : 0;

    /// <summary>Decisions whose bookkeeping threw (never a failed request).</summary>
    public long FailedRecords => Interlocked.Read(ref _failedRecords);

    /// <summary>Batches whose write failed; their rows are lost.</summary>
    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    public async Task<Verdict> DecideAsync(Tenant tenant, string packageId, string version, CancellationToken cancellationToken)
    {
        var verdict = await _inner.DecideAsync(tenant, packageId, version, cancellationToken);
        // A single version comes from the request URL, so any token can make up versions. One the date source has never heard of
        // (DateUnknown) only counts towards a key already held, so made-up versions cannot fill the map.
        Note(tenant, packageId, new[] { new KeyValuePair<string, Verdict>(version, verdict) }, mayCreate: verdict.Reason != VerdictReason.DateUnknown);
        return verdict;
    }

    public async Task<IReadOnlyDictionary<string, Verdict>> DecideManyAsync(
        Tenant tenant, string packageId, IReadOnlyCollection<string> versions, CancellationToken cancellationToken)
    {
        var verdicts = await _inner.DecideManyAsync(tenant, packageId, versions, cancellationToken);
        Note(tenant, packageId, verdicts);
        return verdicts;
    }

    private void Note(Tenant tenant, string packageId, IEnumerable<KeyValuePair<string, Verdict>> verdicts, bool mayCreate = true)
    {
        try
        {
            if (_closed) { Interlocked.Increment(ref _dropped); return; }
            var now = _clock.UtcNow;
            var id = packageId.Trim().ToLowerInvariant();
            var revision = tenant.Policy.Revision;
            long tooLong = 0;
            // Normalising and the length check are done before the lock, so the lock covers only the map updates.
            var notes = new List<(Key Key, Verdict Verdict)>();
            foreach (var (rawVersion, verdict) in verdicts)
            {
                var version = NuGetVersionKey.Normalize(rawVersion);
                if (id.Length > MaxFieldLength || version.Length > MaxFieldLength) { tooLong++; continue; }
                notes.Add((new Key(tenant.Id, Ecosystem.NuGet, id, version, verdict.Decision, verdict.Reason), verdict));
            }

            long full = 0, overShare = 0;
            if (notes.Count > 0)
            {
                lock (_sync)
                {
                    _tenantKeys.TryGetValue(tenant.Id, out var held);
                    foreach (var (key, verdict) in notes)
                    {
                        if (!_map.TryGetValue(key, out var entry))
                        {
                            if (!mayCreate) continue;                                    // counted only if already held: see DecideAsync
                            if (held >= _perTenantCapacity) { overShare++; continue; }
                            if (_map.Count >= _capacity) { full++; continue; }
                            entry = new Entry { First = now, Last = now };
                            _map[key] = entry;
                            held++;
                        }
                        entry.Count++;
                        if (now >= entry.Last)
                        {
                            entry.Last = now;
                            entry.WaiverId = verdict.WaiverId;
                            entry.CooldownDays = verdict.CooldownDaysApplied;
                            entry.PublishedAt = verdict.PublishedAt;
                            entry.Revision = revision;
                        }
                        if (now < entry.First) entry.First = now;
                    }
                    _tenantKeys[tenant.Id] = held;
                }
            }

            if (tooLong + full + overShare == 0) return;
            var n = Interlocked.Add(ref _dropped, tooLong + full + overShare);
            if (overShare > 0)
            {
                var forTenant = _droppedByTenant.AddOrUpdate(tenant.Id, overShare, (_, old) => old + overShare);
                WarnLimited(ref _nextShareWarnTicks, $"Tenant {tenant.Id} is over its share of the decision record ({_perTenantCapacity} new versions per flush); " +
                            $"{forTenant} decision(s) of that tenant not recorded so far. The decision record may be incomplete.");
            }
            else if (full > 0)
                WarnLimited($"Decision record map is full; {n} decision(s) not recorded so far. The decision record may be incomplete.");
            else
                WarnLimited($"A package id or version longer than {MaxFieldLength} characters was not recorded; {n} decision(s) not recorded so far.");
        }
        catch (Exception ex)
        {
            // Bookkeeping must never fail or change a request, but it must not vanish either.
            var n = Interlocked.Increment(ref _failedRecords);
            WarnLimited($"Decision recording failed ({ex.GetType().Name}); {n} failure(s) so far. The decision record may be incomplete.");
        }
    }

    /// <summary>
    /// Writes what is held now as one batch and empties the map. Returns how many rows were written (0 when nothing was held or the
    /// write failed). The background writer calls it every interval; tests and shutdown call it directly. One flush at a time.
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushing.WaitAsync(cancellationToken);
        try
        {
            Dictionary<Key, Entry> batch;
            lock (_sync)
            {
                if (_map.Count == 0) return 0;
                batch = _map;
                _map = new Dictionary<Key, Entry>();
                _tenantKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            }
            var records = batch.Select(kv => new DecisionRecord(kv.Key.TenantId, kv.Key.Ecosystem, kv.Key.PackageId, kv.Key.Version,
                kv.Key.Decision, kv.Key.Reason, kv.Value.WaiverId, kv.Value.CooldownDays, kv.Value.PublishedAt, kv.Value.Revision,
                kv.Value.First, kv.Value.Last, kv.Value.Count)).ToList();
            try
            {
                await _log.UpsertAsync(records, cancellationToken);
                return records.Count;
            }
            catch (Exception ex)
            {
                var n = Interlocked.Increment(ref _failedWrites);
                WarnLimited($"Decision record write failed ({ex.GetType().Name}); {n} failed write(s) so far, up to {records.Count} row(s) lost this time. The decision record may be incomplete.");
                return 0;
            }
        }
        finally { _flushing.Release(); }
    }

    private long NowTicks()
    {
        try { return _clock.UtcNow.UtcTicks; } catch { return DateTime.UtcNow.Ticks; }
    }

    /// <summary>At most one warning per window, so an outage does not log once per request. Never throws.</summary>
    private void WarnLimited(string message) => WarnLimited(ref _nextWarnTicks, message);

    /// <summary>
    /// The same, against its own window: a tenant over its share has its own, so a busy tenant can never hide a failed write or a full
    /// map (and they cannot hide it).
    /// </summary>
    private void WarnLimited(ref long nextTicks, string message)
    {
        try
        {
            if (_warn is null) return;
            var now = NowTicks();
            var next = Interlocked.Read(ref nextTicks);
            if (now < next || Interlocked.CompareExchange(ref nextTicks, now + WarnInterval.Ticks, next) != next) return;
            _warn(message);
        }
        catch { /* a broken logger must not kill the writer or a request */ }
    }

    private async Task WriteLoopAsync()
    {
        while (!_stopToken.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, _stopToken); }
            catch (OperationCanceledException) { return; }
            // Not the stop token: a write already under way is left to finish, so shutdown never throws away a batch it took.
            try { await FlushAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                // FlushAsync guards the write; this is for anything else. Count it and carry on rather than die silently.
                var n = Interlocked.Increment(ref _failedWrites);
                WarnLimited($"Decision record writer failed ({ex.GetType().Name}); {n} failed write(s) so far. The decision record may be incomplete.");
            }
        }
    }

    /// <summary>Stops taking notes and the timer, then writes what is held (waiting a few seconds at most). The gate keeps answering.</summary>
    public void Dispose()
    {
        if (_closed) return;   // idempotent
        _closed = true;
        _stop.Cancel();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        try
        {
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            FlushAsync(last.Token).Wait(TimeSpan.FromSeconds(6));
        }
        catch { /* best effort: at most one interval is lost */ }
        _stop.Dispose();
    }
}
