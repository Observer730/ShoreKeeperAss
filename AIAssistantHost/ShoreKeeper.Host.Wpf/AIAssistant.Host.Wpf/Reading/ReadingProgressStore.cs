using System.IO;
using System.Text;
using System.Text.Json;

namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 把断点写到 <c>&lt;root&gt;\&lt;bookId&gt;\progress.json</c>。损坏或缺失时读取返回 null。
/// </summary>
public sealed class ReadingProgressStore
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ReadingProgressStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("断点目录不能为空。", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string GetProgressPath(string bookId)
    {
        var id = RequireBookId(bookId);
        return Path.Combine(RootDirectory, id, "progress.json");
    }

    public ReadingProgress? Load(string bookId)
    {
        var path = GetProgressPath(bookId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Utf8NoBom);
            return JsonSerializer.Deserialize<ReadingProgress>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(ReadingProgress progress)
    {
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }

        var path = GetProgressPath(progress.BookId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(progress, JsonOptions);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, json, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string RequireBookId(string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            throw new ArgumentException("bookId 不能为空。", nameof(bookId));
        }

        var trimmed = bookId.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            throw new ArgumentException("bookId 含非法路径字符。", nameof(bookId));
        }

        return trimmed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
