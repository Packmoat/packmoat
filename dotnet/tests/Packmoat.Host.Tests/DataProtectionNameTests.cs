using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// The cookie keys' application name is pinned (#197 PR C): the entry assembly was renamed from Packmoat.Host to Packmoat.Hosted.Host, and
/// the core runs as Packmoat.Server. Data protection must not follow the entry point's name or folder, or every signed-in session and
/// antiforgery token issued before a deploy would stop decrypting after it.
/// </summary>
public class DataProtectionNameTests
{
    [Fact]   // mutation: drop .SetApplicationName("packmoat-admin") or change the name in SignedInSite.AddShared
    public void The_signed_in_sites_share_the_application_name_packmoat_admin()
    {
        var services = new ServiceCollection();
        SignedInSite.AddShared(services, keysDir: null);
        using var sp = services.BuildServiceProvider();
        Assert.Equal("packmoat-admin", sp.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Fact]   // mutation: drop .SetApplicationName (renaming it changes both sides, so only the test above catches a rename)
    public void A_payload_protected_under_the_old_entry_point_unprotects_under_the_new_ones()
    {
        var keys = Path.Combine(Path.GetTempPath(), "pm-dp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = Protector(keys, "Packmoat.Host", "/app/host-old").Protect("session");
            Assert.Equal("session", Protector(keys, "Packmoat.Hosted.Host", "/app/host").Unprotect(before));
            Assert.Equal("session", Protector(keys, "Packmoat.Server", "/srv/core").Unprotect(before));
        }
        finally { if (Directory.Exists(keys)) Directory.Delete(keys, true); }
    }

    private static IDataProtector Protector(string keysDir, string applicationName, string contentRoot)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new Environment(applicationName, contentRoot));
        SignedInSite.AddShared(services, keysDir);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    private sealed class Environment(string applicationName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = applicationName;
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
