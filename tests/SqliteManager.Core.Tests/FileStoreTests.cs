using System.Text;
using SqliteManager.Core.IO;
using SqliteManager.Core.Query;
using SqliteManager.Core.Settings;

namespace SqliteManager.Core.Tests;

public sealed class FileStoreTests : IDisposable
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

    private static QueryHistoryEntry Entry(string sql, bool ok = true) =>
        new(new DateTime(2026, 11, 12, 10, 0, 0), @"C:\Data\a.db", sql, 12, ok, 3, ok ? null : "boom");

    [Fact]
    public async Task AtomicFile_replaces_existing_file_and_leaves_no_temp_files()
    {
        var path = PathFor("out.txt");
        await File.WriteAllTextAsync(path, "old");

        await AtomicFile.WriteAllTextAsync(path, "new", Encoding.UTF8);

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AtomicFile_creates_missing_folders()
    {
        var path = Path.Combine(_folder, "a", "b", "c.txt");

        await AtomicFile.WriteAllTextAsync(path, "x", Encoding.UTF8);

        Assert.Equal("x", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SqlFile_round_trip_keeps_text_and_line_endings()
    {
        var path = PathFor("q.sql");
        const string text = "SELECT 'é';\r\nSELECT 2;\n";

        await SqlFileService.WriteAsync(path, text);
        var read = await SqlFileService.ReadAsync(path);

        Assert.Equal(text, read.Text);
        Assert.False((await File.ReadAllBytesAsync(path)).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public async Task SqlFile_keeps_a_utf8_bom_when_saved_back()
    {
        var path = PathFor("bom.sql");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("SELECT 1;")]);

        var read = await SqlFileService.ReadAsync(path);
        await SqlFileService.WriteAsync(path, read.Text + "\n", read.Encoding);

        Assert.Equal("SELECT 1;", read.Text);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("SELECT 1;\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task SqlFile_reads_utf16()
    {
        var path = PathFor("u16.sql");
        await File.WriteAllTextAsync(path, "SELECT 'ü';", new UnicodeEncoding(false, true));

        Assert.Equal("SELECT 'ü';", (await SqlFileService.ReadAsync(path)).Text);
    }

    [Fact]
    public async Task History_round_trip_newest_first()
    {
        var store = new QueryHistoryStore(PathFor(Path.Combine("sub", "history.jsonl")));

        await store.AppendAsync(Entry("SELECT 1"));
        await store.AppendAsync(Entry("SELECT 2", ok: false));
        var entries = await store.LoadAsync();

        Assert.Equal(["SELECT 2", "SELECT 1"], entries.Select(e => e.Sql));
        Assert.Equal(Entry("SELECT 2", ok: false), entries[0]);
    }

    [Fact]
    public async Task History_missing_file_is_empty()
    {
        Assert.Empty(await new QueryHistoryStore(PathFor("none.jsonl")).LoadAsync());
    }

    [Fact]
    public async Task History_keeps_only_the_most_recent_entries()
    {
        var store = new QueryHistoryStore(PathFor("h.jsonl"), maxEntries: 3);

        for (var i = 1; i <= 5; i++)
        {
            await store.AppendAsync(Entry($"SELECT {i}"));
        }

        Assert.Equal(["SELECT 5", "SELECT 4", "SELECT 3"], (await store.LoadAsync()).Select(e => e.Sql));
        Assert.Equal(3, File.ReadAllLines(store.FilePath).Length);
    }

    [Fact]
    public async Task History_skips_damaged_lines()
    {
        var store = new QueryHistoryStore(PathFor("h.jsonl"));
        await store.AppendAsync(Entry("SELECT 1"));
        await File.AppendAllTextAsync(store.FilePath, "{not json\n\n");
        await store.AppendAsync(Entry("SELECT 2"));

        Assert.Equal(["SELECT 2", "SELECT 1"], (await store.LoadAsync()).Select(e => e.Sql));
    }

    [Fact]
    public async Task History_concurrent_appends_are_all_kept()
    {
        var store = new QueryHistoryStore(PathFor("h.jsonl"));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.AppendAsync(Entry($"SELECT {i}"))));

        Assert.Equal(20, (await store.LoadAsync()).Count);
    }

    [Fact]
    public void History_search_matches_sql_and_path_ignoring_case()
    {
        QueryHistoryEntry[] entries =
        [
            Entry("SELECT * FROM Orders"),
            Entry("DELETE FROM customers"),
            Entry("select 1") with { DatabasePath = @"C:\orders.db" },
        ];

        Assert.Equal(2, QueryHistoryStore.Search(entries, "orders").Count);
        Assert.Single(QueryHistoryStore.Search(entries, "  CUSTOMERS "));
        Assert.Equal(3, QueryHistoryStore.Search(entries, "").Count);
        Assert.Empty(QueryHistoryStore.Search(entries, "zzz"));
    }

    [Fact]
    public async Task Settings_defaults_when_missing_or_damaged()
    {
        var store = new SettingsStore(PathFor("settings.json"));
        Assert.Equal(AppSettings.DefaultQueryRowLimit, (await store.LoadAsync()).QueryRowLimit);

        await File.WriteAllTextAsync(store.FilePath, "{ broken");
        Assert.Equal(AppSettings.DefaultQueryRowLimit, (await store.LoadAsync()).QueryRowLimit);
    }

    [Fact]
    public async Task Settings_round_trip_and_clamp()
    {
        var store = new SettingsStore(PathFor("settings.json"));

        await store.SaveAsync(new AppSettings { QueryRowLimit = 2500 });
        Assert.Equal(2500, (await store.LoadAsync()).QueryRowLimit);

        await File.WriteAllTextAsync(store.FilePath, "{ \"queryRowLimit\": -5 }");
        Assert.Equal(AppSettings.MinQueryRowLimit, (await store.LoadAsync()).QueryRowLimit);

        await File.WriteAllTextAsync(store.FilePath, "{ \"queryRowLimit\": 999999999 }");
        Assert.Equal(AppSettings.MaxQueryRowLimit, (await store.LoadAsync()).QueryRowLimit);
    }
}
