using System.Text;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Host.Auth;

/// <summary>
/// Authenticates every request with the Packmoat token (Basic password or Bearer). A request with no or a bad token
/// is answered 401 here, before anything is sent upstream. The token is never logged.
/// </summary>
public sealed class TokenAuthMiddleware
{
    public const string TenantItemKey = "packmoat.tenant";
    public const string TokenIdItemKey = "packmoat.tokenId";

    private readonly RequestDelegate _next;
    private readonly ITenantAuthenticator _tenants;
    private readonly TokenGuessLimiter _limiter;
    private readonly TokenRateLimiter _tokenLimiter;
    private readonly ILogger<TokenAuthMiddleware> _log;

    public TokenAuthMiddleware(RequestDelegate next, ITenantAuthenticator tenants, TokenGuessLimiter limiter, TokenRateLimiter tokenLimiter, ILogger<TokenAuthMiddleware> log)
    {
        _next = next;
        _tenants = tenants;
        _limiter = limiter;
        _tokenLimiter = tokenLimiter;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The health and readiness checks answer without a token: a probe has none, and neither says anything about a tenant.
        if (context.Request.Path.StartsWithSegments("/healthz") || context.Request.Path.StartsWithSegments(Readiness.Route)) { await _next(context); return; }

        var address = ClientKey.Of(context.Connection.RemoteIpAddress);
        var token = ExtractToken(context.Request.Headers.Authorization.ToString());
        if (token is null)
        {
            // NuGet asks every URL first with no credentials and retries after this 401 (NuGet/Home#12517): that costs a
            // fixed answer and no lookup, so it is not counted. A blocked address is still refused.
            if (_limiter.RetryAfterSeconds(address) is { } blockedFor) { TooMany(context, blockedFor); return; }
            _log.LogDebug("Challenged request {Method} {Path}: no credentials", context.Request.Method, context.Request.Path.Value);
            Challenge(context);
            return;
        }

        // Reserve, then settle: the reservation is held while the store is asked, so parallel guesses from one address
        // cannot all reach the store before the first is counted. It is released in the finally, whatever the outcome.
        if (!_limiter.TryBeginLookup(address, out var lookup, out var wait)) { TooMany(context, wait); return; }
        AuthResult? auth;
        string? wrongToken = null;
        try
        {
            auth = await _tenants.AuthenticateAsync(token, context.RequestAborted);
            if (auth is null) wrongToken = token;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // The token store failed. Not a bad token: tell the client to retry, and keep failing closed.
            _log.LogError(ex, "Token store unavailable");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        finally
        {
            _limiter.EndLookup(lookup, wrongToken);
        }

        if (auth is null)
        {
            // Only a presented token that matched no one is counted (in the finally above).
            _log.LogWarning("Rejected request {Method} {Path}: wrong token", context.Request.Method, context.Request.Path.Value);
            Challenge(context);
            return;
        }

        if (_tokenLimiter.TryAcquire(auth.TokenId) is { } slow)
        {
            _log.LogWarning("Token {TokenId} is over its request ceiling", auth.TokenId);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = slow.ToString();
            return;
        }

        context.Items[TenantItemKey] = auth.Tenant;
        context.Items[TokenIdItemKey] = auth.TokenId;
        await _next(context);
    }

    private static void TooMany(HttpContext context, int retryAfterSeconds)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
    }

    private static void Challenge(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Packmoat\"";
    }

    public static Tenant GetTenant(HttpContext context) => (Tenant)context.Items[TenantItemKey]!;
    public static string GetTokenId(HttpContext context) => (string)context.Items[TokenIdItemKey]!;

    internal static string? ExtractToken(string header)
    {
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return NullIfEmpty(header[7..].Trim());
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = decoded.IndexOf(':');
            return NullIfEmpty(colon < 0 ? decoded : decoded[(colon + 1)..]);
        }
        catch (FormatException) { return null; }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
