using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;

namespace Packmoat.Host.Tests;

public sealed partial class ProxyFixture
{
    // From M3 the host needs an audit log; the fixtures built with custom options record to the same in-memory log.
    static partial void ExtraServices(IServiceCollection services, ProxyFixture fixture) =>
        services.AddSingleton<IAuditLog>(fixture.Audit);
}
