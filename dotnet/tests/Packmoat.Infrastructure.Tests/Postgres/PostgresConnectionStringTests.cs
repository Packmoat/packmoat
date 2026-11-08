using System.Collections;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// PACKMOAT_DB as a PostgreSQL connection string (#288), without a server: the value never reaches a message, a ToString() or an inner
/// exception; the options that would leak a password or a value are refused; and an older server is refused. The tests that need a server
/// are in <see cref="PostgresServerTests"/>.
/// </summary>
public class PostgresConnectionStringTests
{
    /// <summary>A password no message could hold by chance: if one shows it, it came from the connection string.</summary>
    internal static string Sentinel() => $"pm-sentinel-{Guid.NewGuid():N}";

    /// <summary>The exception, its inner exceptions (an aggregate's too) and so on down.</summary>
    internal static IEnumerable<Exception> Chain(Exception e)
    {
        var pending = new Stack<Exception>([e]);
        while (pending.TryPop(out var x))
        {
            yield return x;
            if (x is AggregateException a) foreach (var i in a.InnerExceptions) pending.Push(i);
            else if (x.InnerException is { } inner) pending.Push(inner);
        }
    }

    /// <summary>No exception in <paramref name="e"/>'s chain holds the sentinel or the whole string: message, ToString() or Data.</summary>
    internal static void AssertRedacted(Exception e, string sentinel, string whole)
    {
        foreach (var x in Chain(e))
        {
            foreach (var text in new[] { x.Message, x.ToString() }.Concat(x.Data.Cast<DictionaryEntry>().Select(d => $"{d.Key}={d.Value}")))
            {
                Assert.False(text.Contains(sentinel, StringComparison.Ordinal), $"{x.GetType().Name} shows the password: {x.Message}");
                Assert.False(text.Contains(whole, StringComparison.Ordinal), $"{x.GetType().Name} shows the connection string");
            }
        }
    }

