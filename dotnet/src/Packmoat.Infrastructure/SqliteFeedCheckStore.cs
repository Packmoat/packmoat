using System.Globalization;
using System.Text.Json;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>
/// <see cref="IFeedCheckStore"/> on SQLite (migrations 13, 14 and 16). Results and confirmed admins are JSON arrays; a status this build does not
/// know reads back as <see cref="CheckStatus.Fail"/>, and a kind it does not know as neither kind (fail closed: it never qualifies).
/// </summary>
public sealed class SqliteFeedCheckStore : IFeedCheckStore
{
    private readonly SqliteDatabase _db;

    public SqliteFeedCheckStore(SqliteDatabase db) => _db = db;

    /// <summary>One result as stored. Names is left out when null, so a row without names reads and looks as before.</summary>
    private sealed record StoredResult(string Id, string Title, string Status, string Detail,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? Names = null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The typed names as stored in feed_names (migration 16).</summary>
    private sealed record StoredNames(string? Organization, string? Project, string? Feed);

    /// <summary>The names as JSON, or null (the column stays NULL) when there are none.</summary>
    private static string? NamesText(FeedNames? names) =>
        names is null ? null : JsonSerializer.Serialize(new StoredNames(names.Organization, names.Project, names.Feed), Json);

    /// <summary>
    /// The stored names, only when they still pass the name rules (<see cref="AzureFeedNames.Validate"/>); NULL, unreadable or invalid
    /// JSON reads as no names, so the page falls back to the feed URL.
    /// </summary>
    private static FeedNames? ParseNames(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var n = JsonSerializer.Deserialize<StoredNames>(text, Json);
            return n is null ? null : AzureFeedNames.Validate(n.Organization, n.Project, n.Feed).Names;
        }
        catch (JsonException) { return null; }
    }

