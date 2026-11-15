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
            var qualifier = ReadQualifier(text, wordStart - 1);
            if (qualifier is null)
            {
                return CompletionResult.None(caret);
            }

            var statement = SqlScriptSplitter.FindAt(text, caret);
            var table = ResolveTable(qualifier, statement?.Text ?? string.Empty, schema);
            if (table is null || !schema.Columns.TryGetValue(table, out var columns))
            {
                return new CompletionResult(wordStart, prefix, []);
            }

            return new CompletionResult(wordStart, prefix, Filter(columns.Select(c => Item(c, CompletionKind.Column)), prefix));
        }

        if (prefix.Length == 0 && !explicitRequest)
        {
            return CompletionResult.None(caret);
        }

        var items = SqlKeywords.All.Select(k => new CompletionItem(k, k, CompletionKind.Keyword))
            .Concat(schema.Tables.Select(t => Item(t, CompletionKind.Table)))
            .Concat(schema.Views.Select(v => Item(v, CompletionKind.View)))
            .Concat(schema.Functions.Select(f => new CompletionItem(f, f, CompletionKind.Function)));

        return new CompletionResult(wordStart, prefix, Filter(items, prefix));
    }

    /// <summary>
    /// Maps aliases and table names in the FROM and JOIN clauses of <paramref name="statement"/>
    /// to table names. Keys ignore case.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FindTableAliases(string statement, SchemaSnapshot schema)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

                // schema.table: keep only the table part.
                if (j + 1 < tokens.Count && tokens[j].Symbol == '.' && tokens[j + 1].Name is { } tablePart)
                {
                    name = tablePart;
                    j += 2;
                }

                var table = schema.Tables.Concat(schema.Views).FirstOrDefault(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
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

    private static string? ResolveTable(string qualifier, string statement, SchemaSnapshot schema)
    {
        var aliases = FindTableAliases(statement, schema);
        if (aliases.TryGetValue(qualifier, out var table))
        {
            return table;
        }

        return schema.Columns.Keys.FirstOrDefault(t => t.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the identifier that ends just before the dot at <paramref name="dot"/>.</summary>
    private static string? ReadQualifier(string text, int dot)
    {
        var end = dot;
        if (end == 0)
        {
            return null;
        }

        var close = text[end - 1];
        if (close is '"' or '`' or ']')
        {
            var open = close == ']' ? '[' : close;
            var start = text.LastIndexOf(open, end - 2 < 0 ? 0 : end - 2);
            if (start < 0 || start == end - 1)
            {
                return null;
            }

            var inner = text[(start + 1)..(end - 1)];
            return close == ']' ? inner : inner.Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
        }

        var wordStart = end;
        while (wordStart > 0 && SqlScriptSplitter.IsIdentifierPart(text[wordStart - 1]))
        {
            wordStart--;
        }

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
