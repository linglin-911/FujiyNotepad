namespace FujiyNotepad.Core;

/// <summary>A unique occurrence of a saved annotation quote in the current document.</summary>
public readonly record struct AnnotationAnchorQuoteMatch(long StartOffset, long EndOffset);

/// <summary>
/// Finds a saved quote in the current line source without loading the complete document into memory.
/// CRLF and LF are treated as the same logical line break, while returned offsets retain the current
/// document's actual UTF-16 line-ending widths.
/// </summary>
public static class AnnotationAnchorQuoteLocator
{
    private const int MaximumQuoteLength = 2_000_000;

    /// <summary>
    /// Returns a match only when the normalized quote occurs exactly once in the document.
    /// Multiple matches are deliberately treated as ambiguous rather than guessing a location.
    /// </summary>
    public static AnnotationAnchorQuoteMatch? FindUniqueMatch(
        ILineSource lines,
        string quote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(quote);

        string pattern = NormalizeLineEndings(quote);
        if (pattern.Length == 0 || pattern.Length > MaximumQuoteLength || lines.LineCount == 0)
            return null;

        int[] prefix = BuildPrefixTable(pattern);
        var matchStarts = new long[pattern.Length];
        var matchEnds = new long[pattern.Length];
        ILineEndingSource? lineEndings = lines as ILineEndingSource;

        long normalizedOffset = 0;
        long documentOffset = 0;
        int matchedLength = 0;
        AnnotationAnchorQuoteMatch? uniqueMatch = null;
        bool ambiguous = false;

        void Consume(char value, long rawStart, long rawEnd)
        {
            int ringIndex = (int)(normalizedOffset % pattern.Length);
            matchStarts[ringIndex] = rawStart;
            matchEnds[ringIndex] = rawEnd;

            while (matchedLength > 0 && pattern[matchedLength] != value)
                matchedLength = prefix[matchedLength - 1];

            if (pattern[matchedLength] == value)
                matchedLength++;

            normalizedOffset++;

            if (matchedLength != pattern.Length)
                return;

            long startIndex = normalizedOffset - pattern.Length;
            long startOffset = matchStarts[(int)(startIndex % pattern.Length)];
            long endOffset = matchEnds[(int)((normalizedOffset - 1) % pattern.Length)];
            if (uniqueMatch is null)
                uniqueMatch = new AnnotationAnchorQuoteMatch(startOffset, endOffset);
            else
                ambiguous = true;

            matchedLength = prefix[matchedLength - 1];
        }

        for (int lineIndex = 0; lineIndex < lines.LineCount && !ambiguous; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines.GetLineUncached(lineIndex);
            for (int column = 0; column < line.Length && !ambiguous; column++)
            {
                Consume(line[column], documentOffset, documentOffset + 1);
                documentOffset++;
            }

            int terminatorLength = lineEndings is null
                ? (lineIndex < lines.LineCount - 1 ? 1 : 0)
                : lineEndings.GetLineEnding(lineIndex) switch
                {
                    LineEnding.CrLf => 2,
                    LineEnding.Lf => 1,
                    LineEnding.None => 0,
                    // Mixed is a file-level detector result; if a provider reports it for a line,
                    // use the same conservative LF width as DocumentTextOffsetMap.
                    _ => 1,
                };

            if (terminatorLength > 0 && !ambiguous)
            {
                Consume('\n', documentOffset, documentOffset + terminatorLength);
                documentOffset += terminatorLength;
            }
        }

        return ambiguous ? null : uniqueMatch;
    }

    /// <summary>
    /// Finds a quote only inside one chapter range. This prevents identical excerpts in different
    /// chapters from competing with one another and avoids scanning the complete novel for a known chapter.
    /// </summary>
    public static AnnotationAnchorQuoteMatch? FindUniqueMatchInRange(
        ILineSource lines,
        string quote,
        DocumentTextOffsetMap offsetMap,
        long rangeStartOffset,
        long rangeEndOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(offsetMap);
        if (rangeStartOffset < 0 || rangeEndOffset <= rangeStartOffset || lines.LineCount == 0)
            return null;

        string pattern = NormalizeLineEndings(quote);
        if (pattern.Length == 0 || pattern.Length > MaximumQuoteLength)
            return null;

        (int startLine, int startColumn) = offsetMap.GetPosition(rangeStartOffset);
        (int endLine, int endColumn) = offsetMap.GetPosition(rangeEndOffset);
        if (startLine < 0 || endLine < startLine || endLine >= lines.LineCount
            || offsetMap.GetOffset(startLine, startColumn) != rangeStartOffset
            || offsetMap.GetOffset(endLine, endColumn) != rangeEndOffset)
        {
            return null;
        }

        int[] prefix = BuildPrefixTable(pattern);
        var matchStarts = new long[pattern.Length];
        var matchEnds = new long[pattern.Length];
        ILineEndingSource? lineEndings = lines as ILineEndingSource;

        long normalizedOffset = 0;
        long documentOffset = rangeStartOffset;
        int matchedLength = 0;
        AnnotationAnchorQuoteMatch? uniqueMatch = null;
        bool ambiguous = false;

        void Consume(char value, long rawStart, long rawEnd)
        {
            int ringIndex = (int)(normalizedOffset % pattern.Length);
            matchStarts[ringIndex] = rawStart;
            matchEnds[ringIndex] = rawEnd;

            while (matchedLength > 0 && pattern[matchedLength] != value)
                matchedLength = prefix[matchedLength - 1];

            if (pattern[matchedLength] == value)
                matchedLength++;

            normalizedOffset++;
            if (matchedLength != pattern.Length)
                return;

            long startIndex = normalizedOffset - pattern.Length;
            long startOffset = matchStarts[(int)(startIndex % pattern.Length)];
            long endOffset = matchEnds[(int)((normalizedOffset - 1) % pattern.Length)];
            if (uniqueMatch is null)
                uniqueMatch = new AnnotationAnchorQuoteMatch(startOffset, endOffset);
            else
                ambiguous = true;

            matchedLength = prefix[matchedLength - 1];
        }

        for (int lineIndex = startLine; lineIndex <= endLine && !ambiguous; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines.GetLineUncached(lineIndex);
            int from = lineIndex == startLine ? startColumn : 0;
            int to = lineIndex == endLine ? endColumn : line.Length;
            if (from < 0 || to < from || to > line.Length)
                return null;

            for (int column = from; column < to && !ambiguous; column++)
            {
                Consume(line[column], documentOffset, documentOffset + 1);
                documentOffset++;
            }

            if (lineIndex < endLine && !ambiguous)
            {
                int terminatorLength = lineEndings is null
                    ? 1
                    : lineEndings.GetLineEnding(lineIndex) switch
                    {
                        LineEnding.CrLf => 2,
                        LineEnding.Lf => 1,
                        LineEnding.None => 0,
                        _ => 1,
                    };

                if (terminatorLength > 0)
                {
                    Consume('\\n', documentOffset, documentOffset + terminatorLength);
                    documentOffset += terminatorLength;
                }
            }
        }

        return ambiguous ? null : uniqueMatch;
    }

    /// <summary>Normalizes CRLF, CR and LF into a single logical LF character.</summary>
    public static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static int[] BuildPrefixTable(string pattern)
    {
        var prefix = new int[pattern.Length];
        int matched = 0;
        for (int index = 1; index < pattern.Length; index++)
        {
            while (matched > 0 && pattern[matched] != pattern[index])
                matched = prefix[matched - 1];

            if (pattern[matched] == pattern[index])
                matched++;

            prefix[index] = matched;
        }

        return prefix;
    }
}
