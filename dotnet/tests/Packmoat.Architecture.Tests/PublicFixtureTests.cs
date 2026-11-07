using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The public code's examples are made up (#187): every email address in core/ is at a reserved example domain (RFC 2606 and 6761:
/// .example, .test, .invalid, .localhost and example.com, .net and .org), and core/ names no packmoat.com address and no packmoat.com
/// host. The hosted service's own addresses are settings (#186); a test uses TestOperator's Contoso instead.
/// </summary>
public class PublicFixtureTests
{
    /// <summary>
    /// Not the code's own text: build output (bin, obj) and git's metadata (a .git folder, or a worktree's .git file), which holds every
    /// contributor's address and branch names once core/ is a repository's root. Read from the path below the scan's root.
    /// </summary>
    internal static bool Skipped(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git");

    /// <summary>Every text file under <paramref name="root"/> (bin, obj and .git aside), whatever its name: no NUL byte in the first 8 KB.</summary>
    internal static string[] TextFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !Skipped(root, f) && IsText(f)).ToArray();

    private static bool IsText(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[8192];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Array.IndexOf(head, (byte)0, 0, read) < 0;
    }

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})", RegexOptions.CultureInvariant);

    private static readonly Regex HostedHost = new(@"[A-Za-z0-9-]+\.packmoat\.com", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] ReservedSuffixes = [".example", ".test", ".invalid", ".localhost", "example.com", "example.net", "example.org"];

    private static bool Reserved(string domain) =>
        ReservedSuffixes.Any(s => domain.EndsWith(s, StringComparison.OrdinalIgnoreCase)
            && (s.StartsWith('.') || domain.Length == s.Length || domain[domain.Length - s.Length - 1] == '.'));

    /// <summary>
    /// The named exceptions, as "file: address", each with the exact number of times it may appear there. An Azure DevOps feed URL with
    /// a user name before its host (a URL, not an address) is what those tests strip or refuse; and the one packmoat.com address is the
    /// hosted-domain scanner's own test, which must show it finds one. The code of conduct's contact (#265) is the one real address the
    /// public repository gives out. One more of any of them is an offender.
    /// </summary>
    private static readonly Dictionary<string, int> Named = new(StringComparer.Ordinal)
    {
        [Address("Packmoat.Host.Tests/AdminWriteTests.cs", "user", "pkgs.dev.azure.com")] = 2,
        [Address("Packmoat.Host.Tests/AdminWriteTests.cs", "pw", "pkgs.dev.azure.com")] = 1,
        [Address("Packmoat.Host.Tests/DelegatedFeedCheckTests.cs", "user", "feeds.dev.azure.com")] = 1,
        [Address("Packmoat.Host.Tests/DelegatedFeedCheckTests.Private.cs", "user", "feeds.dev.azure.com")] = 1,
        [Address("Packmoat.Architecture.Tests/OperatorLinksArchitectureTests.cs", "support", "packmoat.com")] = 2,
        [AtRoot("CODE_OF_CONDUCT.md", "support", "packmoat.com")] = 1,
    };

    // An exception as the scan reports it. Put together here, so this file names none of the addresses itself.
    private static string Address(string testFile, string local, string domain) => $"dotnet/tests/{testFile}: {local}@{domain}";

    private static string AtRoot(string file, string local, string domain) => $"{file}: {local}@{domain}";

    /// <summary>Every address not at a reserved domain, and every packmoat.com host, as "file: value", with how many times each appears.</summary>
    internal static Dictionary<string, int> Counts(string root, IEnumerable<string> files) =>
        files.SelectMany(f =>
            {
                var name = Path.GetRelativePath(root, f).Replace('\\', '/');
                var text = File.ReadAllText(f);
                var emails = Email.Matches(text).Where(m => !Reserved(m.Groups[1].Value)).Select(m => $"{name}: {m.Value}");
                var hosts = HostedHost.Matches(text).Select(m => $"{name}: {m.Value}");
                return emails.Concat(hosts);
            })
            .GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>What breaks the rule, in order: anything not named, or named but appearing more often than its exception allows.</summary>
    internal static string[] Offenders(string root, IEnumerable<string> files, IReadOnlyDictionary<string, int> named) =>
        Counts(root, files).Where(x => x.Value > named.GetValueOrDefault(x.Key))
            .Select(x => named.ContainsKey(x.Key) ? $"{x.Key} ({x.Value} times, {named[x.Key]} allowed)" : x.Key)
            .Order(StringComparer.Ordinal).ToArray();

    [Fact]   // mutations: TestOperator.SupportEmail set back to the hosted service's address; a third one in OperatorLinksArchitectureTests
    public void Every_address_in_core_is_made_up_and_no_packmoat_com_host_is_named()
    {
        var files = TextFiles(CoreRoot.Path());
        Assert.True(files.Length > 100, "the scan found too few files in core/ to mean anything");
        var found = Offenders(CoreRoot.Path(), files, Named);
        Assert.True(found.Length == 0, "core/ names a real address or a packmoat.com host. Use a reserved example domain (contoso.example) or " +
            "TestOperator:\n" + string.Join("\n", found.Select(x => "  " + x)));
    }

    [Fact]
    public void Each_named_exception_is_still_there_exactly()
    {
        // A named exception that matches less often than it says is stale: lower or drop it, so the list stays the true one.
        var root = CoreRoot.Path();
        static string[] Lines(IEnumerable<KeyValuePair<string, int>> d) => d.Select(x => $"{x.Key} x{x.Value}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Lines(Named), Lines(Counts(root, TextFiles(root))));
    }

    [Fact]
    public void The_scan_sees_a_real_address_and_a_hosted_host_in_any_file()
    {
        // The hosted domain and the real addresses are put together here, so this file names none of them itself.
        const string Hosted = "packmoat" + ".com";
        static string At(string local, string domain) => local + "@" + domain;
        var dir = Directory.CreateTempSubdirectory("packmoat-fixtures-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), $"x = \"support@{Hosted}\"; y = \"ada@contoso.example\"; z = \"bob@example.com\";");
            File.WriteAllText(Path.Combine(dir, "b.md"), $"Served at https://feeds.{Hosted}/v3/index.json, mail me at {At("me", "gmail.com")}.");
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), $"# ADMIN.{Hosted.ToUpperInvariant()}");
            File.WriteAllText(Path.Combine(dir, "ok.cs"), $"a@b.test c@d.invalid e@mail.example.org f@localhost.localhost; the mark, as on {Hosted}.");
            File.WriteAllText(Path.Combine(dir, "near.cs"), At("g", "notexample.com"));   // not example.com
            File.WriteAllText(Path.Combine(dir, "twice.cs"), $"support@{Hosted} and again support@{Hosted}");
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            File.WriteAllText(Path.Combine(dir, "obj", "x.cs"), $"support@{Hosted}");   // build output is not read
            var found = Offenders(dir, TextFiles(dir), new Dictionary<string, int> { ["a.cs: " + At("never-matched", "x.com")] = 1 });
            Assert.Equal(new[]
            {
                $"Dockerfile: ADMIN.{Hosted.ToUpperInvariant()}",
                $"a.cs: support@{Hosted}",
                $"b.md: feeds.{Hosted}",
                "b.md: " + At("me", "gmail.com"),
                "near.cs: " + At("g", "notexample.com"),
                $"twice.cs: support@{Hosted}",
            }, found);
            // A named exception is left out, and only that one, and only as often as it says.
            var named = new Dictionary<string, int>
            {
                [$"a.cs: support@{Hosted}"] = 1, [$"b.md: feeds.{Hosted}"] = 1, [$"Dockerfile: ADMIN.{Hosted.ToUpperInvariant()}"] = 1,
                ["near.cs: " + At("g", "notexample.com")] = 1, [$"twice.cs: support@{Hosted}"] = 2,
            };
            Assert.Equal(["b.md: " + At("me", "gmail.com")], Offenders(dir, TextFiles(dir), named));
            named[$"twice.cs: support@{Hosted}"] = 1;
            Assert.Equal(["b.md: " + At("me", "gmail.com"), $"twice.cs: support@{Hosted} (2 times, 1 allowed)"], Offenders(dir, TextFiles(dir), named));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]   // mutation: Skipped passes .git again
    public void The_scan_does_not_read_git_metadata()
    {
        // Once core/ is a repository's root, .git holds every contributor's address and branch names. A worktree's .git is a file.
        static string At(string local, string domain) => local + "@" + domain;
        var dir = Directory.CreateTempSubdirectory("packmoat-git-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git", "logs"));
            File.WriteAllText(Path.Combine(dir, ".git", "logs", "HEAD"),
                $"0000000 1111111 Ada Lovelace <{At("ada", "gmail.com")}> 1700000000 +0000\tcheckout: moving from main to fix/wording\n");
            File.WriteAllText(Path.Combine(dir, ".git", "config"), $"[user]\n\temail = {At("ada", "gmail.com")}\n");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", ".git"), $"gitdir: /home/{At("ada", "gmail.com")}/repo/.git/worktrees/sub\n");
            File.WriteAllText(Path.Combine(dir, "ok.md"), "nothing here");
            Assert.Equal(["ok.md"], TextFiles(dir).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).ToArray());
            Assert.Empty(Offenders(dir, TextFiles(dir), new Dictionary<string, int>()));
        }
        finally { Directory.Delete(dir, true); }
    }
}
