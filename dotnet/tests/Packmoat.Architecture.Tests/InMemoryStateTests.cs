using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// In-memory state is declared (#278 step 0, architecture test 7). Every field of a core class that holds a mutable collection, a
/// concurrent collection, a channel, a cache or an in-process lock is on this list, which says how it behaves with more than one copy of
/// the server: "per copy: why that is safe", "per copy until #250: what breaks" (E10 #250 gives it a port; until then more than one copy
/// needs sticky sessions), or "shared via port: which". The hosted projects have their own twin.
/// </summary>
public class InMemoryStateTests
{
    internal static readonly string[] Assemblies =
    [
        "Packmoat.Domain", "Packmoat.Policy", "Packmoat.Application", "Packmoat.Protocols.NuGet", "Packmoat.Infrastructure",
        "Packmoat.Host", "Packmoat.Admin", "Packmoat.Server", "Packmoat.Cli",
    ];

    internal static readonly Dictionary<string, string> Declared = new(StringComparer.Ordinal)
    {
        // the admin tool: one command, one process
        ["Packmoat.Admin.AdminCli+CliStores.Notes"] = "per copy: one admin command's notes for its printout",
        ["Packmoat.Admin.AdminCli.CoreGroups"] = "per copy: a fixed table of the admin tool's command groups",
        ["Packmoat.Admin.AdminCliArgs.Flags"] = "per copy: a fixed table of the admin tool's flag names",
        ["Packmoat.Admin.AdminCliArgs._opts"] = "per copy: one admin command's parsed options",
        ["Packmoat.Admin.AdminCliArgs._pos"] = "per copy: one admin command's parsed arguments",
        ["Packmoat.Infrastructure.OnboardingChecks._namedAdmins"] = "per copy: one onboarding check run's findings",
        ["Packmoat.Infrastructure.OnboardingChecks._principals"] = "per copy: one onboarding check run's findings",
        ["Packmoat.Infrastructure.OnboardingOptions.ConfirmedAdmins"] = "per copy: one onboarding check run's options",
        ["Packmoat.Infrastructure.OnboardingOptions.PrivateFeedProbeUrls"] = "per copy: one onboarding check run's options",
        // fixed when made
        ["Packmoat.Host.Admin.AdminAllowlist._pairs"] = "per copy: the admin allowlist from settings, fixed at start; every copy reads the same",
        ["Packmoat.Host.Admin.AdminWrites.Verbs"] = "per copy: a fixed table of the admin site's write verbs",
        ["Packmoat.Infrastructure.MigrationTrack._statements"] = "per copy: a track's statements, fixed when it is made",
        // caches of what a port or a public service holds: each copy's is a bounded-stale view of the same truth
        ["Packmoat.Application.CachedTenantAuthenticator._cache"] = "per copy: token checks for 5 s; a revoke reaches every copy within that",
        ["Packmoat.Infrastructure.EntraCertificateCredentialSource._credentials"] = "per copy: built credentials, re-read from the store every 5 s",
        ["Packmoat.Infrastructure.NuGetOrgPublishDateSource._cache"] = "per copy: nuget.org's public publish dates; every copy gets the same",
        ["Packmoat.Infrastructure.NuGetOrgPublishDateSource._gate"] = "per copy: caps this copy's concurrent fetches from nuget.org",
        ["Packmoat.Infrastructure.NuGetOrgPublishDateSource._inflight"] = "per copy: joins this copy's duplicate fetches of one package",
        ["Packmoat.Host.Readiness._gate"] = "per copy: one readiness check at a time in this copy; it only reads",
        // write buffers in front of a port: each copy drains its own into the same store
        ["Packmoat.Application.QueuedAuditLog._queue"] = "per copy: audit events on their way to the store; each copy drains its own",
        ["Packmoat.Application.ActivityRecordingAuthenticator._queue"] = "per copy: token-use days on their way to the store; the write is idempotent",
        ["Packmoat.Application.ActivityRecordingAuthenticator._gate"] = "per copy: skips days this copy already queued; another copy's repeat is idempotent",
        ["Packmoat.Application.RecordingPackageGate._map"] = "per copy: decision records counted between flushes; each copy adds its own counts",
        ["Packmoat.Application.RecordingPackageGate._tenantKeys"] = "per copy: caps one tenant's share of this copy's buffer",
        ["Packmoat.Application.RecordingPackageGate._droppedByTenant"] = "per copy: this copy's dropped-record counts, for its warning",
        ["Packmoat.Application.RecordingPackageGate._flushing"] = "per copy: one flush at a time from this copy's buffer",
        // the token limiters stay per copy (store-agnostic plan): with N copies an address gets N times the limit, accepted
        ["Packmoat.Host.Auth.TokenRateLimiter._windows"] = "per copy: a token's requests per minute; N copies allow N times, accepted",
        ["Packmoat.Host.Auth.AddressWindows._byKey"] = "per copy: the token guess limiter; N copies allow N times the guesses, accepted",
        ["Packmoat.Host.Auth.AddressWindows+Window.TokenKeys"] = "per copy: the token guess limiter's tokens per address, as above",
        // breaks with more than one copy: E10 #250
        ["Packmoat.Host.Auth.FailedAuthLimiter._byAddress"] = "per copy until #250: each copy counts an address's failed sign-ins alone",
        ["Packmoat.Host.Web.FormNonces._live"] = "per copy until #250: a form posted to another copy is refused",
        ["Packmoat.Application.AzureSetupState._drafts"] = "per copy until #250: the setup wizard's step 1 draft is lost on another copy",
        ["Packmoat.Application.AzureSetupState._held"] = "per copy until #250: the setup wizard's last check result is lost on another copy",
        ["Packmoat.Application.AzureSetupState._running"] = "per copy until #250: two copies can run a tenant's check at once",
        ["Packmoat.Application.PersonRateLimiter._counts"] = "per copy until #250: each copy counts a person's attempts alone",
        ["Packmoat.Host.Admin.AdminWriteLimiter._byAddress"] = "per copy until #250: each copy counts an address's admin writes alone",
    };

