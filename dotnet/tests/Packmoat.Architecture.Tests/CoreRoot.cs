namespace Packmoat.Architecture.Tests;

/// <summary>
/// The core's root (#188): the folder that holds dotnet/Packmoat.sln, docs/ and deploy/. In this repository that is core/; once core/ is
/// published on its own, it is the repository's root. Every path here is relative to it, as the public repository will see it.
/// </summary>
internal static class CoreRoot
{
    public static string Path()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "dotnet", "Packmoat.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the core's root (the folder holding dotnet/Packmoat.sln) not found.");
    }

    public static string Read(params string[] parts) => File.ReadAllText(System.IO.Path.Combine([Path(), .. parts]));
}
