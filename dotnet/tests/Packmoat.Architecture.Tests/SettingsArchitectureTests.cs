namespace Packmoat.Architecture.Tests;

/// <summary>
/// #233: the core reads its settings through one reader (Packmoat.Infrastructure/Settings.cs), so every setting can also come from a file and
/// fails the same way. No other source file in dotnet/src reads the environment. The module API is marked unstable before 1.0.
/// </summary>
public class SettingsArchitectureTests
{
    private const string Unstable = "Unstable: may change in any release before 1.0.";

    private static bool Built(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private static string Src() => Path.Combine(CoreRoot.Path(), "dotnet", "src");

    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(Src(), "*.cs", SearchOption.AllDirectories).Where(f => !Built(f));

    [Fact]   // mutation: put one Environment.GetEnvironmentVariable back in PackmoatApp
    public void Only_the_settings_reader_reads_the_environment()
    {
        var readers = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("GetEnvironmentVariable", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Src(), f).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["Packmoat.Infrastructure/Settings.cs"], readers);
    }

    [Theory]   // mutation: drop the line from one of them
    [InlineData("Packmoat.Host/Modules/IPackmoatModule.cs", "public interface IPackmoatModule")]
    [InlineData("Packmoat.Host/Modules/IPackmoatModule.cs", "public sealed record PackmoatModuleContext")]
    [InlineData("Packmoat.Host/Modules/IPackmoatModule.cs", "public sealed record ModuleSite")]
    [InlineData("Packmoat.Host/Modules/ICustomerSiteModule.cs", "public interface ICustomerSiteModule")]
    [InlineData("Packmoat.Host/Modules/IAdminSiteModule.cs", "public interface IAdminSiteModule")]
    [InlineData("Packmoat.Admin/IAdminCliModule.cs", "public interface IAdminCliModule")]
    // #278 step 0: the engine-neutral seam types and the lifecycle's ports a module implements
    [InlineData("Packmoat.Admin/IAdminCliModule.cs", "public sealed record AdminCliContext")]
    [InlineData("Packmoat.Infrastructure/IRelationalDatabase.cs", "public interface IRelationalDatabase")]
    [InlineData("Packmoat.Infrastructure/DbCommandParameters.cs", "public static class DbCommandParameters")]
    // #278 step 0, PR 3: the schema and key-check ports, and the track a module hands over
    [InlineData("Packmoat.Infrastructure/ISchemaMigrator.cs", "public interface ISchemaMigrator")]
    [InlineData("Packmoat.Application/MasterKeyCheck.cs", "public interface IMasterKeyCheck")]
    [InlineData("Packmoat.Infrastructure/MigrationTrack.cs", "public sealed partial class MigrationTrack")]
    [InlineData("Packmoat.Infrastructure/DatabaseEngine.cs", "public enum DatabaseEngine")]
    [InlineData("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", "public interface ITenantCreationParticipant")]
    [InlineData("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", "public interface IPurgeContributor")]
    [InlineData("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", "public interface IPurgeRecheck")]
    [InlineData("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", "public sealed record TenantLifecycle(")]
    [InlineData("Packmoat.Infrastructure/Lifecycle/TenantLifecycle.cs", "public sealed record TenantLifecycleFactory")]
    public void Each_module_api_type_is_marked_unstable(string file, string declaration)
    {
        var source = File.ReadAllText(Path.Combine(Src(), file)).Replace("\r\n", "\n");
        var at = source.IndexOf("\n" + declaration, StringComparison.Ordinal);
        Assert.True(at > 0, $"{declaration} not found in {file}");
        // The doc comment directly above the declaration: the run of /// lines that ends there.
        var lines = source[..at].Split('\n').Reverse().TakeWhile(l => l.TrimStart().StartsWith("///", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(Unstable, StringComparison.Ordinal));
    }

    [Fact]   // mutation: drop the sentence from architecture.md's Modules paragraph
    public void The_architecture_doc_marks_the_module_api_unstable()
    {
        var modules = CoreRoot.Read("docs", "architecture.md").Split('\n').Single(l => l.StartsWith("**Modules** add ", StringComparison.Ordinal));
        Assert.Contains("The module interfaces may change in any release before 1.0.", modules);
    }

    [Fact]   // no [Experimental]: a module author is told in words, not stopped by a compiler error
    public void No_module_api_type_is_marked_experimental() =>
        Assert.DoesNotContain(SourceFiles(), f => File.ReadAllText(f).Contains("[Experimental", StringComparison.Ordinal));
}
