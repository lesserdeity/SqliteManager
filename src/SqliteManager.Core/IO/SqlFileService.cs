using System.Text;

namespace SqliteManager.Core.IO;

/// <summary>Text of a .sql file and the encoding to save it back with.</summary>
public sealed record SqlFileContent(string Text, Encoding Encoding);

/// <summary>Reads and writes .sql files.</summary>
public static class SqlFileService
{
    /// <summary>UTF-8 without a byte order mark: the encoding for new files.</summary>
    public static Encoding DefaultEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a file. A byte order mark selects the encoding; otherwise the file is read as UTF-8.</summary>
    public static async Task<SqlFileContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        return new SqlFileContent(encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength), encoding);
    }

    /// <summary>Writes a file atomically. Line endings are written exactly as they are in <paramref name="text"/>.</summary>
    public static Task WriteAsync(string path, string text, Encoding? encoding = null, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(path, text, encoding ?? DefaultEncoding, cancellationToken);

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        preambleLength = 0;
        return DefaultEncoding;
    }
}
