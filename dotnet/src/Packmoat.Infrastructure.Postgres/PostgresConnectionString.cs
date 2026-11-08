using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// PACKMOAT_DB as a PostgreSQL connection string (#288), checked once, before anything connects. It never shows the value: a malformed
/// string stops with a <see cref="SettingsException"/> that names PACKMOAT_DB and nothing else (no inner exception, since Npgsql's own
/// message can quote what it could not read), and <see cref="ToString"/> is <see cref="Describe"/>, so the password cannot reach a log by
/// accident. The options that would put a password, a parameter or a row's values into a message or a log are refused.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresConnectionString
{
    /// <summary>The setting this connection string comes from.</summary>
    public const string Variable = "PACKMOAT_DB";

    private readonly string value;

    private PostgresConnectionString(NpgsqlConnectionStringBuilder builder)
    {
        value = builder.ConnectionString;
        Host = builder.Host!;
        Port = builder.Port;
        Database = builder.Database!;
        Username = builder.Username!;
    }

    /// <summary>The server's host name or address.</summary>
    public string Host { get; }

    /// <summary>The server's port.</summary>
    public int Port { get; }

    /// <summary>The database on that server.</summary>
    public string Database { get; }

    /// <summary>The role the server signs in as.</summary>
    public string Username { get; }

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
        var builder = Malformed(() => new NpgsqlConnectionStringBuilder(text));

        if (builder.PersistSecurityInfo) throw Refused("Persist Security Info", "it keeps the password readable on an open connection");
        if (builder.IncludeErrorDetail) throw Refused("Include Error Detail", "it puts row values into error messages");
        if (builder.LogParameters) throw Refused("Log Parameters", "it writes parameter values to the log");
        if (builder.IncludeFailedBatchedCommand) throw Refused("Include Failed Batched Command", "it puts a failed statement's parameter values into the error");
        if (builder.NoResetOnClose) throw Refused("No Reset On Close", "a pooled connection would keep one user's session settings for the next");
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) || string.IsNullOrWhiteSpace(builder.Username))
            throw new SettingsException($"{Variable} must name the Host, the Database and the Username.");

        // Npgsql checks some values only when a data source is built (a host list with a bad port, say): build one, connect nothing.
        Malformed(() => { new NpgsqlDataSourceBuilder(builder.ConnectionString).Build().Dispose(); return 0; });
        return new PostgresConnectionString(builder);
    }

    /// <summary><paramref name="read"/>'s result; anything it throws becomes "PACKMOAT_DB is not a valid PostgreSQL connection string.".</summary>
    private static T Malformed<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            // Never the caught exception as the inner one: its message can quote the value, password included.
            throw new SettingsException($"{Variable} is not a valid PostgreSQL connection string.");
        }
    }

    private static SettingsException Refused(string option, string why) => new($"{Variable} must not turn on {option}: {why}.");
}
