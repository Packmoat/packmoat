using Packmoat.Application;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Modules;

/// <summary>
/// A piece of the server that is not the core proxy: its own services, and optionally its own site on its own hostname. The
/// composition root (<see cref="PackmoatApp.Build"/>) calls each module in order and names none of their types. The core with no modules
/// is a complete proxy.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IPackmoatModule
{
    /// <summary>
    /// Registers the module's services. Runs after the test overrides, so a module registers a port with TryAdd when a test may supply
    /// its own fake.
    /// </summary>
    void ConfigureServices(PackmoatModuleContext context);

    /// <summary>
    /// The module's own site, or null for none. Runs after the admin and customer sites are configured. <paramref name="takenHosts"/>
    /// are the hostnames already answered (the proxy's, the admin site's, the customer site's and earlier modules'). The returned host
    /// joins the allowed hosts, and its branch is mapped before the proxy's GET-only rule.
    /// </summary>
    ModuleSite? ConfigureSite(PackmoatModuleContext context, IReadOnlyCollection<string> takenHosts);

    /// <summary>
    /// The module's own migration track, or null for none. The core's migrations run first, then each module's track in order,
    /// at every start. Two modules may not declare the same track (the start is refused), and a track table in the database that no
    /// module declares refuses the start too.
    /// </summary>
    MigrationTrack? Migrations { get; }
}

/// <summary>
/// What a module is given: the builder it registers into, the core's parsed options, the core's database (PACKMOAT_DB), or null
/// when there is none (a module's stores are made over it, as the core's are), and the database engine (PACKMOAT_DB_ENGINE).
/// A module that can run on only one engine refuses to start on any other. A module reads its own settings itself.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed record PackmoatModuleContext(WebApplicationBuilder Builder, PackmoatOptions Options, IRelationalDatabase? Database, DatabaseEngine Engine)
{
    /// <summary>
    /// The job lease a module's scheduled jobs take, through the core's port: the one the core's retention pass takes, over the
    /// core's database. Call it when the job is made (it needs the built services). Always granted when there is no database.
    /// </summary>
    public Func<IServiceProvider, IJobLease> JobLease { get; init; } = _ => UnsharedJobLease.Instance;
}

/// <summary>A module's site: its hostname and how to map its branch (chosen by the Host header).
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed record ModuleSite(string Host, Action<WebApplication> MapBranch);
