using System.Text;

namespace SqliteManager.Core.Transfer;

/// <summary>Encoding names used in import and export options.</summary>
public static class TextEncodings
{
    static TextEncodings()
    {
        // Windows code pages (windows-1252, and the ones .xls files use) are not built in on .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Names offered in the wizards. "utf-8" writes a byte order mark; "utf-8-nobom" does not.</summary>
    public static IReadOnlyList<string> Names { get; } = ["utf-8", "utf-8-nobom", "utf-16", "windows-1252"];

    /// <summary>Makes sure code-page encodings are available (for example before reading .xls files).</summary>
    public static void EnsureRegistered()
    {
        // Touching the class runs the static constructor.
    }

    /// <summary>The encoding for <paramref name="name"/>, or <paramref name="fallback"/> if the name is empty.</summary>
    public static Encoding Get(string? name, Encoding fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "utf-8" or "utf8" or "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-8-nobom" or "utf8-nobom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf-16" or "unicode" or "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "latin1" or "iso-8859-1" => Encoding.Latin1,
            var other => Encoding.GetEncoding(other),
        };
    }
}
