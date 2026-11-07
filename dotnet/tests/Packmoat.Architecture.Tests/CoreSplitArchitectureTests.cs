using System.Reflection;
using System.Xml.Linq;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The open-source split (#197): core/ stands alone. No core project references a project outside core/, nothing in the core references
/// a hosted assembly, and the core grants its internals only to its own test assemblies. That core/ names none of the hosted service's
/// words or packages is checked from the hosted side (#197 C3), so that this repository's tests need not name them either.
/// </summary>
public class CoreSplitArchitectureTests
{
    /// <summary>core/: the folder that holds dotnet/ (found by dotnet/Packmoat.sln).</summary>
    private static string CoreRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        return dir?.Parent?.FullName ?? throw new InvalidOperationException("Packmoat.sln not found above the test output folder.");
    }

    private static bool Built(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private static string[] CoreProjects() =>
        Directory.GetFiles(Path.Combine(CoreRoot(), "dotnet"), "*.csproj", SearchOption.AllDirectories).Where(f => !Built(f)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The core's product assemblies: the libraries and the core's entry points.</summary>
    private static readonly Assembly[] CoreAssemblies =
    [
        typeof(Packmoat.Domain.Tenant).Assembly,
        typeof(Packmoat.Policy.PolicyEngine).Assembly,
        typeof(Packmoat.Application.TenantOrigin).Assembly,
        typeof(Packmoat.Protocols.NuGet.UrlMap).Assembly,
        typeof(Packmoat.Infrastructure.SqliteDatabase).Assembly,
        typeof(Packmoat.Host.PackmoatApp).Assembly,
        typeof(Packmoat.Admin.AdminCli).Assembly,
        Assembly.Load("Packmoat.Server"),   // the entry points declare only Program, so they are loaded by name
        Assembly.Load("Packmoat.Cli"),
    ];

    // The hosted service's assemblies, by name: all of them are Packmoat.Hosted.* (#197 PR C).
    private static bool IsHosted(string assembly) => assembly.StartsWith("Packmoat.Hosted", StringComparison.Ordinal);

    [Fact]   // mutation: a core project references ..\..\..\..\hosted\dotnet\src\Packmoat.Hosted.Application
    public void No_core_project_references_a_project_outside_core()
    {
        var core = CoreRoot() + Path.DirectorySeparatorChar;
        var projects = CoreProjects();
        Assert.True(projects.Length >= 13, "the scan found too few core projects to mean anything");
        var outside = projects.SelectMany(p => XDocument.Load(p).Descendants("ProjectReference")
                .Select(e => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p)!, ((string)e.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))))
                .Where(target => !target.StartsWith(core, StringComparison.Ordinal))
                .Select(target => $"{Path.GetFileName(p)} -> {target}"))
            .ToArray();
        Assert.True(outside.Length == 0, "core projects reference projects outside core/: " + string.Join(", ", outside));
    }

    [Fact]
    public void No_core_assembly_references_a_hosted_assembly()
    {
        foreach (var a in CoreAssemblies)
        {
            var hosted = a.GetReferencedAssemblies().Select(r => r.Name!).Where(IsHosted).ToArray();
            Assert.True(hosted.Length == 0, $"{a.GetName().Name} references {string.Join(", ", hosted)}");
        }
        Assert.Equal(["Packmoat.Admin", "Packmoat.Application", "Packmoat.Cli", "Packmoat.Domain", "Packmoat.Host", "Packmoat.Infrastructure", "Packmoat.Policy", "Packmoat.Protocols.NuGet", "Packmoat.Server"],
            CoreAssemblies.Select(a => a.GetName().Name!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]   // mutations: <InternalsVisibleTo Include="Packmoat.Hosted.Infrastructure.Tests" /> in Packmoat.Infrastructure; "Packmoat.Host.Tests" in Packmoat.Policy
    public void Each_core_assembly_grants_its_internals_only_to_its_own_tests()
    {
        foreach (var a in CoreAssemblies)
        {
            var name = a.GetName().Name!;
            var friends = a.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(v => v.AssemblyName)
                .Distinct().Order(StringComparer.Ordinal).ToArray();
            var stray = friends.Where(f => f != name + ".Tests").ToArray();
            Assert.True(stray.Length == 0, $"{name} grants its internals to {string.Join(", ", stray)}: only {name}.Tests may read them");
        }
    }

    [Fact]   // mutations: <InternalsVisibleTo Include="Packmoat.Hosted.Host" /> or "Packmoat.Hosted.Host.Tests" in Packmoat.Host (#197 C3 removed the latter)
    public void The_core_grants_its_internals_only_to_test_assemblies()
    {
        var friends = CoreAssemblies.SelectMany(a => a.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(v => v.AssemblyName)).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.All(friends, f => Assert.EndsWith(".Tests", f, StringComparison.Ordinal));
        Assert.Equal(["Packmoat.Host.Tests", "Packmoat.Infrastructure.Tests"], friends);
    }
}
