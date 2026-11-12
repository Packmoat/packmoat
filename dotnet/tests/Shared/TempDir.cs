using System.Collections.Concurrent;

namespace Packmoat.TestSupport;

/// <summary>
/// A temporary folder a test owns: made by <see cref="Create"/>, deleted, with everything in it, when disposed. Use it with
/// <c>using</c> (or as a field a fixture disposes), so the folder goes whether the test passes or fails. A failed delete is not hidden:
/// after a short retry it throws. <c>TempLeakGuard</c> also fails the run if anything is left behind. Compiled into every test project
/// under dotnet/tests (and every tests folder built on the core) by TempLeakGuard.props.
/// </summary>
internal sealed class TempDir : IDisposable, IAsyncDisposable
{
    private readonly bool _clearSqlitePools;

    private TempDir(string path, bool clearSqlitePools)
    {
        Path = path;
        _clearSqlitePools = clearSqlitePools;
    }

    /// <summary>The folder's full path.</summary>
    public string Path { get; }

    /// <summary>A new, empty folder in the temporary folder, its name starting with <paramref name="prefix"/>.</summary>
    /// <param name="clearSqlitePools">See <see cref="Delete"/>.</param>
    public static TempDir Create(string prefix, bool clearSqlitePools = false) =>
        new(Directory.CreateTempSubdirectory(prefix).FullName, clearSqlitePools);

    /// <summary>A path inside this folder (nothing is made there).</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Delete(Path, _clearSqlitePools);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Deletes <paramref name="path"/> and everything in it; nothing to do when it is gone already. On an <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> it tries again, 3 times, 50 ms apart, then throws, unless the code using the folder has
    /// already thrown (<see cref="Failing(string)"/>): then that failure stays the one reported, and this one is kept for
    /// <c>TempLeakGuard</c> to report after the last test.
    /// </summary>
    /// <param name="clearSqlitePools">
    /// Close SQLite's pooled connections first, so that a database file in the folder is not held open (Windows refuses to delete an open
    /// file). The clear is process-wide: only a test in the "sqlite-pools" collection, which runs alone, may ask for it.
    /// </param>
    public static void Delete(string path, bool clearSqlitePools = false)
    {
        var afterFailure = FailedBodies.TryRemove(Key(path), out _);
        if (clearSqlitePools) ClearSqlitePools();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < 3)
                {
                    Thread.Sleep(50);
                    continue;
                }
                if (!afterFailure) throw;
                Deferred[Key(path)] = $"{path} could not be deleted after its test had failed: {e.GetType().Name}: {e.Message}";
                return;
            }
        }
    }

    /// <summary>
    /// Marks that the code using <paramref name="path"/> has thrown, so that the delete in its <c>finally</c> cannot replace that failure
    /// with its own: if the delete fails as well, the test's exception stays the one reported, and <c>TempLeakGuard</c> reports
    /// the failed delete after the last test (the folder is left behind too). Always false: use it only as the filter of a catch between
    /// the try and the finally, <c>catch when (TempDir.Failing(dir)) { throw; }</c>; a filter runs before any finally does. A
    /// <c>using</c> cannot see its body's exception, so a site whose delete can fail after its body has failed uses this form.
    /// </summary>
    public static bool Failing(string path)
    {
        FailedBodies[Key(path)] = 0;
        return false;
    }

    /// <inheritdoc cref="Failing(string)"/>
    public bool Failing() => Failing(Path);

    /// <summary>
    /// <see cref="Delete"/> from inside a catch that rethrows (a factory whose start failed): a delete that fails too does not replace the
    /// exception being handled; the guard reports it instead (see <see cref="Failing(string)"/>).
    /// </summary>
    public static void DeleteAfterFailure(string path, bool clearSqlitePools = false)
    {
        Failing(path);
        Delete(path, clearSqlitePools);
    }

    /// <summary>The deletes that failed after their test had failed (see <see cref="Failing(string)"/>), taken once: the guard reports them.</summary>
    internal static IReadOnlyList<string> TakeDeferred()
    {
        var taken = new List<string>();
        foreach (var key in Deferred.Keys)
            if (Deferred.TryRemove(key, out var problem)) taken.Add(problem);
        return taken.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The deferred failure of the delete of <paramref name="path"/>, taken, if there is one (for the guard's self-test).</summary>
    internal static string? TakeDeferred(string path) => Deferred.TryRemove(Key(path), out var problem) ? problem : null;

    private static readonly ConcurrentDictionary<string, byte> FailedBodies = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Deferred = new(StringComparer.Ordinal);

    private static string Key(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    /// <summary>
    /// Closes every pooled SQLite connection, when the test project uses Microsoft.Data.Sqlite at all. Found by name, so that the projects
    /// that do not use it need no reference to it.
    /// </summary>
    public static void ClearSqlitePools() =>
        Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite", throwOnError: false)
            ?.GetMethod("ClearAllPools", Type.EmptyTypes)?.Invoke(null, null);
}
