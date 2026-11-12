using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Admin;
using Packmoat.Domain;
using Packmoat.Infrastructure;
using Packmoat.TestSupport;

namespace Packmoat.Host.Tests;

/// <summary>
/// The SQLite single-instance guard: the server, and only the server, holds an operating-system lock on <c>&lt;database&gt;.instance</c>.
/// A second server on the same database waits up to 60 s for the first to let go (a container being recreated), then refuses. The command
/// line and a backup work beside a running server. A server that dies leaves the file but no lock, so the next one starts.
/// </summary>
[Collection("sqlite-pools")]
public class SqliteInstanceLockTests
{
    private static TempDir Dir() => TempDir.Create("packmoat-instance-", clearSqlitePools: true);

    private static void Options(PackmoatOptions o, string db, string key, TimeSpan? wait = null)
    {
        o.Database = db;
        o.MasterKeyBase64 = key;
        o.AllowedHosts = "feeds.example.test";
        if (wait is { } w) o.SqliteInstanceWait = w;
    }

    private static Microsoft.AspNetCore.Builder.WebApplication Build(string db, string key, TimeSpan? wait = null) =>
        PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, o => Options(o, db, key, wait));

    /// <summary>Thrown when the server built, which these tests say it must not.</summary>
    private sealed class StartedException : Exception;

    private static (int Code, string Error) Start(Action<PackmoatOptions> configure)
    {
        var error = new StringWriter();
        var code = StartupFailure.Run(() =>
        {
            var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, configure);
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new StartedException();
        }, error);
        return (code, error.ToString().TrimEnd());
    }

    private static bool Held(string db)
    {
        try
        {
            SqliteInstanceLock.AcquireAsync(db, TimeSpan.Zero).GetAwaiter().GetResult().Dispose();
            return false;
        }
        catch (InvalidOperationException) { return true; }
    }

    private static string Refusal(string db, string seconds) =>
        $"Another Packmoat server is using the database {db}: its lock file {db}.instance was still held after {seconds} s. A SQLite database " +
        "takes one server only. Refusing to start: stop the other server first (to run more than one copy, use PostgreSQL).";

    [Fact]   // mutation: the wait set to 0 (or to the few seconds a compose recreate can outlast)
    public void The_server_waits_60_seconds_by_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SqliteInstanceLock.DefaultWait);
        Assert.Equal(SqliteInstanceLock.DefaultWait, new PackmoatOptions().SqliteInstanceWait);
    }

    [Fact]   // mutations: the server takes no lock; a second server is let in; the refusal does not name the database
    public void A_second_server_on_the_same_database_is_refused_while_the_first_runs()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        using (var first = Build(db, key))
        {
            Assert.True(Held(db));
            var (code, error) = Start(o => Options(o, db, key, TimeSpan.FromMilliseconds(500)));
            Assert.Equal(1, code);
            Assert.Equal("error: " + Refusal(db, "0.5"), error);
        }
        Assert.False(Held(db));   // the app let go of it when disposed
        Assert.True(File.Exists(db + ".instance"));   // the file stays: only the lock goes
        using (Build(db, key, TimeSpan.Zero)) Assert.True(Held(db));
    }

    [Fact]   // mutation: the wait set to 0 (the recreated container would refuse while the old one is still stopping)
    public async Task A_second_server_waits_for_the_first_to_stop_then_starts()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var first = Build(db, key);
        var clock = Stopwatch.StartNew();
        var second = Task.Run(() => Build(db, key));   // the default wait
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await using var started = await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.True(Held(db));
    }

    /// <summary>
    /// The lock is held while the server stops: through ApplicationStopping (requests draining, background jobs ending their leases) and
    /// after StopAsync, until the app is disposed. A recreated container's new server must not start on the file before then.
    /// </summary>
    [Fact]   // mutation: the lock let go of when the server is asked to stop (ApplicationStopping), not when it is disposed
    public async Task The_lock_is_held_through_the_stop_until_the_app_is_disposed()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        var app = Build(db, AesGcmSecretProtector.GenerateKeyBase64());
        bool? heldWhileStopping = null;
        bool? heldWhenStopped = null;
        var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => heldWhileStopping = Held(db));
        lifetime.ApplicationStopped.Register(() => heldWhenStopped = Held(db));
        await app.StartAsync();
        await app.StopAsync();
        Assert.True(heldWhileStopping);
        Assert.True(heldWhenStopped);
        Assert.True(Held(db));   // stopped, not yet disposed
        await app.DisposeAsync();
        Assert.False(Held(db));
    }

    /// <summary>
    /// PACKMOAT_DB given as a relative path: the lock file is the database's full path with ".instance", so the same database named
    /// another way (here its full path) is the same lock, and a second server on it is refused.
    /// </summary>
    [Fact]   // mutation: the lock file named from the path as given (a relative and a full spelling of one database would be two locks)
    public void A_relative_database_path_takes_the_lock_beside_the_database()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, db);
        Assert.False(Path.IsPathRooted(relative));
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        using (Build(relative, key))
        {
            Assert.True(File.Exists(db + ".instance"));
            Assert.Equal(db + ".instance", SqliteInstanceLock.PathFor(relative));
            Assert.True(Held(db));
            var (code, error) = Start(o => Options(o, db, key, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(1, code);
            Assert.Equal("error: " + Refusal(db, "0.3"), error);
        }
        Assert.False(Held(db));
    }

    /// <summary>
    /// Only another process holding the lock is waited for. Any other error opening the lock file (here a path that loops back on itself)
    /// is no other server's doing: the start refuses at once, with its own message, never "Another Packmoat server" after 60 s.
    /// </summary>
    [Fact]   // mutation: the lock retrying on every IOException (this waits the whole wait, then blames another server)
    public async Task An_error_that_is_not_another_server_refuses_at_once_with_its_own_message()
    {
        if (OperatingSystem.IsWindows()) return;   // a symbolic link that loops: Linux and macOS, which the server ships for
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        File.CreateSymbolicLink(db + ".instance", db + ".instance");   // opening it fails with "too many levels of symbolic links"
        var clock = Stopwatch.StartNew();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteInstanceLock.AcquireAsync(db, TimeSpan.FromSeconds(20)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"waited {clock.Elapsed}");
        Assert.StartsWith($"The server cannot open the database's lock file {db}.instance (IOException: ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Refusing to start: this is not another server holding it. Check that the path is a plain file the server's user can create and write.", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Another Packmoat server", refused.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(refused.InnerException);

        // and the server says so at once too, as its one error line
        var (code, error) = Start(o => Options(o, db, AesGcmSecretProtector.GenerateKeyBase64()));
        Assert.Equal(1, code);
        Assert.StartsWith($"error: The server cannot open the database's lock file {db}.instance (IOException: ", error, StringComparison.Ordinal);
    }

    [Fact]   // mutation: a start that fails after taking the lock keeps it (a test's next server, or a retry in one process, is refused)
    public void A_start_that_fails_after_the_lock_lets_go_of_it()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        var (code, error) = Start(o => Options(o, db, "not-base64!", TimeSpan.Zero));
        Assert.Equal(1, code);
        Assert.DoesNotContain("lock file", error, StringComparison.Ordinal);
        Assert.False(Held(db));
    }

    [Fact]   // mutation: the command line takes the guard (a backup or any command would wait, then fail, while the server runs)
    public async Task The_command_line_and_a_backup_work_while_the_server_runs()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        await using var server = Build(db, key);
        var env = new CliEnvironment(db, () => AesGcmSecretProtector.FromBase64(key), new SystemClock(), "op");
        foreach (var args in new[] { new[] { "tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json" }, ["tenant", "list"], ["backup", dir.File("backup.db")] })
        {
            var (o, e) = (new StringWriter(), new StringWriter());
            var run = AdminCli.RunAsync(args, o, e, env, []);
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))));   // never a 60 s wait
            Assert.True(await run == 0, string.Join(" ", args) + ": " + e);
        }
        Assert.True(File.Exists(dir.File("backup.db")));
        Assert.True(Held(db));   // the server still holds it
        Assert.False(File.Exists(dir.File("backup.db") + ".instance"));
    }

    [Fact]   // mutation: a marker file instead of an OS lock (the file a crashed server leaves behind would block every later start)
    public async Task A_lock_file_left_by_a_dead_server_does_not_block_the_next_start()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        // What a server killed mid-run leaves: the file, unheld.
        File.WriteAllText(db + ".instance", "");
        using (await SqliteInstanceLock.AcquireAsync(db, TimeSpan.Zero)) Assert.True(Held(db));
        Assert.False(Held(db));
    }

    [Fact]   // mutation: a marker file instead of an OS lock (the killed holder's lock would stay)
    public async Task A_killed_holder_lets_go_of_the_lock_with_its_process()
    {
        if (OperatingSystem.IsWindows()) return;   // flock: Linux and macOS, which the server ships for
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        // Another process takes the same lock (flock, as .NET does for a file opened with no sharing), then is killed: no code of its own runs.
        using var holder = Process.Start(new ProcessStartInfo("python3")
        {
            ArgumentList = { "-c", "import fcntl,sys,time\nf=open(sys.argv[1],'a+')\nfcntl.flock(f,fcntl.LOCK_EX)\nprint('held',flush=True)\ntime.sleep(600)", db + ".instance" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            Assert.Equal("held", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteInstanceLock.AcquireAsync(db, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(Refusal(db, "0.3"), refused.Message);
        }
        finally
        {
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync();
        }
        using (await SqliteInstanceLock.AcquireAsync(db, TimeSpan.Zero)) { }
        Assert.True(File.Exists(db + ".instance"));
    }

    [Fact]   // mutations: the lock file elsewhere than beside the database (another volume, the working folder); readable by others
    public async Task The_lock_file_sits_beside_the_database_and_only_its_owner_may_read_it()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        using var held = await SqliteInstanceLock.AcquireAsync(db, TimeSpan.Zero);
        Assert.Equal(Path.GetFullPath(db) + ".instance", held.Path);
        Assert.Equal(Path.GetFullPath(db) + ".instance", SqliteInstanceLock.PathFor(db));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(held.Path));
    }

    [Theory]   // mutation: the switch that turns .NET's file locks off is ignored (the guard would hold nothing)
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task Turning_off_file_locking_refuses_the_start(string? value, bool off)
    {
        Assert.Equal(off, SqliteInstanceLock.FileLockingDisabled(value));
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        if (!off)
        {
            using (await SqliteInstanceLock.AcquireAsync(db, TimeSpan.Zero, value)) { }
            return;
        }
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteInstanceLock.AcquireAsync(db, TimeSpan.Zero, value));
        Assert.Equal("DOTNET_SYSTEM_IO_DISABLEFILELOCKING is set, which turns off the file lock that keeps a second server off this SQLite " +
            "database. Refusing to start: unset it.", refused.Message);
        Assert.False(File.Exists(db + ".instance"));
    }

    // mutation: the server not passing the setting to the lock. The runtime reads the variable once, at its own start, so setting it here
    // changes no lock; the collection runs alone, so no other test sees it.
    [Fact]
    public void A_server_started_with_file_locking_off_refuses()
    {
        using var dir = Dir();
        var db = dir.File("packmoat.db");
        Environment.SetEnvironmentVariable(SqliteInstanceLock.DisableFileLockingVariable, "1");
        try
        {
            var (code, error) = Start(o => Options(o, db, AesGcmSecretProtector.GenerateKeyBase64()));
            Assert.Equal(1, code);
            Assert.StartsWith("error: DOTNET_SYSTEM_IO_DISABLEFILELOCKING is set", error, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(SqliteInstanceLock.DisableFileLockingVariable, null); }
    }
}
