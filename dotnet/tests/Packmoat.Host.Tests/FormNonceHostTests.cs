using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The form nonces in the server: over a database both sites keep them there (never in the process), so a form shown by one copy is used
/// once on another and survives a restart; a store failure refuses the form and is logged with the scope and the error's type only; and
/// the daily retention pass deletes expired nonces.
/// </summary>
[Collection("sqlite-pools")]
public class FormNonceHostTests : IDisposable
{
    private readonly TempDir _temp = TempDir.Create("packmoat-nonces-", clearSqlitePools: true);
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private readonly FakeClock _clock = new();
    private string DbPath => _temp.File("packmoat.db");

    // xunit 2 disposes a test class through IDisposable only (an IAsyncDisposable one is never called).
    public void Dispose() => _temp.Dispose();

    /// <param name="sites">With the admin and customer sites, whose form nonces are under test (their cookie keys in the folder next to the database).</param>
    private WebApplication BuildServer(bool sites = false) =>
        PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, s => s.AddSingleton<IClock>(_clock), o =>
        {
            o.Database = DbPath;
            o.MasterKeyBase64 = _key;
            o.AllowedHosts = "feeds.example.test";
            if (!sites) return;
            o.AdminHost = "admin.example.test";
            o.AppHost = "app.example.test";
        });

    [Fact]   // mutation: the in-process store registered over a database (each copy would keep its own nonces again)
    public async Task Over_a_database_both_sites_keep_their_nonces_in_the_database()
    {
        await using var app = BuildServer(sites: true);
        Assert.IsType<SqliteFormNonceStore>(app.Services.GetRequiredService<IFormNonceStore>());
        Assert.Single(app.Services.GetServices<IFormNonceStore>());
        await app.Services.GetRequiredService<AdminFormNonces>().IssueAsync("acme", default);
        await app.Services.GetRequiredService<AppFormNonces>().IssueAsync("dir/oid", default);

        await using var c = await new SqliteDatabase(DbPath).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(scope || ':' || subject, ' ') FROM (SELECT scope, subject FROM form_nonces ORDER BY scope)";
        Assert.Equal($"admin:{FormNonceRules.Hash("acme")} app:{FormNonceRules.Hash("dir/oid")}", await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Two copies over one database, then a restart: a form shown by one is used on the other, once; a nonce issued before the restart is
    /// still good after it. A SQLite database takes one server (a second is refused), so the other copy is the site's nonces over its own
    /// connection to the same file, as another copy's would be.
    /// </summary>
    [Fact]   // mutations: the in-process store over a database; the consume not deleting (the replay on the first copy would pass)
    public async Task A_form_shown_by_one_copy_is_used_once_on_another_and_survives_a_restart()
    {
        string kept;
        await using (var one = BuildServer(sites: true))
        {
            var two = new AppFormNonces(new SqliteStores(new SqliteDatabase(DbPath), AesGcmSecretProtector.FromBase64(_key)).FormNonces(_clock));
            var shown = await one.Services.GetRequiredService<AppFormNonces>().IssueAsync("dir/oid", default);
            Assert.True(await two.ConsumeAsync(shown, "dir/oid", default));
            Assert.False(await one.Services.GetRequiredService<AppFormNonces>().ConsumeAsync(shown, "dir/oid", default));
            kept = await one.Services.GetRequiredService<AdminFormNonces>().IssueAsync("acme", default);
        }
        await using var restarted = BuildServer(sites: true);
        Assert.True(await restarted.Services.GetRequiredService<AdminFormNonces>().ConsumeAsync(kept, "acme", default));
    }

    /// <summary>
    /// the production composition's daily retention pass, as the server runs it (the real service, its lease and every step), deletes
    /// the database's expired nonces and keeps a live one.
    /// </summary>
    [Fact]   // mutation: PackmoatApp not handing the retention service the form nonces (expired ones would pile up)
    public async Task The_production_retention_pass_deletes_the_databases_expired_nonces()
    {
        _clock.UtcNow = DateTimeOffset.UtcNow;   // the database's own floors read the real time
        await using var app = BuildServer();
        var store = app.Services.GetRequiredService<IFormNonceStore>();
        await store.IssueAsync(FormNonceScopes.Admin, "expired", "acme", TimeSpan.FromMinutes(1), FormNonces.MaxPerKey);
        await store.IssueAsync(FormNonceScopes.App, "live", "dir/oid", FormNonces.Life, FormNonces.MaxPerKey);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
        Assert.True(await retention.PassAsync(default));

        await using var c = await new SqliteDatabase(DbPath).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(scope) FROM form_nonces";
        Assert.Equal(FormNonceScopes.App, await cmd.ExecuteScalarAsync());
    }

    /// <summary>The retention service's own step, given the store directly: it deletes expired nonces only and logs the count.</summary>
    [Fact]   // mutation: the step deleting a live nonce, or logging no count
    public async Task The_retention_step_deletes_expired_nonces_only_and_logs_the_count()
    {
        var db = new SqliteDatabase(DbPath);
        await db.MigrateAsync(TestTracks.Modules);
        var nonces = new SqliteFormNonceStore(db, _clock);
        await nonces.IssueAsync(FormNonceScopes.Admin, "expired", "acme", TimeSpan.FromMinutes(1), FormNonces.MaxPerKey);
        await nonces.IssueAsync(FormNonceScopes.App, "live", "dir/oid", FormNonces.Life, FormNonces.MaxPerKey);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var log = new JobLeaseHostTests.RecordingLogger();
        var service = new RetentionService(new SqliteFeedCheckStore(db), _clock, log, nonces: nonces);

        Assert.True(await service.PassAsync(default));

        Assert.Contains((LogLevel.Information, "Retention: 1 expired form nonce(s) deleted"), log.Lines);
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT scope FROM form_nonces";
        Assert.Equal(FormNonceScopes.App, await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// When the store cannot be reached the form is refused, never let through: the form still shows (with a nonce the store never kept),
    /// and its post is refused as an old form. Each failure is one warning naming the scope and the error's type, nothing of the key, the
    /// nonce or the error's message.
    /// </summary>
    [Fact]   // mutations: a consume failing open; the failure not logged, or logged with the key or the message
    public async Task A_store_failure_refuses_the_form_and_logs_the_scope_and_the_error_type_only()
    {
        var logs = new RecordingLoggerFactory();
        var app = new AppFormNonces(new DownStore(), logs);
        var admin = new AdminFormNonces(new DownStore(), logs);

        var nonce = await app.IssueAsync("dir/secret-oid", default);
        Assert.Matches("^[0-9a-f]{32}$", nonce);
        Assert.False(await app.ConsumeAsync(nonce, "dir/secret-oid", default));
        Assert.False(await admin.ConsumeAsync("whatever", "acme", default));

        Assert.Equal(new[]
        {
            (FormNonces.LogCategory, LogLevel.Warning, "Form nonces app: the store failed on issue (IOException); the form is refused"),
            (FormNonces.LogCategory, LogLevel.Warning, "Form nonces app: the store failed on consume (IOException); the form is refused"),
            (FormNonces.LogCategory, LogLevel.Warning, "Form nonces admin: the store failed on consume (IOException); the form is refused"),
        }, logs.Lines);
        Assert.DoesNotContain(logs.Lines, l => l.Message.Contains("secret") || l.Message.Contains(nonce) || l.Message.Contains("down"));
    }

    [Fact]   // the caller's cancellation is not a failure: it is thrown, and nothing is logged
    public async Task The_callers_cancellation_is_thrown_and_not_logged()
    {
        var logs = new RecordingLoggerFactory();
        var nonces = new AppFormNonces(new CancelledStore(), logs);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nonces.IssueAsync("dir/oid", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nonces.ConsumeAsync("n", "dir/oid", cts.Token));
        Assert.Empty(logs.Lines);
        // A stray cancellation the caller did not ask for is a failure: refused, and logged.
        Assert.False(await nonces.ConsumeAsync("n", "dir/oid", default));
        Assert.Single(logs.Lines);
    }

    [Fact]   // mutation: a form's life or a key's cap changed (the pages say neither, so this is where they are pinned)
    public void A_nonce_lives_30_minutes_and_a_key_holds_20()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), FormNonces.Life);
        Assert.Equal(20, FormNonces.MaxPerKey);
    }

    private sealed class DownStore : IFormNonceStore
    {
        public Task IssueAsync(string scope, string nonce, string key, TimeSpan life, int maxPerKey, CancellationToken ct = default) =>
            throw new IOException("the database is down");
        public Task<bool> ConsumeAsync(string scope, string nonce, string key, CancellationToken ct = default) => throw new IOException("the database is down");
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => throw new IOException("the database is down");
    }

    private sealed class CancelledStore : IFormNonceStore
    {
        public Task IssueAsync(string scope, string nonce, string key, TimeSpan life, int maxPerKey, CancellationToken ct = default) =>
            throw new OperationCanceledException(ct);
        public Task<bool> ConsumeAsync(string scope, string nonce, string key, CancellationToken ct = default) => throw new OperationCanceledException(ct);
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => throw new OperationCanceledException(ct);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<(string Category, LogLevel Level, string Message)> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(RecordingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines) owner.Lines.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }
}
