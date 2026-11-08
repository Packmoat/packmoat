using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Runs the REAL deploy/backup.sh (the core's: core/ is the public repository's root) with fake docker, age, rclone and sha256sum on PATH. The fake rclone uses a local folder as the
/// bucket, logs every call and can be told to fail, so the off-box pruning is exercised without a real bucket. Names are made relative to
/// now with a margin of at least half a day around the 30-day cutoff, so the result never depends on the second the test runs.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class BackupHarness : IDisposable
{
    private readonly TempDir _temp = TempDir.Create("packmoat-backup-");
    public string Root => _temp.Path;
    public string Bin => Path.Combine(Root, "bin");
    public string Stub => Path.Combine(Root, "stub");
    public string Data => Path.Combine(Root, "data");
    /// <summary>The fake bucket: BACKUP_REMOTE=offsite:bucket/packmoat lives here.</summary>
    public string Bucket => Path.Combine(Stub, "remote", "bucket", "packmoat");
    public const string Secret = "OFFSITE_SECRET_THIS_MUST_NEVER_BE_LOGGED_0123";
    public const string Recipient = "age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq";

    public BackupHarness()
    {
        foreach (var d in new[] { Bin, Stub, Bucket, Path.Combine(Data, "backups") }) Directory.CreateDirectory(d);
        Stub_("docker", """
            echo "docker $*" >> "$STUB_DIR/calls.log"
            for last; do :; done
            echo "plain database bytes" > "$DATA_DIR/backups/$(basename "$last")"
            """);
        // A fake age that encrypts for real enough to tell apart: a header, then the input in base64, so the plaintext never appears in
        // its output. It refuses to run without the configured recipient, and refuses an input file without -o (it never writes a file
        // to stdout). With no input file it reads stdin and writes stdout, as the recipient probe does.
        Stub_("age", """
            echo "age $*" >> "$STUB_DIR/calls.log"
            out=""; in=""; rcpt=""
            while [ $# -gt 0 ]; do case "$1" in -r) rcpt="$2"; shift 2 ;; -o) out="$2"; shift 2 ;; *) in="$1"; shift ;; esac; done
            [ -n "$rcpt" ] && [ "$rcpt" = "$BACKUP_AGE_RECIPIENT" ] || { echo "fake age: -r with the configured recipient is required" >&2; exit 1; }
            if [ -n "$in" ] && [ -z "$out" ]; then echo "fake age: an input file needs -o" >&2; exit 1; fi
            enc() { echo "age-encrypted"; base64; }
            if [ -n "$out" ]; then enc < "$in" > "$out"; else enc; fi
            """);
        Stub_("sha256sum", """exec shasum -a 256 "$@" """);
        Stub_("rclone", """
            echo "rclone $*" >> "$STUB_DIR/calls.log"
            loc() { echo "$STUB_DIR/remote/${1#offsite:}"; }
            case "$1" in
              copyto) [ -n "${STUB_UPLOAD_FAIL:-}" ] && { echo "ERROR : upload failed" >&2; exit 1; }
                      mkdir -p "$(dirname "$(loc "$3")")"; cp "$2" "$(loc "$3")" ;;
              cat) [ -n "${STUB_CAT_CORRUPT:-}" ] && { echo "not the same bytes"; exit 0; }
                   cat "$(loc "$2")" ;;
              lsf) [ "$2" = --files-only ] && [ $# -eq 3 ] || { echo "fake rclone: lsf must be called as lsf --files-only <remote>" >&2; exit 2; }
                   [ -n "${STUB_LSF_FAIL:-}" ] && { echo "ERROR : AccessDenied: Access Denied. status code: 403" >&2; exit 1; }
                   [ -n "${STUB_LSF_FROM:-}" ] && { cat "$STUB_LSF_FROM"; exit 0; }
                   d="$(loc "$3")"; for f in "$d"/*; do [ -f "$f" ] && basename "$f"; done; exit 0 ;;
              deletefile)
                   [ $# -eq 2 ] || { echo "fake rclone: deletefile takes exactly one path" >&2; exit 2; }
                   # Swallows whatever is on stdin, as a careless command could: the script must not hand it the rest of its list.
                   cat > /dev/null
                   [ "${STUB_DELETE_FAIL:-}" = "$(basename "$2")" ] && { echo "ERROR : delete failed" >&2; exit 1; }
                   rm "$(loc "$2")" ;;
              *) exit 1 ;;
            esac
            """);
    }

    /// <summary>A date that answers normally, except for the cutoff calculation (-d or -r), where it prints garbage.</summary>
    public void GarbageCutoffDate() => Stub_("date", """
        for a; do case "$a" in -d|-r) echo "not-a-date"; exit 0 ;; esac; done
        exec /bin/date "$@"
        """);

    private void Stub_(string name, string body)
    {
        var path = Path.Combine(Bin, name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body + "\n");
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
    }

    /// <summary>The name backup.sh gives a backup taken <paramref name="age"/> ago.</summary>
    public static string NameAged(TimeSpan age) =>
        "packmoat-" + (DateTime.UtcNow - age).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".db.age";

    public string PutRemote(string name)
    {
        File.WriteAllText(Path.Combine(Bucket, name), "old backup");
        return name;
    }

    public string PutRemoteAged(double days) => PutRemote(NameAged(TimeSpan.FromDays(days)));

    public string[] RemoteNames() => Directory.GetFiles(Bucket).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    public string Calls => File.Exists(Path.Combine(Stub, "calls.log")) ? File.ReadAllText(Path.Combine(Stub, "calls.log")) : "";
    public int Count(string prefix) => Calls.Split('\n').Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

    public (int Code, string Output) Run(Dictionary<string, string>? env = null)
    {
        var script = Path.Combine(CoreRoot(), "deploy", "backup.sh");
        var psi = new ProcessStartInfo("bash", $"\"{script}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["PATH"] = Bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["STUB_DIR"] = Stub; psi.Environment["DATA_DIR"] = Data;
        psi.Environment["BACKUP_AGE_RECIPIENT"] = Recipient;
        psi.Environment["BACKUP_REMOTE"] = "offsite:bucket/packmoat";
        psi.Environment["RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY"] = Secret;
        foreach (var kv in env ?? new()) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd() + stderr.Result;
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// <summary>
    /// The core's root: the folder that holds dotnet/Packmoat.sln and deploy/backup.sh. In this repository that is core/; once core/ is
    /// published on its own, it is the repository's root.
    /// </summary>
    public static string CoreRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(File.Exists(Path.Combine(dir.FullName, "dotnet", "Packmoat.sln")) && File.Exists(Path.Combine(dir.FullName, "deploy", "backup.sh"))))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the core's root (dotnet/Packmoat.sln and deploy/backup.sh) not found.");
    }

    public void Dispose() => _temp.Dispose();
}

