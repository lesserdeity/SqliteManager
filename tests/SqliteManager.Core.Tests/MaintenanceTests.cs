using Microsoft.Data.Sqlite;
using SqliteManager.Core.Data;
using SqliteManager.Core.Maintenance;
using SqliteManager.Core.Query;
using SqliteManager.Core.Settings;

namespace SqliteManager.Core.Tests;

public class MaintenanceTests
{
    [Fact]
    public async Task Vacuum_shrinks_the_file_and_is_logged()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, data BLOB)",
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) INSERT INTO t SELECT i, randomblob(4000) FROM n",
            "DELETE FROM t");

        var result = await new MaintenanceService(db.Session).VacuumAsync();
        Assert.True(result.SizeAfter < result.SizeBefore / 4, $"{result.SizeBefore} -> {result.SizeAfter}");
        Assert.Contains(db.Log.GetEntries(), e => e.Sql == "VACUUM" && e.Error is null);
    }

    [Fact]
    public async Task Vacuum_refuses_inside_a_transaction_and_read_only()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");
        await db.Session.ExecuteNonQueryAsync("BEGIN");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MaintenanceService(db.Session).VacuumAsync());
        Assert.Contains("transaction", ex.Message);
        await db.Session.ExecuteNonQueryAsync("ROLLBACK");

        var path = db.FilePath;
        await db.Manager.CloseAsync(db.Session);
        var readOnly = await db.Manager.OpenAsync(path, readOnly: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MaintenanceService(readOnly).VacuumAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MaintenanceService(readOnly).AnalyzeAsync());
    }

    [Fact]
    public async Task Analyze_fills_sqlite_stat1()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (a, b)", "CREATE INDEX t_a ON t (a)", "INSERT INTO t VALUES (1, 2), (3, 4)");
        await new MaintenanceService(db.Session).AnalyzeAsync();
        Assert.True(await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_stat1") > 0);
    }

    [Fact]
    public async Task Integrity_check_reports_ok()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (a)", "INSERT INTO t VALUES (1)");
        var service = new MaintenanceService(db.Session);
        Assert.True((await service.IntegrityCheckAsync()).IsOk);
        Assert.True((await service.IntegrityCheckAsync(quick: true)).IsOk);
    }

    [Fact]
    public async Task Integrity_check_finds_a_broken_index()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE t (a INTEGER PRIMARY KEY, b)", "CREATE INDEX t_b ON t (b)",
            "INSERT INTO t VALUES (1, 'x'), (2, 'y')");

        // Corrupt the index on purpose: with writable_schema, change the index to cover another column.
        await db.Session.ExecuteNonQueryAsync("PRAGMA writable_schema = ON");
        await db.Session.ExecuteNonQueryAsync("UPDATE sqlite_schema SET sql = 'CREATE INDEX t_b ON t (a)' WHERE name = 't_b'");
        await db.Session.ExecuteNonQueryAsync("PRAGMA writable_schema = OFF");
        var path = db.FilePath;
        await db.Manager.CloseAsync(db.Session);
        var session = await db.Manager.OpenAsync(path);

        var result = await new MaintenanceService(session).IntegrityCheckAsync();
        Assert.False(result.IsOk);
        Assert.NotEmpty(result.Rows.Rows);
    }

    [Fact]
    public async Task Foreign_key_check_lists_orphan_rows()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE p (id INTEGER PRIMARY KEY)",
            "CREATE TABLE c (id INTEGER PRIMARY KEY, p_id INTEGER REFERENCES p)",
            "INSERT INTO p VALUES (1)");
        var service = new MaintenanceService(db.Session);
        Assert.True((await service.ForeignKeyCheckAsync()).IsOk);

        await db.Session.ExecuteNonQueryAsync("PRAGMA foreign_keys = OFF");
        await db.Session.ExecuteNonQueryAsync("INSERT INTO c VALUES (10, 1), (11, 99)");
        var result = await service.ForeignKeyCheckAsync();
        Assert.False(result.IsOk);
        var row = Assert.Single(result.Rows.Rows);
        Assert.Equal("c", row[0]);
        Assert.Equal(11L, row[1]);
        Assert.Equal("p", row[2]);
    }
}

