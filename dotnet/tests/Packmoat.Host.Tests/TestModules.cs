using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Modules;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The modules the core's server tests run (#197): none, the core alone. The shared fixtures (<see cref="ProxyFixture"/>,
/// <see cref="AdminSiteFixture"/>) build the server with these; the hosted tests have their own TestModules.cs, which gives the hosted
/// module as production does.
/// </summary>
public static class TestModules
{
    public static IReadOnlyList<IPackmoatModule> Server => [];

    /// <summary>The serving gate of the fixtures' token service: the core's, which serves every tenant.</summary>
    public static IServingGate Gate(SqliteDatabase db, IClock clock) => ServeAllGate.Instance;

    /// <summary>The modules' stores <see cref="AdminSiteFixture"/> registers: none in the core.</summary>
    public static void AddStores(AdminSiteFixture f, IServiceCollection services) { }
}

/// <summary>The creation participants the fixtures' stores use: none in the core.</summary>
public static class TestLifecycle
{
    public static IReadOnlyList<Packmoat.Infrastructure.Lifecycle.ITenantCreationParticipant> Participants { get; } = [];
}

/// <summary>The modules' migration tracks the fixtures' databases run after the core's: none in the core.</summary>
public static class TestTracks
{
    public static IReadOnlyList<MigrationTrack> Modules { get; } = [];
}
