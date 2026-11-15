namespace SqliteManager.Core.Design;

/// <summary>
/// Reads a CREATE TABLE statement into a <see cref="TableDefinition"/>. Anything the model does not
/// describe in detail is kept as written. Statements it cannot read fully raise <see cref="SqlParseException"/>,
/// so a table is never changed from an incomplete reading.
/// </summary>
public static class CreateTableParser
{
    private static readonly HashSet<string> ColumnConstraintStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS",
    };

    private static readonly HashSet<string> TableConstraintStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN",
    };

    public static TableDefinition Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var c = new TokenCursor(sql);
        c.Expect("CREATE");
        if (!c.Accept("TEMP"))
        {
            c.Accept("TEMPORARY");
        }

        if (c.Peek().Is("VIRTUAL"))
        {
            throw new SqlParseException("Virtual tables cannot be changed in the table designer.");
        }

        c.Expect("TABLE");
        if (c.Accept("IF"))
        {
            c.Expect("NOT");
            c.Expect("EXISTS");
        }

        var table = new TableDefinition { Name = c.ReadQualifiedName() };
        if (c.Peek().Is("AS"))
        {
            throw c.Error("CREATE TABLE ... AS SELECT is not supported");
        }

        c.ExpectSymbol("(");
        var pkOrder = 0;
        var inTableConstraints = false;
        while (true)
        {
            if (TableConstraintStarts.Contains(c.Peek().Text) && c.Peek().Kind == SqlTokenKind.Word && IsTableConstraint(c))
            {
                inTableConstraints = true;
                ParseTableConstraint(c, table, ref pkOrder);
            }
            else if (inTableConstraints)
            {
                throw c.Error("Expected a table constraint");
            }
            else
            {
                table.Columns.Add(ParseColumn(c, table, ref pkOrder));
            }

            if (c.AcceptSymbol(","))
            {
                continue;
            }

            c.ExpectSymbol(")");
            break;
        }

        while (!c.AtEnd)
        {
            if (c.Accept("WITHOUT"))
            {
                c.Expect("ROWID");
                table.WithoutRowId = true;
            }
            else if (c.Accept("STRICT"))
            {
                table.Strict = true;
            }
            else if (!c.AcceptSymbol(","))
            {
                throw c.Error("Unexpected text after the column list");
            }
        }

        foreach (var column in table.Columns)
        {
            column.OriginalName = column.Name;
        }

        return table;
    }

    /// <summary>CONSTRAINT name followed by a table constraint, or a table constraint keyword (not a column named "check" etc.).</summary>
    private static bool IsTableConstraint(TokenCursor c)
    {
        var first = c.Peek();
        if (first.Is("CONSTRAINT"))
        {
            return true;
        }

        var second = c.Peek(1);
        return (first.Is("PRIMARY") && second.Is("KEY"))
            || (first.Is("FOREIGN") && second.Is("KEY"))
            || (first.Is("UNIQUE") && second.IsSymbol("("))
            || (first.Is("CHECK") && second.IsSymbol("("));
    }

    private static ColumnDefinition ParseColumn(TokenCursor c, TableDefinition table, ref int pkOrder)
    {
        var column = new ColumnDefinition { Name = c.ReadName() };

        var typeStart = c.Position;
        while (!c.AtEnd && !c.Peek().IsSymbol(",") && !c.Peek().IsSymbol(")") && !(c.Peek().Kind == SqlTokenKind.Word && ColumnConstraintStarts.Contains(c.Peek().Text)))
        {
            if (c.AcceptSymbol("("))
            {
                c.SkipToClose();
            }
            else
            {
                c.Next();
            }
        }

        column.Type = c.Raw(typeStart, c.Position);

        while (!c.Peek().IsSymbol(",") && !c.Peek().IsSymbol(")"))
        {
            var constraintStart = c.Position;
            string? name = null;
            if (c.Accept("CONSTRAINT"))
            {
                name = c.ReadName();
            }

            if (c.Accept("PRIMARY"))
            {
                c.Expect("KEY");
                if (c.Accept("DESC"))
                {
                    column.PrimaryKeyDescending = true;
                }
                else
                {
                    c.Accept("ASC");
                }

                Remember(column, ColumnConstraintKind.PrimaryKey, name, ReadConflict(c));
                column.AutoIncrement = c.Accept("AUTOINCREMENT");
                column.PrimaryKeyOrder = ++pkOrder;
            }
            else if (c.Accept("NOT"))
            {
                c.Expect("NULL");
                column.NotNull = true;
                Remember(column, ColumnConstraintKind.NotNull, name, ReadConflict(c));
            }
            else if (c.Accept("NULL"))
            {
                ReadConflict(c); // "NULL" allows NULL, which is the default.
            }
            else if (c.Accept("UNIQUE"))
            {
                column.Unique = true;
                Remember(column, ColumnConstraintKind.Unique, name, ReadConflict(c));
            }
            else if (c.Accept("CHECK"))
            {
                column.Check = c.ReadParenthesized();
                Remember(column, ColumnConstraintKind.Check, name, null);
            }
            else if (c.Accept("DEFAULT"))
            {
                column.Default = ReadDefault(c);
                Remember(column, ColumnConstraintKind.Default, name, null);
            }
            else if (c.Accept("COLLATE"))
            {
                column.Collation = c.ReadName();
                Remember(column, ColumnConstraintKind.Collate, name, null);
            }
            else if (c.Accept("REFERENCES"))
            {
                var fk = ParseForeignKeyClause(c);
                fk.Columns = [column.Name];
                fk.ConstraintName = name;
                table.ForeignKeys.Add(fk);
            }
            else if (c.Peek().Is("GENERATED") || c.Peek().Is("AS"))
            {
                if (c.Accept("GENERATED"))
                {
                    c.Expect("ALWAYS");
                }

                c.Expect("AS");
                c.ReadParenthesized();
                if (!c.Accept("STORED"))
                {
                    c.Accept("VIRTUAL");
                }

                var raw = c.Raw(constraintStart, c.Position);
                column.Extra = string.IsNullOrEmpty(column.Extra) ? raw : column.Extra + " " + raw;
            }
            else
            {
                throw c.Error($"Unsupported constraint on column \"{column.Name}\"");
            }
        }

        return column;
    }

    private static void ParseTableConstraint(TokenCursor c, TableDefinition table, ref int pkOrder)
    {
        var start = c.Position;
        string? name = null;
        if (c.Accept("CONSTRAINT"))
        {
            name = c.ReadName();
        }

        if (c.Accept("PRIMARY"))
        {
            c.Expect("KEY");
            c.ExpectSymbol("(");
            do
            {
                var columnName = c.ReadName();
                var column = table.FindColumn(columnName) ?? throw c.Error($"PRIMARY KEY names unknown column \"{columnName}\"");
                if (c.Peek().Is("COLLATE"))
                {
                    throw c.Error("COLLATE inside PRIMARY KEY (...) is not supported");
                }

                if (c.Accept("DESC"))
                {
                    column.PrimaryKeyDescending = true;
                }
                else
                {
                    c.Accept("ASC");
                }

                if (c.Accept("AUTOINCREMENT"))
                {
                    column.AutoIncrement = true;
                }

                column.PrimaryKeyOrder = ++pkOrder;
            }
            while (c.AcceptSymbol(","));

            c.ExpectSymbol(")");
            table.PrimaryKeyConstraintName = name;
            table.PrimaryKeyConflict = ReadConflict(c);
        }
        else if (c.Accept("UNIQUE"))
        {
            c.ExpectSymbol("(");
            c.SkipToClose();
            ReadConflict(c);
            table.TableConstraints.Add(c.Raw(start, c.Position));
        }
        else if (c.Accept("CHECK"))
        {
            c.ReadParenthesized();
            table.TableConstraints.Add(c.Raw(start, c.Position));
        }
        else if (c.Accept("FOREIGN"))
        {
            c.Expect("KEY");
            var columns = ReadNameList(c);
            c.Expect("REFERENCES");
            var fk = ParseForeignKeyClause(c);
            fk.Columns = columns;
            fk.ConstraintName = name;
            table.ForeignKeys.Add(fk);
        }
        else
        {
            throw c.Error("Unsupported table constraint");
        }
    }

    /// <summary>After REFERENCES: table [(columns)] [ON DELETE|UPDATE action] [MATCH name] [[NOT] DEFERRABLE ...].</summary>
    private static ForeignKeyDefinition ParseForeignKeyClause(TokenCursor c)
    {
        var fk = new ForeignKeyDefinition { Table = c.ReadName() };
        if (c.Peek().IsSymbol("("))
        {
            fk.ReferencedColumns = ReadNameList(c);
        }

        var extra = new List<string>();
        while (true)
        {
            if (c.Peek().Is("ON") && (c.Peek(1).Is("DELETE") || c.Peek(1).Is("UPDATE")))
            {
                c.Next();
                var isDelete = c.Next().Is("DELETE");
                var actionStart = c.Position;
                if (c.Accept("SET"))
                {
                    if (!c.Accept("NULL"))
                    {
                        c.Expect("DEFAULT");
                    }
                }
                else if (c.Accept("NO"))
                {
                    c.Expect("ACTION");
                }
                else if (!c.Accept("CASCADE") && !c.Accept("RESTRICT"))
                {
                    throw c.Error("Unknown foreign key action");
                }

                var action = c.Raw(actionStart, c.Position).ToUpperInvariant();
                if (isDelete)
                {
                    fk.OnDelete = action;
                }
                else
                {
                    fk.OnUpdate = action;
                }
            }
            else if (c.Peek().Is("MATCH"))
            {
                var s = c.Position;
                c.Next();
                c.ReadName();
                extra.Add(c.Raw(s, c.Position));
            }
            else if (c.Peek().Is("DEFERRABLE") || (c.Peek().Is("NOT") && c.Peek(1).Is("DEFERRABLE")))
            {
                var s = c.Position;
                c.Accept("NOT");
                c.Expect("DEFERRABLE");
                if (c.Accept("INITIALLY"))
                {
                    if (!c.Accept("DEFERRED"))
                    {
                        c.Expect("IMMEDIATE");
                    }
                }

                extra.Add(c.Raw(s, c.Position));
            }
            else
            {
                break;
            }
        }

        fk.Extra = extra.Count == 0 ? null : string.Join(" ", extra);
        return fk;
    }

    private static List<string> ReadNameList(TokenCursor c)
    {
        var names = new List<string>();
        c.ExpectSymbol("(");
        do
        {
            names.Add(c.ReadName());
        }
        while (c.AcceptSymbol(","));

        c.ExpectSymbol(")");
        return names;
    }

    private static string? ReadConflict(TokenCursor c)
    {
        if (c.Peek().Is("ON") && c.Peek(1).Is("CONFLICT"))
        {
            var start = c.Position;
            c.Next();
            c.Next();
            c.Next();
            return c.Raw(start, c.Position);
        }

        return null;
    }

    private static string ReadDefault(TokenCursor c)
    {
        var start = c.Position;
        if (c.AcceptSymbol("("))
        {
            c.SkipToClose();
        }
        else if (c.Peek().IsSymbol("+") || c.Peek().IsSymbol("-"))
        {
            c.Next();
            if (c.Peek().Kind != SqlTokenKind.Number)
            {
                throw c.Error("Expected a number after the sign in DEFAULT");
            }

            c.Next();
        }
        else
        {
            var token = c.Next();
            if (token.Kind == SqlTokenKind.Symbol)
            {
                throw c.Error("Expected a DEFAULT value");
            }
        }

        return c.Raw(start, c.Position);
    }

    private static void Remember(ColumnDefinition column, ColumnConstraintKind kind, string? name, string? conflict)
    {
        if (name is not null)
        {
            column.ConstraintNames[kind] = name;
        }

        if (conflict is not null)
        {
            column.ConflictClauses[kind] = conflict;
        }
    }
}

