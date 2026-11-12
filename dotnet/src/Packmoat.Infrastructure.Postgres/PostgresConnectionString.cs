using System.Data.Common;
using System.Text.RegularExpressions;
using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// PACKMOAT_DB as a PostgreSQL connection string, checked once, before anything connects. It never shows the value: a malformed
/// string stops with a <see cref="SettingsException"/> that names PACKMOAT_DB and nothing else (no inner exception, since Npgsql's own
/// message can quote what it could not read), and <see cref="ToString"/> is <see cref="Describe"/>, so the password cannot reach a log by
/// accident. The options that would put a password, a parameter or a row's values into a message or a log are refused.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed partial class PostgresConnectionString
{
    /// <summary>The setting this connection string comes from.</summary>
    public const string Variable = "PACKMOAT_DB";

    private readonly string value;

    private PostgresConnectionString(NpgsqlConnectionStringBuilder builder, int? configuredMaxPoolSize = null)
    {
        value = builder.ConnectionString;
        Host = builder.Host!;
        Port = builder.Port;
        Database = builder.Database!;
        Username = builder.Username!;
        MaxPoolSize = builder.MaxPoolSize;
        ConfiguredMaxPoolSize = configuredMaxPoolSize ?? builder.MaxPoolSize;
        SslMode = builder.SslMode;
    }

    /// <summary>The server's host name or address.</summary>
    public string Host { get; }

    /// <summary>The server's port.</summary>
    public int Port { get; }

    /// <summary>The database on that server.</summary>
    public string Database { get; }

    /// <summary>The role the server signs in as.</summary>
    public string Username { get; }

    /// <summary>The most connections the pool opens (Maximum Pool Size; Npgsql's default is 100), for the start's pool-size warning.</summary>
    public int MaxPoolSize { get; }

    /// <summary>PACKMOAT_DB's own Maximum Pool Size: <see cref="MaxPoolSize"/>, unless <see cref="WithPoolAtMost"/> capped this process's pool.</summary>
    public int ConfiguredMaxPoolSize { get; }

    /// <summary>
    /// Whether this process's pool is smaller than PACKMOAT_DB's Maximum Pool Size (<see cref="WithPoolAtMost"/>, the command line's).
    /// The start's pool-size warning is about the server's pool, so a process that capped its own skips it.
    /// </summary>
    public bool PoolCapped => MaxPoolSize < ConfiguredMaxPoolSize;

    /// <summary>
    /// This connection string with its pool at most <paramref name="most"/> connections (Minimum Pool Size lowered to match, if it was higher),
    /// or this one when its Maximum Pool Size is already no larger. For the command line: one operator's command needs a few
    /// connections, not the server's whole pool, while the server may be running on the same database. A pool under 2 stays as it is, so
    /// the start still refuses it.
    /// </summary>
    public PostgresConnectionString WithPoolAtMost(int most)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(most, 2);
        if (MaxPoolSize <= most) return this;
        var builder = new NpgsqlConnectionStringBuilder(value) { MaxPoolSize = most };
        if (builder.MinPoolSize > most) builder.MinPoolSize = most;
        return new PostgresConnectionString(builder, ConfiguredMaxPoolSize);
    }

    /// <summary>The SSL Mode, for the start's TLS check (<see cref="PostgresStartupChecks"/>).</summary>
    internal SslMode SslMode { get; }

    /// <summary>
    /// Where this connects, for a message or a log: "host:port/database as user", never the password. A host list gives each host its own
    /// port, or Port when it names none: "a:5432,b:5433/database as user".
    /// </summary>
    public string Describe() => $"{string.Join(",", Host.Split(',').Select(h => WithPort(h.Trim(), Port)))}/{Database} as {Username}";

    /// <summary><paramref name="host"/> as host:port: kept when it names its own port (b:5433, [::1]:5433); an IPv6 address in brackets.</summary>
    private static string WithPort(string host, int port)
    {
        if (host.StartsWith('[')) return host.Contains("]:", StringComparison.Ordinal) ? host : $"{host}:{port}";
        var colons = host.Count(c => c == ':');
        if (colons > 1) return $"[{host}]:{port}";   // a bare IPv6 address
        return colons == 1 ? host : $"{host}:{port}";
    }

    /// <summary><see cref="Describe"/>: the value itself is never a string anywhere but Npgsql.</summary>
    public override string ToString() => Describe();

    /// <summary>The full value, for Npgsql only.</summary>
    internal string ForNpgsql => value;

    /// <summary>
    /// <paramref name="text"/> (PACKMOAT_DB's value) as a connection string. Unset or blank, malformed, missing its host, database or user,
    /// or setting a refused option each throw a <see cref="SettingsException"/> naming PACKMOAT_DB and never the value.
    /// </summary>
    public static PostgresConnectionString Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw SettingsException.Unset(Variable);
        if (IsUri(text)) throw new SettingsException($"{Variable} must be Key=Value pairs separated by ;, such as Host=…;Database=…;Username=…, not a postgresql:// address.");
        var builder = Malformed(() => new NpgsqlConnectionStringBuilder(text), UnknownOption(text));

        if (builder.PersistSecurityInfo) throw Refused("Persist Security Info", "it keeps the password readable on an open connection");
        if (builder.IncludeErrorDetail) throw Refused("Include Error Detail", "it puts row values into error messages");
        if (builder.LogParameters) throw Refused("Log Parameters", "it writes parameter values to the log");
        if (builder.IncludeFailedBatchedCommand) throw Refused("Include Failed Batched Command", "it puts a failed statement's parameter values into the error");
        if (builder.NoResetOnClose) throw Refused("No Reset On Close", "a pooled connection would keep one user's session settings for the next");
        // Multiplexing sends many commands over one shared connection, so a session lock is not its holder's alone (and
        // the start's pooler check, which opens a connection outside the pool, could not open one).
        if (builder.Multiplexing) throw Refused("Multiplexing", "it shares one connection's session among many commands, and Packmoat's session locks need a session of their own");
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) || string.IsNullOrWhiteSpace(builder.Username))
            throw new SettingsException($"{Variable} must name the Host, the Database and the Username.");

        // GSS encryption: off unless asked for by name. Npgsql's default, Prefer, tries it on every new connection, and on a
        // machine without the Kerberos library (the images) each try writes an error line to the console; the start's TLS check does not
        // count GSS encryption anyway (PostgresStartupChecks.Tls). Require, asked for by name, is kept.
        if (builder.GssEncryptionMode == GssEncryptionMode.Prefer) builder.GssEncryptionMode = GssEncryptionMode.Disable;
        // Npgsql checks some values only when a data source is built (a host list with a bad port, say): build one, connect nothing.
        Malformed(() => { new NpgsqlDataSourceBuilder(builder.ConnectionString).Build().Dispose(); return 0; });
        return new PostgresConnectionString(builder);
    }

    /// <summary>
    /// <paramref name="read"/>'s result; anything it throws becomes "PACKMOAT_DB is not a valid PostgreSQL connection string.", naming
    /// <paramref name="unknownOption"/> when the string has an option Npgsql does not know (its name only, never its value).
    /// </summary>
    private static T Malformed<T>(Func<T> read, string? unknownOption = null)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            // Never the caught exception as the inner one: its message can quote the value, password included.
            throw new SettingsException(unknownOption is null
                ? $"{Variable} is not a valid PostgreSQL connection string."
                : $"{Variable} is not a valid PostgreSQL connection string: it has an option Npgsql does not know, \"{unknownOption}\"{Hint(unknownOption)}.");
        }
    }

    /// <summary>A postgres:// or postgresql:// address (a provider's usual form), which Npgsql does not read: it takes Key=Value pairs only.</summary>
    private static bool IsUri(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first option name in <paramref name="text"/> that Npgsql does not know (for example "Max Pool Size",
    /// which is not Npgsql's name), when it is a slip for one Npgsql knows; otherwise null, and null when the string does not parse at all.
    /// What stands where a name should be can be part of a password (an unquoted ; splits "Password=correct horse;battery staple" into a
    /// second pair), and no shape tells the two apart. So a name is given only when it looks like one (letters, digits and
    /// spaces, at most 40) AND is the Max/Min Pool Size slip or within <see cref="MaxSlip"/> edits of a name Npgsql knows (one edit of a
    /// known name shorter than <see cref="MinLengthForMaxSlip"/>; the unknown one at least <see cref="MinSlipLength"/> long): a typo, not a
    /// secret.
    /// </summary>
    private static string? UnknownOption(string text)
    {
        DbConnectionStringBuilder parsed;
        try
        {
            parsed = new DbConnectionStringBuilder { ConnectionString = text };
        }
        catch (ArgumentException)
        {
            return null;
        }
        var known = new NpgsqlConnectionStringBuilder();
        var keys = parsed.Keys.Cast<string>().ToList();
        var at = keys.FindIndex(k => !known.ContainsKey(k));
        if (at < 0) return null;
        var unknown = keys[at];
        // The pair straight after the password's is where an unquoted ; in a password lands, so never named, slip or not.
        if (at > 0 && PasswordKeys.Contains(keys[at - 1].Trim())) return null;
        return OptionName().IsMatch(unknown) && IsSlip(unknown.Trim()) ? unknown.Trim() : null;
    }

    /// <summary>The names Npgsql reads a password under.</summary>
    private static readonly HashSet<string> PasswordKeys = new(["Password", "PSW", "PWD"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The most edits (insert, delete, change one character) between an unknown name and a known one for it to be named.</summary>
    internal const int MaxSlip = 2;

    /// <summary>
    /// The shortest known name an unknown one may be <see cref="MaxSlip"/> edits from two edits from a
    /// name of 4 or 5 letters (Host, Port) reach ordinary words ("horse", "spot"), so a shorter known name allows one edit only.
    /// </summary>
    internal const int MinLengthForMaxSlip = 7;

    /// <summary>The shortest unknown name that may be named as a slip: a shorter one is too close to too much.</summary>
    internal const int MinSlipLength = 4;

    /// <summary>Whether <paramref name="option"/> is the Max/Min Pool Size slip, or close to a name Npgsql knows (<see cref="KnownNames"/>).</summary>
    internal static bool IsSlip(string option)
    {
        if (Hint(option).Length > 0) return true;
        if (option.Length < MinSlipLength) return false;
        var lower = option.ToLowerInvariant();
        return KnownNames.Value.Any(n => Distance(lower, n) <= (n.Length >= MinLengthForMaxSlip ? MaxSlip : 1));
    }

    /// <summary>Every option name Npgsql knows, in lower case: each property's display name (Maximum Pool Size, SSL Mode, …).</summary>
    private static readonly Lazy<string[]> KnownNames = new(() => typeof(NpgsqlConnectionStringBuilder).GetProperties()
        .Select(p => p.GetCustomAttributes(typeof(System.ComponentModel.DisplayNameAttribute), true).OfType<System.ComponentModel.DisplayNameAttribute>().FirstOrDefault()?.DisplayName)
        .OfType<string>().Where(n => n.Length > 0).Select(n => n.ToLowerInvariant()).Distinct().ToArray());

    /// <summary>The edit distance between <paramref name="a"/> and <paramref name="b"/> (Levenshtein).</summary>
    internal static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }

    /// <summary>For a test: how many names <see cref="KnownNames"/> holds, and whether it holds <paramref name="name"/>.</summary>
    internal static (int Count, bool Has) KnownName(string name) => (KnownNames.Value.Length, KnownNames.Value.Contains(name.ToLowerInvariant()));

    private static string Hint(string option) => option.Trim().ToUpperInvariant() switch
    {
        "MAX POOL SIZE" => " (the pool's largest size is Maximum Pool Size)",
        "MIN POOL SIZE" => " (the pool's smallest size is Minimum Pool Size)",
        _ => "",
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9 ]{0,39}$")]
    private static partial Regex OptionName();

    private static SettingsException Refused(string option, string why) => new($"{Variable} must not turn on {option}: {why}.");
}
