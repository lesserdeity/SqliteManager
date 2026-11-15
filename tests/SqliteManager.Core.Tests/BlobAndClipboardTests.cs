using SqliteManager.Core.Data;

namespace SqliteManager.Core.Tests;

public sealed class BlobAndClipboardTests : IDisposable
{
    private readonly string _folder = TestDatabase.NewFolder();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<byte[], BlobImageFormat> ImageHeaders => new()
    {
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0], BlobImageFormat.Png },
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 0], BlobImageFormat.Jpeg },
        { "GIF89a\0\0"u8.ToArray(), BlobImageFormat.Gif },
        { "GIF87a\0\0"u8.ToArray(), BlobImageFormat.Gif },
        { [0x42, 0x4D, .. new byte[20]], BlobImageFormat.Bmp },
        { [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10], BlobImageFormat.Ico },
        { "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), BlobImageFormat.WebP },
        { "RIFF\0\0\0\0WAVEfmt "u8.ToArray(), BlobImageFormat.None },
        { "BM"u8.ToArray(), BlobImageFormat.None }, // Too short to be a bitmap.
        { "hello"u8.ToArray(), BlobImageFormat.None },
        { [], BlobImageFormat.None },
    };

    [Theory]
    [MemberData(nameof(ImageHeaders))]
    public void DetectImage(byte[] data, BlobImageFormat expected)
    {
        Assert.Equal(expected, BlobService.DetectImage(data));
    }

    [Fact]
    public void Hex_lines_show_offset_bytes_and_ascii()
    {
        var data = "Hello, World! 0123456789"u8.ToArray();

        Assert.Equal(2, BlobService.HexLineCount(data.Length));
        Assert.Equal(0, BlobService.HexLineCount(0));
        Assert.Equal(1, BlobService.HexLineCount(16));
        Assert.Equal("00000000  48 65 6C 6C 6F 2C 20 57  6F 72 6C 64 21 20 30 31  |Hello, World! 01|", BlobService.FormatHexLine(data, 0));
        // Every line has the same width: missing bytes are padded so the ASCII column lines up.
        var full = BlobService.FormatHexLine(data, 0);
        var asciiColumn = full.IndexOf('|', StringComparison.Ordinal);
        var second = BlobService.FormatHexLine(data, 1);
        Assert.StartsWith("00000010  32 33 34 35 36 37 38 39 ", second);
        Assert.Equal(asciiColumn, second.IndexOf('|', StringComparison.Ordinal));
        Assert.EndsWith("|23456789|", second);

        var third = BlobService.FormatHexLine(new byte[] { 0, 0x7F, 0x41 }, 0);
        Assert.StartsWith("00000000  00 7F 41 ", third);
        Assert.Equal(asciiColumn, third.IndexOf('|', StringComparison.Ordinal));
        Assert.EndsWith("|..A|", third);
    }

    [Fact]
    public async Task File_round_trip()
    {
        var path = Path.Combine(_folder, "blob.bin");
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

        await BlobService.WriteFileAsync(path, data);
        Assert.Equal(data, await BlobService.ReadFileAsync(path));

        await BlobService.WriteFileAsync(path, [1, 2, 3]);
        Assert.Equal([1, 2, 3], await BlobService.ReadFileAsync(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Blob_from_file_round_trips_through_the_database()
    {
        var source = Path.Combine(_folder, "in.bin");
        var target = Path.Combine(_folder, "out.bin");
        var data = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray();
        await File.WriteAllBytesAsync(source, data);
        await using var db = await TestDatabase.CreateAsync("CREATE TABLE t (b BLOB)");

        await db.Session.ExecuteNonQueryAsync("INSERT INTO t VALUES (@b)", new Dictionary<string, object?> { ["@b"] = await BlobService.ReadFileAsync(source) });
        var stored = await db.Session.ExecuteScalarAsync<byte[]>("SELECT b FROM t");
        await BlobService.WriteFileAsync(target, stored!);

        Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }

    [Theory]
    [InlineData(BlobImageFormat.Png, ".png")]
    [InlineData(BlobImageFormat.Jpeg, ".jpg")]
    [InlineData(BlobImageFormat.None, ".bin")]
    public void Suggested_extension(BlobImageFormat format, string extension)
    {
        Assert.Equal(extension, BlobService.SuggestedExtension(format));
    }

    [Fact]
    public void Tsv_has_headers_and_formats_values()
    {
        var text = ClipboardFormatter.ToTsv(
            ["id", "name", "amount", "data"],
            [
                [1L, "Ann", 2.5, null],
                [2L, "", 1e21, new byte[] { 0xAB }],
            ]);

        Assert.Equal("id\tname\tamount\tdata\r\n1\tAnn\t2.5\t\r\n2\t\t1E+21\tAB\r\n", text);
    }

    [Fact]
    public void Tsv_quotes_fields_with_tabs_newlines_or_quotes()
    {
        var text = ClipboardFormatter.ToTsv(["a\tb"], [["line1\nline2"], ["say \"hi\""], ["plain"], ["cr\r"]]);

        Assert.Equal("\"a\tb\"\r\n\"line1\nline2\"\r\n\"say \"\"hi\"\"\"\r\nplain\r\n\"cr\r\"\r\n", text);
    }
}
