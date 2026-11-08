using System.Reflection;
using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Host.Modules;
using Packmoat.Infrastructure;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The module seams are engine-neutral (#278 step 0, architecture test 2): starting from <see cref="IPackmoatModule"/> and
/// <see cref="IAdminCliModule"/>, every public signature a module sees (parameters, returns, properties, fields, constructor parameters,
/// base types, generic and Func arguments), followed through every Packmoat type it reaches, names no database engine's type: nothing
/// from Microsoft.Data.Sqlite or Npgsql, and nothing named Sqlite* or Npgsql*. A module gets the database as
/// <see cref="IRelationalDatabase"/>, and a transaction as <see cref="System.Data.Common.DbTransaction"/>.
/// </summary>
public class EngineNeutralSeamTests
{
    /// <summary>A database engine's own type: from its client library, or named for it.</summary>
    internal static bool IsEngineType(Type type)
    {
        var ns = type.Namespace ?? "";
        return ns == "Microsoft.Data.Sqlite" || ns.StartsWith("Microsoft.Data.Sqlite.", StringComparison.Ordinal)
            || ns == "Npgsql" || ns.StartsWith("Npgsql.", StringComparison.Ordinal)
            || type.Name.StartsWith("Sqlite", StringComparison.Ordinal) || type.Name.StartsWith("Npgsql", StringComparison.Ordinal);
    }

    private static bool IsPackmoat(Type type) => type.Namespace?.StartsWith("Packmoat", StringComparison.Ordinal) == true;

    // Public and protected members: a module that derives from a seam type sees both. Private and internal ones are filtered out below.
    private const BindingFlags Visible = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Public or protected (family, or family-or-assembly): what a module outside the assembly can see.</summary>
    private static bool Seen(MethodBase? m) => m is not null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);
    private static bool Seen(FieldInfo f) => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;
    private static bool Seen(PropertyInfo p) => Seen(p.GetMethod) || Seen(p.SetMethod);
    private static bool Seen(EventInfo e) => Seen(e.AddMethod) || Seen(e.RemoveMethod);

    /// <summary>The constraints on a generic type's or method's own type parameters (where T : ...), with where.</summary>
    private static IEnumerable<(string Where, Type Type)> Constraints(string where, Type[] parameters) =>
        parameters.Where(p => p.IsGenericParameter)
            .SelectMany(p => p.GetGenericParameterConstraints().Select(c => ($"{where}<{p.Name}>", c)));

    /// <summary>The types <paramref name="type"/> is made of: itself, an array's or a ref's element, a generic's definition and arguments.</summary>
    private static IEnumerable<Type> Parts(Type type)
    {
        if (type.IsGenericParameter) yield break;
        if (type.HasElementType)
        {
            foreach (var p in Parts(type.GetElementType()!)) yield return p;
            yield break;
        }
        if (type.IsConstructedGenericType)
        {
            yield return type.GetGenericTypeDefinition();
            foreach (var a in type.GetGenericArguments())
            foreach (var p in Parts(a)) yield return p;
            yield break;
        }
        yield return type;
    }

    /// <summary>
    /// Each type a Packmoat type's public and protected face names, with where: base type, interfaces, its generic constraints, fields,
    /// properties, events, methods (with their generic constraints) and constructors.
    /// </summary>
    private static IEnumerable<(string Where, Type Type)> Signature(Type type)
    {
        if (type.BaseType is { } b) yield return ("base", b);
        foreach (var i in type.GetInterfaces()) yield return ("implements", i);
        if (type.IsGenericTypeDefinition) foreach (var c in Constraints("", type.GetGenericArguments())) yield return c;
        foreach (var f in type.GetFields(Visible).Where(Seen)) yield return (f.Name, f.FieldType);
        foreach (var p in type.GetProperties(Visible).Where(Seen))
        {
            yield return (p.Name, p.PropertyType);
            foreach (var x in p.GetIndexParameters()) yield return ($"{p.Name}[{x.Name}]", x.ParameterType);
        }
        foreach (var e in type.GetEvents(Visible).Where(Seen)) if (e.EventHandlerType is { } h) yield return (e.Name, h);
        foreach (var m in type.GetMethods(Visible).Where(Seen))
        {
            yield return ($"{m.Name}()", m.ReturnType);
            foreach (var x in m.GetParameters()) yield return ($"{m.Name}({x.Name})", x.ParameterType);
            if (m.IsGenericMethodDefinition) foreach (var c in Constraints(m.Name, m.GetGenericArguments())) yield return c;
        }
        foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(Seen))
        foreach (var x in c.GetParameters()) yield return ($"new({x.Name})", x.ParameterType);
    }

    /// <summary>
    /// Every engine type reached from <paramref name="roots"/>, each as "the path to it: the type". Packmoat types are followed (their own
    /// public faces are read in turn); any other type (the framework's, ASP.NET's) is checked but not followed.
    /// </summary>
    internal static IReadOnlyList<string> EngineTypesReachedFrom(params Type[] roots)
    {
        var found = new List<string>();
        var seen = new HashSet<Type>();
        var queue = new Queue<(Type Type, string Path)>();
        foreach (var r in roots) { seen.Add(r); queue.Enqueue((r, r.Name)); }
        while (queue.Count > 0)
        {
            var (type, path) = queue.Dequeue();
            foreach (var (where, named) in Signature(type))
            foreach (var part in Parts(named))
            {
                if (IsEngineType(part)) found.Add($"{path}.{where}: {part.FullName}");
                else if (IsPackmoat(part) && seen.Add(part)) queue.Enqueue((part, $"{path}.{where} -> {part.Name}"));
            }
        }
        return found.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    // mutations: put SqliteDatabase back in PackmoatModuleContext, IAdminCliModule.Gate or AdminCliContext; a SqliteTransaction back in a
    // lifecycle participant; Func<SqliteDatabase, TenantLifecycle> back in TenantLifecycleFactory
    [Fact]
    public void The_module_seams_name_no_database_engine_type()
    {
        var found = EngineTypesReachedFrom(typeof(IPackmoatModule), typeof(IAdminCliModule));
        Assert.True(found.Count == 0, "A module seam names a database engine's type. Give it IRelationalDatabase or DbTransaction instead:\n  "
            + string.Join("\n  ", found));
    }

    [Fact]   // the walk reaches what it must, so an empty result means something
    public void The_walk_reaches_the_seams_types()
    {
        var reached = Reached(typeof(IPackmoatModule), typeof(IAdminCliModule));
        foreach (var t in new[] { typeof(PackmoatModuleContext), typeof(AdminCliContext), typeof(IRelationalDatabase),
                     typeof(Packmoat.Infrastructure.Lifecycle.TenantLifecycle), typeof(Packmoat.Infrastructure.Lifecycle.ITenantCreationParticipant),
                     typeof(Packmoat.Infrastructure.Lifecycle.IPurgeContributor), typeof(Packmoat.Infrastructure.Lifecycle.IPurgeRecheck),
                     typeof(Packmoat.Application.IAdminActionLog) })
            Assert.Contains(t, reached);
    }

    private static HashSet<Type> Reached(params Type[] roots)
    {
        var seen = new HashSet<Type>(roots);
        var queue = new Queue<Type>(roots);
        while (queue.Count > 0)
            foreach (var (_, named) in Signature(queue.Dequeue()))
            foreach (var part in Parts(named))
                if (IsPackmoat(part) && seen.Add(part)) queue.Enqueue(part);
        return seen;
    }

    // The scanner's own test: each way a seam can name an engine type is seen, and a clean seam passes.
    public interface IFakeCleanSeam { Task RunAsync(FakeCleanContext context, Func<IRelationalDatabase, int> f); }
    public sealed record FakeCleanContext(System.Data.Common.DbTransaction Transaction, IReadOnlyList<string> Names);
    public interface IFakeFuncSeam { void Run(Func<SqliteDatabase, int> f); }
    public interface IFakeDeepSeam { void Run(FakeOuter outer); }
    public sealed record FakeOuter(FakeInner? Inner);
    public sealed record FakeInner(SqliteTransaction? Transaction);
    public interface IFakeReturnSeam { Task<SqliteConnection[]> OpenAllAsync(); }
    public sealed class FakeCtorSeam { public FakeCtorSeam(Lazy<SqliteCommand> command) => _ = command; }
    public abstract class FakeBaseSeam : FakeEngineBase { }
    public abstract class FakeEngineBase { public SqliteDataReader? Reader { get; init; } }
    public sealed class NpgsqlLookalike { }
    public interface IFakeNamedSeam { NpgsqlLookalike? Connection { get; } }
    public interface IFakeMethodConstraintSeam { void Probe<T>() where T : SqliteConnection; }
    public interface IFakeTypeConstraintSeam<T> where T : SqliteConnection { }
    public abstract class FakeProtectedSeam { protected abstract void Run(SqliteCommand command); }
    public abstract class FakeProtectedCtorSeam { protected FakeProtectedCtorSeam(SqliteTransaction transaction) => _ = transaction; }
    public abstract class FakePrivateOnly { private void Run(SqliteCommand command) => _ = command; internal SqliteConnection? Connection { get; set; } }

    [Fact]
    public void The_scanner_sees_every_way_a_seam_names_an_engine_type()
    {
        Assert.Empty(EngineTypesReachedFrom(typeof(IFakeCleanSeam)));
        Assert.Contains("IFakeFuncSeam.Run(f): Packmoat.Infrastructure.SqliteDatabase", EngineTypesReachedFrom(typeof(IFakeFuncSeam)));
        Assert.Contains("IFakeDeepSeam.Run(outer) -> FakeOuter.Inner -> FakeInner.Transaction: Microsoft.Data.Sqlite.SqliteTransaction",
            EngineTypesReachedFrom(typeof(IFakeDeepSeam)));
        Assert.Contains("IFakeReturnSeam.OpenAllAsync(): Microsoft.Data.Sqlite.SqliteConnection", EngineTypesReachedFrom(typeof(IFakeReturnSeam)));
        Assert.Contains("FakeCtorSeam.new(command): Microsoft.Data.Sqlite.SqliteCommand", EngineTypesReachedFrom(typeof(FakeCtorSeam)));
        Assert.Contains("FakeBaseSeam.base -> FakeEngineBase.Reader: Microsoft.Data.Sqlite.SqliteDataReader", EngineTypesReachedFrom(typeof(FakeBaseSeam)));
        Assert.NotEmpty(EngineTypesReachedFrom(typeof(IFakeNamedSeam)));
        // generic constraints and protected members (review): a module sees both
        Assert.Contains("IFakeMethodConstraintSeam.Probe<T>: Microsoft.Data.Sqlite.SqliteConnection", EngineTypesReachedFrom(typeof(IFakeMethodConstraintSeam)));
        Assert.Contains("IFakeTypeConstraintSeam`1.<T>: Microsoft.Data.Sqlite.SqliteConnection", EngineTypesReachedFrom(typeof(IFakeTypeConstraintSeam<>)));
        Assert.Contains("FakeProtectedSeam.Run(command): Microsoft.Data.Sqlite.SqliteCommand", EngineTypesReachedFrom(typeof(FakeProtectedSeam)));
        Assert.Contains("FakeProtectedCtorSeam.new(transaction): Microsoft.Data.Sqlite.SqliteTransaction", EngineTypesReachedFrom(typeof(FakeProtectedCtorSeam)));
        // what a module cannot see (private, internal) is not its seam
        Assert.Empty(EngineTypesReachedFrom(typeof(FakePrivateOnly)));
        Assert.True(IsEngineType(typeof(SqliteParameter)));
        Assert.False(IsEngineType(typeof(System.Data.Common.DbTransaction)));
        Assert.False(IsEngineType(typeof(IRelationalDatabase)));
        Assert.False(IsEngineType(typeof(DatabaseEngine)));
    }
}
