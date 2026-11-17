using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// A Cloud SQL connection from Cloud Run is a Unix socket under /cloudsql, named by the instance connection name
/// (project:region:instance). Npgsql must keep that whole path as the host (the colons are not a port), and the start's TLS check
/// must treat it as this machine's own socket. These tests document that parsing; the real check is a Cloud Run smoke test.
/// No server, no network.
/// </summary>
public class CloudSqlSocketHostTests
{
    private const string Socket = "/cloudsql/proj:region:inst";

    [Fact]
    public void Npgsql_keeps_the_whole_cloud_sql_socket_path_as_the_host_and_the_default_port()
    {
        var builder = new NpgsqlConnectionStringBuilder($"Host={Socket};Database=packmoat;Username=packmoat_app");
        Assert.Equal(Socket, builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.Equal(Socket, new NpgsqlConnectionStringBuilder(builder.ConnectionString).Host);
    }

    [Fact]
    public void The_start_checks_treat_the_cloud_sql_socket_as_local_so_no_tls_is_demanded()
    {
        var host = new NpgsqlConnectionStringBuilder($"Host={Socket};Database=packmoat").Host!;
        Assert.True(PostgresStartupChecks.IsLocal(host));
        Assert.Null(PostgresStartupChecks.Tls(host, SslMode.Prefer, encrypted: false));
        // The negative control: a remote name is not local, and without TLS it is refused.
        Assert.False(PostgresStartupChecks.IsLocal("db.example.com"));
        Assert.NotNull(PostgresStartupChecks.Tls("db.example.com", SslMode.Prefer, encrypted: false));
    }
}
