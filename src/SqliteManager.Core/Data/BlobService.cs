using System.Text;
using SqliteManager.Core.IO;

namespace SqliteManager.Core.Data;

public enum BlobImageFormat
{
    None,
    Png,
    Jpeg,
    Gif,
    Bmp,
    Ico,
    WebP,
}

/// <summary>Helpers for viewing BLOBs and moving them to and from files.</summary>
public static class BlobService
{
    /// <summary>SQLite's default maximum string or BLOB size (SQLITE_MAX_LENGTH).</summary>
    public const long MaxBlobSize = 1_000_000_000;

    public const int BytesPerHexLine = 16;

    /// <summary>Detects an image format from the first bytes.</summary>
    public static BlobImageFormat DetectImage(ReadOnlySpan<byte> data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => BlobImageFormat.Png,
        [0xFF, 0xD8, 0xFF, ..] => BlobImageFormat.Jpeg,
        [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => BlobImageFormat.Gif,
        [0x42, 0x4D, _, _, _, _, _, _, _, _, _, _, _, _, ..] => BlobImageFormat.Bmp,
        [0x00, 0x00, 0x01, 0x00, _, _, ..] => BlobImageFormat.Ico,
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => BlobImageFormat.WebP,
        _ => BlobImageFormat.None,
    };

    /// <summary>File extension (with dot) for an image format, or ".bin".</summary>
    public static string SuggestedExtension(BlobImageFormat format) => format switch
    {
        BlobImageFormat.Png => ".png",
        BlobImageFormat.Jpeg => ".jpg",
        BlobImageFormat.Gif => ".gif",
        BlobImageFormat.Bmp => ".bmp",
        BlobImageFormat.Ico => ".ico",
        BlobImageFormat.WebP => ".webp",
        _ => ".bin",
    };

    public static int HexLineCount(long length) => (int)((length + BytesPerHexLine - 1) / BytesPerHexLine);

    /// <summary>
    /// One hex dump line: offset, 16 hex bytes, and the printable ASCII characters
    /// ("00000010  48 65 6C 6C 6F ...  |Hello...|").
    /// </summary>
    public static string FormatHexLine(ReadOnlySpan<byte> data, int line)
    {
        var start = line * BytesPerHexLine;
        var count = Math.Min(BytesPerHexLine, data.Length - start);
        var builder = new StringBuilder(80);
        builder.Append(start.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < BytesPerHexLine; i++)
        {
            builder.Append(i < count ? data[start + i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : "  ");
            builder.Append(i == 7 ? "  " : " ");
        }

        builder.Append(" |");
        for (var i = 0; i < count; i++)
        {
            var b = data[start + i];
            builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        builder.Append('|');
        return builder.ToString();
    }

    /// <summary>Reads a file to store as a BLOB. Fails if the file is larger than SQLite allows.</summary>
    public static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var length = new FileInfo(path).Length;
        if (length > MaxBlobSize)
        {
            throw new IOException($"The file is {length:N0} bytes. SQLite BLOBs are limited to {MaxBlobSize:N0} bytes.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a BLOB to a file (atomic write). The caller confirms before replacing an existing file.</summary>
    public static Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllBytesAsync(path, data, cancellationToken);
}