/// <summary>Reads CREATE INDEX statements.</summary>
public static class IndexParser
{
    public static IndexDefinition Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var c = new TokenCursor(sql);
        c.Expect("CREATE");
        var index = new IndexDefinition { Unique = c.Accept("UNIQUE") };
        c.Expect("INDEX");
        if (c.Accept("IF"))
        {
            c.Expect("NOT");
            c.Expect("EXISTS");
        }

        index.Name = c.ReadQualifiedName();
        index.OriginalName = index.Name;
        c.Expect("ON");
        c.ReadName();
        c.ExpectSymbol("(");
        index.Terms = ParseTerms(c, closing: true);
        if (c.Accept("WHERE"))
        {
            var start = c.Position;
            while (!c.AtEnd)
            {
                c.Next();
            }

            index.Where = c.Raw(start, c.Position);
        }

        return index;
    }

    /// <summary>Parses index terms such as <c>a, "b" DESC, lower(c)</c> typed in the designer.</summary>
    public static List<IndexTerm> ParseTermList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var c = new TokenCursor(text);
        return c.AtEnd ? [] : ParseTerms(c, closing: false);
    }

    private static List<IndexTerm> ParseTerms(TokenCursor c, bool closing)
    {
        var terms = new List<IndexTerm>();
        while (true)
        {
            var start = c.Position;
            var depth = 0;
            while (c.Position < c.Tokens.Count)
            {
                var token = c.Peek();
                if (depth == 0 && (token.IsSymbol(",") || (closing && token.IsSymbol(")"))))
                {
                    break;
                }

                if (token.IsSymbol("("))
                {
                    depth++;
                }
                else if (token.IsSymbol(")"))
                {
                    depth--;
                }

                c.Next();
            }

            if (start == c.Position)
            {
                throw c.Error("Expected an index column");
            }

            terms.Add(ToTerm(c, start, c.Position));
            if (c.AcceptSymbol(","))
            {
                continue;
            }

            if (closing)
            {
                c.ExpectSymbol(")");
            }

            return terms;
        }
    }

    /// <summary>name [COLLATE x] [ASC|DESC] becomes a column term; anything else is kept as an expression.</summary>
    private static IndexTerm ToTerm(TokenCursor c, int from, int to)
    {
        var t = c.Tokens.GetRange(from, to - from);
        var descending = false;
        string? collation = null;
        var end = t.Count;
        if (end > 1 && (t[end - 1].Is("ASC") || t[end - 1].Is("DESC")))
        {
            descending = t[end - 1].Is("DESC");
            end--;
        }

        if (end == 3 && t[1].Is("COLLATE") && t[2].IsName)
        {
            collation = t[2].Value;
            end = 1;
        }

        if (end == 1 && t[0].IsName)
        {
            return new IndexTerm(t[0].Value, null, collation, descending);
        }

        return new IndexTerm(null, c.Raw(from, to));
    }
}

