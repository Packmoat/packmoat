using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>A credential for the customer's upstream feed. Its text form never shows the secret.</summary>
public sealed record UpstreamCredential(string Scheme, string Parameter)
{
    public override string ToString() => $"{Scheme} <redacted>";
}

/// <summary>Hides how we authenticate to the customer's feed (service-principal certificate, pass-through, test fake).</summary>
public interface IUpstreamCredentialSource
{
    ValueTask<UpstreamCredential> GetAsync(Tenant tenant, CancellationToken cancellationToken);
}

/// <summary>An upstream answer. Dispose it to release the connection.</summary>
public sealed class UpstreamResponse : IAsyncDisposable
{
    private readonly Func<ValueTask>? _onDispose;

    public UpstreamResponse(int statusCode, string? contentType, long? contentLength, Stream body, Func<ValueTask>? onDispose = null)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        ContentLength = contentLength;
        Body = body;
        _onDispose = onDispose;
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public long? ContentLength { get; }
    public Stream Body { get; }
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        if (_onDispose is not null) await _onDispose();
    }
}

/// <summary>Fetches from the customer's feed. Redirects (for example to blob storage) are followed WITHOUT credentials.</summary>
public interface IUpstreamClient
{
    Task<UpstreamResponse> GetAsync(Tenant tenant, Uri url, string? accept, CancellationToken cancellationToken);
}

/// <summary>Publish dates by normalised version. Null means the source could not answer: callers must fail closed.</summary>
public interface IPublishDateSource
{
    Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(Ecosystem ecosystem, string packageId, CancellationToken cancellationToken);
}

/// <summary>
/// Who a valid token belongs to. TokenId identifies the token in logs and audit; it is not the token. <c>TokenExpiresAt</c> is when the
/// token stops working (null: never), so a cache can never keep an expired token alive. <c>ServeUntil</c> is when the serving gate stops serving
/// the tenant at a known time (for example the end of a free period, #174; null: no such time), so a cache can never serve it past that either.
/// </summary>
public sealed record AuthResult(Tenant Tenant, string TokenId, DateTimeOffset? TokenExpiresAt = null, DateTimeOffset? ServeUntil = null)
{
    /// <summary>The earlier of <see cref="TokenExpiresAt"/> and <see cref="ServeUntil"/>: from then on this result must not be used. Null: neither.</summary>
    public DateTimeOffset? StopsAt => TokenExpiresAt is { } e ? (ServeUntil is { } s && s < e ? s : e) : ServeUntil;
}

/// <summary>Turns a presented token into a tenant and token id, or null. Never logs the token.</summary>
public interface ITenantAuthenticator
{
    ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken cancellationToken);
}
