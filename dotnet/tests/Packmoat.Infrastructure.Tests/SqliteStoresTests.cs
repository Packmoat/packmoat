using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The one list of SQLite adapters (#278 step 0, A3) the server, the command line and the hosted modules all build from: the eager
/// stores are made once, with the database, and every read gives the same one; each factory makes its adapter over the same database.
/// </summary>
[Collection("sqlite-pools")]
public class SqliteStoresTests
{
    private static void WithStores(Action<SqliteStores> body)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-stores-").FullName;
        try { body(new SqliteStores(new SqliteDatabase(Path.Combine(dir, "packmoat.db")), AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()))); }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // mutation: an eager store made afresh on each read (a factory in disguise)
    public void Every_eager_store_is_made_once_and_given_back_each_time() => WithStores(s =>
    {
        object[] first = [s.Audit, s.Decisions, s.DecisionQuery, s.AdminActions, s.SignInDirectory, s.Members, s.AutoJoin, s.FeedChecks, s.SelfServiceFeeds];
        object[] again = [s.Audit, s.Decisions, s.DecisionQuery, s.AdminActions, s.SignInDirectory, s.Members, s.AutoJoin, s.FeedChecks, s.SelfServiceFeeds];
        Assert.All(first, Assert.NotNull);
        Assert.Equal(first.Length, first.Distinct().Count());
        for (var i = 0; i < first.Length; i++) Assert.Same(first[i], again[i]);
    });

    [Fact]   // mutation: a factory returns null, or the wrong adapter
    public void Every_factory_makes_its_adapter() => WithStores(s =>
    {
        var clock = new FakeClock();
        var tenants = s.Tenants(clock, TenantLifecycle.None);
        Assert.IsType<SqliteTenantStore>(tenants);
        Assert.IsType<SqliteTokenService>(s.TokenService(tenants, clock, ServeAllGate.Instance));
        Assert.IsType<SqliteTokenActivity>(s.TokenActivity(clock));
        Assert.IsType<SqliteSharedAppCredentialStore>(s.SharedAppCredentials(clock));
        Assert.IsType<SqliteTenantPurgeStore>(s.PurgeStore(TenantLifecycle.None));
        Assert.IsType<SqliteCookieKeyStore>(s.CookieKeys(clock));
        Assert.IsType<SqliteOrganisationStore>(s.Organisations(TenantLifecycle.None));
    });

    [Fact]
    public void The_stores_are_over_the_database_they_were_given()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-stores-").FullName;
        try
        {
            var db = new SqliteDatabase(Path.Combine(dir, "packmoat.db"));
            Assert.Same(db, new SqliteStores(db, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64())).Database);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir); }
    }
}