/// <summary>A view as edited in the view editor.</summary>
public sealed class ViewDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional column names: CREATE VIEW v (a, b) AS ...</summary>
    public List<string> ColumnNames { get; set; } = [];

    public string Select { get; set; } = string.Empty;

    public static ViewDefinition Parse(string sql)
    {
        var c = new TokenCursor(sql);
        c.Expect("CREATE");
        if (!c.Accept("TEMP"))
        {
            c.Accept("TEMPORARY");
        }

        c.Expect("VIEW");
        if (c.Accept("IF"))
        {
            c.Expect("NOT");
            c.Expect("EXISTS");
        }

        var view = new ViewDefinition { Name = c.ReadQualifiedName() };
        if (c.AcceptSymbol("("))
        {
            do
            {
                view.ColumnNames.Add(c.ReadName());
            }
            while (c.AcceptSymbol(","));

            c.ExpectSymbol(")");
        }

        var asToken = c.Peek();
        c.Expect("AS");
        view.Select = sql[asToken.End..].Trim().TrimEnd(';').TrimEnd();
        return view;
    }

    public string ToSql()
    {
        var columns = ColumnNames.Count == 0 ? string.Empty : " (" + string.Join(", ", ColumnNames.Select(Sql.SqlIdentifier.Quote)) + ")";
        return $"CREATE VIEW {Sql.SqlIdentifier.Quote(Name)}{columns} AS\n{Select.Trim().TrimEnd(';').TrimEnd()}";
    }
}

