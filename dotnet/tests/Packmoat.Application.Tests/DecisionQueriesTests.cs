using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>
/// The support view's read of the decision record: operator only, bounded, and what the operator types is normalised the way
/// the recorder stores it before the store is asked.
/// </summary>
public class DecisionQueriesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class FakeDecisionQuery : IDecisionRecordQuery
    {
        public List<DecisionRecordQuery> Asked { get; } = new();
        public Task<DecisionRecordPage> QueryAsync(DecisionRecordQuery query, CancellationToken ct)
        {
            Asked.Add(query);
            return Task.FromResult(new DecisionRecordPage(Array.Empty<DecisionRecord>(), false));
        }
    }

    private readonly FakeStore _store = new();
    private readonly FakeDecisionQuery _decisions = new();
    private readonly AdminQueries _queries;

    public DecisionQueriesTests() => _queries = new AdminQueries(_store, _store, _store, _store, decisions: _decisions);

    [Fact]
    public async Task The_operator_reads_with_the_package_lower_cased_and_the_version_normalised()
    {
        Assert.True(_queries.HasDecisionRecord);
        await _queries.DecisionsAsync(ActorScope.Operator, new DecisionRecordQuery("acme", "  Moq ", Version: " 4.21 ", Limit: 20), Ct);
        Assert.Equal(new DecisionRecordQuery("acme", "moq", false, "4.21.0", 20), Assert.Single(_decisions.Asked));
    }

    [Fact]
    public async Task Blank_filters_mean_no_filter()
    {
        await _queries.DecisionsAsync(ActorScope.Operator, new DecisionRecordQuery("acme", " ", Version: ""), Ct);
        Assert.Equal(new DecisionRecordQuery("acme"), Assert.Single(_decisions.Asked));
    }

    [Fact]
    public async Task A_tenant_actor_is_refused_even_for_its_own_tenant_and_the_store_is_not_read()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.DecisionsAsync(ActorScope.ForTenant("acme"), new DecisionRecordQuery("acme"), Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _queries.DecisionsAsync(ActorScope.ForTenant("acme"), new DecisionRecordQuery("other"), Ct));
        Assert.Empty(_decisions.Asked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DecisionRecordQuery.MaxLimit + 1)]
    public async Task The_limit_is_bounded(int limit)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.DecisionsAsync(ActorScope.Operator, new DecisionRecordQuery("acme", Limit: limit), Ct));
        Assert.Empty(_decisions.Asked);
    }

    [Fact]
    public async Task The_largest_limit_is_allowed()
    {
        await _queries.DecisionsAsync(ActorScope.Operator, new DecisionRecordQuery("acme", Limit: DecisionRecordQuery.MaxLimit), Ct);
        Assert.Single(_decisions.Asked);
    }

    [Fact]
    public async Task Bad_filters_are_refused_before_the_store_is_read()
    {
        var op = ActorScope.Operator;
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.DecisionsAsync(op, new DecisionRecordQuery(" "), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.DecisionsAsync(op, new DecisionRecordQuery("acme", Version: "1.0.0"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.DecisionsAsync(op, new DecisionRecordQuery("acme", PackagePrefix: true), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.DecisionsAsync(op, new DecisionRecordQuery("acme", new string('a', RecordingPackageGate.MaxFieldLength + 1)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _queries.DecisionsAsync(op, new DecisionRecordQuery("acme", "moq", Version: new string('1', RecordingPackageGate.MaxFieldLength + 1)), Ct));
        Assert.Empty(_decisions.Asked);
        await _queries.DecisionsAsync(op, new DecisionRecordQuery("acme", new string('a', RecordingPackageGate.MaxFieldLength)), Ct);
        Assert.Single(_decisions.Asked);
    }

    [Fact]
    public async Task Without_a_decision_record_store_the_read_says_so()
    {
        var none = new AdminQueries(_store, _store, _store, _store);
        Assert.False(none.HasDecisionRecord);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.DecisionsAsync(ActorScope.Operator, new DecisionRecordQuery("acme"), Ct));
    }
}
