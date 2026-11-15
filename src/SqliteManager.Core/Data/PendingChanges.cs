using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Data;

/// <summary>The table being edited and how its rows are identified.</summary>
public sealed record EditTarget(string TableName, IReadOnlyList<string> Columns, RowKeyKind KeyKind, string? RowIdName, IReadOnlyList<string> KeyColumns)
{
    public static EditTarget FromSnapshot(TableSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var columns = snapshot.Details.Columns.Select(c => c.Name).ToArray();
        return new EditTarget(snapshot.Name, columns, snapshot.KeyKind, snapshot.RowIdName, snapshot.KeyColumnIndexes.Select(i => columns[i]).ToArray());
    }
}

/// <summary>One statement of a write batch. <see cref="ExpectedRows"/> is checked after it runs.</summary>
public sealed record BatchCommand(string Sql, IReadOnlyDictionary<string, object?> Parameters, int? ExpectedRows = null);

/// <summary>A new row that has not been written yet. Columns that were never set use the table default.</summary>
public sealed class InsertedRow
{
    private readonly object?[] _values;
    private readonly bool[] _isSet;

    internal InsertedRow(int columnCount)
    {
        _values = new object?[columnCount];
        _isSet = new bool[columnCount];
    }

    public object? this[int column] => _values[column];

    public bool IsSet(int column) => _isSet[column];

    internal void Set(int column, object? value)
    {
        _values[column] = value;
        _isSet[column] = true;
    }

    internal void Unset(int column)
    {
        _values[column] = null;
        _isSet[column] = false;
    }
}

/// <summary>
/// Edits that have not been written: changed cells of existing rows, new rows, and deleted rows.
/// <see cref="BuildCommands"/> turns them into parameterized statements for one transaction.
/// </summary>
public sealed class PendingChanges
{
    private readonly Dictionary<RowKey, Dictionary<int, CellChange>> _updates = [];
    private readonly HashSet<RowKey> _deletes = [];
    private readonly List<InsertedRow> _inserts = [];

    public PendingChanges(EditTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.KeyKind == RowKeyKind.None)
        {
            throw new ArgumentException("The table has no usable row key, so it cannot be edited.", nameof(target));
        }

