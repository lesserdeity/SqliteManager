namespace SqliteManager.Core.Data;

/// <summary>A column in a result set.</summary>
public sealed record ResultColumn(string Name, string DataTypeName);

/// <summary>
/// Rows returned by a query. Database NULL is returned as <c>null</c>;
/// BLOBs are returned as <c>byte[]</c>.
/// </summary>
public sealed record RowSet(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<object?[]> Rows);
