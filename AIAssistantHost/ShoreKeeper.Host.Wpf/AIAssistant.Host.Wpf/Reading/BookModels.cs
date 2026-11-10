namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 一本书的解析结果。Id 由源文件全部字节的 SHA256 决定。
/// </summary>
public sealed class Book
{
    /// <summary>内容 SHA256 的小写十六进制前 24 位。</summary>
    public string Id { get; init; } = "";

    /// <summary>书名。txt 取文件名；epub 优先用 OPF 的 dc:title。</summary>
    public string Title { get; init; } = "";

    /// <summary>源文件绝对路径。</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>txt 或 epub。</summary>
    public string Format { get; init; } = "";

    /// <summary>源文件字节长度。</summary>
    public long SourceLength { get; init; }

    /// <summary>按阅读顺序排列的章节。空章已剔除并重新编号。</summary>
    public IReadOnlyList<Chapter> Chapters { get; init; } = Array.Empty<Chapter>();

    /// <summary>全书分段总数，与 <see cref="BookSegmenter.Segment"/> 的结果数量一致。</summary>
    public int SegmentCount { get; init; }
}

/// <summary>
/// 一个章节的纯文本。
/// </summary>
public sealed class Chapter
{
    /// <summary>从 0 开始的连续章节序号。</summary>
    public int Index { get; init; }

    /// <summary>章节标题。</summary>
    public string Title { get; init; } = "";

    /// <summary>已去掉标记的纯文本，换行统一为 \n。</summary>
    public string Text { get; init; } = "";
}

/// <summary>
/// 适合一次 TTS 的文本段。
/// </summary>
public sealed class Segment
{
    /// <summary>所属章节的 <see cref="Chapter.Index"/>。</summary>
    public int ChapterIndex { get; init; }

    /// <summary>全书范围内从 0 开始的连续段序号。</summary>
    public int SegmentIndex { get; init; }

    /// <summary>段首字符在所属 <see cref="Chapter.Text"/> 中的偏移。</summary>
    public int CharOffset { get; init; }

    /// <summary>已去掉首尾空白的段文本，长度不超过 200 个 UTF-16 字符。</summary>
    public string Text { get; init; } = "";

    /// <summary>所属章节标题。</summary>
    public string ChapterTitle { get; init; } = "";
}
