using SqliteManager.Core.Data;

namespace SqliteManager.Core.Tests;

public class PendingChangesTests
{
    private static readonly EditTarget RowIdTarget = new("people", ["id", "name", "age"], RowKeyKind.RowId, "rowid", []);
    private static readonly EditTarget PkTarget = new("my \"odd\" table", ["a", "b", "v"], RowKeyKind.PrimaryKey, null, ["b", "a"]);

    [Fact]
    public void Requires_a_row_key()
    {
        Assert.Throws<ArgumentException>(() => new PendingChanges(new EditTarget("v", ["x"], RowKeyKind.None, null, [])));
    }

    [Fact]
    public void Update_builds_parameterized_sql_by_rowid()
    {
        var changes = new PendingChanges(RowIdTarget);
        changes.SetValue(new RowKey(5L), 2, 30L, 31L);
        changes.SetValue(new RowKey(5L), 1, "Ann", null);

        var command = Assert.Single(changes.BuildCommands());

        Assert.Equal("UPDATE \"people\" SET \"name\" = @v0, \"age\" = @v1 WHERE rowid = @k0", command.Sql);
        Assert.Equal(new Dictionary<string, object?> { ["@v0"] = null, ["@v1"] = 31L, ["@k0"] = 5L }, command.Parameters);
        Assert.Equal(1, command.ExpectedRows);
    }

    [Fact]
    public void Primary_key_target_uses_all_key_columns_and_quotes_names()
    {
        var changes = new PendingChanges(PkTarget);
        changes.SetValue(new RowKey(2L, "x"), 2, "old", "new");
        changes.Delete(new RowKey(1L, "y"));

        var commands = changes.BuildCommands();

        Assert.Equal("DELETE FROM \"my \"\"odd\"\" table\" WHERE \"b\" = @k0 AND \"a\" = @k1", commands[0].Sql);
        Assert.Equal(1L, commands[0].Parameters["@k0"]);
        Assert.Equal("y", commands[0].Parameters["@k1"]);
        Assert.Equal("UPDATE \"my \"\"odd\"\" table\" SET \"v\" = @v0 WHERE \"b\" = @k0 AND \"a\" = @k1", commands[1].Sql);
    }

    [Fact]
    public void Setting_a_cell_back_to_its_original_removes_the_change()
    {
        var changes = new PendingChanges(RowIdTarget);
        var key = new RowKey(1L);

        changes.SetValue(key, 1, "Ann", "Bob");
        changes.SetValue(key, 1, "Bob", "Cy"); // Original stays "Ann".
        Assert.True(changes.TryGetValue(key, 1, out var value));
        Assert.Equal("Cy", value);

        changes.SetValue(key, 1, "Cy", "Ann");
        Assert.False(changes.HasChanges);
        Assert.False(changes.IsRowChanged(key));
    }

    [Fact]
    public void Blob_values_compare_by_content()
    {
        var changes = new PendingChanges(RowIdTarget);
        changes.SetValue(new RowKey(1L), 1, new byte[] { 1, 2 }, new byte[] { 1, 2 });

        Assert.False(changes.HasChanges);
    }

    [Fact]
    public void Revert_cell_and_row()
    {
        var changes = new PendingChanges(RowIdTarget);
        var key = new RowKey(1L);
        changes.SetValue(key, 1, "a", "b");
        changes.SetValue(key, 2, 1L, 2L);

        changes.RevertCell(key, 1);
        Assert.False(changes.IsCellChanged(key, 1));
        Assert.True(changes.IsCellChanged(key, 2));

        changes.RevertRow(key);
        Assert.False(changes.HasChanges);

        changes.Delete(key);
        Assert.True(changes.IsDeleted(key));
        changes.RevertRow(key);
        Assert.False(changes.IsDeleted(key));
    }

    [Fact]
    public void Delete_drops_pending_updates_and_blocks_new_ones()
    {
        var changes = new PendingChanges(RowIdTarget);
        var key = new RowKey(1L);
        changes.SetValue(key, 1, "a", "b");

        changes.Delete(key);

        Assert.Equal((0, 1, 0), (changes.UpdatedRowCount, changes.DeletedRowCount, changes.InsertedRowCount));
        Assert.Throws<InvalidOperationException>(() => changes.SetValue(key, 1, "a", "c"));
        Assert.Equal("DELETE FROM \"people\" WHERE rowid = @k0", Assert.Single(changes.BuildCommands()).Sql);
    }

