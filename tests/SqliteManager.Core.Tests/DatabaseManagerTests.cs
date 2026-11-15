using Microsoft.Data.Sqlite;
using SqliteManager.Core.Data;
using SqliteManager.Core.Logging;

namespace SqliteManager.Core.Tests;

public sealed class DatabaseManagerTests : IDisposable
{
    private readonly string _folder = TestDatabase.NewFolder();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathFor(string name) => Path.Combine(_folder, name);

    [Fact]
    public async Task Create_makes_a_new_file_and_tracks_the_session()
    {
        await using var manager = new DatabaseManager(new SqlLog());
        var path = PathFor("new.db");

        var session = await manager.CreateAsync(path);

        Assert.True(File.Exists(path));
        Assert.Equal(path, session.FilePath);
        Assert.Equal("new.db", session.DisplayName);
        Assert.False(session.IsReadOnly);
        Assert.Equal([session], manager.Sessions);
    }

    [Fact]
    public async Task Create_fails_when_file_exists_and_does_not_change_it()
    {
        var path = PathFor("existing.db");
        await File.WriteAllTextAsync(path, "keep me");
        await using var manager = new DatabaseManager(new SqlLog());

        await Assert.ThrowsAsync<IOException>(() => manager.CreateAsync(path));

        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public async Task Create_fails_when_folder_is_missing()
    {
        await using var manager = new DatabaseManager(new SqlLog());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => manager.CreateAsync(Path.Combine(_folder, "missing", "x.db")));
    }

    [Fact]
    public async Task Open_reads_existing_data()
    {
        var path = PathFor("data.db");
        await using (var setup = new DatabaseManager(new SqlLog()))
        {
            var s = await setup.CreateAsync(path);
            await s.ExecuteNonQueryAsync("CREATE TABLE t (x INTEGER)");
            await s.ExecuteNonQueryAsync("INSERT INTO t VALUES (42)");
        }

        await using var manager = new DatabaseManager(new SqlLog());
        var session = await manager.OpenAsync(path);

        Assert.Equal(42L, await session.ExecuteScalarAsync<long>("SELECT x FROM t"));
    }

    [Fact]
    public async Task Open_fails_when_file_is_missing()
    {
        await using var manager = new DatabaseManager(new SqlLog());

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.OpenAsync(PathFor("nope.db")));
        Assert.False(File.Exists(PathFor("nope.db")));
    }

    [Fact]
    public async Task Open_fails_for_a_file_that_is_not_a_database()
    {
        var path = PathFor("notes.txt");
        await File.WriteAllTextAsync(path, new string('x', 4096));
        await using var manager = new DatabaseManager(new SqlLog());

        await Assert.ThrowsAsync<SqliteException>(() => manager.OpenAsync(path));

        Assert.Empty(manager.Sessions);
        Assert.Equal(new string('x', 4096), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Read_only_session_rejects_writes()
    {
        var path = PathFor("ro.db");
        await using (var setup = new DatabaseManager(new SqlLog()))
        {
            var s = await setup.CreateAsync(path);
            await s.ExecuteNonQueryAsync("CREATE TABLE t (x INTEGER)");
        }

        await using var manager = new DatabaseManager(new SqlLog());
        var session = await manager.OpenAsync(path, readOnly: true);

        Assert.True(session.IsReadOnly);
        var ex = await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteNonQueryAsync("INSERT INTO t VALUES (1)"));
        Assert.Equal(8, ex.SqliteErrorCode); // SQLITE_READONLY
        Assert.Equal(0L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task Several_databases_can_be_open_at_once()
    {
        await using var manager = new DatabaseManager(new SqlLog());
        var a = await manager.CreateAsync(PathFor("a.db"));
        var b = await manager.CreateAsync(PathFor("b.db"));
        await a.ExecuteNonQueryAsync("CREATE TABLE only_a (x)");
        await b.ExecuteNonQueryAsync("CREATE TABLE only_b (x)");

        Assert.Equal(2, manager.Sessions.Count);
        Assert.Equal("only_a", await a.ExecuteScalarAsync<string>("SELECT name FROM sqlite_schema"));
        Assert.Equal("only_b", await b.ExecuteScalarAsync<string>("SELECT name FROM sqlite_schema"));
    }

    [Fact]
    public async Task Opening_the_same_file_twice_fails()
    {
        await using var manager = new DatabaseManager(new SqlLog());
        var path = PathFor("dup.db");
        await manager.CreateAsync(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync(path.ToUpperInvariant(), readOnly: true));
    }

    [Fact]
    public async Task Close_removes_the_session_and_releases_the_file()
    {
        await using var manager = new DatabaseManager(new SqlLog());
        var path = PathFor("close.db");
        var session = await manager.CreateAsync(path);

        await manager.CloseAsync(session);

        Assert.Empty(manager.Sessions);
        Assert.False(session.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteNonQueryAsync("SELECT 1"));
        File.Delete(path); // Fails if the file is still locked.
    }

    [Fact]
    public async Task Concurrent_calls_on_one_session_are_serialized()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x INTEGER)");

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            db.Session.ExecuteNonQueryAsync("INSERT INTO t VALUES (@x)", new Dictionary<string, object?> { ["@x"] = i })));

        Assert.Equal(50L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));
    }
}
