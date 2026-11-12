using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="DecisionRecordQueryContract"/> on PostgreSQL, each test on a database of its own. The query plans are
/// SQLite's own and stay in <see cref="DecisionRecordQueryTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresDecisionRecordQueryTests : DecisionRecordQueryContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="DecisionRecordQueryContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class DecisionRecordQueryTests : DecisionRecordQueryContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Theory]
    [InlineData(null, false, null, "ix_decision_records_tenant_seen")]
    [InlineData("moq", false, null, "PRIMARY KEY")]
    [InlineData("moq", false, "1.0.0", "PRIMARY KEY")]
    [InlineData("moq", true, null, "PRIMARY KEY")]
    public async Task Every_query_shape_searches_an_index_and_never_scans_the_table(string? package, bool prefix, string? version, string index)
    {
        var (t, _) = await SeedAsync(Rec("moq"));
        await using var __ = t;
        var plan = await SqliteDecisionRecordQuery.PlanAsync(((TestDb)t).Db, new DecisionRecordQuery("acme", package, prefix, version));
        Assert.True(plan.Contains("SEARCH") && plan.Contains(index), plan);
        Assert.DoesNotContain("SCAN decision_records", plan);
        // A whole tenant is never sorted: the index already gives the newest first (at most ties are ordered among themselves).
        if (package is null) Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", plan);
        // A prefix seek is bounded at both ends, so it stops at the end of the prefix instead of running to the tenant's last id.
        if (prefix) Assert.True(plan.Contains("package_id>? AND package_id<?"), plan);
    }
}
