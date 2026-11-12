using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// <see cref="IFeedCheckStore"/> on PostgreSQL, the twin of <see cref="SqliteFeedCheckStore"/>: the same JSON for the
/// results, the confirmed admins and the feed names (text columns, never jsonb), the same fail-closed reads (a status this build does not
/// know reads as <see cref="CheckStatus.Fail"/>, a kind it does not know as neither kind), and the same redaction. Times are timestamptz, in
/// UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para><b>The redaction</b> selects rows by date alone (<c>checked_at &lt; @before</c>) and decides each in C#, with no JSON in the SQL:
/// a row whose results are not JSON is an ordinary row here, never an error that would abort the transaction. The decision is SQLite's,
/// step for step: first the rows its SQL pre-filter would select (<see cref="MayHoldPersonalData"/>, the same rules over the parsed text),
/// then of those the rows its C# comparison would change. So exactly the rows SQLite changes are changed, and the count is the same. It is
/// one transaction; each row is updated only if it still holds what was read (compare and set), so two redactions at once change each row
/// once and the second counts nothing for it.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresFeedCheckStore : IFeedCheckStore
{
    private readonly PostgresDatabase _db;

    public PostgresFeedCheckStore(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>One result as stored (as SQLite's). Names is left out when null.</summary>
    private sealed record StoredResult(string Id, string Title, string Status, string Detail,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? Names = null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The typed names as stored in feed_names.</summary>
    private sealed record StoredNames(string? Organization, string? Project, string? Feed);

    private static string? NamesText(FeedNames? names) =>
        names is null ? null : JsonSerializer.Serialize(new StoredNames(names.Organization, names.Project, names.Feed), Json);

    /// <summary>The stored names, only when they still pass the name rules; NULL, unreadable or invalid JSON reads as no names.</summary>
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

    /// <summary>An outcome as stored, as SQLite's (the tests hold the two equal). An outcome this build does not know is refused.</summary>
    public static string OutcomeText(FeedCheckOutcome outcome) => outcome switch
    {
        FeedCheckOutcome.Passed => "passed",
        FeedCheckOutcome.Failed => "failed",
        FeedCheckOutcome.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>A kind as stored, as SQLite's (the tests hold the two equal). A kind this build does not know is refused.</summary>
    public static string KindText(FeedCheckKind kind) => kind switch
    {
        FeedCheckKind.Settings => "settings",
        FeedCheckKind.Developer => "developer",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
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
        ArgumentNullException.ThrowIfNull(check);
        var outcome = OutcomeText(check.Outcome);   // before anything is written
        var kind = KindText(check.Kind);
        var checkedAt = PostgresTime.ToDatabase(check.CheckedAt);
        var results = JsonSerializer.Serialize(check.Results.Select(r => new StoredResult(r.Id, r.Title, StatusText(r.Status), r.Detail, r.Names?.ToArray())), Json);
        var admins = JsonSerializer.Serialize(check.ConfirmedAdmins, Json);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, confirmed_admins, kind, feed_names)
            VALUES(@t, @at, @tid, @oid, @name, @dir, @feed, @client, @outcome, @results, @admins, @kind, @names)
            RETURNING id
            """, c);
        cmd.Parameters.AddWithValue("t", check.TenantId);
        cmd.Parameters.AddWithValue("at", checkedAt);
        cmd.Parameters.AddWithValue("tid", check.CheckedByTid);
        cmd.Parameters.AddWithValue("oid", check.CheckedByOid);
        cmd.Parameters.AddWithValue("name", check.CheckedByName);
        cmd.Parameters.AddWithValue("dir", check.AzureTenantId);
        cmd.Parameters.AddWithValue("feed", check.FeedIndexUrl);
        cmd.Parameters.AddWithValue("client", check.ClientId);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("results", results);
        cmd.Parameters.AddWithValue("admins", admins);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.Add("names", NpgsqlDbType.Text).Value = (object?)NamesText(check.Names) ?? DBNull.Value;
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// The results as stored, redacted (<see cref="FeedCheckRedaction.Redact"/>), as SQLite's: results that cannot be read, whatever the
    /// reason, become none (fail closed), and one bad row never stops the blanking of the others.
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

    /// <summary>How deep SQLite's JSON functions read (its JSON_MAX_DEPTH), so a deep value is valid JSON here exactly when it is there.</summary>
    private static readonly JsonDocumentOptions PreFilterJson = new() { MaxDepth = 1000 };

    /// <summary>
    /// SQLite's pre-filter (<c>SqliteFeedCheckStore</c>'s MayHoldPersonalData SQL), in C#: whether SQLite would select the row at all. Confirmed
    /// admins other than "[]", or a checker's name, select it. Results that are valid JSON and an array select it when any element is not
    /// an object, or is an object whose "detail" is not the removed text (missing, not a string, or another string; with a duplicate key,
    /// the first, as json_extract reads it), or that has a key other than id, title, status and detail, or not exactly four keys (a
    /// duplicate counts twice). Results that are not valid JSON, or valid but not an array, select it unless they are exactly "[]".
    /// </summary>
    internal static bool MayHoldPersonalData(string results, string confirmedAdmins, string checkedByName)
    {
        if (confirmedAdmins != "[]" || checkedByName != "") return true;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(results, PreFilterJson);
        }
        catch (JsonException)
        {
            return results != "[]";
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return results != "[]";
            try
            {
                foreach (var e in document.RootElement.EnumerateArray())
                    if (ElementMayHoldPersonalData(e)) return true;
                return false;
            }
            catch (InvalidOperationException)
            {
                // A key that is not valid UTF-16 once unescaped (a lone surrogate, "\ud800"): NameEquals will not compare it (a value
                // like it is compared by ValueEquals, which does not throw, and is not the removed text). SQLite reads the key, finds it is
                // not one of the four, and selects the row (whose results its C# then empties). So it may hold personal data, and the row
                // is decided as SQLite decides it. Nothing but System.Text.Json runs in the try: no database error can be caught here.
                return true;
            }
        }
    }

    /// <summary>
    /// One element of a results array, as SQLite's pre-filter reads it: true unless it is an object with exactly four keys (a duplicate
    /// counts twice), each of id, title, status and detail (compared unescaped and case-sensitive, as json_each gives them), whose first
    /// "detail" is a string equal to the removed text. Keys and values are compared with NameEquals and ValueEquals, never read as strings.
    /// </summary>
    private static bool ElementMayHoldPersonalData(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return true;
        var keys = 0;
        var detailSeen = false;
        var detailRemoved = false;
        foreach (var p in e.EnumerateObject())
        {
            keys++;
            var isDetail = p.NameEquals("detail");
            if (!isDetail && !p.NameEquals("id") && !p.NameEquals("title") && !p.NameEquals("status")) return true;
            if (isDetail && !detailSeen)
            {
                detailSeen = true;
                detailRemoved = p.Value.ValueKind == JsonValueKind.String && p.Value.ValueEquals(FeedCheckRedaction.RemovedDetail);
            }
        }
        return !detailRemoved || keys != 4;
    }

    /// <summary>
    /// What the redaction does to one old row, decided as SQLite decides it: null when the row is left as it is (SQLite's pre-filter passes
    /// over it, or it holds nothing left to remove, so a second run changes nothing and returns 0); otherwise the redacted results, written
    /// with no confirmed admins and no checker's name.
    /// </summary>
    internal static string? RedactionOf(string results, string confirmedAdmins, string checkedByName)
    {
        if (!MayHoldPersonalData(results, confirmedAdmins, checkedByName)) return null;
        var redacted = RedactedResultsText(results);
        return redacted != results || confirmedAdmins != "[]" || checkedByName != "" ? redacted : null;
    }

    /// <summary>
    /// Blanks the personal data of every check made before <paramref name="checkedBefore"/>, in one transaction (see
    /// <see cref="PostgresFeedCheckStore"/>), and returns how many rows it changed.
    /// </summary>
    public async Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct)
    {
        var before = PostgresTime.ToDatabase(checkedBefore);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var due = new List<(long Id, string Results, string StoredResults, string StoredAdmins, string StoredName)>();
        await using (var select = new NpgsqlCommand("SELECT id, results, confirmed_admins, checked_by_name FROM feed_checks WHERE checked_at < @before", c, tx))
        {
            select.Parameters.AddWithValue("before", before);
            await using var r = await select.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (stored, admins, name) = (r.GetString(1), r.GetString(2), r.GetString(3));
                if (RedactionOf(stored, admins, name) is { } results) due.Add((r.GetInt64(0), results, stored, admins, name));
            }
        }
        var changed = 0;
        await using (var update = new NpgsqlCommand(
            "UPDATE feed_checks SET confirmed_admins='[]', checked_by_name='', results=@results " +
            "WHERE id=@id AND results=@was_results AND confirmed_admins=@was_admins AND checked_by_name=@was_name", c, tx))
        {
            var results = update.Parameters.Add("results", NpgsqlDbType.Text);
            var id = update.Parameters.Add("id", NpgsqlDbType.Bigint);
            var wasResults = update.Parameters.Add("was_results", NpgsqlDbType.Text);
            var wasAdmins = update.Parameters.Add("was_admins", NpgsqlDbType.Text);
            var wasName = update.Parameters.Add("was_name", NpgsqlDbType.Text);
            foreach (var row in due)
            {
                (results.Value, id.Value, wasResults.Value, wasAdmins.Value, wasName.Value) = (row.Results, row.Id, row.StoredResults, row.StoredAdmins, row.StoredName);
                changed += await update.ExecuteNonQueryAsync(ct);   // 0 when another redaction changed the row first
            }
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct)
    {
        var kindText = KindText(kind);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT " + Columns + " FROM feed_checks WHERE tenant_id=@t AND kind=@k ORDER BY checked_at DESC, id DESC LIMIT 1", c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", kindText);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>The columns <see cref="Read"/> expects, in its order.</summary>
    internal const string Columns =
        "id, tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, confirmed_admins, kind, feed_names";

    /// <summary>One row selected with <see cref="Columns"/>.</summary>
    internal static FeedCheck Read(NpgsqlDataReader r)
    {
        var results = (JsonSerializer.Deserialize<StoredResult[]>(r.GetString(10), Json) ?? Array.Empty<StoredResult>())
            .Select(x => new CheckResult(x.Id ?? "", x.Title ?? "", ParseStatus(x.Status), x.Detail ?? "", x.Names)).ToList();
        var admins = JsonSerializer.Deserialize<string[]>(r.GetString(11), Json) ?? Array.Empty<string>();
        return new FeedCheck(r.GetString(1), PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(2)),
            r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), ParseOutcome(r.GetString(9)), results, admins)
        { Id = r.GetInt64(0), Kind = ParseKind(r.GetString(12)), Names = ParseNames(r.IsDBNull(13) ? null : r.GetString(13)) };
    }
}