public enum TriggerTiming
{
    /// <summary>No timing keyword (SQLite treats it as BEFORE).</summary>
    Unspecified,
    Before,
    After,
    InsteadOf,
}

public enum TriggerEvent
{
    Delete,
    Insert,
    Update,
}

/// <summary>A trigger as edited in the trigger editor.</summary>
public sealed class TriggerDefinition
{
    public string Name { get; set; } = string.Empty;

    public bool Temporary { get; set; }

    public TriggerTiming Timing { get; set; } = TriggerTiming.After;

    public TriggerEvent Event { get; set; } = TriggerEvent.Insert;

    /// <summary>Columns of UPDATE OF col, ... (only for UPDATE triggers).</summary>
    public List<string> UpdateColumns { get; set; } = [];

    public string Table { get; set; } = string.Empty;

    public bool ForEachRow { get; set; } = true;

    /// <summary>The WHEN expression, or null.</summary>
    public string? When { get; set; }

    /// <summary>Statements between BEGIN and END.</summary>
    public string Body { get; set; } = string.Empty;

    public static TriggerDefinition Parse(string sql)
    {
        var c = new TokenCursor(sql);
        c.Expect("CREATE");
        var trigger = new TriggerDefinition { Temporary = c.Accept("TEMP") || c.Accept("TEMPORARY") };
        c.Expect("TRIGGER");
        if (c.Accept("IF"))
        {
            c.Expect("NOT");
            c.Expect("EXISTS");
        }

        trigger.Name = c.ReadQualifiedName();
        trigger.Timing = c.Accept("BEFORE") ? TriggerTiming.Before
            : c.Accept("AFTER") ? TriggerTiming.After
            : c.Accept("INSTEAD") ? ExpectOf(c)
            : TriggerTiming.Unspecified;

        if (c.Accept("DELETE"))
        {
            trigger.Event = TriggerEvent.Delete;
        }
        else if (c.Accept("INSERT"))
        {
            trigger.Event = TriggerEvent.Insert;
        }
        else
        {
            c.Expect("UPDATE");
            trigger.Event = TriggerEvent.Update;
            if (c.Accept("OF"))
            {
                do
                {
                    trigger.UpdateColumns.Add(c.ReadName());
                }
                while (c.AcceptSymbol(","));
            }
        }

        c.Expect("ON");
        trigger.Table = c.ReadQualifiedName();
        trigger.ForEachRow = false;
        if (c.Accept("FOR"))
        {
            c.Expect("EACH");
            c.Expect("ROW");
            trigger.ForEachRow = true;
        }

        if (c.Accept("WHEN"))
        {
            var start = c.Position;
            while (!c.Peek().Is("BEGIN"))
            {
                if (c.AcceptSymbol("("))
                {
                    c.SkipToClose();
                }
                else
                {
                    c.Next();
                }
            }

            trigger.When = c.Raw(start, c.Position);
        }

        var begin = c.Peek();
        c.Expect("BEGIN");
        var end = c.Tokens.FindLastIndex(t => t.Is("END"));
        if (end < c.Position)
        {
            throw c.Error("Expected END");
        }

        trigger.Body = SqlRewriter.Dedent(sql[begin.End..c.Tokens[end].Start].Trim());
        return trigger;
    }