    // mutation: pass the caught exception through as the inner one (the bad port, the first case, then shows the password in it)
    [Theory]
    [InlineData("Host=localhost;Port={0};Database=packmoat;Username=packmoat;Password={0}")]   // a port that is not a number
    [InlineData("Host=localhost;Database=packmoat;Username=packmoat;Password='{0}")]           // an unclosed quote
    [InlineData("Host=localhost;Database=packmoat;Username=packmoat;Pasword={0}")]             // an unknown keyword
    [InlineData("Host=localhost;Database=packmoat;Username=packmoat;{0}")]                     // no keyword at all
    [InlineData("Host=localhost;Database=packmoat;Username=packmoat;Password={0};SSL Mode={0}")] // a value not in an enumeration
    [InlineData("{0}")]
    // a host's own port that is not a number: only building the data source sees it (mutation: drop that check in Parse)
    [InlineData("Host=localhost:{0};Database=packmoat;Username=packmoat;Password={0}")]
    public void A_malformed_string_names_PACKMOAT_DB_only_and_never_its_value(string template)
    {
        var sentinel = Sentinel();
        var whole = string.Format(template, sentinel);
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(whole));
        Assert.Equal("PACKMOAT_DB is not a valid PostgreSQL connection string.", e.Message);
        Assert.Null(e.InnerException);
        AssertRedacted(e, sentinel, whole);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_is_PACKMOAT_DB_is_not_set(string? value)
    {
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(value));
        Assert.Equal("PACKMOAT_DB is not set.", e.Message);
        Assert.True(e.NotSet);
    }

    // mutation: drop one of the refusals
    [Theory]
    [InlineData("Persist Security Info=true", "Persist Security Info")]
    [InlineData("Include Error Detail=true", "Include Error Detail")]
    [InlineData("Log Parameters=true", "Log Parameters")]
    [InlineData("PersistSecurityInfo=True", "Persist Security Info")]   // the same options under Npgsql's other spellings
    [InlineData("include error detail=TRUE", "Include Error Detail")]
    [InlineData("Include Failed Batched Command=true", "Include Failed Batched Command")]
    [InlineData("No Reset On Close=true", "No Reset On Close")]
    public void The_options_that_leak_a_password_or_a_value_are_refused(string option, string named)
    {
        var sentinel = Sentinel();
        var whole = $"Host=db.internal;Database=packmoat;Username=packmoat;Password={sentinel};{option}";
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(whole));
        Assert.StartsWith($"PACKMOAT_DB must not turn on {named}: ", e.Message);
        AssertRedacted(e, sentinel, whole);
    }

    [Theory]
    [InlineData("Persist Security Info=false")]
    [InlineData("Include Error Detail=false")]
    [InlineData("Log Parameters=false")]
    [InlineData("No Reset On Close=false")]
    public void The_same_options_turned_off_are_accepted(string option) =>
        PostgresConnectionString.Parse($"Host=db.internal;Database=packmoat;Username=packmoat;Password=x;{option}");

    [Theory]
    [InlineData("Database=packmoat;Username=packmoat;Password=x")]
    [InlineData("Host=db.internal;Username=packmoat;Password=x")]
    [InlineData("Host=db.internal;Database=packmoat;Password=x")]
    public void The_host_the_database_and_the_user_are_required(string value)
    {
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(value));
        Assert.Equal("PACKMOAT_DB must name the Host, the Database and the Username.", e.Message);
    }

    // mutations: Describe() or ToString() returns the value
    [Fact]
    public void Describe_and_ToString_show_where_and_who_and_never_the_password()
    {
        var sentinel = Sentinel();
        var parsed = PostgresConnectionString.Parse($"Host=db.internal;Port=6543;Database=packmoat;Username=pm_app;Password={sentinel}");
        Assert.Equal("db.internal:6543/packmoat as pm_app", parsed.Describe());
        Assert.Equal(parsed.Describe(), parsed.ToString());
        Assert.Equal(parsed.Describe(), $"{parsed}");
        Assert.Equal(("db.internal", 6543, "packmoat", "pm_app"), (parsed.Host, parsed.Port, parsed.Database, parsed.Username));
        Assert.Contains(sentinel, parsed.ForNpgsql);   // Npgsql still gets it
    }

    // mutation (review): Describe() as "{Host}:{Port}" again (a,b:5433 then reads a,b:5433:5432)
    [Theory]
    [InlineData("a,b:5433", "a:5432,b:5433")]
    [InlineData("a:6000", "a:6000")]
    [InlineData("a, b", "a:5432,b:5432")]
    [InlineData("[::1]:5433,[::1]", "[::1]:5433,[::1]:5432")]
    [InlineData("::1", "[::1]:5432")]
    public void Describe_gives_each_host_in_a_list_its_own_port(string host, string where) =>
        Assert.Equal($"{where}/packmoat as pm_app", PostgresConnectionString.Parse($"Host={host};Database=packmoat;Username=pm_app;Password=x").Describe());

    [Fact]
    public async Task The_database_describes_itself_the_same_way_and_is_the_postgres_engine()
    {
        var sentinel = Sentinel();
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse($"Host=db.internal;Database=packmoat;Username=pm_app;Password={sentinel}"));
        Assert.Equal("db.internal:5432/packmoat as pm_app", db.Describe());
        Assert.Equal(DatabaseEngine.Postgres, db.Engine);
        Assert.IsAssignableFrom<IRelationalDatabase>(db);
    }

    // An unreachable host needs no server: port 1 on the loopback refuses at once.
    [Fact]
    public async Task An_unreachable_host_fails_without_showing_the_password_or_the_string()
    {
        var sentinel = Sentinel();
        var whole = $"Host=127.0.0.1;Port=1;Database=packmoat;Username=packmoat;Password={sentinel};Timeout=3";
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse(whole));
        var e = await Assert.ThrowsAnyAsync<Exception>(() => db.OpenAsync());
        AssertRedacted(e, sentinel, whole);
    }

    // mutation: < becomes <= (17 refused) or the check is dropped (16 accepted)
    [Theory]
    [InlineData("9.6", false)]
    [InlineData("16.9", false)]
    [InlineData("17.0", true)]
    [InlineData("17.11", true)]
    [InlineData("18.1", true)]
    public void A_server_older_than_the_supported_major_is_refused(string version, bool accepted)
    {
        void Check() => PostgresDatabase.RequireSupported(Version.Parse(version), "db.internal:5432/packmoat as pm_app");
        if (accepted) { Check(); return; }
        var e = Assert.Throws<NotSupportedException>(Check);
        Assert.Equal($"The PostgreSQL server at db.internal:5432/packmoat as pm_app is version {version}; Packmoat needs PostgreSQL 17 or newer.", e.Message);
    }

    [Fact]
    public void The_supported_major_is_17() => Assert.Equal(17, PostgresDatabase.SupportedMajorVersion);

    // mutations: skip in CI too; run (rather than skip) when unset locally
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "false", true)]
    [InlineData(null, "true", false)]       // CI: runs, and fails for want of a server
    [InlineData(null, "TRUE", false)]
    [InlineData("Host=x", null, false)]
    [InlineData("Host=x", "true", false)]
    public void A_postgres_test_skips_only_when_the_variable_is_unset_outside_CI(string? value, string? ci, bool skips)
    {
        var reason = PostgresTestServer.SkipReason(value, ci);
        Assert.Equal(skips, reason is not null);
        if (skips) Assert.Contains("PACKMOAT_TEST_POSTGRES", reason);
    }
}
