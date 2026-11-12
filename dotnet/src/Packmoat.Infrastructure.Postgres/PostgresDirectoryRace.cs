using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// A directory claim that loses the race to another writer at its unique index. Where
/// SQLite's single writer lets a claim see its rival's committed row and answer DirectoryTaken, PostgreSQL's read committed can let both
/// past their checks: the loser then meets the index the directory is unique by, and its transaction is rolled back with 23505
/// (unique_violation). That one failure, on that one index, is the same answer as the check's, so it becomes it: nothing was written,
/// and a retry would see the winner's row and say DirectoryTaken anyway. Any other error, a unique violation on any other constraint
/// included, is thrown as it is.
/// <para>The catch is outside the claim's transaction: <paramref name="claim"/> opens, runs and commits it,
/// and once it throws its connection and transaction are disposed and the server has rolled it back. It is the adapter's one catch of a
/// unique violation (PostgresCatchTests).</para>
/// </summary>
internal static class PostgresDirectoryRace
{
    /// <summary>The shared app's directory: one tenant per directory among the shared bindings (Connect, the operator's bind).</summary>
    public const string SharedDirectory = "ux_shared_directory";

    /// <summary>A sign-in directory's link: one tenant per directory (organisation create and self sign-up, the operator's bind).</summary>
    public const string SignInDirectory = "sign_in_directories_pkey";

    /// <summary>
    /// Runs <paramref name="claim"/>; a unique violation on <paramref name="constraint"/> alone becomes <paramref name="taken"/>, anything
    /// else is thrown.
    /// </summary>
    public static async Task<T> TakenOnConflictAsync<T>(string constraint, T taken, Func<Task<T>> claim)
    {
        try
        {
            return await claim();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == constraint)
        {
            // Outside the transaction: claim's connection and transaction are disposed, and the server rolled it back.
            return taken;
        }
    }
}
