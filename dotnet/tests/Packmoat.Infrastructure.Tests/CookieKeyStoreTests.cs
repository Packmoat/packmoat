using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="CookieKeyStoreContract"/> on SQLite.</summary>
[Collection("sqlite-pools")]
public class CookieKeyStoreTests : CookieKeyStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;
}

/// <summary>
/// <see cref="CookieKeyStoreContract"/> on PostgreSQL, each test on a database of its own, and the PostgreSQL store's
/// encryption, as <see cref="SqliteCookieKeyStoreTests"/> checks SQLite's. The contract's race test is the import's two-writer race: it
/// fails without the <see cref="Packmoat.Infrastructure.Postgres.PostgresLock.CookieKeys"/> lock.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresCookieKeyStoreTests : CookieKeyStoreContract
{
    private const string Xml = "<key id=\"11111111-2222-4333-8444-555555555555\" version=\"1\"><descriptor><masterKey>c2VjcmV0LWtleS1ieXRlcw==</masterKey></descriptor></key>";

    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    private async Task<List<(string Id, byte[] Blob)>> RowsAsync(IEngineDb t)
    {
        var rows = new List<(string, byte[])>();
        foreach (var line in await Engine.ColumnAsync(t, $"SELECT id || '|' || {Engine.HexSql("xml_encrypted")} FROM cookie_keys ORDER BY created, id"))
        {
            var parts = line.Split('|');
            rows.Add((parts[0], Convert.FromHexString(parts[1])));
        }
        return rows;
    }

    [PostgresFact]   // mutations: store the XML as plain text; a fixed context ("cookie-key"); the friendly name as the context
    public async Task Each_row_holds_none_of_the_xml_and_decrypts_under_cookie_key_and_its_own_id_only()
    {
        await using var t = await Engine.CreateAsync();
        await Engine.CookieKeys(t).AddAsync("key-1", Xml, default);
        var (id, blob) = Assert.Single(await RowsAsync(t));
        Assert.True(Guid.TryParse(id, out _));
        Assert.True(blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes("masterKey")) < 0);
        Assert.Equal(Xml, t.Protector.Unprotect(blob, "cookie-key:" + id));
        Assert.ThrowsAny<CryptographicException>(() => t.Protector.Unprotect(blob, "cookie-key"));
        Assert.ThrowsAny<CryptographicException>(() => t.Protector.Unprotect(blob, "cookie-key:key-1"));
    }

    [PostgresFact]   // mutations: a fixed context; skip (or swallow) a row that does not decrypt
    public async Task A_blob_copied_onto_another_row_or_another_master_key_stops_the_read()
    {
        await using var t = await Engine.CreateAsync();
        var store = Engine.CookieKeys(t);
        await store.AddAsync("key-1", Xml, default);
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        await store.AddAsync("key-2", Xml.Replace("11111111", "99999999"), default);
        Assert.Equal(2, (await store.GetAllAsync(default)).Count);

        var other = new Packmoat.Infrastructure.Postgres.PostgresCookieKeyStore(((PostgresTestDb)t).Db, t.Clock, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()));
        await Assert.ThrowsAsync<CryptographicException>(() => other.GetAllAsync(default));

        var rows = await RowsAsync(t);
        await Engine.ExecAsync(t, $"UPDATE cookie_keys SET xml_encrypted = (SELECT xml_encrypted FROM cookie_keys WHERE id='{rows[0].Id}') WHERE id='{rows[1].Id}'");
        var ex = await Assert.ThrowsAsync<CryptographicException>(() => store.GetAllAsync(default));
        Assert.Contains(rows[1].Id, ex.Message);
        Assert.DoesNotContain("<key", ex.ToString());
    }
}

/// <summary>
/// The SQLite cookie key store's encryption: what is on disk is master-key ciphertext bound to its own row, and a row that does
/// not decrypt stops the read instead of being skipped.
/// </summary>
[Collection("sqlite-pools")]
public class SqliteCookieKeyStoreTests
{
    private const string Xml = "<key id=\"11111111-2222-4333-8444-555555555555\" version=\"1\"><descriptor><masterKey>c2VjcmV0LWtleS1ieXRlcw==</masterKey></descriptor></key>";