    [Fact]
    public void Inserts_only_name_the_columns_that_were_set()
    {
        var changes = new PendingChanges(RowIdTarget);
        var first = changes.AddRow();
        changes.SetInsertedValue(first, 1, "Zed");
        changes.SetInsertedValue(first, 2, null);
        var second = changes.AddRow();
        changes.SetInsertedValue(second, 0, 9L);
        changes.UnsetInsertedValue(second, 0);

        var commands = changes.BuildCommands();

        Assert.Equal("INSERT INTO \"people\" (\"name\", \"age\") VALUES (@v0, @v1)", commands[0].Sql);
        Assert.Equal(new Dictionary<string, object?> { ["@v0"] = "Zed", ["@v1"] = null }, commands[0].Parameters);
        Assert.Equal("INSERT INTO \"people\" DEFAULT VALUES", commands[1].Sql);
        Assert.Equal(2, changes.InsertedRowCount);

        changes.RemoveInsertedRow(0);
        Assert.Equal(1, changes.InsertedRowCount);
    }

    [Fact]
    public void Commands_are_ordered_deletes_updates_inserts_and_changed_event_fires()
    {
        var changes = new PendingChanges(RowIdTarget);
        var raised = 0;
        changes.Changed += (_, _) => raised++;

        changes.AddRow();
        changes.SetValue(new RowKey(2L), 1, "x", "y");
        changes.Delete(new RowKey(3L));

        Assert.Equal(["DELETE", "UPDATE", "INSERT"], changes.BuildCommands().Select(c => c.Sql.Split(' ')[0]));
        Assert.Equal(3, raised);
        Assert.Equal(3, changes.Count);

        changes.Clear();
        Assert.False(changes.HasChanges);
        Assert.Empty(changes.BuildCommands());
    }

    [Fact]
    public void RowKey_compares_by_value()
    {
        Assert.Equal(new RowKey(1L, "a"), new RowKey(1L, "a"));
        Assert.Equal(new RowKey(1L, "a").GetHashCode(), new RowKey(1L, "a").GetHashCode());
        Assert.NotEqual(new RowKey(1L, "a"), new RowKey(1L, "b"));
        Assert.Equal(new RowKey(new byte[] { 1 }), new RowKey(new byte[] { 1 }));
        Assert.Equal(new RowKey((object?)null), new RowKey((object?)null));
    }

    // ---- Writing to a real database ----

    private static async Task<(TestDatabase Db, TableDataService Service, TableSnapshot Snapshot, PendingChanges Changes)> PeopleAsync()
    {
        var db = await TestDatabase.CreateAsync(
            "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER DEFAULT 18, photo BLOB)",
            "INSERT INTO people (id, name, age) VALUES (1, 'Ann', 30), (2, 'Bob', 40), (3, 'Cy', 50)");
        var service = new TableDataService(db.Session);
        var snapshot = await service.OpenAsync("people");
        return (db, service, snapshot, new PendingChanges(EditTarget.FromSnapshot(snapshot)));
    }

    private static async Task<List<object?[]>> ReadAllAsync(TestDatabase db) =>
        (await db.Session.QueryRowsAsync("SELECT id, name, age, photo FROM people ORDER BY id")).Rows.ToList();

    [Fact]
    public async Task Write_applies_updates_inserts_and_deletes_in_one_batch()
    {
        var (db, _, _, changes) = await PeopleAsync();
        await using var _db = db;
        changes.SetValue(new RowKey(1L), 1, "Ann", "Anne");
        changes.SetValue(new RowKey(1L), 3, null, new byte[] { 0xCA, 0xFE });
        changes.Delete(new RowKey(2L));
        var row = changes.AddRow();
        changes.SetInsertedValue(row, 1, "Dee");

        var result = await db.Session.ExecuteBatchAsync(changes.BuildCommands());

        Assert.Equal(3, result.CommandCount);
        Assert.False(result.InsideUserTransaction);
        var rows = await ReadAllAsync(db);
        Assert.Equal(3, rows.Count);
        Assert.Equal([1L, "Anne", 30L, new byte[] { 0xCA, 0xFE }], rows[0]);
        Assert.Equal(3L, rows[1][0]);
        Assert.Equal("Dee", rows[2][1]);
        Assert.Equal(18L, rows[2][2]); // Default applied because "age" was not set.
    }

