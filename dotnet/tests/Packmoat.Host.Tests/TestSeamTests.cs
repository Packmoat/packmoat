using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The server's public test seams (#197): exactly five, the fakes a module author needs to test signed-in pages (an identity provider for
/// each site, the Azure DevOps sign-in and transport, the shared app's credential). Each is null by default, is never read from the
/// environment, and is named in a warning at start when set, so a seam set where it should not be is seen in the log.
/// </summary>
[Collection("sqlite-pools")]
public class TestSeamTests
{
    private static readonly string[] Seams =
    [
        nameof(PackmoatOptions.AdminRemoteSchemeOverride), nameof(PackmoatOptions.AppRemoteSchemeOverride),
        nameof(PackmoatOptions.AppAzureDevOpsSchemeOverride), nameof(PackmoatOptions.AzureDevOpsHandlerOverride),
        nameof(PackmoatOptions.SharedAppTokenOverride),
    ];

    private static void Set(PackmoatOptions o, string seam)
    {
        switch (seam)
        {
            case nameof(PackmoatOptions.AdminRemoteSchemeOverride): o.AdminRemoteSchemeOverride = _ => { }; break;
            case nameof(PackmoatOptions.AppRemoteSchemeOverride): o.AppRemoteSchemeOverride = _ => { }; break;
            case nameof(PackmoatOptions.AppAzureDevOpsSchemeOverride): o.AppAzureDevOpsSchemeOverride = _ => { }; break;
            case nameof(PackmoatOptions.AzureDevOpsHandlerOverride): o.AzureDevOpsHandlerOverride = () => new HttpClientHandler(); break;
            case nameof(PackmoatOptions.SharedAppTokenOverride): o.SharedAppTokenOverride = _ => throw new NotSupportedException(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seam));
        }
    }

    [Fact]   // mutations: a non-null default; a sixth public seam
    public void Exactly_the_five_seams_are_public_and_each_is_null_by_default()
    {
        var o = new PackmoatOptions();
        var seams = typeof(PackmoatOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType)).ToList();
        Assert.Equal(Seams.Order(StringComparer.Ordinal), seams.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(seams, p => Assert.Null(p.GetValue(o)));
        Assert.Empty(PackmoatApp.SetTestSeams(o));

        // Every public seam is one the start's warning names.
        foreach (var seam in Seams) Set(o, seam);
        Assert.Equal(Seams, PackmoatApp.SetTestSeams(o));
    }

    [Theory]   // mutation: remove the warning's log line
    [InlineData(nameof(PackmoatOptions.AdminRemoteSchemeOverride))]
    [InlineData(nameof(PackmoatOptions.AppRemoteSchemeOverride))]
    [InlineData(nameof(PackmoatOptions.AppAzureDevOpsSchemeOverride))]
    [InlineData(nameof(PackmoatOptions.AzureDevOpsHandlerOverride))]
    [InlineData(nameof(PackmoatOptions.SharedAppTokenOverride))]
    public async Task A_seam_that_is_set_is_named_in_a_warning_at_start(string seam)
    {
        var lines = await StartLogAsync(o => Set(o, seam));
        Assert.Equal($"Warning: Test seam set: {seam}. A test seam replaces a real part of the server and is never set in production.", Assert.Single(lines));
    }

    [Fact]
    public async Task With_no_seam_set_as_in_production_the_start_has_no_warning_and_the_seams_stay_null()
    {
        PackmoatOptions? built = null;
        var lines = await StartLogAsync(null, app => built = app.Services.GetRequiredService<PackmoatOptions>());
        Assert.Empty(lines);
        Assert.Empty(PackmoatApp.SetTestSeams(built!));   // the options the start built (it reads the environment, which has no seam variable) set none
    }

    /// <summary>Builds a server with a database and returns the start's lines in the test seams' log category.</summary>
    private static async Task<List<string>> StartLogAsync(Action<PackmoatOptions>? configure, Action<Microsoft.AspNetCore.Builder.WebApplication>? inspect = null)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-seams-").FullName;
        try
        {
            var logs = new Lines();
            await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, s => s.AddSingleton<ILoggerProvider>(logs), o =>
            {
                o.DatabasePath = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
                configure?.Invoke(o);
            });
            inspect?.Invoke(app);
            lock (logs.All) return logs.All.Where(l => l.Category == PackmoatApp.TestSeamCategory).Select(l => $"{l.Level}: {l.Message}").ToList();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

    private sealed class Lines : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> All { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }

        private sealed class L(Lines owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.All) owner.All.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }
}
