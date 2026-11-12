namespace Packmoat.Architecture.Tests;

/// <summary>
/// The PostgreSQL adapter lets a database error through: in Packmoat.Infrastructure.Postgres no catch can
/// stop a database error but the pinned few. A refusal must reach the caller; a caught one inside a transaction leaves the transaction
/// aborted and the store's answer wrong. A catch is flagged when
/// <list type="bullet">
/// <item>its head (the clause or its filter) names PostgresException, NpgsqlException or DbException; or</item>
/// <item>it is broad, so it catches a database error without naming one: bare <c>catch</c>, or of Exception,
/// SystemException or ExternalException (DbException's bases), and either has a filter (a helper's verdict, say) or does not end by
/// rethrowing (<c>throw;</c>). A broad catch with no filter that only cleans up and rethrows is let through.</item>
/// </list>
/// Each flagged catch must be on <see cref="Allowed"/>, pinned by its whole head, so a widened filter fails here too.
/// </summary>
public class PostgresCatchTests
{
    private const string Adapter = "Packmoat.Infrastructure.Postgres/";

    /// <summary>The catches allowed, as "file: head" (the head's spaces as one), each with why.</summary>
    internal static readonly string[] Allowed =
    [
        // The connection string's own parse: anything Npgsql's builder throws becomes the setting's error, never quoting the value. No
        // database is reached inside it, so no database error can be caught there.
        "Packmoat.Infrastructure.Postgres/PostgresConnectionString.cs: catch (Exception)",
        // The migration lock not had in time becomes a TimeoutException naming the wait.
        "Packmoat.Infrastructure.Postgres/PostgresDatabase.cs: catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)",
        // The feed-check redaction's parse of one row's stored results, as SQLite's: results that cannot be read, for
        // whatever reason, become none (fail closed). The try holds only System.Text.Json and the redaction rules: no database is reached
        // inside it, so no database error can be caught there.
        "Packmoat.Infrastructure.Postgres/PostgresFeedCheckStore.cs: catch (Exception ex) when (ex is not OperationCanceledException)",
        // The purge's deadlock: never retried; once the server has rolled the purge's one transaction back
        // and it is disposed, a deadlock alone becomes the refusal "Nothing was deleted; run the purge again." The try holds only the call
        // that opens, runs and commits that transaction, so the catch is never inside it.
        "Packmoat.Infrastructure.Postgres/PostgresTenantPurgeStore.cs: catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DeadlockDetected)",
        // db check-migrations: a migration statement the database refuses is what the check is for, so it becomes the
        // report's refusal, not a crash. The statement ran after a savepoint, which the catch rolls back to (so the reads after it run on
        // what was there before), and the whole transaction is rolled back after the report, never committed.
        "Packmoat.Infrastructure.Postgres/PostgresMigrationCheck.cs: catch (Exception e) when (e is SchemaTooNewException or SchemaTrackException or SchemaBaselineException or TimeoutException or PostgresException)",
        // A directory claim that loses the race at its unique index: Connect, organisation create and self sign-up. A unique
        // violation on the one constraint the caller names (ux_shared_directory or sign_in_directories_pkey), never any other 23505,
        // becomes DirectoryTaken, as the claim's own check would have said. The try holds only the call that opens, runs and commits the
        // claim's transaction, so the catch is never inside it.
        "Packmoat.Infrastructure.Postgres/PostgresDirectoryRace.cs: catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == constraint)",
        // The start's pooler check: the server refusing its second connection as one too many (53300) becomes a refusal
        // of its own that says why, not the server's error. The try holds only opening that new, unpooled connection: no transaction.
        "Packmoat.Infrastructure.Postgres/PostgresStartupChecks.cs: catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.TooManyConnections)",
        // The one retry: a deadlock or a serialization failure, at most three attempts.
        "Packmoat.Infrastructure.Postgres/PostgresRetry.cs: catch (PostgresException e) when (IsRetried(e.SqlState) && n < MaxAttempts && !cancellationToken.IsCancellationRequested)",
    ];

