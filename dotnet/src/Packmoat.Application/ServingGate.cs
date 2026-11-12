using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// What a serving gate tells the customer: the base of every notice a gate may attach to its answer (each gate's own type,
/// from its module). The core shows a notice only as its site module renders it; it never looks inside one.
/// </summary>
public abstract record ServingNotice
{
    protected ServingNotice() { }
}

/// <summary>
/// A serving gate's answer about one tenant now. <see cref="Serve"/>, <see cref="ServeUntil"/> (serve, but not at or after
/// <see cref="ServeUntil.At"/>, so a cache never keeps it past then) or <see cref="Refuse"/>; any of them may carry a <see cref="Notice"/>
/// for the customer. <see cref="OperatorText"/> is the gate's one line for the operator's tenant page (null: the
/// gate has nothing to say).
/// </summary>
public abstract record ServingAnswer
{
    private ServingAnswer(ServingNotice? notice) => Notice = notice;

    /// <summary>What the customer's home page says, or null for nothing.</summary>
    public ServingNotice? Notice { get; }

    /// <summary>The operator's one line about this tenant's standing (shown by the admin site's module), or null when the gate has none.</summary>
    public string? OperatorText { get; init; }

    public bool Serves => this is not Refuse;

    /// <summary>When serving stops at a known time (<see cref="ServeUntil.At"/>, exclusive), or null for no such time.</summary>
    public DateTimeOffset? Until => this is ServeUntil u ? u.At : null;

    public sealed record Serve : ServingAnswer
    {
        public Serve(ServingNotice? notice = null) : base(notice) { }
    }

    /// <summary>Serves until <see cref="At"/> (exclusive): from then on a result built from this answer must not be used.</summary>
    public sealed record ServeUntil : ServingAnswer
    {
        public ServeUntil(DateTimeOffset at, ServingNotice? notice = null) : base(notice) => At = at;
        public DateTimeOffset At { get; }
    }

    /// <summary>Does not serve: feed tokens stop working and none are issued.</summary>
    public sealed record Refuse : ServingAnswer
    {
        public Refuse(ServingNotice? notice = null) : base(notice) { }
    }
}

/// <summary>
/// Whether a tenant serves, beyond the operator's switch. The core asks it on the proxy's token path (on a cache miss only), before
/// issuing a customer token, for the customer's home notice and for the operator's tenant page. The core's own gate
/// (<see cref="ServeAllGate"/>) serves every tenant; a module may register one gate of its own. A gate that throws never serves: the
/// proxy answers 503 and the sites show their error page.
/// </summary>
public interface IServingGate
{
    Task<ServingAnswer> AskAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>The core's gate: every tenant serves, with no notice and nothing for the operator. Used when no module registers a gate.</summary>
public sealed class ServeAllGate : IServingGate
{
    public static readonly ServeAllGate Instance = new();
    private static readonly Task<ServingAnswer> Answer = Task.FromResult<ServingAnswer>(new ServingAnswer.Serve());

    private ServeAllGate() { }

    public Task<ServingAnswer> AskAsync(string tenantId, CancellationToken cancellationToken) => Answer;
}

/// <summary>The one serving rule: the operator's switch, then the serving gate, then (where it applies) whether the upstream is ready.</summary>
public static class ServingRule
{
    /// <summary>
    /// The operator's switch and the gate only. Used on the proxy's per-request token path (<c>AuthenticateAsync</c>), which deliberately does
    /// not ask about the upstream: the deploy canary has no upstream and its <c>/v3/ping</c> must keep answering 204, and a tenant whose upstream
    /// is missing or refuses already fails closed on each request (there is no feed to call, or the credential source refuses it).
    /// </summary>
    public static ServingDecision Decide(bool tenantActive, ServingAnswer gate) =>
        !tenantActive ? ServingDecision.TenantSuspended
        : !gate.Serves ? ServingDecision.Refused
        : ServingDecision.Serve;

    /// <summary>
    /// The full rule, for issuing tokens on the customer site: the operator's switch, then the gate, then the upstream. A tenant with no
    /// upstream (or an unchecked shared one) serves nothing, so it issues nothing.
    /// </summary>
    public static ServingDecision Decide(bool tenantActive, ServingAnswer gate, UpstreamReadiness upstream)
    {
        var first = Decide(tenantActive, gate);
        if (first != ServingDecision.Serve) return first;
        return upstream switch
        {
            UpstreamReadiness.Ready => ServingDecision.Serve,
            UpstreamReadiness.NotVerified => ServingDecision.NotVerified,
            _ => ServingDecision.NotSetUp,   // and any value this build does not know: fail closed
        };
    }
}
