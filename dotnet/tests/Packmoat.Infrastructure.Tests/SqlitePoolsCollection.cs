namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Tests that clear SQLite's connection pools (<c>SqliteConnection.ClearAllPools()</c> is process-wide) or build a database run here,
/// alone: xUnit does not start any other collection while one that disables parallelization is running. Without this, one test's
/// clear could dispose a connection another test was in the middle of using (an ObjectDisposedException on SQLitePCL.sqlite3).
/// </summary>
[CollectionDefinition("sqlite-pools", DisableParallelization = true)]
public sealed class SqlitePoolsCollection { }
