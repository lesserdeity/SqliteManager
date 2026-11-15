using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Design;

/// <summary>Writes SQL for table designs. All names are quoted.</summary>
public static class TableSqlGenerator
{
    /// <summary>CREATE TABLE for the design, optionally under another name (used by the rebuild procedure).</summary>
    public static string CreateTable(TableDefinition table, string? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columnLevelPk = UsesColumnLevelPrimaryKey(table);
        var lines = table.Columns.Select(c => ColumnSql(c, columnLevelPk && c.IsPrimaryKey, table)).ToList();

        var pk = table.PrimaryKeyColumns;
        if (pk.Count > 0 && !columnLevelPk)
        {
            var name = table.PrimaryKeyConstraintName is null ? string.Empty : $"CONSTRAINT {SqlIdentifier.Quote(table.PrimaryKeyConstraintName)} ";
            var terms = string.Join(", ", pk.Select(c => SqlIdentifier.Quote(c.Name) + (c.PrimaryKeyDescending ? " DESC" : string.Empty)));
            var conflict = table.PrimaryKeyConflict ?? pk[0].ConflictClauses.GetValueOrDefault(ColumnConstraintKind.PrimaryKey);
            lines.Add($"{name}PRIMARY KEY ({terms}){Prefix(conflict)}");
        }

        lines.AddRange(table.TableConstraints.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
        lines.AddRange(table.ForeignKeys.Select(ForeignKeySql));

        var options = new List<string>();
        if (table.WithoutRowId)
        {
            options.Add("WITHOUT ROWID");
        }

        if (table.Strict)
        {
            options.Add("STRICT");
        }

        return $"CREATE TABLE {SqlIdentifier.Quote(nameOverride ?? table.Name)} (\n    {string.Join(",\n    ", lines)}\n)"
            + (options.Count == 0 ? string.Empty : " " + string.Join(", ", options));
    }

    /// <summary>
    /// A single primary-key column is written on the column (required for AUTOINCREMENT);
    /// composite or named keys are written as a table constraint.
    /// </summary>
    public static bool UsesColumnLevelPrimaryKey(TableDefinition table)
    {
        var pk = table.PrimaryKeyColumns;
        return pk.Count == 1 && (table.PrimaryKeyConstraintName is null || pk[0].AutoIncrement);
    }

    /// <summary>The column definition: "name" TYPE constraints...</summary>
    public static string ColumnSql(ColumnDefinition column, bool includePrimaryKey, TableDefinition? table = null)
    {
        ArgumentNullException.ThrowIfNull(column);
        var parts = new List<string> { SqlIdentifier.Quote(column.Name) };
        if (!string.IsNullOrWhiteSpace(column.Type))
        {
            parts.Add(column.Type.Trim());
        }

        if (includePrimaryKey)
        {
            var name = column.ConstraintNames.GetValueOrDefault(ColumnConstraintKind.PrimaryKey) ?? table?.PrimaryKeyConstraintName;
            var conflict = column.ConflictClauses.GetValueOrDefault(ColumnConstraintKind.PrimaryKey) ?? table?.PrimaryKeyConflict;
            parts.Add(Named(name) + "PRIMARY KEY" + (column.PrimaryKeyDescending ? " DESC" : string.Empty) + Prefix(conflict)
                + (column.AutoIncrement ? " AUTOINCREMENT" : string.Empty));
        }

        if (column.NotNull)
        {
            parts.Add(Named(column, ColumnConstraintKind.NotNull) + "NOT NULL" + Prefix(column.ConflictClauses.GetValueOrDefault(ColumnConstraintKind.NotNull)));
        }

        if (column.Unique)
        {
            parts.Add(Named(column, ColumnConstraintKind.Unique) + "UNIQUE" + Prefix(column.ConflictClauses.GetValueOrDefault(ColumnConstraintKind.Unique)));
        }

        if (!string.IsNullOrWhiteSpace(column.Check))
        {
            parts.Add(Named(column, ColumnConstraintKind.Check) + $"CHECK ({column.Check.Trim()})");
        }

        if (!string.IsNullOrWhiteSpace(column.Default))
        {
            parts.Add(Named(column, ColumnConstraintKind.Default) + "DEFAULT " + column.Default.Trim());
        }

        if (!string.IsNullOrWhiteSpace(column.Collation))
        {
            parts.Add(Named(column, ColumnConstraintKind.Collate) + "COLLATE " + column.Collation.Trim());
        }

        if (!string.IsNullOrWhiteSpace(column.Extra))
        {
            parts.Add(column.Extra.Trim());
        }

        return string.Join(" ", parts);
    }

    public static string ForeignKeySql(ForeignKeyDefinition fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        var sql = Named(fk.ConstraintName)
            + $"FOREIGN KEY ({string.Join(", ", fk.Columns.Select(SqlIdentifier.Quote))}) REFERENCES {SqlIdentifier.Quote(fk.Table)}";
        if (fk.ReferencedColumns.Count > 0)
        {
            sql += $" ({string.Join(", ", fk.ReferencedColumns.Select(SqlIdentifier.Quote))})";
        }

        if (!string.IsNullOrWhiteSpace(fk.OnUpdate))
        {
            sql += " ON UPDATE " + fk.OnUpdate.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(fk.OnDelete))
        {
            sql += " ON DELETE " + fk.OnDelete.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(fk.Extra))
        {
            sql += " " + fk.Extra.Trim();
        }

        return sql;
    }

    public static string IndexTermSql(IndexTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (term.Column is null)
        {
            return term.Expression ?? string.Empty;
        }

        return SqlIdentifier.Quote(term.Column)
            + (term.Collation is null ? string.Empty : " COLLATE " + term.Collation)
            + (term.Descending ? " DESC" : string.Empty);
    }

    /// <summary>Index terms as shown in the designer, for example <c>a, b DESC</c>.</summary>
    public static string IndexTermsDisplay(IEnumerable<IndexTerm> terms) =>
        string.Join(", ", terms.Select(t => t.Column is null
            ? t.Expression
            : (NeedsQuotes(t.Column) ? SqlIdentifier.Quote(t.Column) : t.Column)
              + (t.Collation is null ? string.Empty : " COLLATE " + t.Collation)
              + (t.Descending ? " DESC" : string.Empty)));

    public static string CreateIndex(IndexDefinition index, string table)
    {
        ArgumentNullException.ThrowIfNull(index);
        var sql = $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {SqlIdentifier.Quote(index.Name)} ON {SqlIdentifier.Quote(table)} " +
                  $"({string.Join(", ", index.Terms.Select(IndexTermSql))})";
        return string.IsNullOrWhiteSpace(index.Where) ? sql : sql + " WHERE " + index.Where.Trim();
    }

    public static string Drop(string type, string name) => $"DROP {type.ToUpperInvariant()} {SqlIdentifier.Quote(name)}";

    private static bool NeedsQuotes(string name) =>
        name.Length == 0 || !SqlScriptSplitter.IsIdentifierStart(name[0]) || name.Any(ch => !SqlScriptSplitter.IsIdentifierPart(ch)) || SqlKeywords.IsKeyword(name);

    private static string Named(ColumnDefinition column, ColumnConstraintKind kind) => Named(column.ConstraintNames.GetValueOrDefault(kind));

    private static string Named(string? name) => name is null ? string.Empty : $"CONSTRAINT {SqlIdentifier.Quote(name)} ";

    private static string Prefix(string? clause) => string.IsNullOrWhiteSpace(clause) ? string.Empty : " " + clause.Trim();
}
