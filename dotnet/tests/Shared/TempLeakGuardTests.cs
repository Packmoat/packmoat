namespace Packmoat.TestSupport;

/// <summary>The temp-folder guard and its helper, checked in every test project that runs under the guard.</summary>
public class TempLeakGuardTests
{
    [Fact]   // mutation: TempLeakGuard does not point TMPDIR at its run folder
    public void The_temporary_folder_is_the_runs_own_folder()
    {
        var temp = new DirectoryInfo(Path.GetTempPath());
        Assert.StartsWith("pmrun-" + typeof(TempLeakGuardTests).Assembly.GetName().Name + "-", temp.Name, StringComparison.Ordinal);
        Assert.True(temp.Exists, $"{temp.FullName} does not exist");
    }

    [Fact]
    public void A_temp_dir_is_deleted_with_everything_in_it_and_deleting_it_twice_is_fine()
    {
        var dir = TempDir.Create("packmoat-guard-");
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllText(Path.Combine(dir.File("sub"), "f.txt"), "x");
        dir.Dispose();
        Assert.False(Directory.Exists(dir.Path));
        dir.Dispose();
    }

    [Fact]   // mutation: a catch { } around TempDir.Delete's delete
    public void A_delete_that_keeps_failing_throws()
    {
        if (OperatingSystem.IsWindows()) return;   // a read-only folder's files can still be deleted there; the Unix runs cover it
        var dir = TempDir.Create("packmoat-guard-locked-");
        var locked = dir.File("locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "f.txt"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => TempDir.Delete(locked));
            Assert.True(Directory.Exists(locked));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            dir.Dispose();
        }
    }

    // Check(endOfRun: false) throughout: a self-test runs beside other tests, so it must not clear SQLite's pools for the whole process.

    [Fact]
    public void Only_entries_outside_the_allowlist_are_reported_and_the_folder_goes()
    {
        var folder = Directory.CreateTempSubdirectory("packmoat-guard-check-").FullName;
        File.WriteAllText(Path.Combine(folder, "packmoat-left"), "x");
        var problems = TempLeakGuard.Check(folder, endOfRun: false);
        Assert.Contains(problems, p => p.Contains(Path.Combine(folder, "packmoat-left"), StringComparison.Ordinal));
        Assert.Single(problems);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(TempLeakGuard.Check(Directory.CreateTempSubdirectory("packmoat-guard-empty-").FullName, endOfRun: false));
    }

    [Fact]   // mutation: the allowlist matches a folder too (a leak hidden in a folder named like a keychain is deleted unreported)
    public void Only_files_may_match_the_allowlist_a_folder_named_like_a_keychain_is_reported()
    {
        var folder = Directory.CreateTempSubdirectory("packmoat-guard-keychain-").FullName;
        var keychain = Path.Combine(folder, new string('a', 32) + ".keychain");
        var lookalike = Path.Combine(folder, new string('b', 32) + ".keychain");
        File.WriteAllText(keychain, "x");
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, "packmoat-left"), "x");
        var problems = TempLeakGuard.Check(folder, endOfRun: false);
        Assert.Contains(problems, p => p.Contains(lookalike, StringComparison.Ordinal));
        Assert.Equal(OperatingSystem.IsMacOS() ? 1 : 2, problems.Count);   // the keychain file is allowed on macOS only
        Assert.False(Directory.Exists(folder));
    }

    [Fact]   // mutation: Check lists the folder outside its try (a missing folder throws and stops the test host)
    public void A_missing_run_folder_is_reported_not_thrown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "packmoat-guard-missing-" + Guid.NewGuid().ToString("N"));
        var problem = Assert.Single(TempLeakGuard.Check(folder, endOfRun: false));
        Assert.Contains(folder, problem, StringComparison.Ordinal);
        Assert.Contains("could not be listed", problem, StringComparison.Ordinal);
    }

    [Fact]   // mutation: TempDir.Delete throws whether or not its test had already failed
    public void A_delete_that_fails_after_its_test_failed_keeps_the_tests_failure_and_is_reported_later()
    {
        if (OperatingSystem.IsWindows()) return;   // as A_delete_that_keeps_failing_throws
        var dir = TempDir.Create("packmoat-guard-failing-");
        var locked = dir.File("locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "f.txt"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            void FailingTest()
            {
                try { throw new InvalidOperationException("the test's own failure"); }
                catch when (TempDir.Failing(locked)) { throw; }
                finally { TempDir.Delete(locked); }
            }
            var thrown = Assert.Throws<InvalidOperationException>(FailingTest);
            Assert.Equal("the test's own failure", thrown.Message);
            Assert.Contains(locked, TempDir.TakeDeferred(locked), StringComparison.Ordinal);   // the guard would report it after the last test
            Assert.ThrowsAny<Exception>(() => TempDir.Delete(locked));      // without a failure first, the delete throws
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            dir.Dispose();
        }
    }
}
