using Microsoft.Extensions.Logging;
using Packmoat.Application;

namespace Packmoat.Host.Web;

/// <summary>
/// Single-use form nonces with a short life. Antiforgery tokens can be reused; a nonce makes one rendered form act at most once, so a
/// reload or back-and-resubmit cannot quietly create a second live token. The nonces are kept in <see cref="IFormNonceStore"/>, the
/// database's, so every copy of the server sees them: a form shown by one copy can be posted to another, a used one is refused on all of
/// them, and a restart keeps them. Each site has its own scope (see <see cref="Packmoat.Host.Admin.AdminFormNonces"/> and
/// <see cref="Packmoat.Host.Customer.AppFormNonces"/>), so one site's nonce is never used on the other, and each key (a tenant, or a
/// person) holds at most <see cref="MaxPerKey"/> live ones in it, so one key cannot push out the others'.
/// <para>When the store cannot be reached the form is refused, never let through: a consume answers false, and an issue gives a nonce
/// the store never kept, so the form's post is refused as an old form. Each failure logs one warning naming the scope and the error's type
/// only (<see cref="LogCategory"/>). The caller's own cancellation is thrown.</para>
/// </summary>
public abstract class FormNonces
{
    /// <summary>How long a nonce lives.</summary>
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
    public const int MaxPerKey = 20;

    /// <summary>The log category of the store's failures.</summary>
    public const string LogCategory = "Packmoat.FormNonces";

    private readonly IFormNonceStore _store;
    private readonly ILogger? _log;

    protected FormNonces(IFormNonceStore store, string scope, ILoggerFactory? logs)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!FormNonceRules.IsValidScope(scope)) throw new ArgumentException("Not a form nonce scope.", nameof(scope));
        Scope = scope;
        _log = logs?.CreateLogger(LogCategory);
    }

    /// <summary>This site's scope in the store.</summary>
    public string Scope { get; }

    /// <summary>The line a failed <paramref name="action"/> in <paramref name="scope"/> is logged with: the scope and the error's type, nothing else.</summary>
    public static string FailureLine(string scope, string action, Exception error) =>
        $"Form nonces {scope}: the store failed on {action} ({error.GetType().Name}); the form is refused";

    /// <summary>A fresh nonce for one form shown to <paramref name="key"/>.</summary>
    public async Task<string> IssueAsync(string key, CancellationToken ct)
    {
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            await _store.IssueAsync(Scope, nonce, key, Life, MaxPerKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Warn("issue", ex);   // the form still shows; its post is refused, since the store never kept this nonce
        }
        return nonce;
    }

    /// <summary>True once for a live nonce issued for this key; false for anything else (and the nonce is gone either way).</summary>
    public async Task<bool> ConsumeAsync(string? nonce, string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        try
        {
            return await _store.ConsumeAsync(Scope, nonce, key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Warn("consume", ex);
            return false;
        }
    }

    private void Warn(string action, Exception error)
    {
        try { _log?.LogWarning("{Message}", FailureLine(Scope, action, error)); }
        catch (Exception) { /* reporting never changes the outcome: the form is refused either way */ }
    }
}
