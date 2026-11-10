namespace AIAssistant.Host.Wpf.Reading;

/// <summary>
/// 把章节纯文本切成不超过 <see cref="MaxSegmentLength"/> 的朗读段。
/// </summary>
public static class BookSegmenter
{
    /// <summary>单段最大长度（UTF-16 代码单元）。</summary>
    public const int MaxSegmentLength = 200;

    /// <summary>
    /// 按章节顺序切分全书。段序号全书连续，空白段会被丢掉。
    /// </summary>
    public static IReadOnlyList<Segment> Segment(Book book)
    {
        if (book is null)
        {
            throw new ArgumentNullException(nameof(book));
        }

        if (book.Chapters.Count == 0)
        {
            return Array.Empty<Segment>();
        }

        var segments = new List<Segment>(Math.Max(16, book.Chapters.Count * 4));
        foreach (var chapter in book.Chapters)
        {
            if (chapter is null || string.IsNullOrEmpty(chapter.Text))
            {
                continue;
            }

            AppendChapter(chapter, segments);
        }

        return segments;
    }

    private static void AppendChapter(Chapter chapter, List<Segment> segments)
    {
        var text = chapter.Text;
        var cursor = 0;
        var packStart = -1;
        var packEnd = -1;

        while (cursor < text.Length)
        {
            var sentenceEnd = FindSentenceEnd(text, cursor);
            if (sentenceEnd <= cursor)
            {
                sentenceEnd = Math.Min(text.Length, cursor + 1);
            }

            if (packStart < 0)
            {
                packStart = cursor;
                packEnd = sentenceEnd;
            }
            else if (TrimmedLength(text, packStart, sentenceEnd) <= MaxSegmentLength)
            {
                packEnd = sentenceEnd;
            }
            else
            {
                Flush(text, packStart, packEnd, chapter, segments);
                packStart = cursor;
                packEnd = sentenceEnd;
            }

            cursor = sentenceEnd;
        }

        if (packStart >= 0)
        {
            Flush(text, packStart, packEnd, chapter, segments);
        }
    }

    private static void Flush(string text, int start, int end, Chapter chapter, List<Segment> segments)
    {
        if (TrimmedLength(text, start, end) <= MaxSegmentLength)
        {
            Emit(text, start, end, chapter, segments);
            return;
        }

        SplitLong(text, start, end, chapter, segments);
    }

    private static void SplitLong(string text, int start, int end, Chapter chapter, List<Segment> segments)
    {
        var partStart = start;
        var groupStart = -1;
        var groupEnd = -1;

        for (var i = start; i < end; i++)
        {
            if (!IsSecondaryDelimiter(text[i]))
            {
                continue;
            }

            var partEnd = i + 1;
            PackPart(text, partStart, partEnd, chapter, segments, ref groupStart, ref groupEnd);
            partStart = partEnd;
        }

        if (partStart < end)
        {
            PackPart(text, partStart, end, chapter, segments, ref groupStart, ref groupEnd);
        }

        if (groupStart >= 0)
        {
            WriteOrHardCut(text, groupStart, groupEnd, chapter, segments);
        }
    }

    private static void PackPart(
        string text,
        int partStart,
        int partEnd,
        Chapter chapter,
        List<Segment> segments,
        ref int groupStart,
        ref int groupEnd)
    {
        if (groupStart < 0)
        {
            groupStart = partStart;
            groupEnd = partEnd;
            return;
        }

        if (TrimmedLength(text, groupStart, partEnd) <= MaxSegmentLength)
        {
            groupEnd = partEnd;
            return;
        }

        WriteOrHardCut(text, groupStart, groupEnd, chapter, segments);
        groupStart = partStart;
        groupEnd = partEnd;
    }

    private static void WriteOrHardCut(string text, int start, int end, Chapter chapter, List<Segment> segments)
    {
        if (TrimmedLength(text, start, end) <= MaxSegmentLength)
        {
            Emit(text, start, end, chapter, segments);
            return;
        }

        HardCut(text, start, end, chapter, segments);
    }

    private static void HardCut(string text, int start, int end, Chapter chapter, List<Segment> segments)
    {
        var i = start;
        while (i < end)
        {
            while (i < end && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= end)
            {
                break;
            }

            var len = Math.Min(MaxSegmentLength, end - i);
            if (len > 1
                && i + len < end
                && char.IsHighSurrogate(text[i + len - 1])
                && char.IsLowSurrogate(text[i + len]))
            {
                len--;
            }

            Emit(text, i, i + len, chapter, segments);
            i += len;
        }
    }

    private static void Emit(string text, int start, int end, Chapter chapter, List<Segment> segments)
    {
        TrimRange(text, start, end, out var from, out var to);
        if (from >= to)
        {
            return;
        }

        if (to - from > MaxSegmentLength)
        {
            HardCut(text, from, to, chapter, segments);
            return;
        }

        segments.Add(new Segment
        {
            ChapterIndex = chapter.Index,
            SegmentIndex = segments.Count,
            CharOffset = from,
            Text = text.Substring(from, to - from),
            ChapterTitle = chapter.Title
        });
    }

    private static int FindSentenceEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (TryMatchBreak(text, i, out var breakEnd))
            {
                return breakEnd;
            }
        }

        return text.Length;
    }

    private static bool TryMatchBreak(string text, int index, out int breakEnd)
    {
        var c = text[index];
        if (c == '\n')
        {
            breakEnd = index + 1;
            return true;
        }

        if (c == '\r')
        {
            breakEnd = index + 1 < text.Length && text[index + 1] == '\n' ? index + 2 : index + 1;
            return true;
        }

        if (IsPrimaryEnd(c))
        {
            var j = index + 1;
            while (j < text.Length && IsPrimaryEnd(text[j]))
            {
                j++;
            }

            breakEnd = j;
            return true;
        }

        if (c == '.')
        {
            var j = index + 1;
            while (j < text.Length && text[j] == '.')
            {
                j++;
            }

            if (j >= text.Length || char.IsWhiteSpace(text[j]))
            {
                breakEnd = j;
                return true;
            }
        }

        breakEnd = index;
        return false;
    }

    private static bool IsPrimaryEnd(char c) =>
        c is '。' or '！' or '？' or '；' or '…' or '!' or '?' or ';';

    private static bool IsSecondaryDelimiter(char c) =>
        c is '，' or ',' or '、' or '；' or ';' or '：' or ':' || char.IsWhiteSpace(c);

    private static int TrimmedLength(string text, int start, int end)
    {
        TrimRange(text, start, end, out var from, out var to);
        return to - from;
    }

    private static void TrimRange(string text, int start, int end, out int from, out int to)
    {
        from = start;
        to = end;
        if (from < 0)
        {
            from = 0;
        }

        if (to > text.Length)
        {
            to = text.Length;
        }

        while (from < to && char.IsWhiteSpace(text[from]))
        {
            from++;
        }

        while (to > from && char.IsWhiteSpace(text[to - 1]))
        {
            to--;
        }
    }
}
