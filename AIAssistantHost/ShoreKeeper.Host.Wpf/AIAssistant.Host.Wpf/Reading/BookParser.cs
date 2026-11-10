#if WINDOWS
using System.IO;
#endif
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 把 txt / epub 解析成章节纯文本。不依赖 WPF 或音频库。
/// </summary>
public static class BookParser
{
    private const int MaxTitleTail = 40;
    private const int MaxTitleLength = 80;

    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf8Loose = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Gb18030Strict;

    private static readonly HashSet<string> SkipElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "noscript", "template", "svg", "math"
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "section", "article", "header", "footer", "ul", "ol",
        "table", "hr", "pre", "dt", "dd", "figcaption", "figure", "address"
    };

    private static readonly Regex FallbackDropBlocks = new(
        @"<(script|style|head|noscript|svg|math)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FallbackBlockTags = new(
        @"</?(p|div|br|li|tr|h[1-6]|blockquote|section|article|ul|ol|table|hr|pre|dt|dd)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FallbackAnyTag = new("<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex FallbackHeading = new(
        @"<h([1-3])\b[^>]*>(.*?)</h\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FallbackHtmlTitle = new(
        @"<title\b[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    static BookParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030Strict = Encoding.GetEncoding(
            "GB18030",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    /// <summary>
    /// 解析 txt 或 epub。同一文件的 Id 稳定不变。
    /// </summary>
    public static Book Parse(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("文件路径不能为空。", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到书籍文件。", filePath);
        }

        var extension = Path.GetExtension(filePath);
        var bytes = File.ReadAllBytes(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return ParseTxt(filePath, bytes, cancellationToken);
        }

        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            return ParseEpub(filePath, bytes, cancellationToken);
        }

        throw new NotSupportedException("不支持的书籍格式：" + extension);
    }

    /// <summary>
    /// 在后台线程解析，避免卡住调用方。
    /// </summary>
    public static Task<Book> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Parse(filePath, cancellationToken), cancellationToken);
    }

    private static Book ParseTxt(string filePath, byte[] bytes, CancellationToken cancellationToken)
    {
        var text = NormalizeNewlines(DecodeTxt(bytes));
        var chapters = SplitTxtChapters(text, cancellationToken);
        return Finish(filePath, "txt", bytes, null, chapters);
    }

    private static string DecodeTxt(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Utf8Loose.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (ArgumentException)
        {
        }

        try
        {
            return Gb18030Strict.GetString(bytes);
        }
        catch (ArgumentException)
        {
        }

        return Utf8Loose.GetString(bytes);
    }

    private static List<Chapter> SplitTxtChapters(string text, CancellationToken cancellationToken)
    {
        var marks = new List<TitleHit>();
        var offset = 0;
        var lineIndex = 0;
        while (offset < text.Length)
        {
            if ((lineIndex++ & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var newline = text.IndexOf('\n', offset);
            var contentEnd = newline < 0 ? text.Length : newline;
            var next = newline < 0 ? text.Length : newline + 1;
            var line = TrimSpan(text.AsSpan(offset, contentEnd - offset));
            if (IsTitleLine(line))
            {
                marks.Add(new TitleHit(offset, next, MakeTitle(line)));
            }

            offset = next;
        }

        var drafts = new List<ChapterDraft>();
        if (marks.Count == 0)
        {
            var body = text.Trim();
            if (body.Length > 0)
            {
                drafts.Add(new ChapterDraft { Title = "正文", ExplicitTitle = true, Text = body });
            }

            return Materialize(drafts);
        }

        var preface = text.Substring(0, marks[0].LineStart).Trim();
        if (preface.Length > 0)
        {
            drafts.Add(new ChapterDraft { Title = "前言", ExplicitTitle = true, Text = preface });
        }

        for (var i = 0; i < marks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bodyStart = marks[i].LineNext;
            var bodyEnd = i + 1 < marks.Count ? marks[i + 1].LineStart : text.Length;
            var body = text.Substring(bodyStart, bodyEnd - bodyStart).Trim();
            if (body.Length == 0)
            {
                continue;
            }

            drafts.Add(new ChapterDraft { Title = marks[i].Title, ExplicitTitle = true, Text = body });
        }

        return Materialize(drafts);
    }

    private static bool IsTitleLine(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return false;
        }

        return IsEnglishChapter(line) || IsChineseChapter(line);
    }

    private static bool IsEnglishChapter(ReadOnlySpan<char> line)
    {
        const string word = "chapter";
        if (line.Length < word.Length + 2)
        {
            return false;
        }

        if (!line.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var i = word.Length;
        if (!char.IsWhiteSpace(line[i]))
        {
            return false;
        }

        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        var digits = 0;
        while (i < line.Length && line[i] is >= '0' and <= '9')
        {
            digits++;
            i++;
        }

        return digits > 0 && line.Length - i <= MaxTitleTail;
    }

    private static bool IsChineseChapter(ReadOnlySpan<char> line)
    {
        if (line.Length < 2 || line[0] != '第')
        {
            return false;
        }

        var i = 1;
        var numbers = 0;
        while (i < line.Length && IsChapterNumber(line[i]))
        {
            numbers++;
            i++;
        }

        if (numbers == 0 || i >= line.Length || !IsChapterSuffix(line[i]))
        {
            return false;
        }

        i++;
        return line.Length - i <= MaxTitleTail;
    }

    private static bool IsChapterNumber(char c) =>
        c is '一' or '二' or '三' or '四' or '五' or '六' or '七' or '八' or '九' or '十'
            or '百' or '千' or '万' or '零' or '〇' or '两'
            or >= '0' and <= '9';

    private static bool IsChapterSuffix(char c) =>
        c is '章' or '节' or '回' or '卷' or '部' or '篇';

    private static string MakeTitle(ReadOnlySpan<char> line)
    {
        if (line.Length <= MaxTitleLength)
        {
            return line.ToString();
        }

        return line.Slice(0, MaxTitleLength).ToString();
    }

    private static Book ParseEpub(string filePath, byte[] bytes, CancellationToken cancellationToken)
    {
        using var zip = OpenZip(bytes, filePath);
        var entries = IndexEntries(zip);
        if (!entries.TryGetValue("META-INF/container.xml", out var containerEntry))
        {
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml：" + filePath);
        }

        XDocument container;
        try
        {
            container = LoadXml(containerEntry);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException("EPUB 的 container.xml 无法解析：" + filePath + "。" + ex.Message, ex);
        }

        var opfPath = FindOpfPath(container) ?? "OEBPS/content.opf";
        if (!entries.TryGetValue(NormalizeZipPath(opfPath), out var opfEntry))
        {
            throw new InvalidDataException("EPUB 找不到 OPF（" + opfPath + "）：" + filePath);
        }

        var normalizedOpf = NormalizeZipPath(opfPath);
        XDocument opf;
        try
        {
            opf = LoadXml(opfEntry);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException("EPUB 的 OPF 无法解析：" + filePath + "。" + ex.Message, ex);
        }

        var bookTitle = ReadOpfTitle(opf);
        var chapters = ReadSpine(entries, opf, normalizedOpf, cancellationToken);
        return Finish(filePath, "epub", bytes, bookTitle, chapters);
    }

    private static ZipArchive OpenZip(byte[] bytes, string filePath)
    {
        try
        {
            return new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("EPUB 损坏或无法读取：" + filePath + "。" + ex.Message, ex);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            map[NormalizeZipPath(entry.FullName)] = entry;
        }

        return map;
    }

    private static string? FindOpfPath(XDocument container)
    {
        foreach (var element in container.Descendants())
        {
            var fullPath = element.Attribute("full-path")?.Value;
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath.Trim();
            }
        }

        return null;
    }

    private static string? ReadOpfTitle(XDocument opf)
    {
        foreach (var element in opf.Descendants())
        {
            if (!element.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = element.Parent?.Name.LocalName;
            if (parent is not null && !parent.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = CollapseInline(element.Value);
            if (title.Length > 0)
            {
                return title;
            }
        }

        return null;
    }

    private static List<Chapter> ReadSpine(
        Dictionary<string, ZipArchiveEntry> entries,
        XDocument opf,
        string opfPath,
        CancellationToken cancellationToken)
    {
        var manifest = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var element in opf.Descendants())
        {
            if (!element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (element.Parent is null || !element.Parent.Name.LocalName.Equals("manifest", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = element.Attribute("id")?.Value;
            var href = element.Attribute("href")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href))
            {
                continue;
            }

            manifest[id] = new ManifestItem(href, element.Attribute("media-type")?.Value ?? "");
        }

        var drafts = new List<ChapterDraft>();
        foreach (var element in opf.Descendants())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!element.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (element.Parent is null || !element.Parent.Name.LocalName.Equals("spine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var idref = element.Attribute("idref")?.Value;
            if (string.IsNullOrEmpty(idref) || !manifest.TryGetValue(idref, out var item))
            {
                continue;
            }

            if (!IsHtmlMedia(item.MediaType))
            {
                continue;
            }

            var href = ResolveZipPath(opfPath, item.Href);
            if (!entries.TryGetValue(href, out var entry))
            {
                continue;
            }

            var draft = ReadHtmlChapter(entry);
            if (string.IsNullOrWhiteSpace(draft.Text))
            {
                continue;
            }

            drafts.Add(draft);
        }

        return Materialize(drafts);
    }

    private static ChapterDraft ReadHtmlChapter(ZipArchiveEntry entry)
    {
        try
        {
            var document = LoadXml(entry);
            var root = document.Root;
            if (root is null)
            {
                return new ChapterDraft();
            }

            var heading = ReadHeading(root);
            var htmlTitle = ReadHtmlTitle(root);
            var builder = new StringBuilder();
            Walk(root, builder, pre: false);
            return new ChapterDraft
            {
                Title = heading ?? htmlTitle ?? "",
                ExplicitTitle = heading is not null || htmlTitle is not null,
                Text = FinalizeText(builder.ToString())
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or InvalidDataException)
        {
            return ReadHtmlFallback(ReadEntryText(entry));
        }
    }

    private static ChapterDraft ReadHtmlFallback(string raw)
    {
        var heading = MatchPlainText(FallbackHeading, raw, group: 2);
        var htmlTitle = MatchPlainText(FallbackHtmlTitle, raw, group: 1);
        var withoutBlocks = FallbackDropBlocks.Replace(raw, "\n");
        var withBreaks = FallbackBlockTags.Replace(withoutBlocks, "\n");
        var stripped = FallbackAnyTag.Replace(withBreaks, string.Empty);
        return new ChapterDraft
        {
            Title = heading ?? htmlTitle ?? "",
            ExplicitTitle = heading is not null || htmlTitle is not null,
            Text = FinalizeText(stripped)
        };
    }

    private static string? MatchPlainText(Regex regex, string raw, int group)
    {
        var match = regex.Match(raw);
        if (!match.Success)
        {
            return null;
        }

        var text = CollapseInline(FallbackAnyTag.Replace(match.Groups[group].Value, string.Empty));
        return text.Length == 0 ? null : Truncate(text, MaxTitleLength);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Utf8Loose, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return LoadXml(stream);
    }

    private static XDocument LoadXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string? ReadHeading(XElement root)
    {
        foreach (var element in root.Descendants())
        {
            var name = element.Name.LocalName;
            if (name is not ("h1" or "h2" or "h3"))
            {
                continue;
            }

            if (HasSkippedAncestor(element))
            {
                continue;
            }

            var text = CollapseInline(ExtractInline(element));
            if (text.Length > 0)
            {
                return Truncate(text, MaxTitleLength);
            }
        }

        return null;
    }

    private static string? ReadHtmlTitle(XElement root)
    {
        foreach (var element in root.Descendants())
        {
            if (!element.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = CollapseInline(element.Value);
            if (text.Length > 0)
            {
                return Truncate(text, MaxTitleLength);
            }
        }

        return null;
    }

    private static bool HasSkippedAncestor(XElement element)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (SkipElements.Contains(parent.Name.LocalName))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractInline(XElement element)
    {
        var builder = new StringBuilder();
        AppendInline(element, builder);
        return builder.ToString();
    }

    private static void AppendInline(XElement element, StringBuilder builder)
    {
        if (SkipElements.Contains(element.Name.LocalName))
        {
            return;
        }

        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                AppendText(text.Value, builder, preserveWhitespace: false);
            }
            else if (node is XElement child)
            {
                var name = child.Name.LocalName;
                if (name.Equals("br", StringComparison.OrdinalIgnoreCase) || name.Equals("hr", StringComparison.OrdinalIgnoreCase))
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }
                }
                else
                {
                    AppendInline(child, builder);
                }
            }
        }
    }

    private static void Walk(XElement element, StringBuilder builder, bool pre)
    {
        var name = element.Name.LocalName;
        if (SkipElements.Contains(name))
        {
            return;
        }

        if (name.Equals("br", StringComparison.OrdinalIgnoreCase) || name.Equals("hr", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append('\n');
            return;
        }

        var block = BlockElements.Contains(name);
        var nowPre = pre || name.Equals("pre", StringComparison.OrdinalIgnoreCase);
        if (block)
        {
            builder.Append('\n');
        }

        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                AppendText(text.Value, builder, nowPre);
            }
            else if (node is XElement child)
            {
                Walk(child, builder, nowPre);
            }
        }

        if (block)
        {
            builder.Append('\n');
        }
    }

    private static void AppendText(string value, StringBuilder builder, bool preserveWhitespace)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (preserveWhitespace)
        {
            builder.Append(value);
            return;
        }

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length == 0)
                {
                    continue;
                }

                var last = builder[builder.Length - 1];
                if (last == '\n' || last == ' ')
                {
                    continue;
                }

                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
            }
        }
    }

    private static string FinalizeText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var decoded = NormalizeNewlines(WebUtility.HtmlDecode(raw));
        var builder = new StringBuilder(decoded.Length);
        var newlines = 0;
        for (var i = 0; i < decoded.Length; i++)
        {
            var c = decoded[i];
            if (c == '\n')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] == ' ')
                {
                    builder.Length--;
                }

                newlines++;
                if (newlines <= 2)
                {
                    builder.Append('\n');
                }

                continue;
            }

            if (c is ' ' or '\t')
            {
                if (newlines > 0 || builder.Length == 0 || builder[builder.Length - 1] == ' ')
                {
                    continue;
                }

                builder.Append(' ');
                newlines = 0;
                continue;
            }

            newlines = 0;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static string CollapseInline(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static bool IsHtmlMedia(string mediaType)
    {
        var semi = mediaType.IndexOf(';');
        if (semi >= 0)
        {
            mediaType = mediaType.Substring(0, semi);
        }

        mediaType = mediaType.Trim();
        return mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveZipPath(string opfPath, string href)
    {
        href = href.Trim();
        var hash = href.IndexOf('#');
        if (hash >= 0)
        {
            href = href.Substring(0, hash);
        }

        var query = href.IndexOf('?');
        if (query >= 0)
        {
            href = href.Substring(0, query);
        }

        href = Unescape(href).Replace('\\', '/');
        if (href.StartsWith("/", StringComparison.Ordinal))
        {
            return NormalizeZipPath(href);
        }

        var slash = opfPath.LastIndexOf('/');
        var baseDir = slash >= 0 ? opfPath.Substring(0, slash) : string.Empty;
        var combined = baseDir.Length == 0 ? href : baseDir + "/" + href;
        return NormalizeZipPath(combined);
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static List<Chapter> Materialize(List<ChapterDraft> drafts)
    {
        var chapters = new List<Chapter>(drafts.Count);
        foreach (var draft in drafts)
        {
            var text = draft.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var index = chapters.Count;
            var title = draft.ExplicitTitle && !string.IsNullOrWhiteSpace(draft.Title)
                ? Truncate(draft.Title.Trim(), MaxTitleLength)
                : "第" + (index + 1).ToString() + "章";
            chapters.Add(new Chapter
            {
                Index = index,
                Title = title,
                Text = text
            });
        }

        return chapters;
    }

    private static Book Finish(string filePath, string format, byte[] bytes, string? preferredTitle, List<Chapter> chapters)
    {
        var title = !string.IsNullOrWhiteSpace(preferredTitle)
            ? preferredTitle.Trim()
            : Path.GetFileNameWithoutExtension(filePath);
        var draft = new Book { Chapters = chapters };
        var segmentCount = BookSegmenter.Segment(draft).Count;
        return new Book
        {
            Id = ComputeId(bytes),
            Title = title,
            SourcePath = Path.GetFullPath(filePath),
            Format = format,
            SourceLength = bytes.LongLength,
            Chapters = chapters,
            SegmentCount = segmentCount
        };
    }

    private static string ComputeId(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 24);
    }

    private static string NormalizeNewlines(string text)
    {
        if (text.IndexOf('\r') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
    }

    private static ReadOnlySpan<char> TrimSpan(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value.Slice(start, end - start);
    }

    private readonly struct TitleHit
    {
        public TitleHit(int lineStart, int lineNext, string title)
        {
            LineStart = lineStart;
            LineNext = lineNext;
            Title = title;
        }

        public int LineStart { get; }
        public int LineNext { get; }
        public string Title { get; }
    }

    private readonly record struct ManifestItem(string Href, string MediaType);

    private sealed class ChapterDraft
    {
        public string Title { get; set; } = "";
        public bool ExplicitTitle { get; set; }
        public string Text { get; set; } = "";
    }
}
