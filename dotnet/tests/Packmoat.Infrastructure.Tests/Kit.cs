using System.Security.Cryptography;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// The handle a store contract holds on one fresh database (#288), with nothing of any engine in it: the clock its stores are timed by and
/// the master key its stores encrypt with. Everything else a contract reaches through its engine.
/// </summary>
public interface IEngineDb : IAsyncDisposable
{
    FakeClock Clock { get; }
    AesGcmSecretProtector Protector { get; }
}

/// <summary>The tenants the tests make.</summary>
public static class TestTenants
{
    /// <summary>An operator-made tenant named after its id, on an example Azure DevOps feed.</summary>
    public static NewTenant Operator(string id = "acme", int cooldown = 14) =>
        new(id, id.ToUpperInvariant(), "https://pkgs.dev.azure.com/", "example-org/", "https://pkgs.dev.azure.com/example-org/example-project/_packaging/f/nuget/v3/index.json", cooldown, TenantOrigin.Operator);
}

/// <summary>
/// A migrated database with its stores, built with the test project's composition (<c>TestComposition.cs</c>): the core's tests run the
/// core alone, the hosted tests run the hosted service's parts as production does (#197). This file is shared by both projects. It is the
/// SQLite engine's <see cref="IEngineDb"/>; only that engine and the SQLite fixtures see it as a <see cref="TestDb"/>.
/// </summary>
public sealed partial class TestDb : IEngineDb
{
    /// <summary>The tenant lifecycle the tests' stores are built with: the test project's creation participants, no purge contributors.</summary>
    public static TenantLifecycle Lifecycle { get; } = new(TestLifecycle.Participants, [], null);

    private readonly TempDir _temp = TempDir.Create("packmoat-db-", clearSqlitePools: true);
    public string Dir => _temp.Path;
    public string Path => System.IO.Path.Combine(Dir, "packmoat.db");
    public FakeClock Clock { get; } = new();
    public SqliteDatabase Db { get; }
    /// <summary>The SQLite adapters over <see cref="Db"/>, built as production builds them.</summary>
    public SqliteStores Stores { get; }
    public AesGcmSecretProtector Protector { get; }
    public SqliteTenantStore Tenants { get; }
    public SqliteTokenService Tokens { get; }
    public SqliteAuditLog Audit { get; }
    public string MasterKey { get; } = AesGcmSecretProtector.GenerateKeyBase64();

    public TestDb()
    {
        try
        {
            Db = new SqliteDatabase(Path);
            Protector = AesGcmSecretProtector.FromBase64(MasterKey);
            Stores = new SqliteStores(Db, Protector);
            // One tenant store, shared with the token service, as a composition root shares it.
            Tenants = Stores.Tenants(Clock, Lifecycle);
            Tokens = Stores.TokenService(Tenants, Clock, Gate(Db, Clock));
            Audit = new SqliteAuditLog(Db);
        }
        catch
        {
            _temp.Dispose();   // #292: nobody else holds the folder yet
            throw;
        }
    }

    public static async Task<TestDb> CreateAsync()
    {
        var t = new TestDb();
        try
        {
            await t.Db.MigrateAsync(TestTracks.Modules);
            return t;
        }
        catch
        {
            await t.DisposeAsync();   // #292: a failed migration leaves no folder behind
            throw;
        }
    }

    public Task AddTenantAsync(string id = "acme", int cooldown = 14) => Tenants.CreateAsync(TestTenants.Operator(id, cooldown), default);

    /// <summary>
    /// A tenant made by the core alone, with no module's creation participant (so no module's row): for tests that build an older,
    /// core-only database and then upgrade it (#196: a module's tables come only with its own track, after core version 21).
    /// </summary>
    public Task AddCoreTenantAsync(string id = "acme", int cooldown = 14) =>
        new SqliteTenantStore(Db, Clock, Protector, []).CreateAsync(TestTenants.Operator(id, cooldown), default);

    /// <summary>
    /// A tenant row written the way builds before migration 9 wrote it (no origin, no module's row), for tests that build an older
    /// database and then upgrade it. Today's <see cref="SqliteTenantStore.CreateAsync"/> needs the current schema.
    /// </summary>
    public async Task AddLegacyTenantAsync(string id = "acme", int cooldown = 14)
    {
        await using var c = await Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) VALUES(@id,@n,@b,@p,@f,@d,@t)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@n", id.ToUpperInvariant());
        cmd.Parameters.AddWithValue("@b", "https://pkgs.dev.azure.com/");
        cmd.Parameters.AddWithValue("@p", "example-org/");
        cmd.Parameters.AddWithValue("@f", "https://pkgs.dev.azure.com/example-org/example-project/_packaging/f/nuget/v3/index.json");
        cmd.Parameters.AddWithValue("@d", cooldown);
        cmd.Parameters.AddWithValue("@t", Clock.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Everything on disk, including the write-ahead log, as one byte string for "is the secret in there?" checks.</summary>
    public async Task<byte[]> RawBytesAsync()
    {
        SqliteConnectionPoolCleanup();
        var all = new List<byte>();
        foreach (var f in Directory.GetFiles(Dir)) all.AddRange(await File.ReadAllBytesAsync(f));
        return all.ToArray();
    }

    private static void SqliteConnectionPoolCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    public ValueTask DisposeAsync() => _temp.DisposeAsync();
}

internal static class Bytes
{
    public static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(needle)) >= 0;
}
