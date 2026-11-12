using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The server's store composition shares one adapter where one is meant: the tenant store the ports read is the one
/// the token service checks against, the three audit ports are one audit log, and the token port is the token service. Two copies of a
/// store would each work alone and disagree only under load, so this pins the sharing.
/// </summary>
[Collection("sqlite-pools")]
public class StoreIdentityTests
{
    private static async Task WithServer(Action<IServiceProvider> body)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-identity-").FullName;
        try
        {
            await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [], null, o =>
            {
                o.Database = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            body(app.Services);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    private static T Field<T>(object owner) => (T)owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(f => f.FieldType == typeof(T)).GetValue(owner)!;

    [Fact]   // mutation: a second tenant store instance (for the port, or for the token service)
    public Task The_tenant_port_and_the_token_service_share_one_tenant_store() => WithServer(sp =>
    {
        var tenants = sp.GetRequiredService<SqliteTenantStore>();
        Assert.Same(tenants, sp.GetRequiredService<ITenantStore>());
        Assert.Same(tenants, Field<SqliteTenantStore>(sp.GetRequiredService<SqliteTokenService>()));
    });

    [Fact]   // mutation: an audit port given its own SqliteAuditLog
    public Task The_audit_ports_are_one_audit_log() => WithServer(sp =>
    {
        var audit = sp.GetRequiredService<SqliteAuditLog>();
        Assert.Same(audit, sp.GetRequiredService<IAuditBatchLog>());
        Assert.Same(audit, sp.GetRequiredService<IAuditRetention>());
        Assert.Same(audit, sp.GetRequiredService<IAuditQuery>());
    });

    [Fact]   // mutation: the token port given its own SqliteTokenService
    public Task The_token_port_is_the_token_service() => WithServer(sp =>
        Assert.Same(sp.GetRequiredService<SqliteTokenService>(), sp.GetRequiredService<ITokenService>()));

    [Fact]   // the same for the token activity's three faces
    public Task The_token_activity_ports_are_one_token_activity() => WithServer(sp =>
    {
        var activity = sp.GetRequiredService<SqliteTokenActivity>();
        Assert.Same(activity, sp.GetRequiredService<ITokenActivityRetention>());
        Assert.Same(activity, sp.GetRequiredService<IActiveDeveloperQuery>());
    });

    // mutations: the authenticator given its own token service, or its own token activity, rather than the ones the
    // ports resolve to
    [Fact]
    public Task The_authenticator_checks_the_token_service_and_records_to_the_token_activity() => WithServer(sp =>
    {
        var outer = Assert.IsType<ActivityRecordingAuthenticator>(sp.GetRequiredService<ITenantAuthenticator>());
        Assert.Same(sp.GetRequiredService<SqliteTokenActivity>(), Field<ITokenActivityLog>(outer));
        Assert.Same(sp.GetRequiredService<IActiveDeveloperQuery>(), Field<ITokenActivityLog>(outer));
        var cached = Assert.IsType<CachedTenantAuthenticator>(Field<ITenantAuthenticator>(outer));
        Assert.Same(sp.GetRequiredService<SqliteTokenService>(), Field<ITenantAuthenticator>(cached));
        Assert.Same(sp.GetRequiredService<ITokenService>(), Field<ITenantAuthenticator>(cached));
    });

    [Fact]   // and the decision record's
    public Task The_decision_record_port_is_the_decision_record_log() => WithServer(sp =>
        Assert.Same(sp.GetRequiredService<SqliteDecisionRecordLog>(), sp.GetRequiredService<IDecisionRecordLog>()));
}