[UnsupportedOSPlatform("windows")]
public class BackupScriptTests
{
    private static string NewUpload(BackupHarness h, string[] before) => Assert.Single(h.RemoteNames().Except(before));

    [Fact]
    public void Twelve_backups_past_30_days_and_the_new_one_keep_the_7_newest_and_delete_the_other_6()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();   // 31..42 days old

        var (code, output) = h.Run();

        Assert.Equal(0, code);
        var fresh = NewUpload(h, old);
        Assert.Equal(old.Take(6).Append(fresh).Order(StringComparer.Ordinal), h.RemoteNames());   // the new one and 31..36 days
        foreach (var gone in old.Skip(6)) Assert.Contains($"pruned off-box backup: {gone}", output);
        Assert.Equal(6, h.Count("rclone deletefile offsite:bucket/packmoat/packmoat-"));
        Assert.Contains("off-box backups: pruned 6 older than 30 days", output);
        Assert.Contains($"backup ok: {fresh}", output);
        Assert.DoesNotContain(BackupHarness.Secret, output);
        Assert.DoesNotContain(BackupHarness.Secret, h.Calls);
    }

    [Fact]   // mutations: copy the plain file instead of encrypting it; drop umask 077
    public void Only_encrypted_bytes_leave_the_server_and_the_local_copy_is_private()
    {
        using var h = new BackupHarness();

        var (code, output) = h.Run();

        Assert.Equal(0, code);
        var fresh = NewUpload(h, []);
        var uploaded = File.ReadAllText(Path.Combine(h.Bucket, fresh));
        Assert.StartsWith("age-encrypted\n", uploaded);
        Assert.DoesNotContain("plain database bytes", uploaded);
        Assert.Contains($"age -r {BackupHarness.Recipient} -o ", h.Calls);
        var local = Assert.Single(Directory.GetFiles(Path.Combine(h.Data, "backups")));
        Assert.EndsWith(".db.age", local);
        Assert.Equal(uploaded, File.ReadAllText(local));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(local));
        Assert.Contains($"backup ok: {fresh}", output);
    }

    [Fact]
    public void Twenty_backups_all_under_30_days_are_all_kept()
    {
        using var h = new BackupHarness();
        var recent = Enumerable.Range(1, 20).Select(d => h.PutRemoteAged(d + 0.5)).ToArray();   // 1.5..20.5 days

        var (code, output) = h.Run();

        Assert.Equal(0, code);
        Assert.Equal(21, h.RemoteNames().Length);
        Assert.All(recent, n => Assert.Contains(n, h.RemoteNames()));
        Assert.Equal(0, h.Count("rclone deletefile"));
        Assert.Contains("off-box backups: pruned 0", output);
    }

    [Fact]
    public void Forty_daily_backups_lose_only_those_older_than_30_days()
    {
        using var h = new BackupHarness();
        var all = Enumerable.Range(1, 40).Select(d => (Age: d - 0.5, Name: h.PutRemoteAged(d - 0.5))).ToArray();   // 0.5..39.5 days

        var (code, output) = h.Run();

        Assert.Equal(0, code);
        var remaining = h.RemoteNames();
        Assert.All(all.Where(b => b.Age < 30), b => Assert.Contains(b.Name, remaining));
        Assert.All(all.Where(b => b.Age > 30), b => Assert.DoesNotContain(b.Name, remaining));
        Assert.Equal(10, h.Count("rclone deletefile"));
        Assert.Equal(31, remaining.Length);
        Assert.Contains("off-box backups: pruned 10", output);
    }

    [Theory]
    [InlineData("STUB_CAT_CORRUPT")]
    [InlineData("STUB_UPLOAD_FAIL")]
    public void A_failed_upload_or_checksum_never_lists_or_deletes_anything(string failure)
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();

        var (code, output) = h.Run(new() { [failure] = "1" });

        Assert.Equal(1, code);
        Assert.Equal(0, h.Count("rclone lsf"));
        Assert.Equal(0, h.Count("rclone deletefile"));
        Assert.All(old, n => Assert.Contains(n, h.RemoteNames()));
        Assert.DoesNotContain("backup ok", output);
    }

    [Fact]
    public void Names_the_script_did_not_make_are_never_deleted_however_old()
    {
        using var h = new BackupHarness();
        var foreign = new[]
        {
            "packmoat-20200101T000000Z-manual.db.age",
            "packmoat-20200101T000000Z.db.age.partial",
            "packmoat-2020-01-01.db.age",
            "packmoat-20200101T000000Z.db",
            "manual-copy-20200101.db.age",
            "packmoat-20200101T000000Z.DB.AGE",
            "packmoat-pre-migration.db.age",   // sorts above every real name
            "packmoat-zzzz.db.age",
        }.Select(h.PutRemote).ToArray();
        var old = Enumerable.Range(31, 8).Select(d => h.PutRemoteAged(d)).ToArray();   // 8 old: with the new one, 2 go

        var (code, output) = h.Run();

        Assert.Equal(0, code);
        Assert.All(foreign, n => Assert.Contains(n, h.RemoteNames()));
        Assert.Equal(2, h.Count("rclone deletefile"));
        Assert.All(old.Skip(6), n => Assert.DoesNotContain(n, h.RemoteNames()));
        var fresh = NewUpload(h, foreign.Concat(old).ToArray());
        Assert.Equal(old.Take(6).Append(fresh).Order(StringComparer.Ordinal),
            h.RemoteNames().Except(foreign).Order(StringComparer.Ordinal));                  // still the 7 real newest
        Assert.All(foreign, n => Assert.DoesNotContain($"pruned off-box backup: {n}", output));
    }

    [Fact]
    public void If_the_bucket_cannot_be_listed_the_backup_is_still_ok_but_the_run_fails_with_exit_3()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();

        var (code, output) = h.Run(new() { ["STUB_LSF_FAIL"] = "1" });

        Assert.Equal(3, code);
        Assert.Contains("backup ok: packmoat-", output);
        Assert.Contains("backup ok, but pruning off-box backups failed", output);
        Assert.Equal(0, h.Count("rclone deletefile"));
        Assert.Equal(13, h.RemoteNames().Length);
        Assert.DoesNotContain(BackupHarness.Secret, output);
    }

    [Fact]
    public void If_one_delete_fails_the_others_are_still_deleted_and_the_run_fails_with_exit_3()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();
        var stuck = old[8];

        var (code, output) = h.Run(new() { ["STUB_DELETE_FAIL"] = stuck });

        Assert.Equal(3, code);
        Assert.Contains("backup ok: packmoat-", output);
        Assert.Contains("backup ok, but pruning off-box backups failed", output);
        Assert.Contains($"could not delete off-box backup: {stuck}", output);
        Assert.Equal(6, h.Count("rclone deletefile"));
        Assert.All(old.Skip(6).Where(n => n != stuck), n => Assert.DoesNotContain(n, h.RemoteNames()));
        Assert.Contains(stuck, h.RemoteNames());
        Assert.DoesNotContain(BackupHarness.Secret, output);
    }

    [Fact]
    public void A_listing_without_the_backup_just_uploaded_deletes_nothing_and_fails_with_exit_3()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();
        var stale = Path.Combine(h.Root, "stale-listing.txt");
        File.WriteAllLines(stale, old);   // the old backups, but not today's

        var (code, output) = h.Run(new() { ["STUB_LSF_FROM"] = stale });

        Assert.Equal(3, code);
        Assert.Contains("backup ok: packmoat-", output);
        Assert.Contains("pruning nothing", output);
        Assert.Contains("backup ok, but pruning off-box backups failed", output);
        Assert.Equal(0, h.Count("rclone deletefile"));
        Assert.Equal(13, h.RemoteNames().Length);
    }

    [Theory]
    [InlineData("KEEP_REMOTE", "0")]
    [InlineData("KEEP_REMOTE", "abc")]
    [InlineData("KEEP_REMOTE", "08")]
    [InlineData("KEEP_REMOTE", "-1")]
    [InlineData("RETAIN_DAYS", "0")]
    [InlineData("RETAIN_DAYS", "30d")]
    [InlineData("RETAIN_DAYS", " 30")]
    [InlineData("KEEP_REMOTE", "10000")]
    [InlineData("RETAIN_DAYS", "99999999999999999999")]
    public void A_bad_retention_setting_stops_before_anything_is_backed_up_or_deleted(string setting, string value)
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();

        var (code, output) = h.Run(new() { [setting] = value });

        Assert.Equal(1, code);
        Assert.Contains($"{setting} must be a whole number from 1 to 9999", output);
        Assert.Equal(0, h.Count("docker"));
        Assert.Equal(0, h.Count("rclone"));
        Assert.Equal(12, h.RemoteNames().Length);
    }

    [Fact]
    public void A_cutoff_date_that_cannot_be_worked_out_deletes_nothing_and_fails_with_exit_3()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 12).Select(d => h.PutRemoteAged(d)).ToArray();
        h.GarbageCutoffDate();

        var (code, output) = h.Run();

        Assert.Equal(3, code);
        Assert.Contains("backup ok: packmoat-", output);
        Assert.Contains("backup ok, but pruning off-box backups failed", output);
        Assert.Equal(0, h.Count("rclone deletefile"));
        Assert.Equal(13, h.RemoteNames().Length);
    }

    [Fact]
    public void The_largest_allowed_settings_are_accepted()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(31, 3).Select(d => h.PutRemoteAged(d)).ToArray();

        var (code, _) = h.Run(new() { ["KEEP_REMOTE"] = "9999", ["RETAIN_DAYS"] = "9999" });

        Assert.Equal(0, code);
        Assert.Equal(0, h.Count("rclone deletefile"));
    }

    [Fact]
    public void The_retention_settings_can_be_changed()
    {
        using var h = new BackupHarness();
        var old = Enumerable.Range(1, 12).Select(d => h.PutRemoteAged(d + 0.5)).ToArray();   // 1.5..12.5 days

        var (code, output) = h.Run(new() { ["KEEP_REMOTE"] = "3", ["RETAIN_DAYS"] = "10" });

        Assert.Equal(0, code);
        Assert.Equal(3, h.Count("rclone deletefile"));   // 10.5, 11.5 and 12.5 days
        Assert.All(old.Skip(9), n => Assert.DoesNotContain(n, h.RemoteNames()));
        Assert.Contains("the 3 newest are always kept", output);
    }
}
