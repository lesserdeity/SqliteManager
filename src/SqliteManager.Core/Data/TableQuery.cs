using SqliteManager.Core.Schema;

namespace SqliteManager.Core.Data;

public enum SortDirection
{
    None,
    Ascending,
    Descending,
}

/// <summary>Filter text typed for one column. See <see cref="FilterParser"/> for the syntax.</summary>
public sealed record ColumnFilter(string Column, string Text);

/// <summary>Filters and sort order applied when browsing a table.</summary>
public sealed record TableQuery(IReadOnlyList<ColumnFilter> Filters, string? SortColumn = null, SortDirection SortDirection = SortDirection.None)
{
    public static TableQuery None { get; } = new([]);

    public bool IsFiltered => Filters.Any(f => !string.IsNullOrWhiteSpace(f.Text));

    public bool IsSorted => SortColumn is not null && SortDirection != SortDirection.None;
}

/// <summary>How rows of a table are identified when they are edited.</summary>
public enum RowKeyKind
{
    /// <summary>Rows cannot be identified (views, or no usable key). The data is read-only.</summary>
    None,

    /// <summary>By rowid (see <see cref="TableSnapshot.RowIdName"/>).</summary>
    RowId,

    /// <summary>By the primary-key columns (WITHOUT ROWID tables).</summary>
    PrimaryKey,
}

/// <summary>Identifies one row: its rowid, or its primary-key values. Compares by value.</summary>
public sealed class RowKey : IEquatable<RowKey>
{
    private readonly object?[] _values;

    public RowKey(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = (object?[])values.Clone();
    }

    public IReadOnlyList<object?> Values => _values;

    public bool Equals(RowKey? other) =>
        other is not null && _values.Length == other._values.Length && _values.Zip(other._values).All(p => ValueComparer.AreEqual(p.First, p.Second));

    public override bool Equals(object? obj) => Equals(obj as RowKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value is byte[] b ? b.Length : value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _values.Select(v => v ?? "NULL"));
}

/// <summary>Compares SQLite values. Byte arrays compare by content.</summary>
public static class ValueComparer
{
    public static bool AreEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        _ => Equals(a, b),
    };
}

/// <summary>
/// A table or view opened for browsing with a given query. <see cref="OrderedRowIds"/> is set when a rowid
/// table is filtered or sorted: pages are then read by rowid instead of by OFFSET.
/// </summary>
public sealed record TableSnapshot(
    TableDetails Details,
    RowKeyKind KeyKind,
    string? RowIdName,
    IReadOnlyList<int> KeyColumnIndexes,
    TableQuery Query,
    long TotalRowCount,
    long RowCount,
    IReadOnlyList<long>? OrderedRowIds)
{
    public string Name => Details.Name;

    public bool IsView => Details.Kind == TableKind.View;
}

/// <summary>One row and its key (null when the table has no usable key).</summary>
public sealed record TableRow(RowKey? Key, object?[] Values);
