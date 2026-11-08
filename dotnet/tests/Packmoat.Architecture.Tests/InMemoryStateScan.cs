using System.Reflection;
using System.Runtime.CompilerServices;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Architecture test 7's scanner (#278 step 0): every field, in a Packmoat class, that holds in-memory state one copy of the server keeps
/// to itself: a mutable collection, a concurrent collection, a channel, a cache or an in-process lock. Two copies behind a load balancer
/// do not share such a field, so each one must be declared: safe per copy (and why), or shared through a port. Shared with the hosted
/// twin (linked into Packmoat.Hosted.Architecture.Tests), which passes the hosted assemblies and its own list.
/// <para>A field's type counts when it, or any type it derives from (a <c>class Bag : Dictionary&lt;string, int&gt;</c>), is one of these.</para>
/// <para>Known limits, accepted: a field reassigned with a new immutable value (an ImmutableDictionary, a frozen set, a tuple or record
/// cache such as <c>(DateTimeOffset At, Failure Result)? _cached</c>) is per-copy state the scan does not see, nor is an <c>object</c>
/// used as a lock (it only guards fields the scan does see, so far). Interfaces a type merely implements are not followed: read-only
/// and immutable collections implement IList and ICollection too.</para>
/// </summary>
internal static class InMemoryStateScan
{
    // Mutable by their own API. Read-only views (IReadOnlyList, IReadOnlyDictionary, frozen and immutable collections) and arrays are not
    // counted: a field of those types is a table built once, not state that grows while the server runs.
    private static readonly HashSet<Type> MutableGenerics =
    [
        typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(Queue<>), typeof(Stack<>), typeof(LinkedList<>),
        typeof(SortedDictionary<,>), typeof(SortedList<,>), typeof(SortedSet<>), typeof(PriorityQueue<,>),
        typeof(IList<>), typeof(ICollection<>), typeof(IDictionary<,>), typeof(ISet<>), typeof(ConditionalWeakTable<,>),
    ];

    // The non-generic collections of System.Collections.
    private static readonly HashSet<Type> MutablePlain =
    [
        typeof(System.Collections.Hashtable), typeof(System.Collections.ArrayList), typeof(System.Collections.Queue),
        typeof(System.Collections.Stack), typeof(System.Collections.SortedList), typeof(System.Collections.BitArray),
        typeof(System.Collections.IList), typeof(System.Collections.IDictionary),
    ];

    private static readonly HashSet<string> StateNames =
    [
        "System.Threading.SemaphoreSlim", "System.Threading.Lock", "System.Threading.Mutex",
        "Microsoft.Extensions.Caching.Memory.IMemoryCache", "Microsoft.Extensions.Caching.Memory.MemoryCache",
    ];

    /// <summary>True when a field of <paramref name="type"/> holds per-copy in-memory state: it, or a type it derives from, is one of the kinds.</summary>
    internal static bool IsStateType(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            if (IsStateKind(t)) return true;
        return false;
    }

    private static bool IsStateKind(Type type)
    {
        var plain = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        var ns = plain.Namespace ?? "";
        return MutableGenerics.Contains(plain) || MutablePlain.Contains(plain)
            || ns == "System.Collections.Concurrent" || ns == "System.Threading.Channels"
            || StateNames.Contains(plain.FullName ?? "");
    }

    /// <summary>
    /// A field as the allowlist names it: "Type.field", nested types with '+'; a property's backing field by the property's name, and a
    /// primary constructor's captured parameter by the parameter's.
    /// </summary>
    internal static string Named(FieldInfo field)
    {
        var name = field.Name;
        if (name.StartsWith('<') && name.IndexOf('>') > 1) name = name[1..name.IndexOf('>')];
        return $"{field.DeclaringType!.FullName}.{name}";
    }

    private static bool CompilerMade(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.Contains('<');

    /// <summary>
    /// Every field (instance or static, any visibility) of every class <paramref name="types"/> declares that holds per-copy state, by
    /// <see cref="Named"/>, in order. Compiler-made classes (lambdas' closures, async state machines) are left out: what they capture is
    /// a method's locals, which live as long as one call.
    /// </summary>
    internal static string[] StateFields(IEnumerable<Type> types) =>
        types.Where(t => t.IsClass && !CompilerMade(t))
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.IsLiteral && IsStateType(f.FieldType))
            .Select(Named).Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>Every type of the Packmoat assemblies among <paramref name="assemblies"/>.</summary>
    internal static IEnumerable<Type> PackmoatTypes(IEnumerable<Assembly> assemblies) =>
        assemblies.Where(a => a.GetName().Name?.StartsWith("Packmoat.", StringComparison.Ordinal) == true).SelectMany(a => a.GetTypes());

    /// <summary>The state fields not on <paramref name="declared"/>.</summary>
    internal static string[] Undeclared(IEnumerable<string> found, IReadOnlyDictionary<string, string> declared) =>
        found.Where(f => !declared.ContainsKey(f)).ToArray();

    /// <summary>The entries of <paramref name="declared"/> that name no state field any more: the list shrinks with the code.</summary>
    internal static string[] StaleEntries(IEnumerable<string> found, IReadOnlyDictionary<string, string> declared)
    {
        var have = found.ToHashSet(StringComparer.Ordinal);
        return declared.Keys.Where(k => !have.Contains(k)).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The entries whose reason is not one short line saying why: "per copy: why that is safe", "per copy until #250: what breaks with two
    /// copies" (multi-copy needs sticky sessions until E10 #250 gives it a port), or "shared via port: which port".
    /// </summary>
    internal static string[] BadReasons(IReadOnlyDictionary<string, string> declared) =>
        declared.Where(e => !(e.Value.StartsWith("shared via port: ", StringComparison.Ordinal) || e.Value.StartsWith("per copy: ", StringComparison.Ordinal)
                || e.Value.StartsWith("per copy until #250: ", StringComparison.Ordinal))
                || e.Value[(e.Value.IndexOf(':') + 1)..].Trim().Length < 10 || e.Value.Contains('\n') || e.Value.Length > 130)
            .Select(e => $"{e.Key}: \"{e.Value}\"").Order(StringComparer.Ordinal).ToArray();
}
