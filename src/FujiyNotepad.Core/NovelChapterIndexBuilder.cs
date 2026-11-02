using System.Text.RegularExpressions;

namespace FujiyNotepad.Core;

/// <summary>
/// Finds chapter-heading lines without loading the document into memory. The input must expose its
/// complete line index; production callers run this after <see cref="LineIndexer.IsCompleted"/>.
/// </summary>
public static class NovelChapterIndexBuilder
{
    public const int CurrentParserVersion = 1;

    private static readonly Regex ChineseNumberedHeading = new(
        @"^第\s*[0-9０-９〇零一二三四五六七八九十百千万两兩壹贰貳叁參肆伍陆陸柒捌玖拾佰仟]+\s*(?:章|回|节|節|集|篇)(?=$|[\s:：、.．\-—_【\[（(]).*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishNumberedHeading = new(
        @"^(?:chapter|ch\.?)\s*#?\s*\d+(?:\s*[:：、.．\-—_]\s*.*|\s+.*|\s*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SpecialHeading = new(
        @"^(?:序章|楔子|引子|序幕|终章|尾声|后记|番外(?:\s*\d+)?|prologue|epilogue)(?:\s+.*|\s*[:：、.．\-—_].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds chapter ranges in document order. ChapterNumber is the one-based ordinal in the
    /// detected index, while Title preserves the complete heading line for display.
    /// </summary>
    public static IReadOnlyList<NovelChapter> Build(
        ILineSource lines,
        long documentRevision = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (documentRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(documentRevision));

        int lineCount = lines.LineCount;
        if (lineCount == 0)
            return Array.Empty<NovelChapter>();

        ILineEndingSource? endings = lines as ILineEndingSource;
        var headings = new List<(long Offset, string Title)>();
        long documentEnd = 0;

        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string line = lines.GetLineUncached(lineIndex);
            string candidate = line.Trim().TrimStart('\uFEFF').Trim();
            if (TryParseHeading(candidate, out string title))
                headings.Add((documentEnd, title));

            int terminatorLength = endings is null
                ? (lineIndex < lineCount - 1 ? 1 : 0)
                : endings.GetLineEnding(lineIndex) switch
                {
                    LineEnding.CrLf => 2,
                    LineEnding.Lf => 1,
                    LineEnding.None => 0,
                    _ => 1,
                };

            documentEnd = checked(documentEnd + line.Length + terminatorLength);
        }

        if (headings.Count == 0)
            return Array.Empty<NovelChapter>();

        var chapters = new List<NovelChapter>(headings.Count);
        for (int index = 0; index < headings.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long start, string title) = headings[index];
            long end = index + 1 < headings.Count ? headings[index + 1].Offset : documentEnd;
            if (end <= start)
                continue;

            chapters.Add(new NovelChapter(
                Guid.NewGuid(),
                title,
                start,
                end,
                chapters.Count + 1,
                documentRevision));
        }

        return chapters;
    }

    /// <summary>Returns true only when a complete trimmed line resembles a chapter heading.</summary>
    public static bool TryParseHeading(string line, out string title)
    {
        ArgumentNullException.ThrowIfNull(line);
        string candidate = line.Trim().TrimStart('\uFEFF').Trim();
        if (candidate.Length == 0)
        {
            title = "";
            return false;
        }

        if (ChineseNumberedHeading.IsMatch(candidate)
            || EnglishNumberedHeading.IsMatch(candidate)
            || SpecialHeading.IsMatch(candidate))
        {
            title = candidate;
            return true;
        }

        title = "";
        return false;
    }
}
