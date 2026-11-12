using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The real host wired to the real SQLite stack (tenants, tokens, audit) against the fake upstream. Shared with the hosted tests,
/// which compile it in: it runs <see cref="TestModules.Server"/> with the tracks, participants and gate of the TestModules.cs it is built with.
/// </summary>
public sealed class SqliteHostFixture : IAsyncDisposable
{
    public FakeUpstream Upstream { get; private set; } = null!;
    public FakeDateSource Dates { get; } = new();
    public FakeClock Clock { get; } = new();
    public SqliteDatabase Db { get; private set; } = null!;
    public SqliteTenantStore Tenants { get; private set; } = null!;
    public SqliteTokenService Tokens { get; private set; } = null!;
    public SqliteAuditLog Audit { get; private set; } = null!;
    public SqliteTokenActivity Activity { get; private set; } = null!;
    public ActivityRecordingAuthenticator Recorder { get; private set; } = null!;
    public SqliteDecisionRecordLog Decisions { get; private set; } = null!;
    /// <summary>The host's own gate: the decision recorder PackmoatApp wraps around the policy gate.</summary>
    public RecordingPackageGate DecisionRecorder => (RecordingPackageGate)_app.Services.GetRequiredService<IPackageGate>();
    public string BaseUrl { get; private set; } = "";
    public string SourceUrl => BaseUrl + "/v3/index.json";
    private readonly TempDir _temp = TempDir.Create("packmoat-host-db-", clearSqlitePools: true);
    public string DbDir => _temp.Path;
    private WebApplication _app = null!;

    /// <summary>The host's own audit log (production wiring), when the fixture was started with <c>queuedAudit</c> or <c>auditStore</c>.</summary>
    public QueuedAuditLog AuditQueue => (QueuedAuditLog)_app.Services.GetRequiredService<IAuditLog>();

    /// <param name="queuedAudit">Use PackmoatApp's own audit wiring (the queue) over <see cref="Audit"/>, instead of writing to it directly.</param>
    /// <param name="auditStore">Use PackmoatApp's own audit wiring (the queue) over this store.</param>
    public static async Task<SqliteHostFixture> StartAsync(int cooldown = 14, ITokenActivityLog? activityLog = null, bool queuedAudit = false,
        IDecisionRecordLog? decisionLog = null, IAuditBatchLog? auditStore = null)
    {
        var f = new SqliteHostFixture();
        try
        {
            await f.BuildAndStartAsync(cooldown, activityLog, queuedAudit, decisionLog, auditStore);
            return f;
        }
        catch
        {
            await f.DisposeAsync();   // the folder, and whatever started before the failure
            throw;
        }
    }

    private async Task BuildAndStartAsync(int cooldown, ITokenActivityLog? activityLog, bool queuedAudit, IDecisionRecordLog? decisionLog, IAuditBatchLog? auditStore)
    {
        var f = this;
        f.Upstream = await FakeUpstream.StartAsync();
        f.Db = new SqliteDatabase(Path.Combine(f.DbDir, "packmoat.db"));
        await f.Db.MigrateAsync(TestTracks.Modules);
        f.Tenants = new SqliteTenantStore(f.Db, f.Clock, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()), TestLifecycle.Participants);
        f.Tokens = new SqliteTokenService(f.Db, f.Tenants, f.Clock, TestModules.Gate(f.Db, f.Clock));
        f.Audit = new SqliteAuditLog(f.Db);
        f.Activity = new SqliteTokenActivity(f.Db, f.Clock);
        f.Decisions = new SqliteDecisionRecordLog(f.Db);
        await f.Tenants.CreateAsync(new NewTenant("acme", "Acme", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, cooldown, TenantOrigin.Operator), default);

        f._app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s =>
        {
            s.AddSingleton<IClock>(f.Clock);
            s.AddSingleton<IPublishDateSource>(f.Dates);
            f.Recorder = new ActivityRecordingAuthenticator(new CachedTenantAuthenticator(f.Tokens, f.Clock), activityLog ?? f.Activity, f.Clock);
            s.AddSingleton<ITenantAuthenticator>(f.Recorder);
            if (queuedAudit || auditStore is not null) s.AddSingleton<IAuditBatchLog>(auditStore ?? f.Audit);
            else s.AddSingleton<IAuditLog>(f.Audit);
            s.AddSingleton<IUpstreamCredentialSource>(new StaticBasicCredentialSource(FakeUpstream.User, FakeUpstream.Secret));
            s.AddSingleton<IDecisionRecordLog>(decisionLog ?? f.Decisions);
        });
        await f._app.StartAsync();
        f.BaseUrl = f._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');
    }

    public void Publish(string id, string version, double ageDays)
    {
        Upstream.AddPackage(id, version);
        Dates.Set(id, version, Clock.UtcNow - TimeSpan.FromDays(ageDays));
    }

    /// <summary>Stops what <see cref="StartAsync"/> started (a failed start may have got only part way) and deletes the folder.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(); }
                finally
                {
                    Recorder?.Dispose();
                    await _app.DisposeAsync();
                }
            }
        }
        finally
        {
            try { if (Upstream is not null) await Upstream.DisposeAsync(); }
            finally { _temp.Dispose(); }
        }
    }
}
