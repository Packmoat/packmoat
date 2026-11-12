using System.Reflection;
using System.Xml.Linq;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The dependency rule from the architecture: Domain, Policy and Application know nothing about HTTP, YARP,
/// ASP.NET, Azure, Redis, Cosmos or databases. If this test fails, the build is wrong, not the test.
/// </summary>
public class DependencyRuleTests
{
    private static readonly string[] ForbiddenPrefixes =
    {
        "Yarp", "Azure", "Microsoft.Azure", "Microsoft.Identity", "Microsoft.AspNetCore", "Microsoft.Extensions.Http",
        "StackExchange.Redis", "Microsoft.Azure.Cosmos", "Microsoft.Data.Sqlite", "SQLitePCL", "Npgsql", "Dapper",
        "System.Net.Http", "System.Net.Sockets", "System.Net.Security", "System.Data",
    };

    private static Assembly Load(string name) => Assembly.Load(name);

    private static IEnumerable<string> References(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Theory]
    [InlineData("Packmoat.Domain")]
    [InlineData("Packmoat.Policy")]
    [InlineData("Packmoat.Application")]
    public void Core_assemblies_reference_no_infrastructure(string assembly)
    {
        var bad = References(Load(assembly))
            .Where(r => ForbiddenPrefixes.Any(p => r.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(bad.Length == 0, $"{assembly} must not reference: {string.Join(", ", bad)}");
    }

    [Fact]
    public void Domain_references_no_other_Packmoat_assembly()
        => Assert.DoesNotContain(References(Load("Packmoat.Domain")), r => r.StartsWith("Packmoat."));

    [Fact]
    public void Policy_depends_only_on_Domain()
        => Assert.DoesNotContain(References(Load("Packmoat.Policy")), r => r.StartsWith("Packmoat.") && r != "Packmoat.Domain");

    [Fact]
    public void Application_depends_only_on_Domain_and_Policy()
        => Assert.DoesNotContain(References(Load("Packmoat.Application")), r => r.StartsWith("Packmoat.") && r is not ("Packmoat.Domain" or "Packmoat.Policy"));

    [Fact]
    public void Protocol_adapter_does_not_reference_infrastructure_or_host()
    {
        var packmoat = References(Load("Packmoat.Protocols.NuGet")).Where(r => r.StartsWith("Packmoat.")).ToArray();
        Assert.DoesNotContain("Packmoat.Infrastructure", packmoat);
        Assert.DoesNotContain("Packmoat.Host", packmoat);
    }

    [Fact]
    public void Infrastructure_does_not_reference_the_host_or_the_protocol_adapter()
    {
        var packmoat = References(Load("Packmoat.Infrastructure")).Where(r => r.StartsWith("Packmoat.")).ToArray();
        Assert.DoesNotContain("Packmoat.Host", packmoat);
        Assert.DoesNotContain("Packmoat.Protocols.NuGet", packmoat);
    }

    // ---- Project-file checks: catch a forbidden package or project reference even if no code uses it yet.

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Packmoat.sln not found above the test output folder.");
    }

    private static XDocument Project(string name) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", name, name + ".csproj"));

    private static string[] PackageRefs(string name) =>
        Project(name).Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToArray();

    private static string[] ProjectRefs(string name) =>
        Project(name).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
            .OrderBy(x => x).ToArray();

    [Theory]
    [InlineData("Packmoat.Domain")]
    [InlineData("Packmoat.Policy")]
    [InlineData("Packmoat.Application")]
    public void Core_projects_have_no_package_references_at_all(string project)
        => Assert.True(PackageRefs(project).Length == 0, $"{project} must stay package-free but references: {string.Join(", ", PackageRefs(project))}");

    [Fact]
    public void Only_the_Host_references_the_sign_in_packages()
    {
        foreach (var project in new[] { "Packmoat.Domain", "Packmoat.Policy", "Packmoat.Application", "Packmoat.Protocols.NuGet", "Packmoat.Infrastructure", "Packmoat.Admin" })
            Assert.DoesNotContain(PackageRefs(project), p => p.Contains("OpenIdConnect") || p.Contains("Authentication") || p.Contains("IdentityModel"));
        Assert.Contains("Microsoft.AspNetCore.Authentication.OpenIdConnect", PackageRefs("Packmoat.Host"));
    }

    [Fact]
    public void Core_projects_only_reference_the_projects_the_architecture_allows()
    {
        Assert.Empty(ProjectRefs("Packmoat.Domain"));
        Assert.Equal(new[] { "Packmoat.Domain" }, ProjectRefs("Packmoat.Policy"));
        Assert.Equal(new[] { "Packmoat.Domain", "Packmoat.Policy" }, ProjectRefs("Packmoat.Application"));
    }

    private const string PostgresAdapter = "Packmoat.Infrastructure.Postgres";

    [Fact]   // mutation: a ProjectReference from the adapter to Packmoat.Host
    public void The_Postgres_adapter_references_only_Domain_Application_and_Infrastructure()
    {
        Assert.Equal(new[] { "Packmoat.Application", "Packmoat.Domain", "Packmoat.Infrastructure" }, ProjectRefs(PostgresAdapter));
        Assert.Equal(new[] { "Npgsql" }, PackageRefs(PostgresAdapter));
    }

    /// <summary>Every project folder under src/ but the adapter's own.</summary>
    private static string[] OtherSrcProjects() =>
        Directory.GetDirectories(Path.Combine(RepoRoot(), "src")).Select(Path.GetFileName).Where(n => n != PostgresAdapter)
            .Where(n => File.Exists(Path.Combine(RepoRoot(), "src", n!, n + ".csproj"))).Select(n => n!).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The composition roots: PackmoatApp (Packmoat.Host) picks the server's engine and AdminCli (Packmoat.Admin) the command
    /// line's, so they, and only they, reference the PostgreSQL adapter. The entry points (Packmoat.Server, Packmoat.Cli) reach it through
    /// them; the core (Domain, Policy, Application), the protocol adapter and the SQLite adapters never do.
    /// </summary>
    internal static readonly string[] CompositionRoots = ["Packmoat.Admin", "Packmoat.Host"];

    // Declared: the adapter is selectable, so the composition roots reference it. Npgsql is
    // only ever the adapter's package.
    [Fact]   // mutations: a ProjectReference from Packmoat.Application (or Infrastructure, or Protocols.NuGet) to the adapter; Npgsql added to Packmoat.Host
    public void Only_the_composition_roots_reference_the_Postgres_adapter_and_none_references_Npgsql_itself()
    {
        var projects = OtherSrcProjects();
        Assert.True(projects.Length >= 9, "too few src projects found to mean anything: " + string.Join(", ", projects));
        var referencing = projects.Where(p => ProjectRefs(p).Contains(PostgresAdapter)).ToArray();
        Assert.Equal(CompositionRoots, referencing);
        foreach (var project in projects)
            Assert.DoesNotContain(PackageRefs(project), p => p.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]   // the images publish these two: each reaches the adapter through a composition root, so the image carries it (CoreImageTests)
    [InlineData("Packmoat.Server", "Packmoat.Host")]
    [InlineData("Packmoat.Cli", "Packmoat.Admin")]
    public void Each_entry_point_reaches_the_adapter_through_its_composition_root(string entryPoint, string root)
    {
        Assert.Equal([root], ProjectRefs(entryPoint));
        Assert.Contains(PostgresAdapter, ProjectRefs(root));
        Assert.Contains(References(Load(root)), r => r == PostgresAdapter);   // and really uses it (a reference no code uses is dropped)
    }

    [Fact]
    public void The_forbidden_list_really_detects_things_the_host_uses()
    {
        // Negative control: if this fails, the checks above could be passing vacuously.
        var host = References(Load("Packmoat.Host")).ToArray();
        Assert.Contains(host, r => ForbiddenPrefixes.Any(p => r.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }
}
