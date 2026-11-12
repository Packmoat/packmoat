using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The feed setup scratch and the check lease in the server's own composition: over a database the wizard keeps each person's last check
/// and draft there (never in the process) and runs one check at a time per tenant under the database's job lease, so two copies share
/// both and a restart keeps them; and the daily retention pass deletes expired rows.
/// </summary>
[Collection("sqlite-pools")]
public class FeedSetupScratchHostTests : IDisposable
{
    private readonly TempDir _temp = TempDir.Create("packmoat-scratch-", clearSqlitePools: true);
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private readonly FakeClock _clock = new();
    private static readonly Guid Ada = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private string DbPath => _temp.File("packmoat.db");

    // xunit 2 disposes a test class through IDisposable only (an IAsyncDisposable one is never called).
    public void Dispose() => _temp.Dispose();

    /// <param name="wizard">With the customer site and the setup wizard on, whose state is under test.</param>
    private WebApplication BuildServer(bool wizard = false) =>
        PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, s => s.AddSingleton<IClock>(_clock), o =>
        {
            o.Database = DbPath;
            o.MasterKeyBase64 = _key;
            o.AllowedHosts = "feeds.example.test";
            if (!wizard) return;
            o.AppHost = "app.example.test";
            o.AzureSetup = AzureSetupSettings.Parse("on");
        });

    private static HeldFeedCheck Check(DateTimeOffset at) =>
        new(at, new FeedNames("contoso", null, "feed"), "https://pkgs.dev.azure.com/contoso/_packaging/f/nuget/v3/index.json", FeedCheckOutcome.Incomplete,
            [new CheckResult("permissions", "Permissions", CheckStatus.Warn, "named admins")], [new NamedFeedAdmin("Ada Lovelace (feed creator)", "Ada Lovelace", "aad.ada")], true);

    [Fact]   // mutation: the in-process scratch registered over a database (each copy would keep its own again)
    public async Task Over_a_database_the_wizard_keeps_its_state_in_the_database()
    {
        await using var app = BuildServer(wizard: true);
        Assert.IsType<SqliteFeedSetupScratch>(app.Services.GetRequiredService<IFeedSetupScratch>());
        Assert.Single(app.Services.GetServices<IFeedSetupScratch>());
        await app.Services.GetRequiredService<AzureSetupState>().HoldAsync("acme", Ada, Check(_clock.UtcNow));

        await using var c = await new SqliteDatabase(DbPath).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tenant_id || '|' || person_oid || '|' || kind FROM feed_setup_scratch";
        Assert.Equal($"acme|{Ada:D}|settings", await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Two copies over one database, then a restart: a check one copy holds is read on the other, and still after the restart; a check
    /// running on one copy refuses one on the other until it ends. A SQLite database takes one server (a second is refused), so the other
    /// copy is the wizard's state over its own connection to the same file, as another copy's would be.
    /// </summary>
    [Fact]   // mutations: the in-process scratch over a database; the wizard's state given a lease of its own copy (AppSite given UnsharedJobLease)
    public async Task Two_copies_share_the_held_check_and_the_check_lease_and_a_restart_keeps_the_check()
    {
        var check = Check(_clock.UtcNow);
        await using (var one = BuildServer(wizard: true))
        {
            var other = new SqliteStores(new SqliteDatabase(DbPath), AesGcmSecretProtector.FromBase64(_key));
            var first = one.Services.GetRequiredService<AzureSetupState>();
            var second = new AzureSetupState(other.FeedSetupScratch(_clock), other.JobLease(_clock), _clock);
            await first.HoldAsync("acme", Ada, check);
            Assert.Equal(FeedSetupPayload.Write(check), FeedSetupPayload.Write((await second.GetAsync("acme", Ada))!));

            var running = await first.TryBeginAsync("acme");
            Assert.NotNull(running);
            Assert.Null(await second.TryBeginAsync("acme"));
            await first.EndAsync("acme", running!);
            _clock.Advance(TimeSpan.FromTicks(1));
            Assert.NotNull(await second.TryBeginAsync("acme"));
        }
        _clock.Advance(TimeSpan.FromMinutes(5));
        await using var restarted = BuildServer(wizard: true);
        Assert.Equal(FeedSetupPayload.Write(check), FeedSetupPayload.Write((await restarted.Services.GetRequiredService<AzureSetupState>().GetAsync("acme", Ada))!));
    }

    /// <summary>
    /// the production composition's daily retention pass, as the server runs it (the real service, its lease and every step), deletes
    /// the database's expired scratch rows and keeps a live one.
    /// </summary>
    [Fact]   // mutation: PackmoatApp not handing the retention service the feed setup scratch (expired rows, with admin names, would pile up)
    public async Task The_production_retention_pass_deletes_the_databases_expired_scratch()
    {
        _clock.UtcNow = DateTimeOffset.UtcNow;   // the database's own floors read the real time
        await using var app = BuildServer();
        var store = app.Services.GetRequiredService<IFeedSetupScratch>();
        await store.PutAsync("acme", Ada, FeedSetupScratchKinds.Settings, "{}", TimeSpan.FromMinutes(1));
        await store.PutAsync("acme", Ada, FeedSetupScratchKinds.Draft, "{}", AzureSetupState.HoldFor);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
        Assert.True(await retention.PassAsync(default));

        await using var c = await new SqliteDatabase(DbPath).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(kind) FROM feed_setup_scratch";
        Assert.Equal(FeedSetupScratchKinds.Draft, await cmd.ExecuteScalarAsync());
    }

    /// <summary>The retention service's own step, given the store directly: it deletes expired rows only and logs the count.</summary>
    [Fact]   // mutation: the step deleting a live row, or logging no count
    public async Task The_retention_step_deletes_expired_rows_only_and_logs_the_count()
    {
        var db = new SqliteDatabase(DbPath);
        await db.MigrateAsync(TestTracks.Modules);
        var scratch = new SqliteFeedSetupScratch(db, _clock);
        await scratch.PutAsync("acme", Ada, FeedSetupScratchKinds.Settings, "{}", TimeSpan.FromMinutes(1));
        await scratch.PutAsync("acme", Ada, FeedSetupScratchKinds.Draft, "{}", AzureSetupState.HoldFor);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var log = new JobLeaseHostTests.RecordingLogger();
        var service = new RetentionService(new SqliteFeedCheckStore(db), _clock, log, scratch: scratch);

        Assert.True(await service.PassAsync(default));

        Assert.Contains((LogLevel.Information, "Retention: 1 expired feed setup result(s) and draft(s) deleted"), log.Lines);
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind FROM feed_setup_scratch";
        Assert.Equal(FeedSetupScratchKinds.Draft, await cmd.ExecuteScalarAsync());
    }
}
