using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Packmoat.Application;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// PACKMOAT_COOKIE_KEYS (#232 B1). Unset (as on the live server) is the key folder, exactly as before. <c>database</c> keeps the keys in
/// the database, encrypted with the master key, so every copy of the server shares them; at its first start it imports the folder's keys
/// once, and it never creates or changes the folder. Anything else stops the start, naming the setting and never the value.
/// </summary>
[Collection("sqlite-pools")]
public class CookieKeyStorageTests
{
    private const string Purpose = "cookie-key-storage-test";

    [Theory]   // mutation: the default (unset or empty) is database
    [InlineData(null, CookieKeyStorage.Folder)]
    [InlineData("", CookieKeyStorage.Folder)]
    [InlineData("folder", CookieKeyStorage.Folder)]
    [InlineData("database", CookieKeyStorage.Database)]
    public void Unset_is_the_folder_and_each_storage_is_its_own_word(string? value, CookieKeyStorage storage) =>
        Assert.Equal(storage, CookieKeyStorages.Parse(value));

    [Theory]   // mutations: a case-insensitive or trimming parse; the value echoed in the message
    [InlineData("Database")]
    [InlineData("DATABASE")]
    [InlineData("Folder")]
    [InlineData(" folder")]
    [InlineData("folder ")]
    [InlineData("db")]
    [InlineData("files")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void An_unknown_value_is_refused_naming_the_setting_not_the_value(string value)
    {
        var ex = Assert.Throws<SettingsException>(() => CookieKeyStorages.Parse(value));
        Assert.Equal("PACKMOAT_COOKIE_KEYS must be folder or database.", ex.Message);
    }

    [Fact]   // the setting is read through the one settings reader, so it can come from a file as every other can
    public void The_setting_is_read_by_name_through_the_settings_reader()
    {
        var reader = new Settings(n => n == CookieKeyStorages.Variable + Settings.FileSuffix ? "/run/secrets/cookie-keys" : null, _ => "database\n");
        Assert.Equal(CookieKeyStorage.Database, CookieKeyStorages.Parse(reader.Read(CookieKeyStorages.Variable)));
    }

    // ---------------------------------------------------------------- the server

    private static void Options(PackmoatOptions o, string dbDir, string? keysDir, string? cookieKeys, string? masterKey = null)
    {
        o.DatabasePath = Path.Combine(dbDir, "packmoat.db");
        o.MasterKeyBase64 = masterKey ?? AesGcmSecretProtector.GenerateKeyBase64();
        o.AllowedHosts = "feeds.example.test";
        o.AdminHost = "admin.example.test";   // a signed-in site, so the cookie keys are used at all
        o.AdminKeysDir = keysDir;
        o.CookieKeys = cookieKeys;
    }

    private static WebApplication Build(string dbDir, string? keysDir, string? cookieKeys, string masterKey, ILoggerProvider? logs = null) =>
        PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, s => { if (logs is not null) s.AddSingleton<ILoggerProvider>(logs); },
            o => Options(o, dbDir, keysDir, cookieKeys, masterKey));

    private static IDataProtector Protector(WebApplication app) => app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    /// <summary>The id of the key a payload was protected under (after the 4-byte magic header).</summary>
    private static Guid KeyId(byte[] payload) => new(payload.AsSpan(4, 16));

