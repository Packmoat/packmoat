using System.Security.Cryptography;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Infrastructure.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// A migrated database with its stores, built with the test project's composition (<c>TestComposition.cs</c>): the core's tests run the
/// core alone, the hosted tests run the hosted service's parts as production does (#197). This file is shared by both projects.
/// </summary>
public sealed partial class TestDb : IAsyncDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("packmoat-db-").FullName;
    public string Path => System.IO.Path.Combine(Dir, "packmoat.db");
    public FakeClock Clock { get; } = new();
    public SqliteDatabase Db { get; }
    public AesGcmSecretProtector Protector { get; }
    public SqliteTenantStore Tenants { get; }
    public SqliteTokenService Tokens { get; }
    public SqliteAuditLog Audit { get; }
    public string MasterKey { get; } = AesGcmSecretProtector.GenerateKeyBase64();

    public TestDb()
    {
        Db = new SqliteDatabase(Path);
        Protector = AesGcmSecretProtector.FromBase64(MasterKey);
        Tenants = new SqliteTenantStore(Db, Clock, Protector, TestLifecycle.Participants);
        Tokens = new SqliteTokenService(Db, Tenants, Clock, Gate(Db, Clock));
        Audit = new SqliteAuditLog(Db);
    }

    public static async Task<TestDb> CreateAsync()
    {
        var t = new TestDb();
        await t.Db.MigrateAsync(TestTracks.Modules);
        return t;
    }

    public Task AddTenantAsync(string id = "acme", int cooldown = 14) =>
        Tenants.CreateAsync(new NewTenant(id, id.ToUpperInvariant(), "https://pkgs.dev.azure.com/", "example-org/", "https://pkgs.dev.azure.com/example-org/example-project/_packaging/f/nuget/v3/index.json", cooldown, TenantOrigin.Operator), default);

    /// <summary>
    /// A tenant made by the core alone, with no module's creation participant (so no module's row): for tests that build an older,
    /// core-only database and then upgrade it (#196: a module's tables come only with its own track, after core version 21).
    /// </summary>
    public Task AddCoreTenantAsync(string id = "acme", int cooldown = 14) =>
        new SqliteTenantStore(Db, Clock, Protector, []).CreateAsync(new NewTenant(id, id.ToUpperInvariant(), "https://pkgs.dev.azure.com/", "example-org/", "https://pkgs.dev.azure.com/example-org/example-project/_packaging/f/nuget/v3/index.json", cooldown, TenantOrigin.Operator), default);

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

    public ValueTask DisposeAsync()
    {
        SqliteConnectionPoolCleanup();
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}

internal static class Bytes
{
    public static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(needle)) >= 0;
}
