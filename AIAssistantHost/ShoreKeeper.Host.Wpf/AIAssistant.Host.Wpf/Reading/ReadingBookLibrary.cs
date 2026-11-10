using System.IO;
using System.Security.Cryptography;

namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 书库目录里一本书的目录项。未解析时章数和段数为 null。
/// </summary>
public sealed class BookInfo
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string Format { get; init; } = "";

    public string SourcePath { get; init; } = "";

    public long SourceLength { get; init; }

    public int? ChapterCount { get; init; }

    public int? SegmentCount { get; init; }
}

/// <summary>
/// 枚举 txt/epub，按需解析并缓存。扫描只做文件哈希，不解析正文。
/// </summary>
public sealed class ReadingBookLibrary
{
    private readonly object _gate = new();
    private readonly List<BookInfo> _items = new();
    private readonly List<string> _extraPaths = new();
    private readonly Dictionary<string, Book> _books = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Segment>> _segments = new(StringComparer.Ordinal);

    public ReadingBookLibrary(string booksDirectory)
    {
        if (string.IsNullOrWhiteSpace(booksDirectory))
        {
            throw new ArgumentException("书库目录不能为空。", nameof(booksDirectory));
        }

        BooksDirectory = Path.GetFullPath(booksDirectory);
        Directory.CreateDirectory(BooksDirectory);
    }

    public string BooksDirectory { get; }

    public event Action<string>? Log;

    public IReadOnlyList<BookInfo> Scan()
    {
        var found = new List<BookInfo>();
        if (Directory.Exists(BooksDirectory))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(BooksDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteLog("[Reading] 无法枚举书库: " + ex.Message);
                files = Array.Empty<string>();
            }

            foreach (var path in files)
            {
                if (!IsBookExtension(path))
                {
                    continue;
                }

                try
                {
                    found.Add(Describe(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteLog("[Reading] 跳过 " + path + " " + ex.Message);
                }
            }
        }

        lock (_gate)
        {
            foreach (var extra in _extraPaths.ToArray())
            {
                if (!File.Exists(extra) || !IsBookExtension(extra))
                {
                    continue;
                }

                if (found.Any(item => PathsEqual(item.SourcePath, extra)))
                {
                    continue;
                }

                try
                {
                    found.Add(Describe(extra));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteLog("[Reading] 跳过 " + extra + " " + ex.Message);
                }
            }

            for (var i = 0; i < found.Count; i++)
            {
                if (_books.TryGetValue(found[i].Id, out var book))
                {
                    found[i] = WithParsed(found[i], book);
                }
            }

            found.Sort(static (left, right) =>
            {
                var title = string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
                return title != 0
                    ? title
                    : string.Compare(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase);
            });
            _items.Clear();
            _items.AddRange(found);
            return found;
        }
    }

    public BookInfo? Find(string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return null;
        }

        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (string.Equals(item.Id, bookId, StringComparison.Ordinal))
                {
                    return item;
                }
            }
        }

        return null;
    }

    public bool Register(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("文件路径不能为空。", nameof(filePath));
        }

        var full = Path.GetFullPath(filePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("找不到书籍文件。", full);
        }

        if (!IsBookExtension(full))
        {
            throw new NotSupportedException("不支持的书籍格式：" + Path.GetExtension(full));
        }

        var info = Describe(full);
        lock (_gate)
        {
            if (_extraPaths.Any(path => PathsEqual(path, full))
                || _items.Any(item => PathsEqual(item.SourcePath, full)))
            {
                return false;
            }

            _extraPaths.Add(full);
            if (_books.TryGetValue(info.Id, out var book))
            {
                info = WithParsed(info, book);
            }

            _items.Add(info);
            _items.Sort(static (left, right) =>
                string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase));
            return true;
        }
    }

    public async Task<Book> GetBookAsync(string bookId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            throw new ArgumentException("bookId 不能为空。", nameof(bookId));
        }

        BookInfo? info;
        lock (_gate)
        {
            if (_books.TryGetValue(bookId, out var cached))
            {
                return cached;
            }

            info = null;
            foreach (var item in _items)
            {
                if (string.Equals(item.Id, bookId, StringComparison.Ordinal))
                {
                    info = item;
                    break;
                }
            }
        }

        if (info is null)
        {
            // HTTP 服务可能先收到 /media 或 /api/progress，再收到书库列表；这里兜底扫一次目录。
            Scan();
            lock (_gate)
            {
                foreach (var item in _items)
                {
                    if (string.Equals(item.Id, bookId, StringComparison.Ordinal))
                    {
                        info = item;
                        break;
                    }
                }
            }
        }

        if (info is null)
        {
            throw new InvalidOperationException("书库里没有这本书，请先 Scan 或 Register。");
        }

        var book = await BookParser.ParseAsync(info.SourcePath, cancellationToken).ConfigureAwait(false);
        var segments = BookSegmenter.Segment(book);
        lock (_gate)
        {
            _books[book.Id] = book;
            _segments[book.Id] = segments;
            if (!string.Equals(book.Id, bookId, StringComparison.Ordinal))
            {
                _books[bookId] = book;
                _segments[bookId] = segments;
            }

            ReplaceParsed(info.SourcePath, book);
        }

        WriteLog("[Reading] 已解析 " + book.Title + " 章=" + book.Chapters.Count + " 段=" + book.SegmentCount);
        return book;
    }

    public IReadOnlyList<Segment> GetSegments(string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            throw new ArgumentException("bookId 不能为空。", nameof(bookId));
        }

        lock (_gate)
        {
            if (_segments.TryGetValue(bookId, out var segments))
            {
                return segments;
            }
        }

        throw new InvalidOperationException("这本书还没有解析。请先调用 GetBookAsync。");
    }

    private void ReplaceParsed(string sourcePath, Book book)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (PathsEqual(_items[i].SourcePath, sourcePath) || string.Equals(_items[i].Id, book.Id, StringComparison.Ordinal))
            {
                _items[i] = WithParsed(_items[i], book);
            }
        }
    }

    private static BookInfo WithParsed(BookInfo info, Book book)
    {
        return new BookInfo
        {
            Id = string.IsNullOrEmpty(book.Id) ? info.Id : book.Id,
            Title = string.IsNullOrWhiteSpace(book.Title) ? info.Title : book.Title,
            Format = string.IsNullOrWhiteSpace(book.Format) ? info.Format : book.Format,
            SourcePath = info.SourcePath,
            SourceLength = info.SourceLength,
            ChapterCount = book.Chapters.Count,
            SegmentCount = book.SegmentCount
        };
    }

    private static BookInfo Describe(string path)
    {
        var full = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(full);
        var extension = Path.GetExtension(full);
        return new BookInfo
        {
            Id = ComputeId(bytes),
            Title = Path.GetFileNameWithoutExtension(full),
            Format = extension.TrimStart('.').ToLowerInvariant(),
            SourcePath = full,
            SourceLength = bytes.LongLength
        };
    }

    private static string ComputeId(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 24);
    }

    private static bool IsBookExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".epub", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void WriteLog(string message) => Log?.Invoke(message);
}