    public static string OutcomeText(FeedCheckOutcome outcome) => outcome switch
    {
        FeedCheckOutcome.Passed => "passed",
        FeedCheckOutcome.Failed => "failed",
        FeedCheckOutcome.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static FeedCheckOutcome ParseOutcome(string text) => text switch
    {
        "passed" => FeedCheckOutcome.Passed,
        "incomplete" => FeedCheckOutcome.Incomplete,
        _ => FeedCheckOutcome.Failed,
    };

    private static string StatusText(CheckStatus s) => s switch
    {
        CheckStatus.Pass => "pass",
        CheckStatus.Warn => "warn",
        CheckStatus.Skip => "skip",
        _ => "fail",
    };

    public static string KindText(FeedCheckKind kind) => kind switch
    {
        FeedCheckKind.Settings => "settings",
        FeedCheckKind.Developer => "developer",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>An unknown stored kind reads as a value that is neither kind, so no rule ever accepts it.</summary>
    private static FeedCheckKind ParseKind(string text) => text switch
    {
        "settings" => FeedCheckKind.Settings,
        "developer" => FeedCheckKind.Developer,
        _ => (FeedCheckKind)(-1),
    };

    private static CheckStatus ParseStatus(string? s) => s switch
    {
        "pass" => CheckStatus.Pass,
        "warn" => CheckStatus.Warn,
        "skip" => CheckStatus.Skip,
        _ => CheckStatus.Fail,
    };

    public async Task<long> RecordAsync(FeedCheck check, CancellationToken ct)
    {
        var outcome = OutcomeText(check.Outcome);   // before anything is written
        var kind = KindText(check.Kind);
        var results = JsonSerializer.Serialize(check.Results.Select(r => new StoredResult(r.Id, r.Title, StatusText(r.Status), r.Detail, r.Names?.ToArray())), Json);
        var admins = JsonSerializer.Serialize(check.ConfirmedAdmins, Json);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, confirmed_admins, kind, feed_names)
            VALUES(@t, @at, @tid, @oid, @name, @dir, @feed, @client, @outcome, @results, @admins, @kind, @names)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@t", check.TenantId);
        cmd.Parameters.AddWithValue("@at", check.CheckedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@tid", check.CheckedByTid);
        cmd.Parameters.AddWithValue("@oid", check.CheckedByOid);
        cmd.Parameters.AddWithValue("@name", check.CheckedByName);
        cmd.Parameters.AddWithValue("@dir", check.AzureTenantId);
        cmd.Parameters.AddWithValue("@feed", check.FeedIndexUrl);
        cmd.Parameters.AddWithValue("@client", check.ClientId);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        cmd.Parameters.AddWithValue("@results", results);
        cmd.Parameters.AddWithValue("@admins", admins);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@names", (object?)NamesText(check.Names) ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The results as stored, redacted (<see cref="FeedCheckRedaction.Redact"/>). Results that cannot be read, whatever the reason (not
    /// JSON, not an array of objects, a null element), become none: fail closed, and one bad row never stops the blanking of the others.
    /// </summary>
    private static string RedactedResultsText(string stored)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<StoredResult?[]>(stored, Json);
            if (parsed is null || parsed.Any(x => x is null)) return "[]";
            var redacted = FeedCheckRedaction.Redact(parsed.Select(x => new CheckResult(x!.Id ?? "", x.Title ?? "", ParseStatus(x.Status), x.Detail ?? "")));
            return JsonSerializer.Serialize(redacted.Select(r => new StoredResult(r.Id, r.Title, StatusText(r.Status), r.Detail)), Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "[]"; }
    }

    /// <summary>
    /// The rows that may still hold personal data: a superset of those <see cref="RedactedResultsText"/> would change, so an old row that is
    /// already redacted is not parsed again on every call. Anything that is not an array of objects each with exactly id, title, status and
    /// the removed detail is selected. The CASEs read an element as JSON only when it is valid JSON and an object: json_each gives a string
    /// element back as plain text, which json_extract would refuse (malformed JSON) and so fail the whole pass. The key count catches a duplicate key (json_extract
    /// reads the first "detail", the C# parser the last). The C# comparison below still decides what is written.
    /// </summary>
    private const string MayHoldPersonalData = """
        (confirmed_admins <> '[]' OR checked_by_name <> '' OR
         CASE WHEN json_valid(results) AND json_type(results) = 'array'
              THEN EXISTS (SELECT 1 FROM json_each(results) e
                           WHERE CASE WHEN e.type IS NOT 'object' THEN 1
                                      ELSE json_extract(e.value, '$.detail') IS NOT @removed
                                        OR EXISTS (SELECT 1 FROM json_each(e.value) k WHERE k.key NOT IN ('id', 'title', 'status', 'detail'))
                                        OR (SELECT COUNT(*) FROM json_each(e.value)) <> 4 END)
              ELSE results <> '[]' END)
        """;

    public async Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE
        var due = new List<(long Id, string Results)>();
        await using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            // checked_at is always written as round-trip UTC ("O"), so the text order is the time order.
            select.CommandText = "SELECT id, results, confirmed_admins, checked_by_name FROM feed_checks WHERE checked_at < @before AND " + MayHoldPersonalData;
            select.Parameters.AddWithValue("@before", checkedBefore.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            select.Parameters.AddWithValue("@removed", FeedCheckRedaction.RemovedDetail);
            await using var r = await select.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var results = RedactedResultsText(r.GetString(1));
                // Only rows that still hold something to remove, so a second run changes nothing and returns 0.
                if (results != r.GetString(1) || r.GetString(2) != "[]" || r.GetString(3) != "") due.Add((r.GetInt64(0), results));
            }
        }
        foreach (var (id, results) in due)
        {
            await using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE feed_checks SET confirmed_admins='[]', checked_by_name='', results=@results WHERE id=@id";
            update.Parameters.AddWithValue("@results", results);
            update.Parameters.AddWithValue("@id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return due.Count;
    }

    public async Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM feed_checks WHERE tenant_id=@t AND kind=@k ORDER BY checked_at DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@k", KindText(kind));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>The columns <see cref="Read"/> expects, in its order.</summary>
    internal const string Columns =
        "id, tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, confirmed_admins, kind, feed_names";

    /// <summary>One row selected with <see cref="Columns"/>.</summary>
    internal static FeedCheck Read(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var results = (JsonSerializer.Deserialize<StoredResult[]>(r.GetString(10), Json) ?? Array.Empty<StoredResult>())
            .Select(x => new CheckResult(x.Id ?? "", x.Title ?? "", ParseStatus(x.Status), x.Detail ?? "", x.Names)).ToList();
        var admins = JsonSerializer.Deserialize<string[]>(r.GetString(11), Json) ?? Array.Empty<string>();
        return new FeedCheck(r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), ParseOutcome(r.GetString(9)), results, admins)
        { Id = r.GetInt64(0), Kind = ParseKind(r.GetString(12)), Names = ParseNames(r.IsDBNull(13) ? null : r.GetString(13)) };
    }
}
