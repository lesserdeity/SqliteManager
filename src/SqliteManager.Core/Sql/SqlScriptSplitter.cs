namespace SqliteManager.Core.Sql;

/// <summary>One statement in a script. Offsets are character positions in the script.</summary>
/// <param name="Text">Statement text without the terminating semicolon.</param>
/// <param name="StartOffset">Position of the first character of the statement (comments before it are excluded).</param>
/// <param name="EndOffset">Position just after the terminating semicolon, or the end of the script.</param>
/// <param name="StartLine">1-based line of <paramref name="StartOffset"/>.</param>
public sealed record SqlStatement(string Text, int StartOffset, int EndOffset, int StartLine)
{
    /// <summary>The first keyword in upper case, such as SELECT or CREATE.</summary>
    public string FirstKeyword
    {
        get
        {
            var i = 0;
            while (i < Text.Length && (char.IsLetter(Text[i]) || Text[i] == '_'))
            {
                i++;
            }

            return Text[..i].ToUpperInvariant();
        }
    }

    /// <summary>True for CREATE, ALTER, DROP, ATTACH, and DETACH statements.</summary>
    public bool ChangesSchema => FirstKeyword is "CREATE" or "ALTER" or "DROP" or "ATTACH" or "DETACH";
}

/// <summary>
/// Splits SQL scripts into statements. Semicolons inside strings, quoted names, and comments are ignored.
/// Inside CREATE TRIGGER, only a semicolon after END ends the statement (the same rule as <c>sqlite3_complete</c>).
/// </summary>
public static class SqlScriptSplitter
{
    public static IReadOnlyList<SqlStatement> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var lines = new LineIndex(script);
        var statements = new List<SqlStatement>();

        var start = -1;
        var leadingTokens = new List<string>(6);
        var isTrigger = false;
        string? lastToken = null;

        void Reset()
        {
            start = -1;
            leadingTokens.Clear();
            isTrigger = false;
            lastToken = null;
        }

        void AddToken(string token)
        {
            lastToken = token;
            if (leadingTokens.Count < 6)
            {
                leadingTokens.Add(token);
                isTrigger = IsCreateTrigger(leadingTokens);
            }
        }

        var i = 0;
        var n = script.Length;
        while (i < n)
        {
            var c = script[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < n && script[i + 1] == '-')
            {
                i = SkipLineComment(script, i);
                continue;
            }

            if (c == '/' && i + 1 < n && script[i + 1] == '*')
            {
                i = SkipBlockComment(script, i);
                continue;
            }

            if (c == ';')
            {
                if (start >= 0 && (!isTrigger || lastToken == "END"))
                {
                    statements.Add(Create(script, lines, start, i, i + 1));
                    Reset();
                }
                else if (start >= 0)
                {
                    AddToken(";");
                }

                i++;
                continue;
            }

            if (start < 0)
            {
                start = i;
            }

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(script, i, c);
                AddToken("<quoted>");
            }
            else if (c == '[')
            {
                i = SkipQuoted(script, i, ']');
                AddToken("<quoted>");
            }
            else if (IsIdentifierStart(c))
            {
                var wordStart = i;
                while (i < n && IsIdentifierPart(script[i]))
                {
                    i++;
                }

                AddToken(script[wordStart..i].ToUpperInvariant());
            }
            else
            {
                AddToken(c.ToString());
                i++;
            }
        }

        if (start >= 0)
        {
            statements.Add(Create(script, lines, start, n, n));
        }

        return statements;
    }

    /// <summary>
    /// Returns the statement at <paramref name="caret"/>. A caret between statements selects the next one;
    /// a caret after the last statement selects the last one.
    /// </summary>
    public static SqlStatement? FindAt(string script, int caret)
    {
        var statements = Split(script);
        if (statements.Count == 0)
        {
            return null;
        }

        foreach (var statement in statements)
        {
            if (caret <= statement.EndOffset)
            {
                return statement;
            }
        }

        return statements[^1];
    }

    /// <summary>True if <paramref name="position"/> is inside a comment, string, or quoted name.</summary>
    public static bool IsInsideLiteralOrComment(string script, int position)
    {
        ArgumentNullException.ThrowIfNull(script);
        var i = 0;
        while (i < position && i < script.Length)
        {
            var c = script[i];
            int end;
            bool terminated;
            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                end = SkipLineComment(script, i);
                terminated = end < script.Length || script[^1] == '\n';
            }
            else if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                end = SkipBlockComment(script, i);
                terminated = script.IndexOf("*/", i + 2, StringComparison.Ordinal) >= 0;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                end = SkipQuoted(script, i, c == '[' ? ']' : c, out terminated);
            }
            else
            {
                i++;
                continue;
            }

            // The caret is inside when it is after the opening character and before the end,
            // or exactly at the end of an unterminated token.
            if (position < end || (position == end && !terminated))
            {
                return true;
            }

            i = end;
        }

        return false;
    }

    /// <summary>1-based line number of a position in <paramref name="text"/>.</summary>
    public static int LineOf(string text, int position)
    {
        var line = 1;
        var end = Math.Min(position, text.Length);
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    public static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c > 127;

    public static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c > 127;

    private static SqlStatement Create(string script, LineIndex lines, int start, int endExclusive, int endOffset)
    {
        var text = script[start..endExclusive].TrimEnd();
        return new SqlStatement(text, start, endOffset, lines.LineOf(start));
    }

    private static bool IsCreateTrigger(List<string> tokens)
    {
        var i = 0;
        if (i < tokens.Count && tokens[i] == "EXPLAIN")
        {
            i++;
            if (i + 1 < tokens.Count && tokens[i] == "QUERY" && tokens[i + 1] == "PLAN")
            {
                i += 2;
            }
        }

        if (i >= tokens.Count || tokens[i] != "CREATE")
        {
            return false;
        }

        i++;
        if (i < tokens.Count && tokens[i] is "TEMP" or "TEMPORARY")
        {
            i++;
        }

        return i < tokens.Count && tokens[i] == "TRIGGER";
    }

    private static int SkipLineComment(string s, int i)
    {
        var newline = s.IndexOf('\n', i + 2);
        return newline < 0 ? s.Length : newline + 1;
    }

    private static int SkipBlockComment(string s, int i)
    {
        var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + 2;
    }

    /// <summary>Skips a quoted token. Doubled closing quotes are escapes. Returns the position after the closing quote.</summary>
    private static int SkipQuoted(string s, int i, char close) => SkipQuoted(s, i, close, out _);

    private static int SkipQuoted(string s, int i, char close, out bool terminated)
    {
        var j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == close)
            {
                if (close != ']' && j + 1 < s.Length && s[j + 1] == close)
                {
                    j += 2;
                    continue;
                }

                terminated = true;
                return j + 1;
            }

            j++;
        }

        terminated = false;
        return s.Length;
    }

    private sealed class LineIndex
    {
        private readonly List<int> _lineStarts = [0];

        public LineIndex(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    _lineStarts.Add(i + 1);
                }
            }
        }

        public int LineOf(int position)
        {
            var index = _lineStarts.BinarySearch(position);
            return (index >= 0 ? index : ~index - 1) + 1;
        }
    }
}
