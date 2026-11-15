namespace SqliteManager.Core.Sql;

/// <summary>SQLite keywords and built-in functions.</summary>
public static class SqlKeywords
{
    /// <summary>The SQLite keyword list (https://sqlite.org/lang_keywords.html).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS", "ASC", "ATTACH",
        "AUTOINCREMENT", "BEFORE", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE", "CAST", "CHECK", "COLLATE",
        "COLUMN", "COMMIT", "CONFLICT", "CONSTRAINT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE",
        "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT", "DEFERRABLE", "DEFERRED", "DELETE", "DESC",
        "DETACH", "DISTINCT", "DO", "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXCLUSIVE",
        "EXISTS", "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL",
        "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN", "INDEX", "INDEXED",
        "INITIALLY", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST",
        "LEFT", "LIKE", "LIMIT", "MATCH", "MATERIALIZED", "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL",
        "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER", "OTHERS", "OUTER", "OVER", "PARTITION", "PLAN", "PRAGMA",
        "PRECEDING", "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP", "REINDEX",
        "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT",
        "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY", "THEN", "TIES", "TO", "TRANSACTION", "TRIGGER", "UNBOUNDED",
        "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "VIRTUAL", "WHEN", "WHERE", "WINDOW",
        "WITH", "WITHOUT",
    ];

    /// <summary>Type names and table options that are not keywords but are highlighted like them.</summary>
    public static readonly IReadOnlyList<string> TypeNames =
    [
        "INTEGER", "INT", "REAL", "TEXT", "BLOB", "NUMERIC", "BOOLEAN", "DATE", "DATETIME", "VARCHAR", "CHAR",
        "DOUBLE", "FLOAT", "DECIMAL", "ANY", "STRICT", "ROWID",
    ];

    /// <summary>Built-in functions used when the database cannot list its own functions.</summary>
    public static readonly IReadOnlyList<string> BuiltInFunctions =
    [
        "abs", "avg", "changes", "char", "coalesce", "count", "date", "datetime", "format", "glob", "group_concat",
        "hex", "ifnull", "iif", "instr", "json", "json_array", "json_extract", "json_object", "julianday",
        "last_insert_rowid", "length", "like", "lower", "ltrim", "max", "min", "nullif", "printf", "quote",
        "random", "randomblob", "replace", "round", "rtrim", "sign", "soundex", "strftime", "string_agg",
        "substr", "substring", "sum", "time", "total", "total_changes", "trim", "typeof", "unhex", "unicode",
        "unixepoch", "upper", "zeroblob",
    ];

    private static readonly HashSet<string> KeywordSet = new(All, StringComparer.OrdinalIgnoreCase);

    public static bool IsKeyword(string word) => KeywordSet.Contains(word);
}
