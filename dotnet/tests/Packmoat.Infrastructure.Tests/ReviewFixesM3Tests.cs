using System.Runtime.Versioning;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

[UnsupportedOSPlatform("windows")]
[Collection("sqlite-pools")]
public class FilePermissionTests
{
    private static string Mode(string path) => Convert.ToString((int)File.GetUnixFileMode(path), 8);
    private static bool OwnerOnly(string path) => (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

    [Fact]
    public async Task A_database_in_a_new_directory_is_private_including_its_write_ahead_log()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("packmoat-perm-").FullName;
        try
        {
            var dir = Path.Combine(root, "data");
            var path = Path.Combine(dir, "packmoat.db");
            var db = new SqliteDatabase(path);
            await db.MigrateAsync(TestTracks.Modules);
            await using (var c = await db.OpenAsync())
            await using (var cmd = c.CreateCommand())   // keep a connection open so the -wal and -shm files exist
            {
                cmd.CommandText = "INSERT INTO tenants(id,name,upstream_base,allowed_path_prefix,feed_index_url,cooldown_days,created_at) VALUES('a','A','https://x/','','https://x/i',1,'2026-01-01')";
                await cmd.ExecuteNonQueryAsync();

                Assert.True(OwnerOnly(dir), $"directory mode {Mode(dir)}");
                foreach (var f in new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists))
                    Assert.True(OwnerOnly(f), $"{Path.GetFileName(f)} mode {Mode(f)}");
                Assert.True(File.Exists(path + "-wal"), "the test must actually look at a -wal file");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void An_existing_directory_is_left_as_the_operator_made_it()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("packmoat-perm2-").FullName;
        try
        {
            File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("755", 8));
            _ = new SqliteDatabase(Path.Combine(root, "packmoat.db"));
            Assert.Equal("755", Mode(root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task An_existing_database_file_keeps_its_mode()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("packmoat-perm3-").FullName;
        try
        {
            var path = Path.Combine(root, "packmoat.db");
            await File.WriteAllBytesAsync(path, Array.Empty<byte>());
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("640", 8));
            _ = new SqliteDatabase(path);
            Assert.Equal("640", Mode(path));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class WeakMasterKeyTests
{
    [Fact] public void All_zeros_is_rejected() => Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[32]));

    [Fact] public void One_repeated_byte_is_rejected() => Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(Enumerable.Repeat((byte)0xFF, 32).ToArray()));

    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)]
    public void A_short_repeating_pattern_is_rejected(int period) =>
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(Enumerable.Range(0, 32).Select(i => (byte)(1 + (i % period) * 7)).ToArray()));

    [Fact] public void A_counting_sequence_is_rejected() => Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));

    [Fact] public void A_descending_sequence_is_rejected() => Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray()));

    [Fact] public void Too_few_distinct_bytes_is_rejected() => Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(Enumerable.Range(0, 32).Select(i => (byte)(i * 5 % 11)).ToArray()));

    [Fact]
    public void Base64_of_a_weak_key_is_rejected_the_same_way() =>
        Assert.Throws<ArgumentException>(() => AesGcmSecretProtector.FromBase64(Convert.ToBase64String(new byte[32])));

    [Fact]
    public void Keys_from_keygen_are_always_accepted()
    {
        for (var i = 0; i < 200; i++) _ = AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());
    }
}
