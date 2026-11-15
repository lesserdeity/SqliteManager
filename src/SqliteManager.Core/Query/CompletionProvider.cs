using SqliteManager.Core.Schema;
using SqliteManager.Core.Sql;

namespace SqliteManager.Core.Query;

public enum CompletionKind
{
    Keyword,
    Table,
    View,
    Column,
    Function,

    /// <summary>The schema name of an attached database.</summary>
    Schema,
}

/// <summary>One suggestion. <see cref="InsertText"/> is quoted when the name needs it.</summary>
public sealed record CompletionItem(string Text, string InsertText, CompletionKind Kind);

/// <summary>Suggestions for the word that starts at <see cref="WordStart"/> and ends at the caret.</summary>
public sealed record CompletionResult(int WordStart, string Prefix, IReadOnlyList<CompletionItem> Items)
{
    public static CompletionResult None(int caret) => new(caret, string.Empty, []);
}

/// <summary>Builds autocomplete suggestions from the live schema.</summary>
public static class CompletionProvider
{
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "USING", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER", "GROUP",
        "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "SET", "VALUES", "AS", "INDEXED", "NOT",
        "RETURNING",
    };

    /// <param name="text">The whole editor text.</param>
    /// <param name="caret">Caret position.</param>
    /// <param name="schema">Names from the database.</param>
    /// <param name="explicitRequest">True for Ctrl+Space: suggest even when nothing has been typed.</param>
    public static CompletionResult Complete(string text, int caret, SchemaSnapshot schema, bool explicitRequest = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        caret = Math.Clamp(caret, 0, text.Length);

        var wordStart = caret;
        while (wordStart > 0 && SqlScriptSplitter.IsIdentifierPart(text[wordStart - 1]))
        {
            wordStart--;
        }

        if (SqlScriptSplitter.IsInsideLiteralOrComment(text, wordStart))
        {
            return CompletionResult.None(caret);
        }

        var prefix = text[wordStart..caret];
        if (wordStart > 0 && text[wordStart - 1] == '.')
        {
            var qualifier = ReadQualifier(text, wordStart - 1, out var qualifierStart);
            if (qualifier is null)
            {
                return CompletionResult.None(caret);
            }

            // schema.table.| : columns of that table in that schema.
            var outer = qualifierStart > 0 && text[qualifierStart - 1] == '.' ? ReadQualifier(text, qualifierStart - 1, out _) : null;
            if (outer is not null)
            {
                var qualified = FindTable(new TableRef(outer, qualifier), schema);
                return new CompletionResult(wordStart, prefix, qualified is null ? [] : ColumnItems(qualified.Value, schema, prefix));
            }

            var statement = SqlScriptSplitter.FindAt(text, caret);
            var aliases = FindTableReferences(statement?.Text ?? string.Empty, schema);
            // An alias wins only when it names a known table: in "FROM aux.|" the half-typed source is not one.
            if (aliases.TryGetValue(qualifier, out var aliased) && FindTable(aliased, schema) is not null)
            {
                return new CompletionResult(wordStart, prefix, ColumnItems(aliased, schema, prefix));
            }

            // schema.| : tables and views of main or of an attached database.
            if (SchemaTables(qualifier, schema) is { } tables)
            {
                return new CompletionResult(wordStart, prefix, Filter(tables, prefix));
            }

            var table = FindTable(new TableRef(null, qualifier), schema);
            return new CompletionResult(wordStart, prefix, table is null ? [] : ColumnItems(table.Value, schema, prefix));
        }

        if (prefix.Length == 0 && !explicitRequest)
        {
            return CompletionResult.None(caret);
        }

        var items = SqlKeywords.All.Select(k => new CompletionItem(k, k, CompletionKind.Keyword))
            .Concat(schema.Tables.Select(t => Item(t, CompletionKind.Table)))
            .Concat(schema.Views.Select(v => Item(v, CompletionKind.View)))
            .Concat(schema.Functions.Select(f => new CompletionItem(f, f, CompletionKind.Function)))
            .Concat(schema.AttachedSchemas.Select(a => Item(a.Name, CompletionKind.Schema)));

        return new CompletionResult(wordStart, prefix, Filter(items, prefix));
    }

    /// <summary>
    /// Maps aliases and table names in the FROM and JOIN clauses of <paramref name="statement"/>
    /// to table names. Keys ignore case.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FindTableAliases(string statement, SchemaSnapshot schema) =>
        FindTableReferences(statement, schema).ToDictionary(p => p.Key, p => p.Value.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A table or view. <see cref="Schema"/> is null for main, otherwise an attached schema name.</summary>
    private readonly record struct TableRef(string? Schema, string Name);

    /// <summary>Like <see cref="FindTableAliases"/>, but keeps the schema of <c>schema.table</c> sources.</summary>
    private static Dictionary<string, TableRef> FindTableReferences(string statement, SchemaSnapshot schema)
    {
        var aliases = new Dictionary<string, TableRef>(StringComparer.OrdinalIgnoreCase);
        var tokens = Tokenize(statement);
        for (var i = 0; i < tokens.Count; i++)
        {
            var isSource = tokens[i].Word is { } w && (w.Equals("FROM", StringComparison.OrdinalIgnoreCase) || w.Equals("JOIN", StringComparison.OrdinalIgnoreCase) || w.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) || w.Equals("INTO", StringComparison.OrdinalIgnoreCase));
            if (!isSource)
            {
                continue;
            }

            // FROM a, b x, c AS y: read comma-separated sources until a keyword or other punctuation.
            var j = i + 1;
            while (j < tokens.Count)
            {
                if (tokens[j].Name is not { } name)
                {
                    break;
                }

                j++;

                string? schemaName = null;
                if (j + 1 < tokens.Count && tokens[j].Symbol == '.' && tokens[j + 1].Name is { } tablePart)
                {
                    schemaName = name;
                    name = tablePart;
                    j += 2;
                }

                var table = FindTable(new TableRef(schemaName, name), schema) ?? new TableRef(schemaName, name);
                aliases.TryAdd(name, table);

                if (j < tokens.Count && tokens[j].Word is { } asWord && asWord.Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    j++;
                }

                if (j < tokens.Count && tokens[j].Name is { } alias && !(tokens[j].Word is { } aw && NotAliases.Contains(aw)))
                {
                    aliases[alias] = table;
                    j++;
                }

                if (j < tokens.Count && tokens[j].Symbol == ',')
                {
                    j++;
                    continue;
                }

                break;
            }
        }

        return aliases;
    }

    /// <summary>
    /// Finds a table or view with its real spelling. Without a schema, main is searched first and then
    /// the attached databases in attach order, the same order SQLite uses. Returns null when not found.
    /// </summary>
    private static TableRef? FindTable(TableRef reference, SchemaSnapshot schema)
    {
        static string? Match(IEnumerable<string> names, string name) =>
            names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

        var inMain = reference.Schema is null || reference.Schema.Equals("main", StringComparison.OrdinalIgnoreCase);
        if (inMain && Match(schema.Columns.Keys.Concat(schema.Tables).Concat(schema.Views), reference.Name) is { } mainName)
        {
            return new TableRef(null, mainName);
        }

        IEnumerable<AttachedSchema> candidates = reference.Schema is null
            ? schema.AttachedSchemas
            : schema.FindAttached(reference.Schema) is { } one ? [one] : [];
        foreach (var attached in candidates)
        {
            if (Match(attached.Columns.Keys.Concat(attached.Tables).Concat(attached.Views), reference.Name) is { } attachedName)
            {
                return new TableRef(attached.Name, attachedName);
            }
        }

        return null;
    }

    private static List<CompletionItem> ColumnItems(TableRef table, SchemaSnapshot schema, string prefix)
    {
        var columnsByTable = table.Schema is null ? schema.Columns : schema.FindAttached(table.Schema)?.Columns;
        return columnsByTable is not null && columnsByTable.TryGetValue(table.Name, out var columns)
            ? Filter(columns.Select(c => Item(c, CompletionKind.Column)), prefix)
            : [];
    }

    /// <summary>Tables and views of <c>main</c> or of an attached database; null when the name is neither.</summary>
    private static IEnumerable<CompletionItem>? SchemaTables(string schemaName, SchemaSnapshot schema)
    {
        if (schemaName.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return schema.Tables.Select(t => Item(t, CompletionKind.Table)).Concat(schema.Views.Select(v => Item(v, CompletionKind.View)));
        }

        return schema.FindAttached(schemaName) is { } attached
            ? attached.Tables.Select(t => Item(t, CompletionKind.Table)).Concat(attached.Views.Select(v => Item(v, CompletionKind.View)))
            : null;
    }

    /// <summary>
    /// Reads the identifier that ends just before the dot at <paramref name="dot"/>.
    /// <paramref name="start"/> receives where it starts (its opening quote, if quoted).
    /// </summary>
    private static string? ReadQualifier(string text, int dot, out int start)
    {
        var end = dot;
        start = dot;
        if (end == 0)
        {
            return null;
        }

        var close = text[end - 1];
        if (close is '"' or '`' or ']')
        {
            var open = close == ']' ? '[' : close;
            var openAt = text.LastIndexOf(open, end - 2 < 0 ? 0 : end - 2);
            if (openAt < 0 || openAt == end - 1)
            {
                return null;
            }

            start = openAt;
            var inner = text[(openAt + 1)..(end - 1)];
            return close == ']' ? inner : inner.Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
        }

        var wordStart = end;
        while (wordStart > 0 && SqlScriptSplitter.IsIdentifierPart(text[wordStart - 1]))
        {
            wordStart--;
        }

        start = wordStart;
        return wordStart == end ? null : text[wordStart..end];
    }

    private static CompletionItem Item(string name, CompletionKind kind) => new(name, NeedsQuoting(name) ? SqlIdentifier.Quote(name) : name, kind);

    private static bool NeedsQuoting(string name) =>
        name.Length == 0
        || !SqlScriptSplitter.IsIdentifierStart(name[0])
        || name.Any(c => !SqlScriptSplitter.IsIdentifierPart(c))
        || SqlKeywords.IsKeyword(name);

    private static List<CompletionItem> Filter(IEnumerable<CompletionItem> items, string prefix) =>
        items.Where(i => i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(i => (i.Text.ToUpperInvariant(), i.Kind))
            .OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Kind)
            .ToList();

    private readonly record struct Token(string? Word, string? Name, char Symbol);

    /// <summary>Splits SQL into names (plain or quoted), and single-character symbols. Strings and comments are skipped.</summary>
    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i);
                i = nl < 0 ? sql.Length : nl + 1;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c == '\'')
            {
                i = SkipQuoted(sql, i, '\'', out _);
                tokens.Add(new Token(null, null, '\''));
            }
            else if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                i = SkipQuoted(sql, i, close, out var inner);
                tokens.Add(new Token(null, inner, '\0'));
            }
            else if (SqlScriptSplitter.IsIdentifierStart(c))
            {
                var start = i;
                while (i < sql.Length && SqlScriptSplitter.IsIdentifierPart(sql[i]))
                {
                    i++;
                }

                var word = sql[start..i];
                tokens.Add(new Token(word, SqlKeywords.IsKeyword(word) ? null : word, '\0'));
            }
            else
            {
                tokens.Add(new Token(null, null, c));
                i++;
            }
        }

        return tokens;
    }

    private static int SkipQuoted(string s, int i, char close, out string inner)
    {
        var builder = new System.Text.StringBuilder();
        var j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == close)
            {
                if (close != ']' && j + 1 < s.Length && s[j + 1] == close)
                {
                    builder.Append(close);
                    j += 2;
                    continue;
                }

                inner = builder.ToString();
                return j + 1;
            }

            builder.Append(s[j]);
            j++;
        }

        inner = builder.ToString();
        return s.Length;
    }
}