    [Fact]
    public async Task Failing_command_rolls_back_everything()
    {
        var (db, _, _, changes) = await PeopleAsync();
        await using var _db = db;
        var before = await ReadAllAsync(db);
        changes.SetValue(new RowKey(1L), 1, "Ann", "Changed");
        changes.Delete(new RowKey(3L));
        var row = changes.AddRow();
        changes.SetInsertedValue(row, 1, null); // Violates NOT NULL.

        var ex = await Assert.ThrowsAsync<BatchExecutionException>(() => db.Session.ExecuteBatchAsync(changes.BuildCommands()));

        Assert.Equal(2, ex.CommandIndex);
        Assert.Contains("NOT NULL", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await ReadAllAsync(db));
        Assert.Equal(1L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM people WHERE name = 'Ann'"));
    }

    [Fact]
    public async Task Row_changed_by_someone_else_is_detected_and_rolled_back()
    {
        var (db, _, _, changes) = await PeopleAsync();
        await using var _db = db;
        changes.SetValue(new RowKey(1L), 1, "Ann", "Anne");
        changes.SetValue(new RowKey(2L), 1, "Bob", "Bobby");
        await db.Session.ExecuteNonQueryAsync("DELETE FROM people WHERE id = 2");

        var ex = await Assert.ThrowsAsync<BatchExecutionException>(() => db.Session.ExecuteBatchAsync(changes.BuildCommands()));

        Assert.Contains("affected 0 rows", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Ann", await db.Session.ExecuteScalarAsync<string>("SELECT name FROM people WHERE id = 1"));
    }

    [Fact]
    public async Task Batch_joins_an_open_user_transaction()
    {
        var (db, _, _, changes) = await PeopleAsync();
        await using var _db = db;
        await db.Session.ExecuteNonQueryAsync("BEGIN");
        changes.Delete(new RowKey(1L));

        var result = await db.Session.ExecuteBatchAsync(changes.BuildCommands());
        Assert.True(result.InsideUserTransaction);
        Assert.Equal(2L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM people"));

        await db.Session.ExecuteNonQueryAsync("ROLLBACK");
        Assert.Equal(3L, await db.Session.ExecuteScalarAsync<long>("SELECT count(*) FROM people"));
    }

    [Fact]
    public async Task Failed_batch_inside_user_transaction_keeps_earlier_work()
    {
        var (db, _, _, changes) = await PeopleAsync();
        await using var _db = db;
        await db.Session.ExecuteNonQueryAsync("BEGIN");
        await db.Session.ExecuteNonQueryAsync("UPDATE people SET age = 99 WHERE id = 3");
        var row = changes.AddRow();
        changes.SetInsertedValue(row, 1, null);

        await Assert.ThrowsAsync<BatchExecutionException>(() => db.Session.ExecuteBatchAsync(changes.BuildCommands()));
        await db.Session.ExecuteNonQueryAsync("COMMIT");

        Assert.Equal(99L, await db.Session.ExecuteScalarAsync<long>("SELECT age FROM people WHERE id = 3"));
    }

    [Fact]
    public async Task Batch_is_logged_including_the_savepoint()
    {
        var (db, _, _, changes) = await PeopleAsync();
        await using var _db = db;
        changes.SetValue(new RowKey(1L), 2, 30L, 31L);
        db.Log.Clear();

        await db.Session.ExecuteBatchAsync(changes.BuildCommands());

        Assert.Equal(
            ["SAVEPOINT sqlite_manager_write", "UPDATE \"people\" SET \"age\" = @v0 WHERE rowid = @k0", "RELEASE sqlite_manager_write"],
            db.Log.GetEntries().Select(e => e.Sql));
    }

    [Fact]
    public async Task Without_rowid_table_round_trip()
    {
        await using var db = await TestDatabase.CreateAsync(
            "CREATE TABLE w (a TEXT, b INTEGER, v TEXT, PRIMARY KEY (b, a)) WITHOUT ROWID",
            "INSERT INTO w VALUES ('x', 2, 'one'), ('y', 1, 'two')");
        var service = new TableDataService(db.Session);
        var snapshot = await service.OpenAsync("w");
        var rows = await service.GetPageAsync(snapshot, 0, 10);
        var changes = new PendingChanges(EditTarget.FromSnapshot(snapshot));

        changes.SetValue(rows[0].Key!, 0, "y", "z"); // Changing a key column; WHERE uses the old key.
        changes.Delete(rows[1].Key!);
        await db.Session.ExecuteBatchAsync(changes.BuildCommands());

        var after = (await db.Session.QueryRowsAsync("SELECT a, b, v FROM w")).Rows;
        Assert.Equal([["z", 1L, "two"]], after.Select(r => r.ToArray()));
    }
}