    private static async Task<long> RowsAsync(string dbDir)
    {
        await using var c = new SqliteConnection($"Data Source={Path.Combine(dbDir, "packmoat.db")};Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cookie_keys";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

    private static string Dir(string name) => Directory.CreateTempSubdirectory($"packmoat-cookie-keys-{name}-").FullName;

    private static void Delete(params string[] dirs)
    {
        SqliteConnection.ClearAllPools();
        foreach (var d in dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    /// <summary>Thrown when the server built, which these tests say it must not.</summary>
    private sealed class StartedException : Exception;

    private static (int Code, string Error) Start(Action<PackmoatOptions> configure)
    {
        var error = new StringWriter();
        // A build that succeeds is never run (that would serve until killed): it is disposed and reported as a plain failure.
        var code = StartupFailure.Run(() =>
        {
            var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, configure);
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new StartedException();
        }, error);
        return (code, error.ToString().TrimEnd());
    }

    [Fact]   // mutation: an unknown value is taken (as folder or as database), or parsed after the database is opened
    public void An_unknown_value_stops_the_start_with_one_error_line_before_anything_is_written()
    {
        var dir = Dir("bad");
        try
        {
            var (code, error) = Start(o => Options(o, dir, Path.Combine(dir, "keys"), "Database"));
            Assert.Equal(1, code);
            Assert.Equal("error: PACKMOAT_COOKIE_KEYS must be folder or database.", error);
            Assert.False(File.Exists(Path.Combine(dir, "packmoat.db")));
            Assert.False(Directory.Exists(Path.Combine(dir, "keys")));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutation: database mode without a database falls back to the folder (or to keys in memory)
    public void Database_mode_without_PACKMOAT_DB_stops_the_start()
    {
        var dir = Dir("nodb");
        try
        {
            var (code, error) = Start(o =>
            {
                Options(o, dir, Path.Combine(dir, "keys"), "database");
                o.DatabasePath = null;
            });
            Assert.Equal(1, code);
            Assert.Equal("error: PACKMOAT_COOKIE_KEYS=database needs a database: PACKMOAT_DB is not set.", error);
            Assert.False(Directory.Exists(Path.Combine(dir, "keys")));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutation: the default is database (production would move its keys without being asked)
    public async Task Unset_keeps_the_keys_in_the_folder_as_before_and_registers_no_cookie_key_store()
    {
        var dir = Dir("folder");
        var keys = Path.Combine(dir, "admin-keys");
        try
        {
            await using (var app = Build(dir, keys, null, AesGcmSecretProtector.GenerateKeyBase64()))
            {
                Assert.IsType<FileSystemXmlRepository>(app.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
                Assert.Null(app.Services.GetService<ICookieKeyStore>());
                Assert.Equal("session", Protector(app).Unprotect(Protector(app).Protect("session")));
                Assert.Equal("packmoat-admin", app.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
            }
            Assert.NotEmpty(Directory.GetFiles(keys, "*.xml"));
            Assert.Equal(0L, await RowsAsync(dir));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutations: the database mode creates or prepares the key folder; drops SetApplicationName; stores nothing in the database
    public async Task Two_servers_on_one_database_share_protected_data_and_neither_creates_a_key_folder()
    {
        var dir = Dir("shared");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var folderA = Path.Combine(dir, "keys-a");   // neither exists
        try
        {
            await using var a = Build(dir, folderA, "database", key);
            await using var b = Build(dir, null, "database", key);   // no folder set: the default, admin-keys next to the database
            Assert.IsType<DatabaseXmlRepository>(a.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
            Assert.Equal("packmoat-admin", b.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);

            var payload = Protector(a).Protect("session");
            Assert.Equal("session", Protector(b).Unprotect(payload));
            Assert.Equal("antiforgery", Protector(a).Unprotect(Protector(b).Protect("antiforgery")));
            Assert.Equal(1L, await RowsAsync(dir));   // one key, made by the first server to need one
            Assert.False(Directory.Exists(folderA));
            Assert.False(Directory.Exists(Path.Combine(dir, "admin-keys")));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutations: no import (old cookies stop decrypting); import every start; write to (or delete from) the folder; log a key's XML
    public async Task Switching_from_the_folder_to_the_database_keeps_old_payloads_and_imports_the_folder_once()
    {
        var dir = Dir("switch");
        var keys = Path.Combine(dir, "admin-keys");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var logs = new CapturingLoggerProvider();
        try
        {
            // The folder holds several keys and a revocation, as a real one does after months: a payload under each key, and a revoked key.
            string old;
            byte[] first, second;
            Guid revoked;
            await using (var folder = Build(dir, keys, null, key))
            {
                old = Protector(folder).Protect("session from before the switch");
                first = Protector(folder).Protect("under the first key"u8.ToArray());
                folder.Services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            }
            await using (var folder = Build(dir, keys, null, key))   // a fresh key ring: the newer key is the default
            {
                second = Protector(folder).Protect("under the second key"u8.ToArray());
                var manager = folder.Services.GetRequiredService<IKeyManager>();
                revoked = manager.CreateNewKey(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(90)).KeyId;
                manager.RevokeKey(revoked, "test");
            }
            Assert.NotEqual(KeyId(first), KeyId(second));
            Assert.Equal(3, Directory.GetFiles(keys, "key-*.xml").Length);
            Assert.Single(Directory.GetFiles(keys, "revocation-*.xml"));
            var files = Snapshot(keys);
            Assert.NotEmpty(files);

            await using (var db = Build(dir, keys, "database", key, logs))
            {
                Assert.Equal("session from before the switch", Protector(db).Unprotect(old));
                Assert.Equal("under the first key"u8.ToArray(), Protector(db).Unprotect(first));
                Assert.Equal("under the second key"u8.ToArray(), Protector(db).Unprotect(second));
                Assert.Equal("new", Protector(db).Unprotect(Protector(db).Protect("new")));
                var ring = db.Services.GetRequiredService<IKeyManager>().GetAllKeys();
                Assert.Equal(3, ring.Count);
                Assert.True(ring.Single(k => k.KeyId == revoked).IsRevoked);   // the revocation came across too
            }
            Assert.Equal(files.Count, await RowsAsync(dir));
            Assert.Equal(files, Snapshot(keys));   // the folder is read, never changed

            // A key file added after the import is never imported: the database holds keys, so the folder is not read into it again.
            var extra = files.First(f => f.Key.StartsWith("key-", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(keys, "key-00000000-0000-4000-8000-000000000001.xml"), extra.Value.Replace(extra.Key[4..^4], "00000000-0000-4000-8000-000000000001"));
            await using (var again = Build(dir, keys, "database", key, logs)) Assert.Equal("session from before the switch", Protector(again).Unprotect(old));
            Assert.Equal(files.Count, await RowsAsync(dir));

            // And back: the folder still holds its keys, so the old payload still reads in folder mode.
            await using (var back = Build(dir, keys, "folder", key)) Assert.Equal("session from before the switch", Protector(back).Unprotect(old));

            List<string> lines;
            lock (logs.Lines) lines = logs.Lines.ToList();
            Assert.Contains(lines, l => l.StartsWith(CookieKeyImport.LogCategory + ": ", StringComparison.Ordinal) && l.Contains($"Imported {files.Count} key files"));
            Assert.Contains(lines, l => l.StartsWith(CookieKeyImport.LogCategory + ": ", StringComparison.Ordinal) && l.Contains("nothing imported"));
            foreach (var secret in new[] { "<key", "masterKey", "<encryptedSecret", "<descriptor" })
                Assert.DoesNotContain(lines, l => l.Contains(secret, StringComparison.Ordinal));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutations: a malformed key file is skipped (its sessions would quietly end); the error quotes the file
    public void A_key_file_that_is_not_xml_stops_the_start_naming_the_file_only()
    {
        var dir = Dir("malformed");
        var keys = Path.Combine(dir, "admin-keys");
        try
        {
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "key-broken.xml"), "<key id=\"x\"><masterKey>TOPSECRETBYTES</masterKey>");
            var (code, error) = Start(o => Options(o, dir, keys, "database"));
            Assert.Equal(1, code);
            Assert.Equal($"error: The cookie key file key-broken.xml in {keys} is not valid XML. Refusing to start: restore it from a backup or remove it, then start again.", error);
            Assert.DoesNotContain("TOPSECRETBYTES", error);
            Assert.DoesNotContain("masterKey", error);
            Assert.Equal(["key-broken.xml"], Directory.GetFiles(keys).Select(Path.GetFileName));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutations: a row that does not decrypt is skipped (the key ring would mint a new key and sign everyone out); the folder is read again
    public async Task A_key_the_master_key_cannot_read_stops_the_start_and_a_filled_database_never_reads_the_folder()
    {
        var dir = Dir("unreadable");
        var keys = Path.Combine(dir, "admin-keys");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        try
        {
            await using (var first = Build(dir, null, "database", key)) _ = Protector(first).Protect("x");
            Assert.Equal(1L, await RowsAsync(dir));

            // The database holds a key, so a broken file in the folder is never read.
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "key-broken.xml"), "<key");
            await using (var second = Build(dir, keys, "database", key)) Assert.Equal(1L, await RowsAsync(dir));

            await using (var c = new SqliteConnection($"Data Source={Path.Combine(dir, "packmoat.db")};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE cookie_keys SET xml_encrypted = zeroblob(64)";
                Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            }
            var (code, error) = Start(o => Options(o, dir, keys, "database", key));
            Assert.Equal(1, code);
            Assert.Equal("error: A cookie key in the database cannot be read with this master key. Refusing to start: use the master key this database was made with.", error);
        }
        finally { Delete(dir); }
    }

    /// <summary>Every log line as "category level: message".</summary>
    private sealed class LevelLogs : ILoggerProvider
    {
        public List<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }
        public List<string> Snapshot() { lock (Lines) return Lines.ToList(); }
        private sealed class L(LevelLogs o, string c) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (o.Lines) o.Lines.Add($"{c} {logLevel}: {formatter(state, exception)}");
            }
        }
    }

    private const string NoKeysWarning = "has no key files: a new key is made, so anyone signed in before signs in again. Start the first server " +
        "with PACKMOAT_COOKIE_KEYS=database where the existing key folder is mounted.";

    [Fact]   // mutations: a missing or empty folder stops the start (or is created); its line is only Information (an operator would miss it)
    public async Task Database_mode_with_no_key_files_warns_imports_nothing_and_makes_its_first_key_in_the_database()
    {
        var dir = Dir("fresh");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var logs = new LevelLogs();
        try
        {
            await using (var app = Build(dir, null, "database", key, logs))
            {
                Assert.Equal(0L, await RowsAsync(dir));
                Assert.Equal("x", Protector(app).Unprotect(Protector(app).Protect("x")));
            }
            Assert.Equal(1L, await RowsAsync(dir));
            Assert.False(Directory.Exists(Path.Combine(dir, "admin-keys")));
            var line = Assert.Single(logs.Snapshot(), l => l.StartsWith(CookieKeyImport.LogCategory + " ", StringComparison.Ordinal));
            Assert.Equal($"{CookieKeyImport.LogCategory} Warning: Cookie keys are in the database, which holds none, and the key folder " +
                $"{Path.Combine(dir, "admin-keys")} {NoKeysWarning}", line);

            // An empty folder, on a new database, likewise.
            var other = Dir("fresh-empty");
            var empty = Path.Combine(other, "keys");
            Directory.CreateDirectory(empty);
            var more = new LevelLogs();
            try
            {
                await using (var app = Build(other, empty, "database", AesGcmSecretProtector.GenerateKeyBase64(), more)) { }
                Assert.Contains($"{CookieKeyImport.LogCategory} Warning: Cookie keys are in the database, which holds none, and the key folder {empty} {NoKeysWarning}", more.Snapshot());
                Assert.Empty(Directory.GetFiles(empty));
            }
            finally { Delete(other); }

            // Once the database holds a key, a later start has nothing to warn about.
            var later = new LevelLogs();
            await using (var app = Build(dir, null, "database", key, later)) { }
            Assert.DoesNotContain(later.Snapshot(), l => l.Contains(" Warning: ", StringComparison.Ordinal) && l.StartsWith(CookieKeyImport.LogCategory, StringComparison.Ordinal));
        }
        finally { Delete(dir); }
    }

    [Fact]   // mutations: the store is registered only when the admin site is set; the customer site passes the folder mode regardless
    public async Task A_customer_site_only_server_keeps_its_cookie_keys_in_the_database()
    {
        var dir = Dir("customer-only");
        try
        {
            await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, o =>
            {
                Options(o, dir, Path.Combine(dir, "keys"), "database");
                o.AdminHost = null;
                o.AppHost = "app.example.test";
                o.AppClientId = "55555555-5555-4555-8555-555555555555";
            });
            Assert.IsType<DatabaseXmlRepository>(app.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
            Assert.IsType<SqliteCookieKeyStore>(app.Services.GetRequiredService<ICookieKeyStore>());
            Assert.Equal("x", Protector(app).Unprotect(Protector(app).Protect("x")));
            Assert.Equal(1L, await RowsAsync(dir));
            Assert.False(Directory.Exists(Path.Combine(dir, "keys")));
        }
        finally { Delete(dir); }
    }

    private sealed class ThrowingStore : ICookieKeyStore
    {
        public Task<IReadOnlyList<CookieKey>> GetAllAsync(CancellationToken ct) => throw new System.Security.Cryptography.CryptographicException("unreadable");
        public Task AddAsync(string friendlyName, string xml, CancellationToken ct) => throw new InvalidOperationException("disk full");
        public Task<int> ImportIfEmptyAsync(IReadOnlyList<CookieKey> keys, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ListStore : ICookieKeyStore
    {
        public List<CookieKey> Keys { get; } = new();
        public Task<IReadOnlyList<CookieKey>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CookieKey>>(Keys.ToList());
        public Task AddAsync(string friendlyName, string xml, CancellationToken ct) { Keys.Add(new CookieKey(friendlyName, xml)); return Task.CompletedTask; }
        public Task<int> ImportIfEmptyAsync(IReadOnlyList<CookieKey> keys, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]   // mutation: the repository catches a store error and returns no keys (the key ring would mint a new key and sign everyone out)
    public void The_repository_passes_a_store_error_on_and_never_reads_as_empty()
    {
        var repository = new DatabaseXmlRepository(new ThrowingStore());
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => repository.GetAllElements());
        Assert.Throws<InvalidOperationException>(() => repository.StoreElement(new System.Xml.Linq.XElement("key"), "key-1"));
    }

    [Fact]   // mutations: store the element formatted (or as its inner text); parse nothing back
    public void The_repository_stores_each_element_unformatted_and_reads_it_back()
    {
        var store = new ListStore();
        var repository = new DatabaseXmlRepository(store);
        var element = System.Xml.Linq.XElement.Parse("<key id=\"1\">\n  <descriptor>\n    <masterKey>abc</masterKey>\n  </descriptor>\n</key>");
        repository.StoreElement(element, "key-1");
        Assert.Equal(new CookieKey("key-1", "<key id=\"1\"><descriptor><masterKey>abc</masterKey></descriptor></key>"), Assert.Single(store.Keys));
        Assert.True(System.Xml.Linq.XNode.DeepEquals(element, Assert.Single(repository.GetAllElements())));
    }
}
