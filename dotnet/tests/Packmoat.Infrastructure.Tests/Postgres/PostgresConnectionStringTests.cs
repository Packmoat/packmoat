using System.Collections;
using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// PACKMOAT_DB as a PostgreSQL connection string, without a server: the value never reaches a message, a ToString() or an inner
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
    // An option (for example "Max Pool Size", which is not Npgsql's name): Npgsql does not know is named (in lower case, as parsed), with a
    // hint for the usual slips, and its value never shown. mutations: the option's value in the message; no name given; the hint dropped
    [Theory]
    [InlineData("Host=h;Max Pool Size=10;Database=d;Username=u;Password={0}", "max pool size", " (the pool's largest size is Maximum Pool Size)")]
    [InlineData("Host=h;Database=d;min pool size=1;Username=u;Password={0}", "min pool size", " (the pool's smallest size is Minimum Pool Size)")]
    [InlineData("Host=h;Database=d;Username=u;Pasword={0}", "pasword", "")]
    public void An_unknown_option_is_named_and_its_value_never_shown(string template, string option, string hint)
    {
        var sentinel = Sentinel();
        var whole = string.Format(template, sentinel);
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(whole));
        Assert.Equal($"PACKMOAT_DB is not a valid PostgreSQL connection string: it has an option Npgsql does not know, \"{option}\"{hint}.", e.Message);
        Assert.Null(e.InnerException);
        AssertRedacted(e, sentinel, whole);
    }

    // An unquoted ; in a password splits it, and what follows stands where an option's name should be. Only a slip for a
    // name Npgsql knows (within 2 edits, or Max/Min Pool Size) is ever named; anything else, a password's words included, gets the plain
    // message and is never shown. mutation: the shape guard alone (any name-like text named)
    [Theory]
    [InlineData("Host=h;Database=d;Username=u;Password=correct horse;battery staple=1", "battery staple")]
    [InlineData("Host=h;Database=d;Username=u;Password=abc;Secret99=x", "secret99")]
    [InlineData("Host=h;Database=d;Username=u;Password=abc;Pot=x", "pot")]   // close to Port, but too short to name
    [InlineData("Host=h;horse=x;Database=d;Username=u;Password=abc", "horse")]   // 2 edits from Host: too short a known name
    // Straight after the password's pair, nothing is named, a slip or the pool-size hint included.
    // mutation: the password-neighbour rule removed
    [InlineData("Host=h;Database=d;Username=u;Password=abc;Pasword=x", "pasword")]
    [InlineData("Host=h;Database=d;Username=u;Password=abc;Max Pool Size=10", "max pool size")]
    [InlineData("Host=h;Database=d;Username=u;PWD=abc;timeot=1", "timeot")]
    public void Part_of_a_password_standing_as_a_name_is_never_shown(string whole, string fragment)
    {
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(whole));
        Assert.Equal("PACKMOAT_DB is not a valid PostgreSQL connection string.", e.Message);
        Assert.DoesNotContain(fragment, e.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(e.InnerException);
    }

    [Fact]   // the same with a letters-only sentinel, which the shape guard alone would name. mutation: as above
    public void A_letters_only_sentinel_standing_as_a_name_is_never_shown()
    {
        var sentinel = "pms" + new string(Guid.NewGuid().ToString("N")[..16].Select(c => (char)('a' + c % 26)).ToArray());   // 19 letters: a name by shape
        var whole = $"Host=h;Database=d;Username=u;Password=x;{sentinel}=1";
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(whole));
        Assert.Equal("PACKMOAT_DB is not a valid PostgreSQL connection string.", e.Message);
        AssertRedacted(e, sentinel, whole);
    }

    [Theory]   // the slip rule itself, and that Npgsql's own names were found (a reflection that found none would name nothing)
    [InlineData("pasword", true)]          // Password, 1 edit
    [InlineData("sslmod", true)]           // SSL Mode, 2 edits
    [InlineData("databse", true)]          // Database, 1 edit
    [InlineData("max pool size", true)]    // the hint case, 4 edits from Maximum Pool Size
    [InlineData("min pool size", true)]
    [InlineData("battery staple", false)]
    [InlineData("secret99", false)]
    [InlineData("pot", false)]             // too short
    [InlineData("hunter", false)]
    // 2 edits only from a known name of 7 or more characters, 1 from a shorter one
    [InlineData("horse", false)]           // Host, 2 edits
    [InlineData("spot", false)]            // Port, 2 edits
    [InlineData("hosts", true)]            // Host, 1 edit
    [InlineData("tmeot", true)]            // Timeout (7), 2 edits
    public void Only_a_slip_for_a_known_name_is_named(string option, bool named)
    {
        Assert.Equal(named, PostgresConnectionString.IsSlip(option));
        var (count, has) = PostgresConnectionString.KnownName("Maximum Pool Size");
        Assert.True(count > 40, $"Npgsql's names: {count}");
        Assert.True(has);
        Assert.True(PostgresConnectionString.KnownName("SSL Mode").Has);
        Assert.Equal(2, PostgresConnectionString.Distance("sslmod", "ssl mode"));
    }

    // A provider's postgresql:// address, which Npgsql does not read, is refused as such, and never shown (its password is
    // inside it). mutation: the address parsed as Key=Value, naming "postgresql://user:password@…" as an unknown option
    [Theory]
    [InlineData("postgresql://packmoat_app:{0}@db.example.com:5432/packmoat?sslmode=require")]
    [InlineData("  postgres://packmoat_app:{0}@db.example.com/packmoat")]
    public void A_postgres_address_is_refused_as_not_key_value_pairs_and_never_shown(string template)
    {
        var sentinel = Sentinel();
        var whole = string.Format(template, sentinel);
        var e = Assert.Throws<SettingsException>(() => PostgresConnectionString.Parse(whole));
        Assert.StartsWith("PACKMOAT_DB must be Key=Value pairs separated by ;", e.Message);
        AssertRedacted(e, sentinel, whole);
    }

    [Theory]
    [InlineData("Host=localhost;Port={0};Database=packmoat;Username=packmoat;Password={0}")]   // a port that is not a number
    [InlineData("Host=localhost;Database=packmoat;Username=packmoat;Password='{0}")]           // an unclosed quote
    [InlineData("Host=localhost;Database=packmoat;Username=packmoat;{0}=x")]                   // an unknown keyword that is no name
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
    [InlineData("Multiplexing=true", "Multiplexing")]   // A session lock needs a session of its own
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
    [InlineData("Multiplexing=false")]
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

    // The PgBouncer test runs whenever CI=true (CI has the service), and locally only with both variables set.
    // mutations: skip in CI when unset; run locally with only one variable
    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("Host=b", null, null, true)]
    [InlineData(null, "Host=p", "false", true)]
    [InlineData("Host=b", "Host=p", null, false)]
    [InlineData(null, null, "true", false)]
    public void The_pgbouncer_test_skips_only_outside_CI_without_both_variables(string? value, string? postgres, string? ci, bool skips)
    {
        var reason = PgBouncerFactAttribute.SkipReason(value, postgres, ci);
        Assert.Equal(skips, reason is not null);
        if (skips) Assert.Contains("PACKMOAT_TEST_PGBOUNCER", reason);
    }

    // The images have no Kerberos library, and Npgsql's default (Prefer) tries GSS encryption on every new connection, writing
    // an error line to the console each time. mutation: the default left as Prefer
    [Theory]
    [InlineData("Host=h;Database=d;Username=u", GssEncryptionMode.Disable)]
    [InlineData("Host=h;Database=d;Username=u;Gss Encryption Mode=Prefer", GssEncryptionMode.Disable)]
    [InlineData("Host=h;Database=d;Username=u;Gss Encryption Mode=Disable", GssEncryptionMode.Disable)]
    [InlineData("Host=h;Database=d;Username=u;Gss Encryption Mode=Require", GssEncryptionMode.Require)]
    public void GSS_encryption_is_off_unless_required_by_name(string text, GssEncryptionMode expected) =>
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(PostgresConnectionString.Parse(text).ForNpgsql).GssEncryptionMode);

    // The command line's own pool is PACKMOAT_DB's Maximum Pool Size capped at 5; a pool already that small (under 2 included,
    // which the start refuses) is left as it is, and only a pool the cap lowered counts as capped. mutations: the cap not applied; a pool
    // the cap did not lower counted as capped; a pool under 2 raised
    [Theory]
    [InlineData(null, 5, 100, true)]   // Npgsql's default, 100
    [InlineData(1000, 5, 1000, true)]
    [InlineData(6, 5, 6, true)]
    [InlineData(5, 5, 5, false)]
    [InlineData(3, 3, 3, false)]
    [InlineData(1, 1, 1, false)]
    public void The_pool_is_capped_and_only_a_lowered_pool_counts_as_capped(int? configured, int max, int kept, bool capped)
    {
        var text = "Host=h;Database=d;Username=u;Password=p" + (configured is { } c ? $";Maximum Pool Size={c}" : "");
        var cs = PostgresConnectionString.Parse(text);
        Assert.False(cs.PoolCapped);
        var at = cs.WithPoolAtMost(5);
        Assert.Equal(max, at.MaxPoolSize);
        Assert.Equal(kept, at.ConfiguredMaxPoolSize);
        Assert.Equal(capped, at.PoolCapped);
        var builder = new NpgsqlConnectionStringBuilder(at.ForNpgsql);
        Assert.Equal(max, builder.MaxPoolSize);
        Assert.Equal("p", builder.Password);
        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
        Assert.Equal(cs.Describe(), at.Describe());
    }

    [Fact]   // a Minimum Pool Size above the cap is lowered with it, or Npgsql would refuse the pool. mutation: Minimum Pool Size left above the cap
    public void A_min_pool_size_above_the_cap_is_lowered_to_it()
    {
        var at = PostgresConnectionString.Parse("Host=h;Database=d;Username=u;Minimum Pool Size=20;Maximum Pool Size=50").WithPoolAtMost(5);
        var builder = new NpgsqlConnectionStringBuilder(at.ForNpgsql);
        Assert.Equal(5, builder.MinPoolSize);
        Assert.Equal(5, builder.MaxPoolSize);
        new NpgsqlDataSourceBuilder(at.ForNpgsql).Build().Dispose();   // connects nothing; throws on a min above the max
    }

    [Fact]   // a cap under 2 would make the command's own pool one the start refuses
    public void A_cap_under_two_is_an_argument_error() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresConnectionString.Parse("Host=h;Database=d;Username=u").WithPoolAtMost(1));
}