public class PragmaTests
{
    private static PragmaValues Defaults => new("DELETE", true, 2, -2000, 4096, 0, 1, 0, "UTF-8");

    [Fact]
    public async Task Reads_the_current_values()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)", "PRAGMA user_version = 7");
        var values = await new PragmaService(db.Session).ReadAsync();
        Assert.Equal("DELETE", values.JournalMode);
        Assert.True(values.ForeignKeys);
        Assert.Equal(7, values.UserVersion);
        Assert.Equal("UTF-8", values.Encoding);
        Assert.True(values.PageCount >= 2);
        Assert.Contains(values.PageSize, PragmaService.PageSizes);
    }

    [Fact]
    public void Plans_only_the_changed_settings()
    {
        var current = Defaults;
        Assert.Empty(PragmaService.PlanChanges(current, current));
        var wanted = current with { ForeignKeys = false, Synchronous = 1, CacheSize = 5000, UserVersion = 3, JournalMode = "wal" };
        Assert.Equal(
            ["PRAGMA foreign_keys = OFF", "PRAGMA synchronous = NORMAL", "PRAGMA cache_size = 5000", "PRAGMA user_version = 3", "PRAGMA journal_mode = WAL"],
            PragmaService.PlanChanges(current, wanted));
    }

    [Fact]
    public void Page_size_change_vacuums_and_leaves_wal_first()
    {
        var wal = Defaults with { JournalMode = "WAL" };
        Assert.Equal(
            ["PRAGMA journal_mode = DELETE", "PRAGMA page_size = 8192", "VACUUM"],
            PragmaService.PlanChanges(wal, Defaults with { PageSize = 8192 }));
        Assert.Equal(
            ["PRAGMA page_size = 8192", "VACUUM", "PRAGMA journal_mode = WAL"],
            PragmaService.PlanChanges(Defaults, wal with { PageSize = 8192 }));
        Assert.Throws<ArgumentException>(() => PragmaService.PlanChanges(wal, wal with { PageSize = 8192 }));
    }

    [Theory]
    [InlineData("WAL2", 1, 4096)]
    [InlineData("DELETE; DROP TABLE t", 1, 4096)]
    [InlineData("DELETE", 4, 4096)]
    [InlineData("DELETE", -1, 4096)]
    [InlineData("DELETE", 1, 3000)]
    public void Rejects_bad_values(string journal, int synchronous, int pageSize) =>
        Assert.Throws<ArgumentException>(() => PragmaService.PlanChanges(Defaults, Defaults with { JournalMode = journal, Synchronous = synchronous, PageSize = pageSize }));

    [Fact]
    public async Task Applies_changes_and_reads_them_back()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)", "INSERT INTO t VALUES (1)");
        var service = new PragmaService(db.Session);
        var current = await service.ReadAsync();
        var newPageSize = current.PageSize == 8192 ? 4096 : 8192;
        var result = await service.ApplyAsync(current with { UserVersion = 42, Synchronous = 1, CacheSize = 1234, PageSize = newPageSize, JournalMode = "WAL" });
        Assert.Equal(42, result.UserVersion);
        Assert.Equal(1, result.Synchronous);
        Assert.Equal(1234, result.CacheSize);
        Assert.Equal(newPageSize, result.PageSize);
        Assert.Equal("WAL", result.JournalMode);
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));

        // Back to DELETE so the temp folder can be removed.
        Assert.Equal("DELETE", (await service.ApplyAsync(result with { JournalMode = "DELETE" })).JournalMode);
    }

    [Fact]
    public async Task Read_only_database_allows_only_connection_settings()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)");
        var path = db.FilePath;
        await db.Manager.CloseAsync(db.Session);
        var session = await db.Manager.OpenAsync(path, readOnly: true);
        var service = new PragmaService(session);
        var current = await service.ReadAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(current with { UserVersion = 5 }));
        Assert.Equal(999, (await service.ApplyAsync(current with { CacheSize = 999 })).CacheSize);
    }
}

