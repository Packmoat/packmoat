using Packmoat.Application;
using Packmoat.Host.Modules;

namespace Packmoat.Host.Admin;

/// <summary>The admin site with no module: nothing on the tenant page beyond the core's, no actions and no notices.</summary>
public sealed class NoAdminSiteModule : IAdminSiteModule
{
    public static readonly NoAdminSiteModule Instance = new();

    private NoAdminSiteModule() { }

    public Task<string> TenantSectionAsync(AdminTenantPage page, ServingAnswer serving) => Task.FromResult("");

    public IReadOnlyList<AdminTenantAction> Actions { get; } = Array.Empty<AdminTenantAction>();

    public IReadOnlyDictionary<string, (bool Ok, string Text)> Notices { get; } = new Dictionary<string, (bool, string)>();
}