    private static TriggerTiming ExpectOf(TokenCursor c)
    {
        c.Expect("OF");
        return TriggerTiming.InsteadOf;
    }

    public string ToSql()
    {
        var timing = Timing switch
        {
            TriggerTiming.Before => " BEFORE",
            TriggerTiming.After => " AFTER",
            TriggerTiming.InsteadOf => " INSTEAD OF",
            _ => string.Empty,
        };
        var ev = Event switch
        {
            TriggerEvent.Delete => "DELETE",
            TriggerEvent.Insert => "INSERT",
            _ => UpdateColumns.Count == 0 ? "UPDATE" : "UPDATE OF " + string.Join(", ", UpdateColumns.Select(Sql.SqlIdentifier.Quote)),
        };
        var body = Body.Trim();
        if (body.Length > 0 && !body.EndsWith(';'))
        {
            body += ";";
        }

        var when = string.IsNullOrWhiteSpace(When) ? string.Empty : $"\nWHEN {When.Trim()}";
        var temp = Temporary ? "TEMP " : string.Empty;
        return $"CREATE {temp}TRIGGER {Sql.SqlIdentifier.Quote(Name)}{timing} {ev} ON {Sql.SqlIdentifier.Quote(Table)}" +
               $"{(ForEachRow ? " FOR EACH ROW" : string.Empty)}{when}\nBEGIN\n    {body.ReplaceLineEndings("\n    ")}\nEND";
    }
}
