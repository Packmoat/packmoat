using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The core is published as its own repository, with this folder as its root (#274). There, a path that starts with the private
/// repository's folder name names nothing, so no text file in the core may name one: dotnet/src, not the same path under that folder.
/// </summary>
public class CorePathTests
{
    // The folder's name and its four top-level folders, put together here, so this file names none of the paths itself.
    private const string Folder = "core";

    private static readonly Regex StalePath = new(@"(?<![\w.-])" + Folder + @"/(?:dotnet|spec|docs|deploy)(?![\w-])", RegexOptions.CultureInvariant);

    /// <summary>Each line of a text file under <paramref name="root"/> that names such a path, as "file:line: path".</summary>
    internal static string[] StalePaths(string root) =>
        PublicFixtureTests.TextFiles(root).SelectMany(f =>
            {
                var name = Path.GetRelativePath(root, f).Replace('\\', '/');
                return File.ReadAllText(f).Split('\n').SelectMany((line, i) => StalePath.Matches(line).Select(m => $"{name}:{i + 1}: {m.Value}"));
            })
            .Order(StringComparer.Ordinal).ToArray();

    [Fact]   // mutations: a dotnet path under the folder put back in the Dockerfile's build command; in a schema's description; in a doc
    public void No_text_file_in_the_core_names_a_path_under_the_private_repositorys_folder()
    {
        var root = CoreRoot.Path();
        Assert.True(PublicFixtureTests.TextFiles(root).Length > 100, "the scan found too few files in the core to mean anything");
        var found = StalePaths(root);
        Assert.True(found.Length == 0, "the core names paths as the private repository sees them. Name them from the core's root (dotnet/, spec/, " +
            "docs/, deploy/):\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void The_scan_sees_each_folder_in_any_file_and_skips_other_words()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-paths-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "spec", "schema"));
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), $"# build:\n#   docker build -t x {Folder}/dotnet\n");
            File.WriteAllText(Path.Combine(dir, "spec", "schema", "a.json"), $"{{ \"description\": \"See {Folder}/spec/README.md.\" }}");
            File.WriteAllText(Path.Combine(dir, "guide.md"), string.Join('\n',
                $"See `{Folder}/docs/architecture.md` and ({Folder}/deploy/backup.sh).",          // 1: two
                $"ghcr.io/packmoat/packmoat-{Folder}/dotnet and hosted-{Folder}/docs",            // 2: part of another name
                $"the {Folder}/dotnet-tools and {Folder}/specs folders, {Folder}/.github",        // 3: other folders
                $"{Folder.ToUpperInvariant()}/DOCS is not a path either",                        // 4: paths are lower case
                $"[link](../{Folder}/spec/README.md)"));                                         // 5: after ../
            File.WriteAllText(Path.Combine(dir, "obj", "x.cs"), $"{Folder}/dotnet");              // build output is not read
            Assert.Equal(new[]
            {
                $"Dockerfile:2: {Folder}/dotnet",
                $"guide.md:1: {Folder}/deploy",
                $"guide.md:1: {Folder}/docs",
                $"guide.md:5: {Folder}/spec",
                $"spec/schema/a.json:1: {Folder}/spec",
            }, StalePaths(dir));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir); }
    }
}
