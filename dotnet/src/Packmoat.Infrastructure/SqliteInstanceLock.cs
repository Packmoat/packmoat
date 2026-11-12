using System.Globalization;

namespace Packmoat.Infrastructure;

/// <summary>
/// The SQLite server's single-instance guard: an exclusive lock on the file <c>&lt;database&gt;.instance</c> next to the database, held
/// for as long as the server runs. A SQLite database has one writer, so a second server on the same file is refused: it waits up to
/// <see cref="DefaultWait"/> for the first to stop (a container being recreated), then refuses to start. Only the server takes it: the
/// command line, a backup and a restore never do, so they work while the server runs.
/// <para>The lock is the operating system's (the file opened with no sharing: flock on Linux and macOS, a sharing lock on Windows), never
/// the file's existence, so a server that crashes or is killed leaves no lock behind: the system lets go of it with the process. The file
/// stays and is never deleted (deleting it would let a waiting server lock a file another is about to make anew). It holds nothing,
/// and is made owner-only, as the database is.</para>
/// </summary>
public sealed class SqliteInstanceLock : IDisposable
{
    /// <summary>How long a starting server waits for another to let go: past a container's stop (10 s by default, then killed) and its start.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    /// <summary>How often a waiting server tries again.</summary>
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>The setting that turns .NET's file locking off on Linux and macOS, which would make this guard hold nothing.</summary>
    public const string DisableFileLockingVariable = "DOTNET_SYSTEM_IO_DISABLEFILELOCKING";

    private readonly FileStream _file;

    private SqliteInstanceLock(FileStream file, string path)
    {
        _file = file;
        Path = path;
    }

    /// <summary>The lock file: the database's full path with ".instance" added.</summary>
    public string Path { get; }

    /// <summary>The lock file of the database at <paramref name="databasePath"/>.</summary>
    public static string PathFor(string databasePath) => System.IO.Path.GetFullPath(databasePath) + ".instance";

    /// <summary>
    /// Takes the lock for the database at <paramref name="databasePath"/> (whose folder must exist), trying again every
    /// <see cref="RetryEvery"/> while another process holds it, for up to <paramref name="wait"/>; then refuses
    /// (<see cref="InvalidOperationException"/>, naming the database). Any other error opening the file (no permission, a path that is no plain file) refuses at once, with its own message.
    /// <paramref name="disableFileLocking"/> is the value of <see cref="DisableFileLockingVariable"/>: when it turns .NET's file locks off,
    /// the start is refused, since the lock would hold nothing.
    /// </summary>
    public static async Task<SqliteInstanceLock> AcquireAsync(string databasePath, TimeSpan wait, string? disableFileLocking = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wait));
        if (FileLockingDisabled(disableFileLocking))
            throw new InvalidOperationException($"{DisableFileLockingVariable} is set, which turns off the file lock that keeps a second server off this SQLite database. Refusing to start: unset it.");
        var path = PathFor(databasePath);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (TryOpen(path) is { } file) return new SqliteInstanceLock(file, path);
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"Another Packmoat server is using the database {System.IO.Path.GetFullPath(databasePath)}: its lock file {path} was still held after " +
                    $"{wait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s. A SQLite database takes one server only. Refusing to start: stop the other server first " +
                    "(to run more than one copy, use PostgreSQL).");
            await Task.Delay(RetryEvery, ct);
        }
    }

    /// <summary>Whether .NET's file-locking switch is on: "1" or "true" (any case), as the runtime reads it.</summary>
    public static bool FileLockingDisabled(string? value) =>
        AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var on) && on
        || value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lock file opened with no sharing, or null while another process holds it (a sharing violation, <see cref="HeldByAnother"/>).
    /// Any other error opening it is no other server's doing, so it is never waited out: it refuses at once, with its own message.
    /// </summary>
    private static FileStream? TryOpen(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(path, options); }
        catch (IOException e) when (HeldByAnother(e)) { return null; }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The server cannot open the database's lock file {path}: permission denied. Refusing to start: " +
                "the server's user must be able to create and write files in the database's folder, as SQLite itself must.");
        }
        catch (IOException e) when (e is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException)
        {
            throw new InvalidOperationException($"The server cannot open the database's lock file {path} ({e.GetType().Name}: {e.Message}). Refusing to " +
                "start: this is not another server holding it. Check that the path is a plain file the server's user can create and write.", e);
        }
    }

    /// <summary>
    /// Whether opening the lock file failed only because another process holds it: on Linux and macOS the lock's EWOULDBLOCK (EAGAIN: 11
    /// on Linux, 35 on macOS), which .NET reports as the error number itself; on Windows a sharing or lock violation (32 or 33).
    /// </summary>
    internal static bool HeldByAnother(IOException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException) return false;
        if (OperatingSystem.IsWindows()) return (e.HResult & 0xFFFF) is 32 or 33;
        return e.HResult == (OperatingSystem.IsLinux() ? 11 : 35);
    }

    /// <summary>Lets go of the lock (the file stays). Safe to call more than once.</summary>
    public void Dispose() => _file.Dispose();
}
