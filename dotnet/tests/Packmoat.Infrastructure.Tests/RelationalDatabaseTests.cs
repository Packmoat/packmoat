namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The module seams give the database as <see cref="IRelationalDatabase"/>. <see cref="SqliteDatabase.From"/> is the one
/// approved way back to SQLite's own: it returns the same instance for SQLite, and refuses any other engine, naming it.
/// </summary>
[Collection("sqlite-pools")]
public class RelationalDatabaseTests
{
    private sealed class FakeDatabase(DatabaseEngine engine) : IRelationalDatabase
    {
        public DatabaseEngine Engine { get; } = engine;
    }

    private static void WithDatabase(Action<SqliteDatabase> body)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-relational-").FullName;
        try { body(new SqliteDatabase(Path.Combine(dir, "packmoat.db"))); }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public void SQLite_says_it_is_SQLite() => WithDatabase(db => Assert.Equal(DatabaseEngine.Sqlite, ((IRelationalDatabase)db).Engine));

    [Fact]   // mutation: From returns null, or a new instance
    public void From_gives_back_the_same_SQLite_database() => WithDatabase(db => Assert.Same(db, SqliteDatabase.From(db)));

    [Fact]   // mutation: From casts without checking, or names no engine
    public void From_refuses_another_engine_and_names_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteDatabase.From(new FakeDatabase(DatabaseEngine.Postgres)));
        Assert.Contains("postgres", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SQLite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]   // an object that only says it is SQLite is not SQLite's database
    public void From_refuses_a_database_that_is_not_SQLites_own()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteDatabase.From(new FakeDatabase(DatabaseEngine.Sqlite)));
        Assert.Contains("sqlite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_refuses_null() => Assert.Throws<ArgumentNullException>(() => SqliteDatabase.From(null!));

    [Fact]   // a pure pass-through: the name and the value as given; null stays null (no DBNull), as AddWithValue left it
    public void AddParameter_adds_the_name_and_value_as_given()
    {
        using var cmd = new Microsoft.Data.Sqlite.SqliteCommand();
        System.Data.Common.DbCommand general = cmd;
        general.AddParameter("@a", "x");
        general.AddParameter("@b", 7L);
        general.AddParameter("@c", null);
        using var expected = new Microsoft.Data.Sqlite.SqliteCommand();
        expected.Parameters.AddWithValue("@a", "x");
        expected.Parameters.AddWithValue("@b", 7L);
        expected.Parameters.AddWithValue("@c", null);
        Assert.Equal(expected.Parameters.Cast<Microsoft.Data.Sqlite.SqliteParameter>().Select(p => (p.ParameterName, p.Value, p.DbType, p.SqliteType)),
            cmd.Parameters.Cast<Microsoft.Data.Sqlite.SqliteParameter>().Select(p => (p.ParameterName, p.Value, p.DbType, p.SqliteType)));
        Assert.Null(cmd.Parameters["@c"].Value);
    }
}
