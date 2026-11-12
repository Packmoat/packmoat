using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The self-hosting deploy kit in deploy/: rules that are cheap to check and costly to get wrong. Ported from the hosted
/// deploy files' tests, and run on the core's own copies.
/// </summary>
public class DeployKitTests
{
    private static string Deploy(string name) => CoreRoot.Read("deploy", name);

    [Fact]   // mutation: give the caddy service env_file: /etc/packmoat/packmoat.env
    public void Caddy_never_reads_the_file_that_holds_the_master_key()
    {
        var compose = Deploy("docker-compose.yml");
        var caddy = Regex.Match(compose, @"^  caddy:.*?(?=^  \S|^\S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Value;
        Assert.Contains("env_file: /etc/packmoat/caddy.env", caddy);
        Assert.DoesNotContain("packmoat.env", caddy);
    }

    [Fact]   // mutation: --env-file in restore.sh
    public void Restore_passes_the_container_only_the_master_key_by_name()
    {
        var restore = Deploy("restore.sh");
        Assert.DoesNotContain("--env-file", restore);
        Assert.Contains("-e PACKMOAT_MASTER_KEY ", restore);
    }

    [Fact]   // mutations: drop the trap; copy the plain file instead of encrypting it; drop umask 077 (from either script)
    public void Backup_encrypts_privately_cleans_up_on_any_exit_and_validates_the_recipient()
    {
        var backup = Deploy("backup.sh");
        Assert.Contains("\ntrap cleanup EXIT\n", backup);
        Assert.Contains("age1", backup);
        // The one line that makes the off-box copy: age, to the recipient, from the plain copy into the partial file.
        Assert.Contains("\nage -r \"${BACKUP_AGE_RECIPIENT}\" -o \"${partial}\" \"${plain}\"\n", backup);
        Assert.Contains("\nmv \"${partial}\" \"${plain}.age\"\n", backup);
        Assert.Contains("\nrclone copyto \"${plain}.age\" ", backup);
        Assert.Contains("\numask 077\n", backup);
        Assert.Contains("\numask 077\n", Deploy("restore.sh"));
    }

    [Fact]   // mutation: drop a digest from the caddy image
    public void Every_fixed_image_is_pinned_by_digest()
    {
        var refs = Regex.Matches(Deploy("docker-compose.yml"), @"^\s*image:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
            .Where(r => !r.StartsWith("${", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(refs);
        Assert.All(refs, r => Assert.Matches(@"@sha256:[0-9a-f]{64}$", r));
    }

    /// <summary>Each service of the compose file: its name and its lines.</summary>
    internal static Dictionary<string, string> Services(string compose)
    {
        var services = Regex.Match(compose, @"(?ms)^services:\n(.*?)(?=^\S|\z)").Groups[1].Value;
        return Regex.Matches(services, @"(?ms)^  ([A-Za-z0-9_-]+):\n(.*?)(?=^  \S|\z)").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    [Fact]   // mutation: drop init: true
    public void The_app_runs_under_an_init_so_signals_reach_it_as_they_would_any_other_process()
    {
        // A container's first process gets no default signal handling. The server exits 1 on its own at start (StartupFailure);
        // the init is the second layer, for anything that would still end in an abort.
        Assert.Contains("    init: true\n", Services(Deploy("docker-compose.yml"))["packmoat"]);
    }

    [Fact]   // mutations: drop cap_drop or no-new-privileges from one service; add an environment: block or an ASPNETCORE_URLS override
    public void The_compose_file_runs_the_local_image_unprivileged_with_no_published_ports()
    {
        var compose = Deploy("docker-compose.yml");
        var services = Services(compose);
        Assert.Equal(new[] { "caddy", "packmoat" }, services.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("image: ${PACKMOAT_IMAGE:-packmoat:local}", services["packmoat"]);
        Assert.Contains("user: \"10001:10001\"", services["packmoat"]);
        Assert.Contains("read_only: true", services["packmoat"]);
        foreach (var (name, body) in services)
        {
            Assert.True(body.Contains("    cap_drop: [ALL]\n", StringComparison.Ordinal), $"{name}: cap_drop [ALL] is missing");
            Assert.True(body.Contains("    security_opt: [\"no-new-privileges:true\"]\n", StringComparison.Ordinal), $"{name}: no-new-privileges is missing");
            Assert.True(body.Contains("    network_mode: host\n", StringComparison.Ordinal), $"{name}: not on host networking");
        }
        // Settings come only from the env files: no inline environment, and nothing that moves the app off 127.0.0.1:8080.
        Assert.DoesNotMatch(@"(?m)^\s*environment:", compose);
        Assert.DoesNotContain("ASPNETCORE_URLS", compose);
        Assert.DoesNotContain("privileged", compose);
        Assert.DoesNotContain("docker.sock", compose);
        Assert.DoesNotContain("ports:", compose);
    }

    [Fact]   // mutation: remove "admin off" (or move it into a comment)
    public void Caddy_has_no_admin_api()
    {
        var global = Assert.Single(Blocks(Deploy("Caddyfile")), b => b.Address.Length == 0).Body;
        Assert.Matches(@"(?m)^\tadmin off$", global);
    }

    /// <summary>Each top-level block of a Caddyfile, comments removed: its address ("" for the global options) and its body.</summary>
    internal static List<(string Address, string Body)> Blocks(string caddyfile)
    {
        var blocks = new List<(string, string)>();
        var depth = 0;
        string? address = null;
        var body = new System.Text.StringBuilder();
        foreach (var raw in caddyfile.Split('\n'))
        {
            var line = Regex.Replace(raw, @"#.*$", "").TrimEnd();
            if (depth == 0 && line.Length > 0)
            {
                Assert.EndsWith("{", line);
                address = line[..^1].Trim();
                body.Clear();
            }
            else if (depth > 0) body.Append(line).Append('\n');
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
            Assert.True(depth >= 0, "unbalanced braces in the Caddyfile");
            if (depth == 0 && address is not null)
            {
                blocks.Add((address, body.ToString()));
                address = null;
            }
        }
        Assert.Equal(0, depth);
        return blocks;
    }

    private static List<(string Address, string Body)> Sites() => Blocks(Deploy("Caddyfile")).Where(b => b.Address.Length > 0).ToList();

    [Fact]   // mutation: paste the hosted service's extra site block into the Caddyfile
    public void The_Caddyfile_serves_only_the_feed_the_admin_site_and_the_customer_site()
    {
        Assert.Equal(new[] { "{$PACKMOAT_HOSTS}", "{$PACKMOAT_ADMIN_HOST:admin.localhost}", "{$PACKMOAT_APP_HOST:app.localhost}" },
            Sites().Select(s => s.Address));
    }

    [Fact]   // mutations: drop the Authorization delete from one site's log; move it into a comment
    public void Every_site_logs_without_the_Authorization_and_Cookie_headers()
    {
        var sites = Sites();
        Assert.NotEmpty(sites);
        foreach (var (address, body) in sites)
        {
            var log = Regex.Match(body, @"(?m)^\tlog \{\n(.*?)^\t\}", RegexOptions.Singleline);
            Assert.True(log.Success, $"{address}: no log block");
            Assert.Matches(@"\n\t\tformat filter \{", log.Value);
            Assert.True(Regex.IsMatch(log.Value, @"\n\t+request>headers>Authorization delete\n"), $"{address}: the access log keeps the Authorization header");
            Assert.True(Regex.IsMatch(log.Value, @"\n\t+request>headers>Cookie delete\n"), $"{address}: the access log keeps the Cookie header");
        }
    }

    [Theory]   // mutations: drop the Set-Cookie, Location or "delete code" line from a signed-in site's log
    [InlineData("{$PACKMOAT_ADMIN_HOST:admin.localhost}")]
    [InlineData("{$PACKMOAT_APP_HOST:app.localhost}")]
    public void The_signed_in_sites_log_no_session_cookie_redirect_or_sign_in_return_values(string address)
    {
        var body = Assert.Single(Sites(), s => s.Address == address).Body;
        var log = Regex.Match(body, @"(?m)^\tlog \{\n(.*?)^\t\}", RegexOptions.Singleline).Value;
        foreach (var line in new[] { "resp_headers>Set-Cookie delete", "resp_headers>Location delete" })
            Assert.True(Regex.IsMatch(log, @"\n\t+" + Regex.Escape(line) + @"\n"), $"{address}: no '{line}'");
        var query = Regex.Match(log, @"\n\t+request>uri query \{\n(.*?)\n\t+\}", RegexOptions.Singleline);
        Assert.True(query.Success, $"{address}: the query string is logged unfiltered");
        foreach (var name in new[] { "code", "state", "session_state", "id_token", "error_description" })
            Assert.True(Regex.IsMatch(query.Groups[1].Value, @"(?m)^\t+delete " + name + "$"), $"{address}: the query keeps {name}");
    }

    [Fact]
    public void The_block_reader_ignores_comments_and_sees_every_site()
    {
        var blocks = Blocks("{\n\temail {$E}\n}\n\na.example {\n\t# request>headers>Authorization delete\n\tlog {\n\t}\n}\nb.example {\n}\n");
        Assert.Equal(new[] { "", "a.example", "b.example" }, blocks.Select(b => b.Address));
        Assert.DoesNotContain("Authorization", blocks[1].Body);
    }

    [Fact]   // mutation: point the backup unit at another script or settings file
    public void The_backup_timer_runs_the_installed_script_with_its_settings_file()
    {
        var service = CoreRoot.Read("deploy", "systemd", "packmoat-backup.service");
        Assert.Contains("EnvironmentFile=/etc/packmoat/backup.env", service);
        Assert.Contains("ExecStart=/opt/packmoat/deploy/backup.sh", service);
        Assert.Contains("/etc/packmoat/backup.env", Deploy("backup.sh"));
        Assert.Contains("COMPOSE_FILE:-/opt/packmoat/deploy/docker-compose.yml", Deploy("restore.sh"));
        Assert.Contains("OnCalendar=", CoreRoot.Read("deploy", "systemd", "packmoat-backup.timer"));
    }
}
