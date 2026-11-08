namespace Packmoat.Architecture.Tests;

/// <summary>
/// No file writes in the core outside an allowlist (#278 step 0, architecture test 6). A copy of the server does not share its disk with
/// another copy, so whatever it writes to a file only it has. Each file below writes, and says what and why; anything else keeps its state
/// behind a port. The hosted projects have their own twin.
/// </summary>
public class FileWriteTests
{
    internal static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Packmoat.Host/Web/SignedInSite.cs"] = "the cookie keys' folder in folder mode; more than one copy needs PACKMOAT_COOKIE_KEYS=database",
        ["Packmoat.Infrastructure/CertificateTools.cs"] = "the admin tool's per-customer certificate and private key, made once on the operator's machine",
        ["Packmoat.Infrastructure/MigrationDryRun.cs"] = "db check-migrations: a private temporary copy of a database file, deleted after",
        ["Packmoat.Infrastructure/SqliteBackup.cs"] = "the admin tool's backup and restore of the SQLite database file",
        ["Packmoat.Infrastructure/SqliteDatabase.cs"] = "the SQLite database file itself (owner-only), and its folder",
    };

    private static (string Named, string Source)[] CoreSource() => EngineTypeScan.SourceFiles(Path.Combine(CoreRoot.Path(), "dotnet", "src"));

    // mutation: File.WriteAllText in PackmoatApp
    [Fact]
    public void Only_the_allowlisted_files_write_to_disk()
    {
        var files = CoreSource();
        Assert.True(files.Length > 100, "the scan found too few source files to mean anything");
        var found = FileWriteScan.Offenders(files, Allowed);
        Assert.True(found.Length == 0, "These files write to the local disk, which another copy of the server does not share. Keep the state " +
            "behind a port instead, or add the file to the allowlist with what it writes and why that is safe:\n  " + string.Join("\n  ", found));
    }

    [Fact]   // the list shrinks with the code: an entry that writes nothing any more (or is gone) is taken off
    public void Every_entry_on_the_allowlist_still_writes() =>
        Assert.Empty(FileWriteScan.StaleEntries(CoreSource(), Allowed));

    [Fact]
    public void Every_entry_says_what_it_writes_in_one_short_line() =>
        Assert.All(Allowed, e => Assert.True(e.Value.Length is > 20 and <= 120 && !e.Value.Contains('\n'), $"{e.Key}: \"{e.Value}\""));

    [Fact]
    public void The_scan_sees_each_kind_of_write_in_code_and_not_in_comments()
    {
        Assert.Equal(["File.WriteAllText"], FileWriteScan.WritesNamed("File.WriteAllText(path, text);"));
        Assert.Equal(["File.WriteAllBytesAsync"], FileWriteScan.WritesNamed("await File.WriteAllBytesAsync(p, b);"));
        Assert.Equal(["File.Create", "File.Move", "File.Copy", "File.Delete", "File.SetUnixFileMode"],
            FileWriteScan.WritesNamed("File.Create(p); File.Move(a, b); File.Copy(a, b); File.Delete(a); File.SetUnixFileMode(a, m);"));
        Assert.Equal(["File.AppendAllText", "File.OpenWrite"], FileWriteScan.WritesNamed("File.AppendAllText(p, t); using var s = File.OpenWrite(p);"));
        Assert.Equal(["new FileStream"], FileWriteScan.WritesNamed("using var s = new  FileStream(p, o);"));
        Assert.Equal(["Directory.CreateDirectory", "Directory.CreateTempSubdirectory", "Directory.Delete"],
            FileWriteScan.WritesNamed("Directory.CreateDirectory(d); Directory.CreateTempSubdirectory(x); Directory.Delete(d, true);"));
        Assert.Equal(["PersistKeysToFileSystem"], FileWriteScan.WritesNamed("dp.PersistKeysToFileSystem(dir);"));
        Assert.Equal(["ReadWriteCreate", "BackupDatabase"], FileWriteScan.WritesNamed("Mode = SqliteOpenMode.ReadWriteCreate; from.BackupDatabase(to);"));
        Assert.Equal(["File.SetUnixFileMode"], FileWriteScan.WritesNamed("EnsureOwnerOnly(path, File.SetUnixFileMode, File.GetUnixFileMode);"));   // a method group
        Assert.Equal(["new StreamWriter"], FileWriteScan.WritesNamed("using var w = new StreamWriter(path);"));
        Assert.Equal(["File.Open", "File.OpenHandle"], FileWriteScan.WritesNamed("using var s = File.Open(p, FileMode.Create); using var h = File.OpenHandle(p);"));
        Assert.Equal(["Path.GetTempFileName"], FileWriteScan.WritesNamed("var t = Path.GetTempFileName();"));
        Assert.Equal(["SqliteOpenMode.ReadWrite"], FileWriteScan.WritesNamed("var b = new SqliteConnectionStringBuilder { Mode = SqliteOpenMode.ReadWrite };"));
        Assert.Equal(["new SqliteConnection"], FileWriteScan.WritesNamed("using var c = new SqliteConnection(\"Data Source=x.db\");"));   // default: ReadWriteCreate
        // a FileInfo's or DirectoryInfo's own writes: made inline, or on a name the file declares as one
        Assert.Equal(["FileSystemInfo.Delete"], FileWriteScan.WritesNamed("new FileInfo(Path.Combine(a, b)).Delete();"));
        Assert.Equal(["FileSystemInfo.MoveTo"], FileWriteScan.WritesNamed("var f = new FileInfo(p);\nf.MoveTo(q);"));
        Assert.Equal(["FileSystemInfo.Create"], FileWriteScan.WritesNamed("var info = new DirectoryInfo(path); if (!info.Exists) info.Create();"));
        Assert.Equal(["Directory.CreateTempSubdirectory", "FileSystemInfo.Delete"],
            FileWriteScan.WritesNamed("var dir = Directory.CreateTempSubdirectory(x); try { } finally { dir.Delete(recursive: true); }"));
        Assert.Equal(["FileSystemInfo.CopyTo", "FileSystemInfo.Replace", "FileSystemInfo.OpenWrite"],
            FileWriteScan.WritesNamed("void M(FileInfo source, FileSystemInfo? other) { source.CopyTo(t); source.Replace(t, b); source.OpenWrite(); }"));
        Assert.Equal(["FileSystemInfo.Delete"], FileWriteScan.WritesNamed("private readonly DirectoryInfo _root;\nvoid M() => _root.Delete(true);"));
        // reads are not writes; nor is another type's Delete or Replace; comments are not code; a string is data
        Assert.Empty(FileWriteScan.WritesNamed("File.ReadAllText(p); File.Exists(p); Directory.GetFiles(d); new FileStreamOptions();"));
        Assert.Empty(FileWriteScan.WritesNamed("using var r = File.OpenRead(p); using var t = File.OpenText(p); new StreamReader(p);"));
        Assert.Empty(FileWriteScan.WritesNamed("ctx.Response.Cookies.Delete(name); var s = text.Replace(\"a\", \"b\"); var i = new FileInfo(p).Length;"));
        Assert.Empty(FileWriteScan.WritesNamed("Mode = SqliteOpenMode.ReadOnly; var c = new SqliteConnectionStringBuilder();"));
        Assert.Empty(FileWriteScan.WritesNamed("// File.WriteAllText(p, t)\n/// <see cref=\"File.Delete\"/>\n/* new FileStream */ var x = 1;"));
        Assert.Equal(["File.Delete"], FileWriteScan.WritesNamed("var u = \"http://x\"; File.Delete(p);"));
        Assert.Equal(["File.Delete"], FileWriteScan.WritesNamed("var q = '\"'; var x = \"a//\"; File.Delete(p);"));
    }

    [Fact]
    public void The_allowlist_is_by_exact_path_and_a_stale_entry_is_found()
    {
        var files = new[]
        {
            ("Packmoat.Host/PackmoatApp.cs", "File.WriteAllText(\"x\", \"y\");"),
            ("Packmoat.Infrastructure/SqliteDatabase.cs", "Directory.CreateDirectory(dir);"),
            ("Packmoat.Infrastructure/CertificateTools.cs", "// await using var stream = new FileStream(path, options);\nvar x = 1;"),
            ("Packmoat.Infrastructure/Lifecycle/SqliteDatabase.cs", "File.Delete(p);"),
        };
        var allowed = new Dictionary<string, string>
        {
            ["Packmoat.Infrastructure/SqliteDatabase.cs"] = "the database file",
            ["Packmoat.Infrastructure/CertificateTools.cs"] = "the certificate",
            ["Packmoat.Infrastructure/Gone.cs"] = "a file that is gone",
        };
        Assert.Equal(["Packmoat.Host/PackmoatApp.cs: File.WriteAllText", "Packmoat.Infrastructure/Lifecycle/SqliteDatabase.cs: File.Delete"],
            FileWriteScan.Offenders(files, allowed));
        Assert.Equal(["Packmoat.Infrastructure/CertificateTools.cs", "Packmoat.Infrastructure/Gone.cs"], FileWriteScan.StaleEntries(files, allowed));
    }
}
