using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

// The composition the core's tests run: the core alone. No module's creation participant (a new tenant gets only its own row), no
// module's migration track, every tenant served, and no command-line module. The hosted tests have their own TestComposition.cs.

/// <summary>The creation participants the tests' stores use: none (the core alone).</summary>
public static class TestLifecycle
{
    public static IReadOnlyList<Lifecycle.ITenantCreationParticipant> Participants { get; } = [];
}

/// <summary>The migration tracks the tests' databases run after the core's: none (the core alone).</summary>
public static class TestTracks
{
    public static IReadOnlyList<MigrationTrack> Modules { get; } = [];
}

/// <summary>The modules the tests' packmoat-admin runs with: none (the core alone).</summary>
public static class TestCli
{
    public static IReadOnlyList<IAdminCliModule> Modules { get; } = [];
}

public sealed partial class TestDb
{
    /// <summary>The gate the token service asks: the core's, which serves every tenant.</summary>
    public static IServingGate Gate(SqliteDatabase db, IClock clock) => ServeAllGate.Instance;
}
