using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The PostgreSQL adapter lets a database error through (#303 decision "Transactions"): no catch of PostgresException, NpgsqlException or
/// DbException in Packmoat.Infrastructure.Postgres, in the clause or its filter, but the pinned few. A refusal must reach the caller; a
/// caught one inside a transaction leaves the transaction aborted and the store's answer wrong. Each catch allowed is pinned by its whole
/// head, so a widened filter fails here too.
/// </summary>
public class PostgresCatchTests
{
    private const string Adapter = "Packmoat.Infrastructure.Postgres/";

    /// <summary>The catches allowed, as "file: head" (the head's spaces as one): each turns one exact error into another, or retries it.</summary>
    internal static readonly string[] Allowed =
    [
        // The migration lock not had in time becomes a TimeoutException naming the wait (#288).
        "Packmoat.Infrastructure.Postgres/PostgresDatabase.cs: catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)",
        // The one retry: a deadlock or a serialization failure, at most three attempts (#303).
        "Packmoat.Infrastructure.Postgres/PostgresRetry.cs: catch (PostgresException e) when (IsRetried(e.SqlState) && n < MaxAttempts && !cancellationToken.IsCancellationRequested)",
    ];

    /// <summary>A catch clause's head: the keyword, its declaration and any filter, up to the block's brace.</summary>
    private static readonly Regex CatchHead = new(@"\bcatch\b[^{]*", RegexOptions.CultureInvariant);

    private static readonly Regex DatabaseError = new(@"\b(PostgresException|NpgsqlException|DbException)\b", RegexOptions.CultureInvariant);

    /// <summary>Every catch head in the files' code (comments aside) that names a database error, as "file: head".</summary>
    internal static string[] DatabaseCatches(IEnumerable<(string Named, string Source)> files) =>
        files.SelectMany(f => CatchHead.Matches(EngineTypeScan.WithoutComments(f.Source))
                .Select(m => Regex.Replace(m.Value, @"\s+", " ").Trim())
                .Where(h => DatabaseError.IsMatch(h))
                .Select(h => $"{f.Named}: {h}"))
            .Order(StringComparer.Ordinal).ToArray();

    // mutations (#303): a catch (PostgresException) added to an adapter; a filter widened; an allowed catch removed (the list must shrink)
    [Fact]
    public void The_postgres_adapter_catches_no_database_error_but_the_pinned_ones()
    {
        var files = EngineTypeScan.SourceFiles(Path.Combine(CoreRoot.Path(), "dotnet", "src")).Where(f => f.Named.StartsWith(Adapter, StringComparison.Ordinal)).ToList();
        Assert.Contains(files, f => f.Named == Adapter + "PostgresDatabase.cs");
        Assert.Equal(Allowed.Order(StringComparer.Ordinal), DatabaseCatches(files));
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
            DatabaseCatches([
                ("A.cs", "try { } catch (PostgresException) { }\ntry { } catch (Npgsql.NpgsqlException e)\n    when (e.IsTransient) { }"),
                ("B.cs", "try { } catch (System.Data.Common.DbException) { } try { } catch (Exception e) when (e is PostgresException { SqlState: \"23505\" }) { }"),
                ("C.cs", "// catch (PostgresException) { }\ntry { } catch (InvalidOperationException) { } try { } catch { throw; }"),
            ]));
    }
}
