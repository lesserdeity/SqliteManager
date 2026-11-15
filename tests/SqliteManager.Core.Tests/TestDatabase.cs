using SqliteManager.Core.Data;
using SqliteManager.Core.Logging;

namespace SqliteManager.Core.Tests;

/// <summary>A database file in a temporary folder that is deleted on dispose.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string folder, SqlLog log, DatabaseManager manager, DatabaseSession session)
    {
        Folder = folder;
        Log = log;
        Manager = manager;
        Session = session;
    }

    public string Folder { get; }

    public string FilePath => Session.FilePath;

    public SqlLog Log { get; }

    public DatabaseManager Manager { get; }

    public DatabaseSession Session { get; }

    public static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SqliteManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static async Task<TestDatabase> CreateAsync(params string[] setupStatements)
    {
        var folder = NewFolder();
        var log = new SqlLog();
        var manager = new DatabaseManager(log);
        var session = await manager.CreateAsync(Path.Combine(folder, "test.db"));
        foreach (var statement in setupStatements)
        {
            await session.ExecuteNonQueryAsync(statement);
        }

        return new TestDatabase(folder, log, manager, session);
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }
    }
}