    private static async Task<List<(string Id, byte[] Blob)>> RowsAsync(TestDb t)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, xml_encrypted FROM cookie_keys ORDER BY created, id";
        var rows = new List<(string, byte[])>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), (byte[])r.GetValue(1)));
        return rows;
    }

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    [Fact]   // mutation: store the XML as plain text (or under no encryption at all)
    public async Task The_stored_blob_holds_none_of_the_xml()
    {
        await using var t = await TestDb.CreateAsync();
        await new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector).AddAsync("key-1", Xml, default);
        var (_, blob) = Assert.Single(await RowsAsync(t));
        Assert.False(Contains(blob, Xml));
        Assert.False(Contains(blob, "<key"));
        Assert.False(Contains(blob, "masterKey"));
        Assert.False(Contains(blob, "c2VjcmV0LWtleS1ieXRlcw=="));
        foreach (var file in new[] { t.Path, t.Path + "-wal" }.Where(File.Exists))   // nor do the database's files
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.False(Contains(copy.ToArray(), "masterKey"));
        }
    }

    /// <summary>
    /// The add-unless-any runs under the write lock from its first statement (BEGIN IMMEDIATE), so of two servers at once the second
    /// reads what the first wrote. A deferred transaction reads first and takes the write lock at its insert: if another writer commits
    /// in between, the insert fails on the stale read (SQLITE_BUSY_SNAPSHOT) instead of the add returning false.
    /// </summary>
    [Fact]   // mutation: the transaction deferred (BEGIN, not BEGIN IMMEDIATE)
    public async Task Add_unless_any_waits_for_a_writer_then_reads_what_it_wrote()
    {
        await using var t = await TestDb.CreateAsync();
        var store = new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector);
        await using var writer = await t.Db.OpenAsync();
        await using var tx = writer.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);   // the other server's write lock
        await using (var insert = writer.CreateCommand())
        {
            var id = Guid.NewGuid().ToString("D");
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO cookie_keys(id, friendly_name, created, xml_encrypted) VALUES(@id, 'theirs', @created, @xml)";
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@created", t.Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("@xml", t.Protector.Protect(Xml, "cookie-key:" + id));
            await insert.ExecuteNonQueryAsync();
        }

        var add = Task.Run(() => store.AddUnlessAnyAsync(new CookieKey("mine", Xml), _ => true, default));
        await Task.Delay(500);   // it is waiting for the lock (immediate), or has read the empty table and waits to insert (deferred)
        Assert.False(add.IsCompleted);
        await tx.CommitAsync();

        Assert.False(await add.WaitAsync(TimeSpan.FromSeconds(10)));   // it saw the other's key and added nothing
        Assert.Single(await RowsAsync(t));
    }

    [Fact]   // mutations: a fixed context ("cookie-key"); the friendly name as the context
    public async Task Each_row_decrypts_under_cookie_key_and_its_own_id_only()
    {
        await using var t = await TestDb.CreateAsync();
        var store = new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector);
        await store.AddAsync("key-1", Xml, default);
        var (id, blob) = Assert.Single(await RowsAsync(t));
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(Xml, t.Protector.Unprotect(blob, "cookie-key:" + id));
        Assert.ThrowsAny<CryptographicException>(() => t.Protector.Unprotect(blob, "cookie-key"));
        Assert.ThrowsAny<CryptographicException>(() => t.Protector.Unprotect(blob, "cookie-key:key-1"));
    }

    [Fact]   // mutations: a fixed context; skip (or swallow) a row that does not decrypt
    public async Task A_blob_copied_onto_another_row_stops_the_read()
    {
        await using var t = await TestDb.CreateAsync();
        var store = new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector);
        await store.AddAsync("key-1", Xml, default);
        await store.AddAsync("key-2", Xml.Replace("11111111", "99999999"), default);
        Assert.Equal(2, (await store.GetAllAsync(default)).Count);

        var rows = await RowsAsync(t);
        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE cookie_keys SET xml_encrypted=@b WHERE id=@id";
            cmd.Parameters.AddWithValue("@b", rows[0].Blob);
            cmd.Parameters.AddWithValue("@id", rows[1].Id);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var ex = await Assert.ThrowsAsync<CryptographicException>(() => store.GetAllAsync(default));
        Assert.Contains(rows[1].Id, ex.Message);
        Assert.DoesNotContain("<key", ex.ToString());
    }

    [Fact]   // mutation: skip (or swallow) a row that does not decrypt: the key ring would read as empty and mint a new key
    public async Task Another_master_key_throws_and_never_reads_as_empty()
    {
        await using var t = await TestDb.CreateAsync();
        await new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector).AddAsync("key-1", Xml, default);
        var other = AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());
        await Assert.ThrowsAsync<CryptographicException>(() => new SqliteCookieKeyStore(t.Db, t.Clock, other).GetAllAsync(default));
        SqliteConnection.ClearAllPools();
    }

    [Fact]   // mutation: cast the column to bytes unchecked (a text value then throws InvalidCastException, which the start cannot name)
    public async Task A_value_that_is_not_bytes_stops_the_read_as_unreadable_naming_the_row()
    {
        await using var t = await TestDb.CreateAsync();
        var store = new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector);
        await store.AddAsync("key-1", Xml, default);
        var (id, _) = Assert.Single(await RowsAsync(t));
        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE cookie_keys SET xml_encrypted = 'not bytes'";   // SQLite's BLOB column takes text too
            await cmd.ExecuteNonQueryAsync();
        }
        var ex = await Assert.ThrowsAsync<CryptographicException>(() => store.GetAllAsync(default));
        Assert.Equal($"The cookie key {id} in the database is not stored as encrypted bytes.", ex.Message);
    }

    [Fact]   // the record never prints the XML (a key in a log line could forge sessions)
    public void A_cookie_key_prints_its_name_not_its_xml()
    {
        var text = new CookieKey("key-1", Xml).ToString();
        Assert.Contains("key-1", text);
        Assert.DoesNotContain("<key", text);
        Assert.DoesNotContain("masterKey", text);
    }
}