public class BackupAndAttachTests
{
    [Fact]
    public async Task Backup_copies_the_database_while_it_is_open()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)", "INSERT INTO t VALUES (1), (2)");
        await db.Session.ExecuteNonQueryAsync("BEGIN");
        await db.Session.ExecuteNonQueryAsync("INSERT INTO t VALUES (3)");
        var target = Path.Combine(db.Folder, "backup.db");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Session.BackupToAsync(target, overwrite: false));
        Assert.Contains("transaction", ex.Message);
        Assert.False(File.Exists(target));
        await db.Session.ExecuteNonQueryAsync("COMMIT");
        await db.Session.BackupToAsync(target, overwrite: false);

        var copy = await db.Manager.OpenAsync(target, readOnly: true);
        Assert.Equal(3L, await copy.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));
        Assert.Contains(db.Log.GetEntries(), e => e.Sql.StartsWith("-- online backup to", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(db.Folder, "*.tmp"));
    }

    [Fact]
    public async Task Backup_does_not_overwrite_without_permission_or_onto_itself()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (x)", "INSERT INTO t VALUES (1)");
        var target = Path.Combine(db.Folder, "existing.db");
        await File.WriteAllTextAsync(target, "keep me");
        await Assert.ThrowsAsync<IOException>(() => db.Session.BackupToAsync(target, overwrite: false));
        Assert.Equal("keep me", await File.ReadAllTextAsync(target));
        await Assert.ThrowsAsync<IOException>(() => db.Session.BackupToAsync(db.FilePath, overwrite: true));

        await db.Session.BackupToAsync(target, overwrite: true);
        var copy = await db.Manager.OpenAsync(target, readOnly: true);
        Assert.Equal(1L, await copy.ExecuteScalarAsync<long>("SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task Attach_query_across_files_and_detach()
    {
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE a (id INTEGER, name TEXT)", "INSERT INTO a VALUES (1, 'one')");
        var otherPath = Path.Combine(db.Folder, "other file.db");
        var other = await db.Manager.CreateAsync(otherPath);
        await other.ExecuteNonQueryAsync("CREATE TABLE b (id INTEGER, extra TEXT)");
        await other.ExecuteNonQueryAsync("INSERT INTO b VALUES (1, 'uno')");
        await db.Manager.CloseAsync(other);

        var attach = new AttachService(db.Session);
        var name = AttachService.SuggestName(otherPath, []);
        Assert.Equal("other_file", name);
        await attach.AttachAsync(otherPath, name);
        var list = await attach.ListAsync();
        Assert.Equal(name, Assert.Single(list).Name);
        Assert.Equal("uno", await db.Session.ExecuteScalarAsync<string>("SELECT b.extra FROM a JOIN other_file.b AS b USING (id)"));

        await Assert.ThrowsAsync<ArgumentException>(() => attach.AttachAsync(otherPath, "again"));
        await Assert.ThrowsAsync<ArgumentException>(() => attach.AttachAsync(otherPath, name));
        await Assert.ThrowsAsync<ArgumentException>(() => attach.AttachAsync(otherPath, "main"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => attach.AttachAsync(Path.Combine(db.Folder, "missing.db"), "m"));
        Assert.False(File.Exists(Path.Combine(db.Folder, "missing.db")));

        await attach.DetachAsync(name);
        Assert.Empty(await attach.ListAsync());
    }

    [Theory]
    [InlineData(@"C:\x\sales 2024.db", "sales_2024")]
    [InlineData(@"C:\x\2024.db", "db2024")]
    [InlineData(@"C:\x\---.db", "db")]
    [InlineData(@"C:\x\main.db", "main2")]
    public void Suggests_safe_schema_names(string path, string expected) =>
        Assert.Equal(expected, AttachService.SuggestName(path, []));
}

public class QueryPlanTests
{
    [Fact]
    public void Builds_a_tree_from_parent_ids()
    {
        var roots = QueryPlan.BuildTree([(2, 0, "CO-ROUTINE x"), (5, 2, "SCAN t"), (9, 0, "SCAN x"), (12, 9, "USING INDEX"), (15, 77, "orphan")]);
        Assert.Equal(["CO-ROUTINE x", "SCAN x", "orphan"], roots.Select(r => r.Detail));
        Assert.Equal("SCAN t", Assert.Single(roots[0].Children).Detail);
        Assert.Equal("USING INDEX", Assert.Single(roots[1].Children).Detail);
    }

    [Fact]
    public async Task Explains_a_join_with_parameters()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE c (id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE o (id INTEGER PRIMARY KEY, c_id INTEGER, total REAL)",
            "CREATE INDEX o_c ON o (c_id)");

        var (statement, plan) = await QueryPlan.ExplainAsync(db.Session,
            "SELECT c.name, (SELECT sum(total) FROM o WHERE o.c_id = c.id) FROM c WHERE c.name = @name; SELECT 2;");
        Assert.StartsWith("SELECT c.name", statement.Text);
        var all = Flatten(plan).ToList();
        Assert.Contains(all, n => n.Detail.StartsWith("SCAN c", StringComparison.Ordinal) && n.IsFullScan);
        Assert.Contains(all, n => n.Detail.Contains("USING INDEX o_c", StringComparison.Ordinal) && !n.IsFullScan);
        Assert.Contains(all, n => n.Children.Count > 0);
    }

    [Fact]
    public async Task Rejects_empty_and_explain_statements()
    {
        await using var db = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => QueryPlan.ExplainAsync(db.Session, "  -- nothing"));
        await Assert.ThrowsAsync<ArgumentException>(() => QueryPlan.ExplainAsync(db.Session, "EXPLAIN SELECT 1"));
        await Assert.ThrowsAsync<SqliteException>(() => QueryPlan.ExplainAsync(db.Session, "SELECT * FROM missing"));
    }

    private static IEnumerable<QueryPlanNode> Flatten(IEnumerable<QueryPlanNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
}

public class SettingsLayoutTests
{
    [Fact]
    public void Recent_files_are_newest_first_unique_and_capped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 12; i++)
        {
            settings.AddRecentFile($@"C:\db\{i}.db", readOnly: false);
        }

        settings.AddRecentFile(@"c:\DB\5.db", readOnly: true);
        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
        Assert.Equal(@"c:\DB\5.db", settings.RecentFiles[0].Path);
        Assert.True(settings.RecentFiles[0].ReadOnly);
        Assert.Single(settings.RecentFiles, f => f.Path.Equals(@"C:\db\5.db", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\db\11.db", settings.RecentFiles[1].Path);

        settings.RemoveRecentFile(@"C:\DB\11.DB");
        Assert.DoesNotContain(settings.RecentFiles, f => f.Path.EndsWith("11.db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Layout_theme_and_recent_files_round_trip()
    {
        var folder = TestDatabase.NewFolder();
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            var settings = new AppSettings
            {
                Theme = "DARK",
                Window = new WindowLayout { X = 10, Y = 20, Width = 50, Height = 900, Maximized = true, ExplorerWidth = 5, OutputHeight = 250, OutputVisible = false },
            };
            settings.AddRecentFile(@"C:\a.db", readOnly: true);
            await store.SaveAsync(settings);
            var text = await File.ReadAllTextAsync(store.FilePath);
            Assert.DoesNotContain("isDarkTheme", text);

            var loaded = await store.LoadAsync();
            Assert.True(loaded.IsDarkTheme);
            Assert.Equal("dark", loaded.Theme);
            Assert.Equal(@"C:\a.db", Assert.Single(loaded.RecentFiles).Path);
            Assert.Equal(400, loaded.Window!.Width); // Raised to the minimum.
            Assert.Equal(100, loaded.Window.ExplorerWidth);
            Assert.True(loaded.Window.Maximized);
            Assert.False(loaded.Window.OutputVisible);

            await File.WriteAllTextAsync(store.FilePath, "{\"theme\": \"purple\", \"recentFiles\": null}");
            loaded = await store.LoadAsync();
            Assert.Equal("light", loaded.Theme);
            Assert.Empty(loaded.RecentFiles);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
