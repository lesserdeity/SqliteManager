using Microsoft.Data.Sqlite;
using SqliteManager.Core.Data;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Schema;

/// <summary>Reads schema information from a database.</summary>
public sealed class SchemaService
{
    private readonly DatabaseSession _session;

    public SchemaService(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Returns all tables, views, indexes, and triggers in the main schema.</summary>
    public async Task<IReadOnlyList<SchemaObject>> GetObjectsAsync(CancellationToken cancellationToken = default)
    {
        const string sql =
            "SELECT type, name, tbl_name, sql FROM sqlite_schema " +
            "WHERE type IN ('table', 'view', 'index', 'trigger') ORDER BY type, name COLLATE NOCASE";

        return await _session.QueryAsync(sql, r => new SchemaObject(
                r.GetString(1),
                ParseObjectType(r.GetString(0)),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the type, columns, foreign keys, and indexes of a table or view.</summary>
    public async Task<TableDetails> GetTableDetailsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        var nameParameter = new Dictionary<string, object?> { ["@name"] = tableName };

        var tableInfo = await _session.QueryAsync(
            "SELECT type, wr, strict FROM pragma_table_list WHERE schema = 'main' AND name = @name",
            r => (Type: r.GetString(0), WithoutRowId: r.GetInt64(1) != 0, Strict: r.GetInt64(2) != 0),
            nameParameter, cancellationToken).ConfigureAwait(false);

        if (tableInfo.Count == 0)
        {
            throw new ArgumentException($"The table or view '{tableName}' does not exist.", nameof(tableName));
        }

        var (type, withoutRowId, strict) = tableInfo[0];
        var kind = type switch
        {
            "view" => TableKind.View,
            "virtual" => TableKind.Virtual,
            "shadow" => TableKind.Shadow,
            _ => TableKind.Table,
        };

        // hidden: 0 = normal, 1 = hidden column of a virtual table, 2/3 = generated column.
        var columns = await _session.QueryAsync(
            "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo(@name) " +
            "WHERE hidden <> 1 ORDER BY cid",
            r => new ColumnInfo(
                r.GetInt32(0),
                r.GetString(1),
                r.GetString(2),
                r.GetInt64(3) != 0,
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5),
                r.GetInt64(6) is 2 or 3),
            nameParameter, cancellationToken).ConfigureAwait(false);

        var foreignKeys = new List<ForeignKeyInfo>();
        var indexes = new List<IndexInfo>();
        if (kind != TableKind.View)
        {
            var fkRows = await _session.QueryAsync(
                "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete " +
                "FROM pragma_foreign_key_list(@name) ORDER BY id, seq",
                r => (Id: r.GetInt32(0), Table: r.GetString(2), From: r.GetString(3),
                      To: r.IsDBNull(4) ? null : r.GetString(4), OnUpdate: r.GetString(5), OnDelete: r.GetString(6)),
                nameParameter, cancellationToken).ConfigureAwait(false);

            foreignKeys.AddRange(fkRows
                .GroupBy(f => f.Id)
                .Select(g => new ForeignKeyInfo(
                    g.Key,
                    g.First().Table,
                    g.Select(f => f.From).ToArray(),
                    g.Select(f => f.To).ToArray(),
                    g.First().OnUpdate,
                    g.First().OnDelete)));

            var indexRows = await _session.QueryAsync(
                "SELECT il.name, il.\"unique\", il.origin, il.partial, ii.seqno, ii.name " +
                "FROM pragma_index_list(@name) AS il LEFT JOIN pragma_index_info(il.name) AS ii " +
                "ORDER BY il.name COLLATE NOCASE, ii.seqno",
                r => (Name: r.GetString(0), Unique: r.GetInt64(1) != 0, Origin: r.GetString(2),
                      Partial: r.GetInt64(3) != 0, Column: r.IsDBNull(5) ? null : r.GetString(5)),
                nameParameter, cancellationToken).ConfigureAwait(false);

            indexes.AddRange(indexRows
                .GroupBy(i => i.Name)
                .Select(g => new IndexInfo(
                    g.Key,
                    g.First().Unique,
                    g.First().Origin,
                    g.First().Partial,
                    g.Select(i => i.Column).ToArray())));
        }

        return new TableDetails(
            tableName,
            kind,
            HasRowId: kind == TableKind.Table && !withoutRowId,
            IsStrict: strict,
            columns,
            foreignKeys,
            indexes);
    }

    /// <summary>Reads the table, view, column, and function names used for autocomplete.</summary>
    public async Task<SchemaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var objects = await _session.QueryAsync(
            "SELECT type, name FROM sqlite_schema WHERE type IN ('table', 'view') ORDER BY name COLLATE NOCASE",
            r => (Type: r.GetString(0), Name: r.GetString(1)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var columns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = await _session.QueryAsync(
                "SELECT m.name, p.name FROM sqlite_schema AS m JOIN pragma_table_info(m.name) AS p " +
                "WHERE m.type IN ('table', 'view') ORDER BY m.name, p.cid",
                r => (Table: r.GetString(0), Column: r.GetString(1)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase))
            {
                columns[group.Key] = group.Select(r => r.Column).ToArray();
            }
        }
        catch (SqliteException)
        {
            // A broken view (for example one that uses a dropped table) fails the combined query.
            // Read each object on its own and skip the ones that fail.
            foreach (var (_, name) in objects)
            {
                try
                {
                    columns[name] = await _session.QueryAsync(
                        "SELECT name FROM pragma_table_info(@name) ORDER BY cid",
                        r => r.GetString(0),
                        new Dictionary<string, object?> { ["@name"] = name },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException)
                {
                    columns[name] = [];
                }
            }
        }

        IReadOnlyList<string> functions;
        try
        {
            functions = await _session.QueryAsync(
                "SELECT DISTINCT name FROM pragma_function_list ORDER BY name",
                r => r.GetString(0),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            functions = SqlKeywords.BuiltInFunctions;
        }

        return new SchemaSnapshot(
            objects.Where(o => o.Type == "table").Select(o => o.Name).ToArray(),
            objects.Where(o => o.Type == "view").Select(o => o.Name).ToArray(),
            columns,
            functions);
    }

    private static SchemaObjectType ParseObjectType(string type) => type switch
    {
        "table" => SchemaObjectType.Table,
        "view" => SchemaObjectType.View,
        "index" => SchemaObjectType.Index,
        "trigger" => SchemaObjectType.Trigger,
        _ => throw new InvalidOperationException($"Unknown schema object type '{type}'."),
    };
}
