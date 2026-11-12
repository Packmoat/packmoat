namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Every BLOB column of the core's schema, and whether what it holds is encrypted with the master key (AesGcmSecretProtector). There is no
/// master-key rotation command yet (the KNOWN GAP in AesGcmSecretProtector); when one is written, it must re-encrypt every row this list
/// marks encrypted, and the guard below makes sure a new encrypted column cannot be left off the list.
/// </summary>
public static class MasterKeyEncryptedColumns
{
    /// <summary>One BLOB column, or the rows of it <paramref name="Rows"/> names; <paramref name="Encrypted"/>: master-key ciphertext.</summary>
    public sealed record Column(string Table, string Name, string? Rows, bool Encrypted, string What);

    public static IReadOnlyList<Column> All { get; } =
    [
        new("meta", "value", "key='key_check'", true, "the master key check (SqliteMasterKeyCheck.EnsureAsync)"),
        new("meta", "value", "key='purge_nudge_day'", false, "the last purge nudge day, as UTF-8 text"),
        new("upstream_identities", "cert_encrypted", null, true, "a tenant's own app certificate, bound to the tenant id"),
        new("shared_app_credentials", "cert_encrypted", null, true, "Packmoat's shared app certificate, bound to its slot"),
        new("cookie_keys", "xml_encrypted", null, true, "the cookie protection keys, bound to the row id"),
    ];
}

[Collection("sqlite-pools")]
public class MasterKeyEncryptedColumnsTests
{
    [Fact]   // mutations: drop cookie_keys (or any other entry) from the list; add a BLOB column to a migration without listing it
    public async Task Every_blob_column_in_a_fresh_schema_is_listed_as_master_key_encrypted_or_not()
    {
        await using var t = await TestDb.CreateAsync();
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.name || '.' || p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p
            WHERE m.type = 'table' AND upper(p.type) = 'BLOB' ORDER BY 1
            """;
        var blobs = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) blobs.Add(r.GetString(0));

        Assert.True(blobs.Count >= 4, "the scan found too few BLOB columns to mean anything");
        var listed = MasterKeyEncryptedColumns.All.Select(x => x.Table + "." + x.Name).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(blobs, listed);
        Assert.Equal(MasterKeyEncryptedColumns.All.Count, MasterKeyEncryptedColumns.All.Select(x => (x.Table, x.Name, x.Rows)).Distinct().Count());
        Assert.Contains(MasterKeyEncryptedColumns.All, x => x is { Table: "cookie_keys", Name: "xml_encrypted", Encrypted: true });
    }
}
