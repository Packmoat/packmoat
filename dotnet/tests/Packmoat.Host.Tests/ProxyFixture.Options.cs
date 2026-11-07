using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

public sealed partial class ProxyFixture
{
    static partial void ExtraServices(IServiceCollection services, ProxyFixture fixture);

    /// <summary>Same fixture, but with non-default host options (for example a small JSON size cap).</summary>
    public static async Task<ProxyFixture> StartWithOptionsAsync(Packmoat.Host.PackmoatOptions options)
    {
        var f = new ProxyFixture { Upstream = await FakeUpstream.StartAsync() };
        f.Acme = new Tenant("acme", "Acme", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, new TenantPolicy(14, new[] { "Contoso." }));
        f.Tenants.Add(AcmeToken, f.Acme);
        f._app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, services =>
        {
            services.AddSingleton<IClock>(f.Clock);
            services.AddSingleton<IPublishDateSource>(f.Dates);
            services.AddSingleton<ITenantAuthenticator>(f.Tenants);
            services.AddSingleton<IUpstreamCredentialSource>(new StaticBasicCredentialSource(FakeUpstream.User, FakeUpstream.Secret));
            services.AddSingleton(options);
            ExtraServices(services, f);   // later pull requests add what the host then requires
        });
        await f._app.StartAsync();
        f.BaseUrl = f._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');
        return f;
    }
}
