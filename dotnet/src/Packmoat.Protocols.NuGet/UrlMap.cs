namespace Packmoat.Protocols.NuGet;

/// <summary>Rewrites URLs that point at the upstream host so clients come back through Packmoat.</summary>
public sealed record UrlMap(string UpstreamBase, string PublicProxyBase)
{
    public string Rewrite(string text) => text.Replace(UpstreamBase, PublicProxyBase, StringComparison.OrdinalIgnoreCase);
}
