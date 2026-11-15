using System.Globalization;
using System.Text;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.QueryDesign;

/// <summary>The SQL for a design, or the problems that stop it from being generated.</summary>
public sealed record GeneratedQuery(string? Sql, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Sql is not null;
}

/// <summary>Builds a SELECT statement from a <see cref="QueryDesignDocument"/>. Generation is one-way.</summary>
public static class QuerySqlGenerator
{
    public static GeneratedQuery Generate(QueryDesignDocument design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var errors = new List<string>();

        var fields = design.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Field)).ToList();
        if (design.Tables.Count == 0 && fields.Count == 0)
        {
            errors.Add("Add a table to the design.");
            return new GeneratedQuery(null, errors);
        }

        // Field expressions.
        var plain = new Dictionary<DesignField, string>();
        foreach (var (field, index) in fields.Select((f, i) => (f, i)))
        {
            if (field.Table is not null && design.FindTable(field.Table) is null)
            {
                errors.Add($"Field {index + 1} ({field.Field}): the table \"{field.Table}\" is not in the design.");
                continue;
            }

            plain[field] = FieldSql(design, field);
        }

        if (design.Limit is < 0)
        {
            errors.Add("Limit must be 0 or more.");
        }

        var selectItems = new List<string>();
        var groupBy = new List<string>();
        var orderBy = new List<(int? Order, int Position, string Sql)>();
        var whereRows = new List<List<string>>();
        var havingRows = new List<List<string>>();
        var rowCount = fields.Count == 0 ? 1 : fields.Max(f => f.Or.Count + 1);
        for (var r = 0; r < rowCount; r++)
        {
            whereRows.Add([]);
            havingRows.Add([]);
        }

        foreach (var (field, position) in fields.Select((f, i) => (f, i)))
        {
            if (!plain.TryGetValue(field, out var expression))
            {
                continue;
            }

            var total = design.Totals ? field.Total : TotalKind.Expression;
            string output; // What SELECT, ORDER BY, and the criteria use.
            var criteriaInHaving = false;
            if (!design.Totals)
            {
                output = expression;
            }
            else
            {
                switch (total)
                {
                    case TotalKind.GroupBy:
                        output = expression;
                        if (field.Field == "*")
                        {
                            errors.Add($"Field {position + 1}: cannot group by *.");
                        }

                        groupBy.Add(expression);
                        criteriaInHaving = true;
                        break;
                    case TotalKind.Where:
                        output = expression;
                        break;
                    case TotalKind.Expression:
                        output = expression;
                        criteriaInHaving = true;
                        break;
                    default:
                        if (field.Field == "*" && total != TotalKind.Count)
                        {
                            errors.Add($"Field {position + 1}: {total.ToString().ToUpperInvariant()} needs a column, not *.");
                        }

                        var argument = field.Field == "*" ? "*" : expression;
                        output = $"{total.ToString().ToUpperInvariant()}({argument})";
                        criteriaInHaving = true;
                        break;
                }
            }

            var shown = field.Show && !(design.Totals && total == TotalKind.Where);
            if (shown)
            {
                selectItems.Add(string.IsNullOrWhiteSpace(field.Alias) ? output : $"{output} AS {SqlIdentifier.Quote(field.Alias.Trim())}");
            }

            if (field.Sort != SortDirection.None)
            {
                if (design.Totals && total == TotalKind.Where)
                {
                    errors.Add($"Field {position + 1}: a Where field cannot be sorted.");
                }
                else if (field.Field == "*")
                {
                    errors.Add($"Field {position + 1}: cannot sort by *.");
                }
                else
                {
                    orderBy.Add((field.SortOrder, position, output + (field.Sort == SortDirection.Descending ? " DESC" : string.Empty)));
                }
            }

            for (var r = 0; r < rowCount; r++)
            {
                var text = field.CriteriaRow(r);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (field.Field == "*")
                {
                    errors.Add($"Field {position + 1}: * cannot have criteria.");
                    continue;
                }

                try
                {
                    var condition = CriteriaParser.ToCondition(output, text)!;
                    (criteriaInHaving ? havingRows : whereRows)[r].Add(condition);
                }
                catch (CriteriaException ex)
                {
                    errors.Add($"Field {position + 1} ({field.Field}), criteria \"{text!.Trim()}\": {ex.Message}");
                }
            }
        }

        if (selectItems.Count == 0)
        {
            errors.Add("Show at least one field (tick a column, or tick Show in the grid).");
        }

        var from = BuildFrom(design, errors);
        if (errors.Count > 0)
        {
            return new GeneratedQuery(null, errors);
        }

        var sql = new StringBuilder();
        sql.Append(design.Distinct ? "SELECT DISTINCT " : "SELECT ").Append(string.Join(", ", selectItems));
        if (from.Length > 0)
        {
            sql.Append('\n').Append(from);
        }

        AppendConditions(sql, "WHERE", whereRows);
        if (groupBy.Count > 0)
        {
            sql.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));
        }

        AppendConditions(sql, "HAVING", havingRows);
        if (orderBy.Count > 0)
        {
            var ordered = orderBy.OrderBy(o => o.Order ?? int.MaxValue).ThenBy(o => o.Position).Select(o => o.Sql);
            sql.Append("\nORDER BY ").Append(string.Join(", ", ordered));
        }

        if (design.Limit is { } limit)
        {
            sql.Append("\nLIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        }

        sql.Append(';');
        return new GeneratedQuery(sql.ToString(), errors);
    }

    /// <summary>The SQL for a field without its total: <c>"alias"."column"</c>, <c>"alias".*</c>, or the expression as typed.</summary>
    private static string FieldSql(QueryDesignDocument design, DesignField field)
    {
        var name = field.Field.Trim();
        if (field.Table is null)
        {
            return name;
        }

        var alias = SqlIdentifier.Quote(design.FindTable(field.Table)!.Alias);
        return name == "*" ? $"{alias}.*" : $"{alias}.{SqlIdentifier.Quote(name)}";
    }

    /// <summary>Criteria rows are ORed; the conditions inside a row are ANDed.</summary>
    private static void AppendConditions(StringBuilder sql, string keyword, List<List<string>> rows)
    {
        var used = rows.Where(r => r.Count > 0).ToList();
        if (used.Count == 0)
        {
            return;
        }

        string Row(List<string> row) => string.Join(" AND ", row);
        sql.Append('\n').Append(keyword).Append(' ');
        sql.Append(used.Count == 1
            ? Row(used[0])
            : string.Join("\n   OR ", used.Select(r => r.Count == 1 ? r[0] : $"({Row(r)})")));
    }

    /// <summary>
    /// FROM and JOIN clauses. Tables are added in design order, each one joined to the tables already added
    /// (all join lines between them go in one ON). Tables without a join line are cross joined.
    /// </summary>
    private static string BuildFrom(QueryDesignDocument design, List<string> errors)
    {
        if (design.Tables.Count == 0)
        {
            return string.Empty;
        }

        foreach (var join in design.Joins)
        {
            if (design.FindTable(join.LeftTable) is null || design.FindTable(join.RightTable) is null)
            {
                errors.Add($"A join uses a table that is not in the design ({join.LeftTable} – {join.RightTable}).");
            }
        }

        if (design.Tables.GroupBy(t => t.Alias, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            errors.Add("Two tables in the design have the same alias.");
            return string.Empty;
        }

        var included = new List<DesignTable> { design.Tables[0] };
        var remaining = design.Tables.Skip(1).ToList();
        var sql = new StringBuilder("FROM ").Append(TableSql(design.Tables[0]));
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(t => design.Joins.Any(j => j.Touches(t.Alias) && included.Any(i => j.Connects(t.Alias, i.Alias))))
                ?? remaining[0];
            remaining.Remove(next);
            var joins = design.Joins.Where(j => included.Any(i => j.Connects(next.Alias, i.Alias))).ToList();
            included.Add(next);

            if (joins.Count == 0 || joins.Any(j => j.Kind == JoinKind.Cross))
            {
                sql.Append("\nCROSS JOIN ").Append(TableSql(next));
                continue;
            }

            var first = joins[0];
            var keyword = first.Kind switch
            {
                JoinKind.Left when DesignJoin.Same(first.RightTable, next.Alias) => "LEFT JOIN",
                JoinKind.Left => "RIGHT JOIN", // All rows of the table being added are kept.
                _ => "INNER JOIN",
            };
            var conditions = joins.Select(j =>
                $"{SqlIdentifier.Quote(design.FindTable(j.LeftTable)!.Alias)}.{SqlIdentifier.Quote(j.LeftColumn)} = " +
                $"{SqlIdentifier.Quote(design.FindTable(j.RightTable)!.Alias)}.{SqlIdentifier.Quote(j.RightColumn)}");
            sql.Append('\n').Append(keyword).Append(' ').Append(TableSql(next)).Append(" ON ").Append(string.Join(" AND ", conditions));
        }

        return sql.ToString();
    }

    private static string TableSql(DesignTable table) =>
        string.Equals(table.Alias, table.Table, StringComparison.Ordinal)
            ? SqlIdentifier.Quote(table.Table)
            : $"{SqlIdentifier.Quote(table.Table)} AS {SqlIdentifier.Quote(table.Alias)}";
}
