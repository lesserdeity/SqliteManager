using System.Text;
using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Data;

/// <summary>Reads table and view data one page at a time, with optional filters and sort order.</summary>
public sealed class TableDataService
{
    private static readonly string[] RowIdNames = ["rowid", "_rowid_", "oid"];

    private readonly DatabaseSession _session;

    public TableDataService(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Reads the table's structure, counts rows, and (for filtered or sorted rowid tables) reads the ordered rowids.</summary>
    public async Task<TableSnapshot> OpenAsync(string name, TableQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        query ??= TableQuery.None;

        var details = await new SchemaService(_session).GetTableDetailsAsync(name, cancellationToken).ConfigureAwait(false);
        var columnNames = details.Columns.Select(c => c.Name).ToArray();

        // A rowid alias is usable only if no real column has that name.
        var rowIdName = details.HasRowId
            ? RowIdNames.FirstOrDefault(n => !columnNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            : null;

        var keyKind = RowKeyKind.None;
        IReadOnlyList<int> keyIndexes = [];
        if (details.Kind == TableKind.Table)
        {
            if (rowIdName is not null)
            {
                keyKind = RowKeyKind.RowId;
            }
            else if (details.PrimaryKey.Count > 0)
            {
                keyKind = RowKeyKind.PrimaryKey;
                keyIndexes = details.PrimaryKey.Select(pk => details.Columns.ToList().IndexOf(pk)).ToArray();
            }
        }

        var filter = FilterParser.Build(query.Filters, columnNames);
        var table = SqlIdentifier.Quote(name);
        var where = filter.IsEmpty ? string.Empty : " WHERE " + filter.Sql;

        var total = await _session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<long>? orderedRowIds = null;
        long rowCount;
        if (keyKind == RowKeyKind.RowId && (query.IsFiltered || query.IsSorted))
        {
            var sql = $"SELECT {rowIdName} FROM {table}{where}{OrderBy(query, columnNames, [rowIdName!])}";
            orderedRowIds = await _session.QueryAsync(sql, r => r.GetInt64(0), filter.Parameters, cancellationToken).ConfigureAwait(false);
            rowCount = orderedRowIds.Count;
        }
        else if (!filter.IsEmpty)
        {
            rowCount = await _session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}{where}", filter.Parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            rowCount = total;
        }

        return new TableSnapshot(details, keyKind, rowIdName, keyIndexes, query, total, rowCount, orderedRowIds);
    }

    /// <summary>Reads up to <paramref name="limit"/> rows starting at row <paramref name="offset"/> of the snapshot.</summary>
    public async Task<IReadOnlyList<TableRow>> GetPageAsync(TableSnapshot snapshot, long offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var table = SqlIdentifier.Quote(snapshot.Name);
        var columnNames = snapshot.Details.Columns.Select(c => c.Name).ToArray();

        if (snapshot.OrderedRowIds is { } ids)
        {
            if (offset >= ids.Count)
            {
                return [];
            }

            var slice = ids.Skip((int)offset).Take(limit).ToArray();
            var parameters = new Dictionary<string, object?>();
            var names = new StringBuilder();
            for (var i = 0; i < slice.Length; i++)
            {
                names.Append(i == 0 ? "@k0" : $", @k{i}");
                parameters[$"@k{i}"] = slice[i];
            }

            var rows = await _session.QueryRowsAsync(
                $"SELECT {snapshot.RowIdName}, * FROM {table} WHERE {snapshot.RowIdName} IN ({names})", parameters, cancellationToken)
                .ConfigureAwait(false);
            var byId = rows.Rows.ToDictionary(r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture));

            // Keep the snapshot's order. Rows deleted since the snapshot are skipped.
            return slice.Where(byId.ContainsKey).Select(id => new TableRow(new RowKey(id), byId[id][1..])).ToList();
        }

        var filter = FilterParser.Build(snapshot.Query.Filters, columnNames);
        var where = filter.IsEmpty ? string.Empty : " WHERE " + filter.Sql;
        var pageParameters = new Dictionary<string, object?>(filter.Parameters) { ["@limit"] = limit, ["@offset"] = offset };

        if (snapshot.KeyKind == RowKeyKind.RowId)
        {
            var sql = $"SELECT {snapshot.RowIdName}, * FROM {table}{where}{OrderBy(snapshot.Query, columnNames, [snapshot.RowIdName!])} LIMIT @limit OFFSET @offset";
            var rows = await _session.QueryRowsAsync(sql, pageParameters, cancellationToken).ConfigureAwait(false);
            return rows.Rows.Select(r => new TableRow(new RowKey(r[0]), r[1..])).ToList();
        }

        var tieBreak = snapshot.KeyColumnIndexes.Select(i => SqlIdentifier.Quote(columnNames[i])).ToArray();
        var plainSql = $"SELECT * FROM {table}{where}{OrderBy(snapshot.Query, columnNames, tieBreak)} LIMIT @limit OFFSET @offset";
        var plainRows = await _session.QueryRowsAsync(plainSql, pageParameters, cancellationToken).ConfigureAwait(false);
        return plainRows.Rows.Select(r => new TableRow(
                snapshot.KeyKind == RowKeyKind.PrimaryKey ? new RowKey(snapshot.KeyColumnIndexes.Select(i => r[i]).ToArray()) : null,
                r))
            .ToList();
    }

    /// <summary>ORDER BY for the query's sort column, then the tie-break expressions so paging is stable.</summary>
    private static string OrderBy(TableQuery query, IReadOnlyCollection<string> columns, IReadOnlyList<string> tieBreak)
    {
        var parts = new List<string>();
        var sortColumn = query.IsSorted
            ? columns.FirstOrDefault(c => string.Equals(c, query.SortColumn, StringComparison.OrdinalIgnoreCase))
            : null;
        if (sortColumn is not null)
        {
            parts.Add(SqlIdentifier.Quote(sortColumn) + (query.SortDirection == SortDirection.Descending ? " DESC" : " ASC"));
        }

        parts.AddRange(tieBreak);
        return parts.Count == 0 ? string.Empty : " ORDER BY " + string.Join(", ", parts);
    }
}
