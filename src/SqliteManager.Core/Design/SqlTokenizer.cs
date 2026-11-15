namespace SqliteManager.Core.Design;

public enum SqlTokenKind
{
    /// <summary>A bare word: keyword or unquoted name.</summary>
    Word,

    /// <summary>A name in "double quotes", [brackets], or `backticks`.</summary>
    QuotedName,

    /// <summary>A 'string literal'.</summary>
    String,

    /// <summary>A x'blob literal'.</summary>
    Blob,

    Number,

    /// <summary>A parameter: ?, ?1, :name, @name, $name.</summary>
    Variable,

    /// <summary>Punctuation or an operator.</summary>
    Symbol,
}

/// <summary>A token with its position in the source text (<see cref="Start"/> inclusive, <see cref="End"/> exclusive).</summary>
public readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Start, int End)
{
    /// <summary>True for a bare word equal to <paramref name="keyword"/> (ignoring case).</summary>
    public bool Is(string keyword) => Kind == SqlTokenKind.Word && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string symbol) => Kind == SqlTokenKind.Symbol && Text == symbol;

    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.QuotedName;

    /// <summary>The name without quotes (for names), or the text without quotes (for strings).</summary>
    public string Value => Kind switch
    {
        SqlTokenKind.QuotedName when Text[0] == '[' => Text[1..^1],
        SqlTokenKind.QuotedName => Text[1..^1].Replace(new string(Text[0], 2), Text[0].ToString(), StringComparison.Ordinal),
        SqlTokenKind.String => Text[1..^1].Replace("''", "'", StringComparison.Ordinal),
        _ => Text,
    };
}

/// <summary>Raised when SQL cannot be read by the designer's parsers.</summary>
public sealed class SqlParseException(string message) : Exception(message);

/// <summary>Splits SQL into tokens, skipping whitespace and comments.</summary>
public static class SqlTokenizer
{
    private static readonly string[] TwoCharSymbols = ["||", "<=", ">=", "<>", "!=", "==", "<<", ">>", "->"];

    public static List<SqlToken> Tokenize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && Next(sql, i) == '-')
            {
                var nl = sql.IndexOf('\n', i);
                i = nl < 0 ? sql.Length : nl + 1;
                continue;
            }

            if (c == '/' && Next(sql, i) == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new SqlParseException("A /* comment is not closed.");
                }

                i = end + 2;
                continue;
            }

            SqlTokenKind kind;
            if (c is 'x' or 'X' && Next(sql, i) == '\'')
            {
                i = SkipQuoted(sql, i + 1, '\'');
                kind = SqlTokenKind.Blob;
            }
            else if (c == '\'')
            {
                i = SkipQuoted(sql, i, '\'');
                kind = SqlTokenKind.String;
            }
            else if (c is '"' or '`')
            {
                i = SkipQuoted(sql, i, c);
                kind = SqlTokenKind.QuotedName;
            }
            else if (c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                if (end < 0)
                {
                    throw new SqlParseException("A [name is not closed.");
                }

                i = end + 1;
                kind = SqlTokenKind.QuotedName;
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(Next(sql, i))))
            {
                i = SkipNumber(sql, i);
                kind = SqlTokenKind.Number;
            }
            else if (c is '?' or ':' or '@' or '$')
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                {
                    i++;
                }

                kind = SqlTokenKind.Variable;
            }
            else if (char.IsLetter(c) || c == '_' || c > 127)
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' || sql[i] > 127))
                {
                    i++;
                }

                kind = SqlTokenKind.Word;
            }
            else
            {
                var two = i + 1 < sql.Length ? sql.Substring(i, 2) : string.Empty;
                i += TwoCharSymbols.Contains(two) ? 2 : 1;
                kind = SqlTokenKind.Symbol;
            }

            tokens.Add(new SqlToken(kind, sql[start..i], start, i));
        }

        return tokens;
    }

    private static char Next(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';

    private static int SkipQuoted(string s, int i, char quote)
    {
        var j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == quote)
            {
                if (j + 1 < s.Length && s[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        throw new SqlParseException($"A {quote}quoted{quote} value is not closed.");
    }

    private static int SkipNumber(string s, int i)
    {
        if (s[i] == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'X')
        {
            i += 2;
            while (i < s.Length && Uri.IsHexDigit(s[i]))
            {
                i++;
            }

            return i;
        }

        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '_'))
        {
            i++;
        }

        if (i < s.Length && s[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < s.Length && s[j] is '+' or '-')
            {
                j++;
            }

            if (j < s.Length && char.IsDigit(s[j]))
            {
                i = j;
                while (i < s.Length && char.IsDigit(s[i]))
                {
                    i++;
                }
            }
        }

        return i;
    }
}

