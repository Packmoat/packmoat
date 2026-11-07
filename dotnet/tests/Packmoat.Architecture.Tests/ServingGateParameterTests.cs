using System.Reflection;
using Packmoat.Application;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The serving gate is never optional (#200): a constructor that takes an <see cref="IServingGate"/> must be given one. A nullable or
/// defaulted gate parameter would quietly fall back to "serve everyone" (fail open) wherever a composition root forgot to pass the
/// module's gate. The core's composition passes <see cref="ServeAllGate.Instance"/> explicitly where it has no gate of its own.
/// </summary>
public class ServingGateParameterTests
{
    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Every public constructor parameter of type <see cref="IServingGate"/> in the assembly's types (public or not).</summary>
    private static IEnumerable<ParameterInfo> GateParameters(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType == typeof(IServingGate));

    /// <summary>Why a gate parameter is loose (nullable, optional or defaulted), or null when it is required.</summary>
    internal static string? Loose(ParameterInfo p)
    {
        if (p.IsOptional || p.HasDefaultValue) return "has a default";
        if (Nullability.Create(p).WriteState != NullabilityState.NotNull) return "is nullable";
        return null;
    }

    private static string Name(ParameterInfo p) => $"{p.Member.DeclaringType!.FullName}({p.Name})";

    [Theory]   // mutation: put `IServingGate? gate = null` back on CustomerQueries, CustomerTokenCommands or SqliteTokenService
    [InlineData("Packmoat.Application")]
    [InlineData("Packmoat.Infrastructure")]
    public void No_constructor_takes_a_nullable_or_defaulted_serving_gate(string assembly)
    {
        var parameters = GateParameters(Assembly.Load(assembly)).ToList();
        Assert.NotEmpty(parameters);   // the scan sees the gate's users (it would pass vacuously otherwise)
        var loose = parameters.Select(p => (Name: Name(p), Why: Loose(p))).Where(x => x.Why is not null).Select(x => $"{x.Name} {x.Why}").ToList();
        Assert.True(loose.Count == 0, "A serving gate parameter must be required and non-nullable: " + string.Join("; ", loose));
    }

    [Fact]
    public void The_scan_finds_the_known_users_of_the_gate()
    {
        var names = GateParameters(Assembly.Load("Packmoat.Application")).Concat(GateParameters(Assembly.Load("Packmoat.Infrastructure")))
            .Select(p => p.Member.DeclaringType!.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "CustomerQueries", "CustomerTokenCommands", "CustomerTokenQueries", "SqliteTokenService" }, names);
    }

    // The rule itself: each loose form is caught, and a required gate is not.
    private sealed class RequiredGate(IServingGate gate) { public IServingGate G => gate; }
    private sealed class NullableGate(IServingGate? gate) { public IServingGate? G => gate; }
    private sealed class DefaultedGate(IServingGate? gate = null) { public IServingGate? G => gate; }
    private sealed class DefaultedNonNullableGate(IServingGate gate = null!) { public IServingGate G => gate; }

    [Fact]
    public void The_rule_catches_a_nullable_or_defaulted_gate_and_passes_a_required_one()
    {
        static ParameterInfo Gate(Type t) => t.GetConstructors().Single().GetParameters().Single();
        Assert.Null(Loose(Gate(typeof(RequiredGate))));
        Assert.Equal("is nullable", Loose(Gate(typeof(NullableGate))));
        Assert.Equal("has a default", Loose(Gate(typeof(DefaultedGate))));
        Assert.Equal("has a default", Loose(Gate(typeof(DefaultedNonNullableGate))));
    }
}
