namespace SqliteManager.Core.Design;

public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>A problem with a design. Errors block Apply; warnings are shown.</summary>
public sealed record DesignIssue(IssueSeverity Severity, string Message);

/// <summary>Checks a table design before any SQL runs.</summary>
public static class DesignValidator
{
    private static readonly HashSet<string> StrictTypes = new(StringComparer.OrdinalIgnoreCase) { "INT", "INTEGER", "REAL", "TEXT", "BLOB", "ANY" };
    private static readonly HashSet<string> DefaultKeywords = new(StringComparer.OrdinalIgnoreCase) { "NULL", "TRUE", "FALSE", "CURRENT_TIME", "CURRENT_DATE", "CURRENT_TIMESTAMP" };

    /// <param name="design">The edited table.</param>
    /// <param name="original">The table as it is in the database, or null for a new table.</param>
    /// <param name="existingRowCount">Rows in the table now (0 for a new table).</param>
    /// <param name="otherTableNames">Names of other tables and views, to catch name clashes.</param>
    public static IReadOnlyList<DesignIssue> Validate(TableDefinition design, TableDefinition? original, long existingRowCount, IEnumerable<string> otherTableNames)
    {
        ArgumentNullException.ThrowIfNull(design);
        var issues = new List<DesignIssue>();
        void Error(string message) => issues.Add(new DesignIssue(IssueSeverity.Error, message));

        if (string.IsNullOrWhiteSpace(design.Name))
        {
            Error("Enter a table name.");
        }
        else if (design.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            Error("Table names starting with \"sqlite_\" are reserved.");
        }
        else if (otherTableNames.Any(n => n.Equals(design.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Error($"An object named \"{design.Name}\" already exists.");
        }

        if (design.Columns.Count == 0)
        {
            Error("Add at least one column.");
        }

        foreach (var column in design.Columns.Where(c => string.IsNullOrWhiteSpace(c.Name)))
        {
            Error($"Column {design.Columns.IndexOf(column) + 1} has no name.");
        }

        foreach (var duplicate in design.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Name)).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            Error($"Column name \"{duplicate.Key}\" is used more than once.");
        }

        var pk = design.PrimaryKeyColumns;
        foreach (var column in design.Columns.Where(c => c.AutoIncrement))
        {
            if (!column.IsPrimaryKey || pk.Count != 1 || !column.Type.Trim().Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                Error($"AUTOINCREMENT on \"{column.Name}\" needs it to be the only primary-key column, with type INTEGER.");
            }

            if (design.WithoutRowId)
            {
                Error("AUTOINCREMENT cannot be used in a WITHOUT ROWID table.");
            }
        }

        if (design.WithoutRowId && pk.Count == 0)
        {
            Error("A WITHOUT ROWID table needs a primary key.");
        }

        if (design.Strict)
        {
            foreach (var column in design.Columns)
            {
                var type = column.Type.Trim();
                if (!StrictTypes.Contains(type))
                {
                    Error($"In a STRICT table, \"{column.Name}\" must have type INT, INTEGER, REAL, TEXT, BLOB, or ANY.");
                }
            }
        }

        foreach (var column in design.Columns)
        {
            if (!string.IsNullOrWhiteSpace(column.Default) && !IsValidDefault(column.Default))
            {
                Error($"The default for \"{column.Name}\" must be a value such as 'text', 42, NULL, CURRENT_TIMESTAMP, or (expression). Put text in single quotes.");
            }

            CheckExpression(column.Check, $"The CHECK for \"{column.Name}\"", Error);
            CheckExpression(column.Extra, $"The other constraints of \"{column.Name}\"", Error);
            if (column.IsGenerated && column.IsPrimaryKey)
            {
                Error($"Generated column \"{column.Name}\" cannot be part of the primary key.");
            }
        }

        // A new NOT NULL column without a default cannot be filled for existing rows.
        if (existingRowCount > 0)
        {
            foreach (var column in design.Columns.Where(c => IsNewColumn(c, original) && c.NotNull && !c.IsGenerated && (string.IsNullOrWhiteSpace(c.Default) || c.Default.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase))))
            {
                Error($"New column \"{column.Name}\" is NOT NULL but has no default, so the {existingRowCount:N0} existing row(s) would have no value. Add a default.");
            }
        }