    /// <summary>What is known to break with more than one copy (store-agnostic plan, E10 #250). It must say so until #250 lands.</summary>
    internal static readonly string[] UntilE10 =
    [
        "Packmoat.Host.Web.FormNonces._live",
        "Packmoat.Application.AzureSetupState._drafts", "Packmoat.Application.AzureSetupState._held", "Packmoat.Application.AzureSetupState._running",
        "Packmoat.Application.PersonRateLimiter._counts", "Packmoat.Host.Admin.AdminWriteLimiter._byAddress",
        "Packmoat.Host.Auth.FailedAuthLimiter._byAddress",
    ];

    private static string[] CoreState() =>
        InMemoryStateScan.StateFields(InMemoryStateScan.PackmoatTypes(Assemblies.Select(n => Assembly.Load(n))));

    // mutation: a ConcurrentDictionary field in a singleton (CachedTenantAuthenticator)
    [Fact]
    public void Every_in_memory_state_field_is_declared()
    {
        var undeclared = InMemoryStateScan.Undeclared(CoreState(), Declared);
        Assert.True(undeclared.Length == 0, "These fields hold in-memory state another copy of the server does not see. Keep it behind a " +
            "port, or declare it here (\"per copy: why safe\", \"per copy until #250: what breaks\" or \"shared via port: which\"):\n  "
            + string.Join("\n  ", undeclared));
    }

    [Fact]   // the list shrinks with the code
    public void Every_declared_field_still_holds_state() => Assert.Empty(InMemoryStateScan.StaleEntries(CoreState(), Declared));

    [Fact]
    public void Every_reason_is_one_short_line_of_a_known_kind() => Assert.Empty(InMemoryStateScan.BadReasons(Declared));

    [Fact]   // not hidden: what breaks multi-copy says so
    public void What_breaks_with_more_than_one_copy_says_until_250() =>
        Assert.All(UntilE10, f => Assert.StartsWith("per copy until #250: ", Declared[f]));

    [Fact]
    public void The_scan_reads_every_core_assembly() =>
        Assert.All(Assemblies, n => Assert.NotEmpty(InMemoryStateScan.PackmoatTypes([Assembly.Load(n)])));

