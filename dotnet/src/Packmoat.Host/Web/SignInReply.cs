using Microsoft.AspNetCore.Http;

namespace Packmoat.Host.Web;

/// <summary>
/// Microsoft's answer on a sign-in callback, read only for its OAuth <c>error</c> code. Only the code is ever looked at: never
/// error_description, error_subcode or an AADSTS number, which Microsoft says not to build on. Nothing read here is shown on a page,
/// and only a code from <see cref="Known"/> is logged.
/// </summary>
internal static class SignInReply
{
    /// <summary>The person declined (access_denied: "Resource owner denied consent"), or consent is still needed (consent_required).</summary>
    private static readonly string[] Declined = { "access_denied", "consent_required" };

    /// <summary>The OAuth and OpenID Connect error codes a log line may name; anything else is logged as "other".</summary>
    private static readonly string[] Known =
    {
        "access_denied", "consent_required", "interaction_required", "login_required", "account_selection_required", "invalid_request",
        "invalid_client", "invalid_grant", "invalid_resource", "invalid_scope", "unauthorized_client", "unsupported_response_type",
        "server_error", "temporarily_unavailable",
    };

    /// <summary>Exact, case-sensitive: the error code says the sign-in was declined or still needs consent. Anything else is a failure.</summary>
    public static bool IsDeclined(string? error) => error is not null && Array.IndexOf(Declined, error) >= 0;

    /// <summary>What a log line may say about the code: the code itself when it is a known one, "none" when there was none, else "other".</summary>
    public static string LogCode(string? error) => error is null ? "none" : Array.IndexOf(Known, error) >= 0 ? error : "other";

    /// <summary>
    /// What a log line may say about the token endpoint's answer when a sign-in failed after Microsoft issued a code: its OAuth
    /// <c>error</c> code (invalid_client for an expired certificate, invalid_grant for a reused or expired code, ...) through the same
    /// allowlist as <see cref="LogCode"/>. The handler puts the code in the exception's Data["error"]; its error_description, also
    /// there, is never read. "none" when no code was issued on this request or the failure was not the token endpoint's.
    /// </summary>
    public static string TokenLogCode(HttpContext ctx, Exception? failure) =>
        SignedInSite.CodeWasReceived(ctx) && failure?.Data["error"] is string code ? LogCode(code) : "none";

    /// <summary>
    /// The callback's <c>error</c>: from the form of a form_post answer, otherwise from the query. A missing or repeated value is null.
    /// The OpenID Connect handler has already read the form by the time its failure event runs, so this reads no more of the request.
    /// </summary>
    public static async Task<string?> ErrorAsync(HttpRequest request)
    {
        try
        {
            var values = HttpMethods.IsPost(request.Method) && request.HasFormContentType
                ? (await request.ReadFormAsync(request.HttpContext.RequestAborted))["error"]
                : request.Query["error"];
            return values.Count == 1 ? values[0] : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or BadHttpRequestException) { return null; }   // an unreadable form: no code
    }
}