        var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in design.Indexes)
        {
            var label = string.IsNullOrWhiteSpace(index.Name) ? $"Index {design.Indexes.IndexOf(index) + 1}" : $"Index \"{index.Name}\"";
            if (string.IsNullOrWhiteSpace(index.Name))
            {
                Error($"{label} has no name.");
            }
            else if (!indexNames.Add(index.Name))
            {
                Error($"Index name \"{index.Name}\" is used more than once.");
            }
            else if (index.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                Error($"{label}: names starting with \"sqlite_\" are reserved.");
            }

            if (index.Terms.Count == 0)
            {
                Error($"{label} has no columns.");
            }

            foreach (var term in index.Terms.Where(t => t.Column is not null && design.FindColumn(t.Column) is null))
            {
                Error($"{label} uses unknown column \"{term.Column}\".");
            }

            CheckExpression(index.Where, $"The WHERE of {label}", Error);
        }

        foreach (var fk in design.ForeignKeys)
        {
            var label = $"Foreign key {design.ForeignKeys.IndexOf(fk) + 1}";
            if (fk.Columns.Count == 0)
            {
                Error($"{label} has no columns.");
            }

            if (string.IsNullOrWhiteSpace(fk.Table))
            {
                Error($"{label} has no referenced table.");
            }

            if (fk.ReferencedColumns.Count > 0 && fk.ReferencedColumns.Count != fk.Columns.Count)
            {
                Error($"{label} has {fk.Columns.Count} column(s) but references {fk.ReferencedColumns.Count}.");
            }

            foreach (var name in fk.Columns.Where(n => design.FindColumn(n) is null))
            {
                Error($"{label} uses unknown column \"{name}\".");
            }
        }

        foreach (var constraint in design.TableConstraints)
        {
            CheckExpression(constraint, "A table constraint", Error);
        }

        return issues;
    }

    /// <summary>True for a literal, signed number, NULL/TRUE/FALSE/CURRENT_*, or a parenthesized expression.</summary>
    public static bool IsValidDefault(string text)
    {
        List<SqlToken> tokens;
        try
        {
            tokens = SqlTokenizer.Tokenize(text);
        }
        catch (SqlParseException)
        {
            return false;
        }

        return tokens switch
        {
            [{ Kind: SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob }] => true,
            [{ Kind: SqlTokenKind.Word } word] => DefaultKeywords.Contains(word.Text),
            [{ Kind: SqlTokenKind.Symbol, Text: "+" or "-" }, { Kind: SqlTokenKind.Number }] => true,
            [{ Text: "(" }, .., { Text: ")" }] => IsBalanced(tokens) && ClosesAtEnd(tokens),
            _ => false,
        };
    }

    private static bool IsNewColumn(ColumnDefinition column, TableDefinition? original) =>
        original is not null && (column.OriginalName is null || original.FindColumn(column.OriginalName) is null);

    private static void CheckExpression(string? text, string what, Action<string> error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            if (!IsBalanced(SqlTokenizer.Tokenize(text)))
            {
                error($"{what} has unbalanced parentheses.");
            }
        }
        catch (SqlParseException ex)
        {
            error($"{what}: {ex.Message}");
        }
    }

    private static bool IsBalanced(List<SqlToken> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")") && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>True if the first '(' closes at the last token, so "(a) + (b)" is rejected.</summary>
    private static bool ClosesAtEnd(List<SqlToken> tokens)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[i].IsSymbol(")") && --depth == 0)
            {
                return i == tokens.Count - 1;
            }
        }

        return false;
    }
}