    // mutations: a catch (PostgresException) added to an adapter; a filter widened; an allowed catch removed (the list must shrink);
    // (F3) a catch (Exception) {} or a bare catch {} in an adapter, or catch (Exception e) when (Helper(e))
    [Fact]
    public void The_postgres_adapter_catches_no_database_error_but_the_pinned_ones()
    {
        var files = EngineTypeScan.SourceFiles(Path.Combine(CoreRoot.Path(), "dotnet", "src")).Where(f => f.Named.StartsWith(Adapter, StringComparison.Ordinal)).ToList();
        Assert.Contains(files, f => f.Named == Adapter + "PostgresDatabase.cs");
        Assert.Contains(files, f => f.Named == Adapter + "PostgresTenantStore.cs");
        Assert.Equal(Allowed.Order(StringComparer.Ordinal), PostgresCatchScan.DatabaseCatches(files));
    }

    [Fact]
    public void The_catch_scan_sees_each_form_and_not_a_comment()
    {
        Assert.Equal(
            [
                "A.cs: catch (Npgsql.NpgsqlException e) when (e.IsTransient)",
                "A.cs: catch (PostgresException)",
                "B.cs: catch (Exception e) when (e is PostgresException",   // the head ends at the first brace: a property pattern's will do
                "B.cs: catch (System.Data.Common.DbException)",
            ],
            PostgresCatchScan.DatabaseCatches([
                ("A.cs", "try { } catch (PostgresException) { }\ntry { } catch (Npgsql.NpgsqlException e)\n    when (e.IsTransient) { }"),
                ("B.cs", "try { } catch (System.Data.Common.DbException) { } try { } catch (Exception e) when (e is PostgresException { SqlState: \"23505\" }) { }"),
                ("C.cs", "// catch (PostgresException) { }\ntry { } catch (InvalidOperationException) { } try { } catch { throw; }"),
            ]));
    }

    // The broad forms, which catch a database error without naming it.
    [Fact]
    public void The_catch_scan_sees_a_broad_catch_that_does_not_rethrow_or_has_a_filter()
    {
        Assert.Equal(
            [
                "D.cs: catch",
                "D.cs: catch (Exception e) when (Helper(e))",
                "D.cs: catch (Exception)",
                "E.cs: catch (Exception e) when (e is not OperationCanceledException)",
                "E.cs: catch (System.Exception ex)",
                "E.cs: catch (System.Runtime.InteropServices.ExternalException)",
                "F.cs: catch (SystemException)",
                "H.cs: catch",
                "H.cs: catch (Exception e)",
                "I.cs: catch (Exception)",
                "I.cs: catch (Exception)",
            ],
            PostgresCatchScan.DatabaseCatches([
                ("D.cs", "try { } catch (Exception) { }\ntry { } catch { }\ntry { } catch (Exception e) when (Helper(e)) { Log(e); }"),
                ("E.cs", "try { } catch (System.Exception ex) { return null; } try { } catch (Exception e) when (e is not OperationCanceledException) { throw; }\n" +
                    "try { } catch (System.Runtime.InteropServices.ExternalException) { throw new X($\"{y}\"); }"),
                ("F.cs", "try { } catch (SystemException) { if (a) { b(); } }"),
                // Let through: cleanup then rethrow, with no filter; a narrow type; a comment; an exception type that only starts with "Exception".
                ("G.cs", "try { } catch { await x.DisposeAsync(); throw; } try { } catch (Exception) { if (a) { b(); } throw; }\n" +
                    "try { } catch (InvalidOperationException e) when (Helper(e)) { } // catch (Exception) { }\ntry { } catch (ExceptionDispatchInfoX) { }"),
                // Ends with a rethrow, but can leave the block before it.
                ("H.cs", "try { } catch (Exception e) { if (e is PostgresX) return false; throw; } try { } catch { goto done; throw; }"),
                ("I.cs", "for (;;) { try { } catch (Exception) { if (a) break; throw; } try { } catch (Exception) { if (a) continue; throw; } }"),
            ]));
    }
}
