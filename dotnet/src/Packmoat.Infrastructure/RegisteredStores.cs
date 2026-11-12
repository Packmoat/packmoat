using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// What the server's composition root takes from an engine's store registration, as ports only, so the root names no
/// adapter type past the one place it picks the engine. Nothing here is registered: the composition is unchanged.
/// <para>Each engine's registration returns it: SQLite's (<c>AddSqliteStores</c>, in Packmoat.Host) and PostgreSQL's
/// (<c>AddPostgresStores</c>, in the PostgreSQL adapter). It lives here, beside the ports it carries, so the adapter can
/// return it without referencing the host.</para>
/// <para>The two factories resolve the adapters the registration put in the container, so the authenticator checks the one token
/// service the token port resolves to, and records to the one token activity the activity ports resolve to (StoreIdentityTests).</para>
/// </summary>
/// <param name="Database">The database the stores are over, for the modules (the module context's Database).</param>
/// <param name="Migrator">At start, runs the core's track and the modules' (SQLite), or only checks them and never migrates (PostgreSQL).</param>
/// <param name="MasterKeyCheck">Refuses a start with the wrong master key.</param>
/// <param name="JobLease">The database's job lease, made when a scheduled job asks for one.</param>
/// <param name="RateCounter">The database's shared rate counter, which the composition root registers with its failures reported.</param>
/// <param name="FormNonces">The database's form nonces, which the composition root registers for both sites.</param>
/// <param name="FeedSetupScratch">The database's feed setup scratch, which the composition root registers for the setup wizard.</param>
/// <param name="TokenAuthenticator">The registered token service, as the authenticator the cache and the activity recorder wrap.</param>
/// <param name="TokenActivityLog">The registered token activity, as the log the activity recorder writes to.</param>
public sealed record RegisteredStores(
    IRelationalDatabase Database,
    ISchemaMigrator Migrator,
    IMasterKeyCheck MasterKeyCheck,
    Func<IClock, IJobLease> JobLease,
    Func<IClock, IRateCounter> RateCounter,
    Func<IClock, IFormNonceStore> FormNonces,
    Func<IClock, IFeedSetupScratch> FeedSetupScratch,
    Func<IServiceProvider, ITenantAuthenticator> TokenAuthenticator,
    Func<IServiceProvider, ITokenActivityLog> TokenActivityLog);
