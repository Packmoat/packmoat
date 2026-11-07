using Packmoat.Domain;

namespace Packmoat.Host.Proxy;

/// <summary>Raised when the answer depends on publish dates that cannot be fetched right now. The proxy turns it into a retryable 503.</summary>
internal sealed class TemporarilyUnavailableException : Exception;

/// <summary>Decides what a version list or registration keeps. Anything not positively allowed is dropped.</summary>
internal static class VerdictFilter
{
    internal static bool IsHidden(IReadOnlyDictionary<string, Verdict> verdicts, string version)
    {
        if (!verdicts.TryGetValue(version, out var verdict)) return true;     // a version nobody decided on is hidden
        // A list cannot be filtered honestly while dates are unavailable: a shorter list would look like "no such version".
        if (verdict.Reason == VerdictReason.DateSourceUnavailable) throw new TemporarilyUnavailableException();
        return !verdict.IsAllowed;
    }
}
