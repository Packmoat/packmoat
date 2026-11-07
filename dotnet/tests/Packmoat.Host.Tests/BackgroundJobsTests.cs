using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// PACKMOAT_BACKGROUND_JOBS (#233): on (the default, unset) composes exactly as before; off leaves out the daily retention pass and nothing
/// else (the audit queue and the start-up blanking still run); anything else stops the start, naming the setting and never the value.
/// </summary>
[Collection("sqlite-pools")]
public class BackgroundJobsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void On_or_off_and_unset_is_on(string? value, bool on) => Assert.Equal(on, BackgroundJobs.Parse(value));

    [Theory]
    [InlineData("ON")]
    [InlineData("Off")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData(" on")]
    [InlineData("off ")]
    [InlineData("disabled-xyz")]
    public void Anything_else_is_refused_naming_the_setting_not_the_value(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BackgroundJobs.Parse(value));
        Assert.Equal("PACKMOAT_BACKGROUND_JOBS must be on or off.", ex.Message);   // the same words whatever the value
    }

    /// <summary>Counts the start-up blanking; everything else is the real store's business.</summary>
    private sealed class CountingFeedChecks : IFeedCheckStore
    {
        public int Blanks;
        public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct) { Interlocked.Increment(ref Blanks); return Task.FromResult(0); }
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<T> WithServer<T>(string? jobs, Func<WebApplication, IServiceCollection, T> look, Action<IServiceCollection>? more = null)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-jobs-").FullName;
        try
        {
            IServiceCollection? services = null;
            await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, s => { services = s; more?.Invoke(s); }, o =>
            {
                o.DatabasePath = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
                o.BackgroundJobs = jobs;
            });
            return look(app, services!);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static string[] Registrations(IServiceCollection services) =>
        services.Select(d => $"{d.ServiceType.FullName} {d.Lifetime} {d.ImplementationType?.FullName ?? d.ImplementationInstance?.GetType().FullName ?? "factory"}").ToArray();

    [Fact]   // mutation: PackmoatApp registers the retention pass whatever the switch says
    public async Task Off_registers_no_retention_pass_but_keeps_the_audit_queue_and_the_start_up_blanking()
    {
        var checks = new CountingFeedChecks();
        var (hosted, audit) = await WithServer("off", (app, _) =>
            (app.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToArray(), app.Services.GetRequiredService<IAuditLog>()), s => s.AddSingleton<IFeedCheckStore>(checks));
        Assert.DoesNotContain(typeof(RetentionService), hosted);
        Assert.IsType<QueuedAuditLog>(audit);
        Assert.Equal(1, checks.Blanks);   // BlankAtStartup ran at the start
    }

    [Fact]
    public async Task On_and_unset_compose_the_same_as_before()
    {
        var unset = await WithServer(null, (app, s) => (Hosted: app.Services.GetServices<IHostedService>().OfType<RetentionService>().Count(), Lines: Registrations(s)));
        var on = await WithServer("on", (app, s) => (Hosted: app.Services.GetServices<IHostedService>().OfType<RetentionService>().Count(), Lines: Registrations(s)));
        Assert.Equal(1, unset.Hosted);
        Assert.Equal(1, on.Hosted);
        Assert.Equal(unset.Lines, on.Lines);
        var off = await WithServer("off", (_, s) => Registrations(s));
        Assert.Equal(unset.Lines.Length - 1, off.Length);   // exactly the retention pass's registration is gone
    }

    [Fact]
    public async Task A_bad_value_stops_the_start_before_anything_is_written()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-jobs-bad-").FullName;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, o =>
            {
                o.DatabasePath = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
                o.BackgroundJobs = "sometimes";
            }));
            Assert.Contains("PACKMOAT_BACKGROUND_JOBS", ex.Message);
            Assert.DoesNotContain("sometimes", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "packmoat.db")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]   // the setting is read through the one settings reader, so it can come from a file as every other can
    public void The_setting_is_read_by_name_through_the_settings_reader()
    {
        var reader = new Settings(n => n == BackgroundJobs.Variable + Settings.FileSuffix ? "/run/secrets/jobs" : null, _ => "off\n");
        Assert.False(BackgroundJobs.Parse(reader.Read(BackgroundJobs.Variable)));
    }
}
