using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// Calls the customer's feed with OUR credential only. The caller's own Authorization header is never copied.
/// Redirects (for example the 303 to blob storage) are followed WITHOUT any credential, never from https to http,
/// and at most five hops.
/// </summary>
public sealed class UpstreamHttpClient : IUpstreamClient
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly IUpstreamCredentialSource _credentials;

    public UpstreamHttpClient(HttpClient http, IUpstreamCredentialSource credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public async Task<UpstreamResponse> GetAsync(Tenant tenant, Uri url, string? accept, CancellationToken cancellationToken)
    {
        // Defence in depth: the proxy only builds URLs under the tenant's base, but never trust that blindly.
        if (!url.AbsoluteUri.StartsWith(tenant.UpstreamBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("URL is outside the tenant's upstream base.");

        var credential = await _credentials.GetAsync(tenant, cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"{credential.Scheme} {credential.Parameter}");
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);

        var response = await SendAsync(request, cancellationToken);
        var current = url;
        for (var hop = 0; hop < MaxRedirects && IsRedirect((int)response.StatusCode); hop++)
        {
            var location = response.Headers.Location ?? throw new InvalidOperationException("Redirect without a location.");
            if (!location.IsAbsoluteUri) location = new Uri(current, location);
            if (location.Scheme is not ("http" or "https")) throw new InvalidOperationException("Redirect to an unsupported scheme.");
            if (current.Scheme == "https" && location.Scheme == "http") throw new InvalidOperationException("Redirect downgrades https to http.");
            response.Dispose();
            current = location;
            response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, location), cancellationToken); // no credential
        }
        if (IsRedirect((int)response.StatusCode)) { response.Dispose(); throw new InvalidOperationException("Too many redirects."); }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new UpstreamResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength,
            body,
            () => { response.Dispose(); return ValueTask.CompletedTask; });
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HeaderTimeout);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    private static bool IsRedirect(int status) => status is 301 or 302 or 303 or 307 or 308;
}