        Target = target;
    }

    public EditTarget Target { get; }

    /// <summary>Raised after any change to the pending edits.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<InsertedRow> InsertedRows => _inserts;

    public int UpdatedRowCount => _updates.Keys.Count(k => !_deletes.Contains(k));

    public int DeletedRowCount => _deletes.Count;

    public int InsertedRowCount => _inserts.Count;

    /// <summary>Number of rows with pending changes.</summary>
    public int Count => UpdatedRowCount + DeletedRowCount + InsertedRowCount;

    public bool HasChanges => Count > 0;

    // ---- Existing rows ----

    /// <summary>Changes a cell. Setting it back to <paramref name="original"/> removes the change.</summary>
    public void SetValue(RowKey key, int column, object? original, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckColumn(column);
        if (_deletes.Contains(key))
        {
            throw new InvalidOperationException("The row is marked for deletion.");
        }

        if (!_updates.TryGetValue(key, out var row))
        {
            row = [];
            _updates[key] = row;
        }

        var firstOriginal = row.TryGetValue(column, out var existing) ? existing.Original : original;
        if (ValueComparer.AreEqual(firstOriginal, value))
        {
            row.Remove(column);
            if (row.Count == 0)
            {
                _updates.Remove(key);
            }
        }
        else
        {
            row[column] = new CellChange(firstOriginal, value);
        }

        OnChanged();
    }

    /// <summary>Gets the pending value of a cell, if it was changed.</summary>
    public bool TryGetValue(RowKey key, int column, out object? value)
    {
        if (_updates.TryGetValue(key, out var row) && row.TryGetValue(column, out var change))
        {
            value = change.Value;
            return true;
        }

        value = null;
        return false;
    }

    public bool IsCellChanged(RowKey key, int column) => _updates.TryGetValue(key, out var row) && row.ContainsKey(column);

    public bool IsRowChanged(RowKey key) => _updates.ContainsKey(key);

    public bool IsDeleted(RowKey key) => _deletes.Contains(key);

    public void RevertCell(RowKey key, int column)
    {
        if (_updates.TryGetValue(key, out var row) && row.Remove(column))
        {
            if (row.Count == 0)
            {
                _updates.Remove(key);
            }

            OnChanged();
        }
    }

    /// <summary>Removes all pending changes and the deletion mark from a row.</summary>
    public void RevertRow(RowKey key)
    {
        var changed = _updates.Remove(key) | _deletes.Remove(key);
        if (changed)
        {
            OnChanged();
        }
    }

    /// <summary>Marks a row for deletion. Its pending cell changes are dropped.</summary>
    public void Delete(RowKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _updates.Remove(key);
        if (_deletes.Add(key))
        {
            OnChanged();
        }
    }

    // ---- New rows ----

    /// <summary>Adds a new row and returns its index in <see cref="InsertedRows"/>.</summary>
    public int AddRow()
    {
        _inserts.Add(new InsertedRow(Target.Columns.Count));
        OnChanged();
        return _inserts.Count - 1;
    }

    public void SetInsertedValue(int rowIndex, int column, object? value)
    {
        CheckColumn(column);
        _inserts[rowIndex].Set(column, value);
        OnChanged();
    }

    /// <summary>Puts a column of a new row back to the table default.</summary>
    public void UnsetInsertedValue(int rowIndex, int column)
    {
        CheckColumn(column);
        _inserts[rowIndex].Unset(column);
        OnChanged();
    }

    public void RemoveInsertedRow(int rowIndex)
    {
        _inserts.RemoveAt(rowIndex);
        OnChanged();
    }

    public void Clear()
    {
        _updates.Clear();
        _deletes.Clear();
        _inserts.Clear();
        OnChanged();
    }

    /// <summary>Statements that apply the changes: deletes, then updates, then inserts. Each must affect exactly one row.</summary>
    public IReadOnlyList<BatchCommand> BuildCommands()
    {
        var table = SqlIdentifier.Quote(Target.TableName);
        var commands = new List<BatchCommand>();

        foreach (var key in _deletes)
        {
            var parameters = new Dictionary<string, object?>();
            commands.Add(new BatchCommand($"DELETE FROM {table} WHERE {KeyCondition(key, parameters)}", parameters, 1));
        }

        foreach (var (key, row) in _updates)
        {
            if (_deletes.Contains(key))
            {
                continue;
            }

            var parameters = new Dictionary<string, object?>();
            var sets = new List<string>();
            foreach (var (column, change) in row.OrderBy(c => c.Key))
            {
                var name = $"@v{sets.Count}";
                sets.Add($"{SqlIdentifier.Quote(Target.Columns[column])} = {name}");
                parameters[name] = change.Value;
            }

            commands.Add(new BatchCommand($"UPDATE {table} SET {string.Join(", ", sets)} WHERE {KeyCondition(key, parameters)}", parameters, 1));
        }

        foreach (var row in _inserts)
        {
            var parameters = new Dictionary<string, object?>();
            var columns = new List<string>();
            for (var c = 0; c < Target.Columns.Count; c++)
            {
                if (row.IsSet(c))
                {
                    var name = $"@v{columns.Count}";
                    columns.Add(SqlIdentifier.Quote(Target.Columns[c]));
                    parameters[name] = row[c];
                }
            }

            var sql = columns.Count == 0
                ? $"INSERT INTO {table} DEFAULT VALUES"
                : $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters.Keys)})";
            commands.Add(new BatchCommand(sql, parameters, 1));
        }

        return commands;
    }

    private string KeyCondition(RowKey key, Dictionary<string, object?> parameters)
    {
        if (Target.KeyKind == RowKeyKind.RowId)
        {
            parameters["@k0"] = key.Values[0];
            return $"{Target.RowIdName} = @k0";
        }

        var parts = new List<string>();
        for (var i = 0; i < Target.KeyColumns.Count; i++)
        {
            parameters[$"@k{i}"] = key.Values[i];
            parts.Add($"{SqlIdentifier.Quote(Target.KeyColumns[i])} = @k{i}");
        }

        return string.Join(" AND ", parts);
    }

    private void CheckColumn(int column) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)column, (uint)Target.Columns.Count, nameof(column));

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private readonly record struct CellChange(object? Original, object? Value);
}