    // The scanner's own test.
#pragma warning disable CS0169, CS0414, CS0649   // the fakes' fields exist to be found
    public sealed class FakeSingleton
    {
        private readonly ConcurrentDictionary<string, int> _byKey = new();
        private static readonly List<string> Seen = [];
        private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, HashSet<int>> _nested = new();
        public ISet<string> Names { get; } = new HashSet<string>();
        private readonly IDictionary<string, string> _map = new Dictionary<string, string>();
        // the non-generic collections, and a class derived from a collection (at any depth)
        private readonly System.Collections.Hashtable _hashtable = new();
        private static readonly System.Collections.ArrayList Arrayed = new();
        private readonly System.Collections.Queue _plainQueue = new();
        private readonly System.Collections.Stack _plainStack = new();
        private readonly System.Collections.SortedList _sorted = new();
        private readonly FakeBag _bag = new();
        private readonly FakeDeeperBag _deeper = new();
        // not state: read-only views, arrays, plain values, a lock object, an immutable value swapped whole (known limits)
        private readonly IReadOnlyList<string> _view = [];
        private readonly IReadOnlyDictionary<string, int> _table = new Dictionary<string, int>();
        private readonly string[] _array = [];
        private readonly object _lock = new();
        private int _count;
        private System.Collections.Immutable.ImmutableDictionary<string, int> _swapped = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty;
        private (DateTimeOffset At, int Result)? _cached;
        // a lambda's closure and an async method's state machine hold a List<int> each: one call's locals
        public async Task<int> CountAsync() { var local = new List<int>(); await Task.Yield(); Func<int> f = () => local.Count; return f(); }
    }
    public class FakeBag : Dictionary<string, int> { }
    public sealed class FakeDeeperBag : FakeBag { }
    public sealed class FakeCapture(SemaphoreSlim gate) { public void Release() => gate.Release(); }
    public readonly struct FakeStruct { private readonly List<int> _items; public FakeStruct(List<int> items) => _items = items; }
#pragma warning restore CS0169, CS0414, CS0649

    [Fact]
    public void The_scan_finds_each_kind_of_state_and_nothing_else()
    {
        var nested = typeof(FakeSingleton).GetNestedTypes(BindingFlags.NonPublic);
        Assert.Contains(nested, t => t.Name.Contains('<'));   // the closure and the state machine are there to be left out
        var found = InMemoryStateScan.StateFields([typeof(FakeSingleton), .. nested, typeof(FakeCapture), typeof(FakeStruct)]);
        var prefix = typeof(FakeSingleton).FullName + ".";
        Assert.Equal(new[] { "Arrayed", "Names", "Seen", "_bag", "_byKey", "_deeper", "_gate", "_hashtable", "_map", "_nested", "_plainQueue",
                    "_plainStack", "_queue", "_sorted" }.Select(n => prefix + n)
                .Append(typeof(FakeCapture).FullName + ".gate").Order(StringComparer.Ordinal),
            found);
        // a lambda's closure and an async method's state machine are a call's locals, not a class's state
        Assert.DoesNotContain(found, f => f.Contains('<'));
        Assert.True(InMemoryStateScan.IsStateType(typeof(ConcurrentQueue<int>)));
        Assert.True(InMemoryStateScan.IsStateType(typeof(ChannelWriter<int>)));
        Assert.False(InMemoryStateScan.IsStateType(typeof(IReadOnlySet<int>)));
        Assert.False(InMemoryStateScan.IsStateType(typeof(System.Collections.Frozen.FrozenSet<int>)));
        Assert.True(InMemoryStateScan.IsStateType(typeof(System.Collections.Hashtable)));
        Assert.True(InMemoryStateScan.IsStateType(typeof(FakeDeeperBag)));   // two steps from Dictionary<,>
        Assert.False(InMemoryStateScan.IsStateType(typeof(System.Collections.ObjectModel.ReadOnlyCollection<int>)));   // implements IList<>, not derived
        Assert.False(InMemoryStateScan.IsStateType(typeof(System.Collections.Immutable.ImmutableList<int>)));
    }

    [Fact]
    public void Undeclared_stale_and_badly_reasoned_entries_are_found()
    {
        var found = new[] { "A._live", "B._cache" };
        var declared = new Dictionary<string, string>
        {
            ["A._live"] = "per copy until #250: a form posted to another copy is refused",
            ["C._gone"] = "per copy: a field that is no longer there",
            ["D._x"] = "fine",
            ["E._y"] = "per copy: ok",
            ["F._z"] = "shared via port: ICookieKeyStore holds the keys",
        };
        Assert.Equal(["B._cache"], InMemoryStateScan.Undeclared(found, declared));
        Assert.Equal(["C._gone", "D._x", "E._y", "F._z"], InMemoryStateScan.StaleEntries(found, declared));
        Assert.Equal(["D._x: \"fine\"", "E._y: \"per copy: ok\""], InMemoryStateScan.BadReasons(declared));
    }
}
