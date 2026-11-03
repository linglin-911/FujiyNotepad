namespace FujiyNotepad.Core;

/// <summary>
/// Snapshot data captured when a small-annotation anchor is created. Relative offsets count UTF-16
/// characters with every line ending normalized to one LF, so they survive LF/CRLF conversion.
/// </summary>
public sealed record AnnotationAnchorMetadata(
    long RelativeStartOffset,
    long RelativeEndOffset,
    long ChapterBodyStartRelativeOffset,
    string ChapterBodySha256,
    string PrefixContext,
    string SuffixContext);

public readonly record struct AnnotationAnchorContext(string Prefix, string Suffix);

/// <summary>Builds and restores chapter-relative anchor metadata without materializing the full document.</summary>
public static class AnnotationAnchorMetadataBuilder
{
    public const int DefaultContextLength = 64;

    public static AnnotationAnchorMetadata Create(
        NovelChapter chapter,
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        long startOffset,
        long endOffset,
        int contextLength = DefaultContextLength)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(offsetMap);
        if (!chapter.IsActive || startOffset < chapter.StartOffset || endOffset <= startOffset
            || endOffset > chapter.EndOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset),
                "A new annotation anchor must be a non-empty range wholly inside one active chapter.");
        }
        if (contextLength < 0 || contextLength > 256)
            throw new ArgumentOutOfRangeException(nameof(contextLength));

        long bodyStart = Math.Clamp(chapter.BodyStartOffset, chapter.StartOffset, chapter.EndOffset);
        long relativeStart = GetNormalizedRelativeOffset(lines, offsetMap, chapter.StartOffset, startOffset);
        long relativeEnd = GetNormalizedRelativeOffset(lines, offsetMap, chapter.StartOffset, endOffset);
        long relativeBodyStart = GetNormalizedRelativeOffset(lines, offsetMap, chapter.StartOffset, bodyStart);
        AnnotationAnchorContext context = CaptureContext(
            lines, offsetMap, chapter.StartOffset, chapter.EndOffset,
            startOffset, endOffset, contextLength);

        return new AnnotationAnchorMetadata(
            relativeStart,
            relativeEnd,
            relativeBodyStart,
            chapter.BodySha256 ?? string.Empty,
            context.Prefix,
            context.Suffix);
    }

    /// <summary>
    /// Computes the current absolute range represented by a saved chapter-relative range. This is
    /// useful as a search hint even after the chapter body changed.
    /// </summary>
    public static bool TryGetExpectedRange(
        AnnotationAnchor anchor,
        NovelChapter chapter,
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        out AnnotationAnchorQuoteMatch expected)
    {
        expected = default;
        if (anchor.ChapterId != chapter.Id
            || anchor.RelativeStartOffset is not long savedStart
            || anchor.RelativeEndOffset is not long savedEnd
            || anchor.ChapterBodyStartRelativeOffset is not long savedBodyStart
            || savedStart < 0 || savedEnd <= savedStart || savedBodyStart < 0)
        {
            return false;
        }

        try
        {
            long currentBodyStart = GetNormalizedRelativeOffset(
                lines, offsetMap, chapter.StartOffset,
                Math.Clamp(chapter.BodyStartOffset, chapter.StartOffset, chapter.EndOffset));
            bool startsInBody = savedStart >= savedBodyStart;
            bool endsInBody = savedEnd > savedBodyStart;
            if (startsInBody != endsInBody)
                return false;

            long shift = startsInBody ? currentBodyStart - savedBodyStart : 0;
            long currentStart = checked(savedStart + shift);
            long currentEnd = checked(savedEnd + shift);
            long absoluteStart = GetAbsoluteOffsetFromNormalizedRelative(
                lines, offsetMap, chapter.StartOffset, currentStart);
            long absoluteEnd = GetAbsoluteOffsetFromNormalizedRelative(
                lines, offsetMap, chapter.StartOffset, currentEnd);

            if (absoluteStart < chapter.StartOffset || absoluteEnd <= absoluteStart
                || absoluteEnd > chapter.EndOffset)
            {
                return false;
            }

            expected = new AnnotationAnchorQuoteMatch(absoluteStart, absoluteEnd);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Restores directly only when the chapter body fingerprint is unchanged.</summary>
    public static bool TryRestoreUnchangedChapterAnchor(
        AnnotationAnchor anchor,
        NovelChapter chapter,
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        out AnnotationAnchorQuoteMatch restored)
    {
        restored = default;
        if (string.IsNullOrWhiteSpace(anchor.ChapterBodySha256)
            || string.IsNullOrWhiteSpace(chapter.BodySha256)
            || !string.Equals(anchor.ChapterBodySha256, chapter.BodySha256, StringComparison.Ordinal))
        {
            return false;
        }

        return TryGetExpectedRange(anchor, chapter, lines, offsetMap, out restored);
    }

    /// <summary>Returns bounded, normalized context immediately surrounding one quote.</summary>
    public static AnnotationAnchorContext CaptureContext(
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        long chapterStartOffset,
        long chapterEndOffset,
        long startOffset,
        long endOffset,
        int contextLength = DefaultContextLength)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(offsetMap);
        if (chapterStartOffset < 0 || chapterEndOffset <= chapterStartOffset
            || startOffset < chapterStartOffset || endOffset <= startOffset || endOffset > chapterEndOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), "The context range must be inside its chapter.");
        }
        if (contextLength < 0 || contextLength > 256)
            throw new ArgumentOutOfRangeException(nameof(contextLength));

        long rawRadius = checked(contextLength * 2L + 8);
        long prefixStart = Math.Max(chapterStartOffset, startOffset - rawRadius);
        long suffixEnd = Math.Min(chapterEndOffset, endOffset + rawRadius);
        string prefix = ExtractNormalizedRange(lines, offsetMap, prefixStart, startOffset);
        string suffix = ExtractNormalizedRange(lines, offsetMap, endOffset, suffixEnd);

        if (prefix.Length > contextLength)
            prefix = prefix[^contextLength..];
        if (suffix.Length > contextLength)
            suffix = suffix[..contextLength];

        return new AnnotationAnchorContext(prefix, suffix);
    }

    private static long GetNormalizedRelativeOffset(
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        long chapterStartOffset,
        long absoluteOffset)
    {
        if (absoluteOffset < chapterStartOffset)
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset));

        (int startLine, int startColumn) = offsetMap.GetPosition(chapterStartOffset);
        (int targetLine, int targetColumn) = offsetMap.GetPosition(absoluteOffset);
        if (targetLine < startLine || targetLine >= lines.LineCount || startLine < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset));

        long result = 0;
        for (int lineIndex = startLine; lineIndex < targetLine; lineIndex++)
        {
            string line = lines.GetLineUncached(lineIndex);
            int from = lineIndex == startLine ? startColumn : 0;
            result = checked(result + line.Length - from);
            if (GetTerminatorLength(lines, lineIndex) == 0)
                throw new InvalidOperationException("The chapter-relative range crosses a missing line terminator.");
            result = checked(result + 1);
        }

        result = checked(result + targetColumn - (targetLine == startLine ? startColumn : 0));
        return result;
    }

    private static long GetAbsoluteOffsetFromNormalizedRelative(
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        long chapterStartOffset,
        long normalizedRelativeOffset)
    {
        if (normalizedRelativeOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(normalizedRelativeOffset));

        (int lineIndex, int startColumn) = offsetMap.GetPosition(chapterStartOffset);
        int chapterStartLine = lineIndex;
        long remaining = normalizedRelativeOffset;
        while (lineIndex < lines.LineCount)
        {
            string line = lines.GetLineUncached(lineIndex);
            int from = lineIndex == chapterStartLine ? startColumn : 0;
            int available = line.Length - from;
            if (remaining <= available)
                return offsetMap.GetOffset(lineIndex, from + checked((int)remaining));

            remaining -= available;
            if (GetTerminatorLength(lines, lineIndex) == 0 || lineIndex + 1 >= lines.LineCount)
                throw new ArgumentOutOfRangeException(nameof(normalizedRelativeOffset));

            // The normalized coordinate counts every real line terminator as exactly one LF.
            remaining--;
            lineIndex++;
            if (remaining == 0)
                return offsetMap.GetLineStartOffset(lineIndex);
        }

        throw new ArgumentOutOfRangeException(nameof(normalizedRelativeOffset));
    }

    private static string ExtractNormalizedRange(
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        long startOffset,
        long endOffset)
    {
        if (endOffset <= startOffset)
            return string.Empty;

        (int startLine, int startColumn) = offsetMap.GetPosition(startOffset);
        (int endLine, int endColumn) = offsetMap.GetPosition(endOffset);
        if (startLine < 0 || endLine < startLine || endLine >= lines.LineCount)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        for (int lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            string line = lines.GetLineUncached(lineIndex);
            int from = lineIndex == startLine ? Math.Clamp(startColumn, 0, line.Length) : 0;
            int to = lineIndex == endLine ? Math.Clamp(endColumn, from, line.Length) : line.Length;
            builder.Append(line.AsSpan(from, to - from));
            if (lineIndex < endLine)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int GetTerminatorLength(ILineSource lines, int lineIndex)
    {
        if (lines is ILineEndingSource endings)
        {
            return endings.GetLineEnding(lineIndex) switch
            {
                LineEnding.CrLf => 2,
                LineEnding.Lf => 1,
                LineEnding.None => 0,
                _ => 1,
            };
        }

        return lineIndex < lines.LineCount - 1 ? 1 : 0;
    }
}

/// <summary>
/// Disambiguates repeated quote occurrences using saved before/after context and the old relative
/// position. A candidate is accepted only when context evidence is meaningful and clearly stronger.
/// </summary>
public static class AnnotationAnchorContextMatcher
{
    private const int MaximumCandidates = 64;
    private const int MinimumContextScore = 8;
    private const int MinimumScoreLead = 9;

    public static AnnotationAnchorQuoteMatch? FindBestMatchInRange(
        ILineSource lines,
        DocumentTextOffsetMap offsetMap,
        AnnotationAnchor anchor,
        NovelChapter chapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(offsetMap);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(chapter);

        AnnotationAnchorQuoteSearchResult search = AnnotationAnchorQuoteLocator.FindMatchesInRange(
            lines, anchor.Quote, offsetMap, chapter.StartOffset, chapter.EndOffset,
            MaximumCandidates, cancellationToken);
        if (search.IsTruncated || search.Matches.Count == 0)
            return null;
        if (search.Matches.Count == 1)
            return search.Matches[0];
        if (string.IsNullOrEmpty(anchor.PrefixContext) && string.IsNullOrEmpty(anchor.SuffixContext))
            return null;

        long? expectedStart = AnnotationAnchorMetadataBuilder.TryGetExpectedRange(
            anchor, chapter, lines, offsetMap, out AnnotationAnchorQuoteMatch expected)
            ? expected.StartOffset
            : null;

        var scores = new List<(AnnotationAnchorQuoteMatch Match, int Context, int Total)>(search.Matches.Count);
        foreach (AnnotationAnchorQuoteMatch candidate in search.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnnotationAnchorContext context = AnnotationAnchorMetadataBuilder.CaptureContext(
                lines, offsetMap, chapter.StartOffset, chapter.EndOffset,
                candidate.StartOffset, candidate.EndOffset);

            int contextScore = LongestCommonSuffix(anchor.PrefixContext, context.Prefix)
                + LongestCommonPrefix(anchor.SuffixContext, context.Suffix);
            int proximityScore = GetProximityScore(candidate.StartOffset, expectedStart);
            scores.Add((candidate, contextScore, contextScore + proximityScore));
        }

        var ordered = scores.OrderByDescending(item => item.Total)
            .ThenByDescending(item => item.Context)
            .ToArray();
        if (ordered[0].Context < MinimumContextScore
            || ordered[0].Total - ordered[1].Total < MinimumScoreLead)
        {
            return null;
        }

        return ordered[0].Match;
    }

    private static int GetProximityScore(long candidate, long? expected)
    {
        if (expected is not long expectedOffset)
            return 0;
        long distance = candidate >= expectedOffset
            ? candidate - expectedOffset
            : expectedOffset - candidate;
        return distance >= 256 ? 0 : 8 - (int)(distance / 32);
    }

    private static int LongestCommonSuffix(string left, string right)
    {
        int count = 0;
        int meaningfulCharacters = 0;
        while (count < left.Length && count < right.Length
            && left[left.Length - count - 1] == right[right.Length - count - 1])
        {
            char matched = left[left.Length - count - 1];
            if (!char.IsWhiteSpace(matched))
                meaningfulCharacters++;
            count++;
        }
        return meaningfulCharacters;
    }

    private static int LongestCommonPrefix(string left, string right)
    {
        int count = 0;
        int meaningfulCharacters = 0;
        while (count < left.Length && count < right.Length && left[count] == right[count])
        {
            if (!char.IsWhiteSpace(left[count]))
                meaningfulCharacters++;
            count++;
        }
        return meaningfulCharacters;
    }
}