/// <summary>Reads tokens in order. Used by the designer's parsers.</summary>
internal sealed class TokenCursor(string source, List<SqlToken> tokens)
{
    public TokenCursor(string source)
        : this(source, SqlTokenizer.Tokenize(source))
    {
    }

    public string Source { get; } = source;

    public List<SqlToken> Tokens { get; } = tokens;

    public int Position { get; set; }

    public bool AtEnd => Position >= Tokens.Count || (Tokens[Position].IsSymbol(";") && Position == Tokens.Count - 1);

    public SqlToken Peek(int offset = 0) =>
        Position + offset < Tokens.Count ? Tokens[Position + offset] : new SqlToken(SqlTokenKind.Symbol, string.Empty, Source.Length, Source.Length);

    public SqlToken Next()
    {
        if (Position >= Tokens.Count)
        {
            throw new SqlParseException("The statement ended too early.");
        }

        return Tokens[Position++];
    }

    public bool Accept(string keyword)
    {
        if (Peek().Is(keyword))
        {
            Position++;
            return true;
        }

        return false;
    }

    public void Expect(string keyword)
    {
        if (!Accept(keyword))
        {
            throw Error($"Expected {keyword}");
        }
    }

    public bool AcceptSymbol(string symbol)
    {
        if (Peek().IsSymbol(symbol))
        {
            Position++;
            return true;
        }

        return false;
    }

    public void ExpectSymbol(string symbol)
    {
        if (!AcceptSymbol(symbol))
        {
            throw Error($"Expected '{symbol}'");
        }
    }

    /// <summary>Reads a name (bare, quoted, or a 'string' used as a name).</summary>
    public string ReadName()
    {
        var token = Peek();
        if (!token.IsName && token.Kind != SqlTokenKind.String)
        {
            throw Error("Expected a name");
        }

        Position++;
        return token.Value;
    }

    /// <summary>Reads [schema.]name and returns the name.</summary>
    public string ReadQualifiedName()
    {
        var name = ReadName();
        if (AcceptSymbol("."))
        {
            name = ReadName();
        }

        return name;
    }

    /// <summary>At '(': returns the text between the parentheses and moves past ')'.</summary>
    public string ReadParenthesized()
    {
        var open = Peek();
        ExpectSymbol("(");
        var close = SkipToClose();
        return Source[open.End..close.Start].Trim();
    }

    /// <summary>After '(' was read: moves past the matching ')' and returns it.</summary>
    public SqlToken SkipToClose()
    {
        var depth = 1;
        while (Position < Tokens.Count)
        {
            var token = Tokens[Position++];
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")") && --depth == 0)
            {
                return token;
            }
        }

        throw new SqlParseException("A '(' is not closed.");
    }

    /// <summary>Source text from token <paramref name="from"/> up to (not including) token <paramref name="to"/>.</summary>
    public string Raw(int from, int to) => from >= to ? string.Empty : Source[Tokens[from].Start..Tokens[to - 1].End];

    public SqlParseException Error(string message)
    {
        var near = Position < Tokens.Count ? $" near \"{Tokens[Position].Text}\"" : " at the end";
        return new SqlParseException(message + near + ".");
    }
}
