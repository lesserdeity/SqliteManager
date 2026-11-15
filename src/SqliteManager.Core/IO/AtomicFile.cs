using System.Text;

namespace SqliteManager.Core.IO;

/// <summary>Writes files so that a crash never leaves a half-written file: write a temp file, then replace.</summary>
public static class AtomicFile
{
    public static Task WriteAllTextAsync(string path, string text, Encoding encoding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        return WriteAsync(path, (temp, ct) => File.WriteAllTextAsync(temp, text, encoding, ct), cancellationToken);
    }

    public static Task WriteAllBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return WriteAsync(path, (temp, ct) => File.WriteAllBytesAsync(temp, data, ct), cancellationToken);
    }

    private static async Task WriteAsync(string path, Func<string, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await write(tempPath, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
