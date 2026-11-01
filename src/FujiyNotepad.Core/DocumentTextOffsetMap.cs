namespace FujiyNotepad.Core;

/// <summary>
/// Maps the reader's zero-based (line, UTF-16 column) positions to absolute UTF-16 offsets in the
/// decoded document, and back. Line terminators retain their actual decoded width (LF = 1, CRLF = 2).
/// Sparse line checkpoints keep repeated lookups near a previously visited region inexpensive.
/// </summary>
public sealed class DocumentTextOffsetMap
{
    private readonly ILineSource lines;
    private readonly ILineEndingSource? lineEndings;
    private readonly int checkpointInterval;
    private readonly SortedDictionary<int, long> lineStarts = new() { [0] = 0 };
    private readonly object gate = new();

    public DocumentTextOffsetMap(ILineSource lines, int checkpointInterval = 1024)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (checkpointInterval < 1)
            throw new ArgumentOutOfRangeException(nameof(checkpointInterval));

        this.lines = lines;
        lineEndings = lines as ILineEndingSource;
        this.checkpointInterval = checkpointInterval;
    }

    /// <summary>Returns the absolute UTF-16 offset for a zero-based line and column.</summary>
    public long GetOffset(int lineIndex, int column)
    {
        lock (gate)
        {
            long lineStart = GetLineStartOffsetCore(lineIndex);
            string line = GetLine(lineIndex);
            return checked(lineStart + Math.Clamp(column, 0, line.Length));
        }
    }

    /// <summary>Returns the absolute UTF-16 offset of the beginning of a zero-based line.</summary>
    public long GetLineStartOffset(int lineIndex)
    {
        lock (gate)
            return GetLineStartOffsetCore(lineIndex);
    }

    /// <summary>
    /// Converts an absolute UTF-16 offset back to a reader line/column position. Offsets inside a CRLF
    /// terminator resolve to the end of the preceding line; the offset after the terminator resolves to
    /// column zero of the next line.
    /// </summary>
    public (int Line, int Column) GetPosition(long utf16Offset)
    {
        if (utf16Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(utf16Offset));

        lock (gate)
        {
            if (lines.LineCount == 0)
                return (0, 0);

            var checkpoint = lineStarts.LastOrDefault(pair => pair.Value <= utf16Offset);
            int lineIndex = checkpoint.Key;
            long lineStart = checkpoint.Value;

            while (lineIndex < lines.LineCount)
            {
                string line = GetLine(lineIndex);
                long textEnd = checked(lineStart + line.Length);
                if (utf16Offset <= textEnd)
                    return (lineIndex, (int)(utf16Offset - lineStart));

                int terminatorLength = GetTerminatorLength(lineIndex);
                long nextLineStart = checked(textEnd + terminatorLength);
                if (utf16Offset < nextLineStart)
                    return (lineIndex, line.Length);

                lineIndex++;
                lineStart = nextLineStart;
                AddCheckpoint(lineIndex, lineStart);
            }

            int lastLine = lines.LineCount - 1;
            string lastText = GetLine(lastLine);
            return (lastLine, lastText.Length);
        }
    }

    private long GetLineStartOffsetCore(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lines.LineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        var checkpoint = lineStarts.Last(pair => pair.Key <= lineIndex);
        int currentLine = checkpoint.Key;
        long offset = checkpoint.Value;

        while (currentLine < lineIndex)
        {
            string line = GetLine(currentLine);
            offset = checked(offset + line.Length + GetTerminatorLength(currentLine));
            currentLine++;
            AddCheckpoint(currentLine, offset);
        }

        return offset;
    }

    private string GetLine(int lineIndex) => lines.GetLineUncached(lineIndex);

    private int GetTerminatorLength(int lineIndex)
    {
        if (lineEndings is null)
            return 1;

        return lineEndings.GetLineEnding(lineIndex) switch
        {
            LineEnding.CrLf => 2,
            LineEnding.Lf => 1,
            LineEnding.None => 0,
            // Mixed is a file-level detector result, not a per-line result. Treat an unknown line as LF.
            _ => 1,
        };
    }

    private void AddCheckpoint(int lineIndex, long offset)
    {
        if (lineIndex % checkpointInterval == 0)
            lineStarts.TryAdd(lineIndex, offset);
    }
}
