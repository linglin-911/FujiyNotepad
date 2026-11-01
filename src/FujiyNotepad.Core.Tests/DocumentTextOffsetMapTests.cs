using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class DocumentTextOffsetMapTests
{
    [Fact]
    public void MapsLfAndCrLfPositionsToDecodedUtf16Offsets()
    {
        var lines = new FakeLines(
            new[] { "第一行", "second", "third" },
            new[] { LineEnding.CrLf, LineEnding.Lf, LineEnding.None });

        var map = new DocumentTextOffsetMap(lines, checkpointInterval: 1);

        Assert.Equal(0, map.GetOffset(0, 0));
        Assert.Equal(2, map.GetOffset(0, 2));
        Assert.Equal(5, map.GetLineStartOffset(1)); // 3 UTF-16 chars + CRLF
        Assert.Equal(12, map.GetLineStartOffset(2)); // + 6 chars + LF
        Assert.Equal(15, map.GetOffset(2, 3));
    }

    [Fact]
    public void CountsSupplementaryCharactersAsTwoUtf16CodeUnits()
    {
        var lines = new FakeLines(
            new[] { "A😀B", "尾" },
            new[] { LineEnding.CrLf, LineEnding.None });

        var map = new DocumentTextOffsetMap(lines, checkpointInterval: 1);

        Assert.Equal(3, map.GetOffset(0, 3));
        Assert.Equal(6, map.GetLineStartOffset(1)); // A + surrogate pair + B + CRLF
        Assert.Equal((0, 2), map.GetPosition(2));
        Assert.Equal((1, 0), map.GetPosition(6));
    }

    [Fact]
    public void ReusesSparseCheckpointsAndMapsOffsetsBackToLineColumns()
    {
        var texts = Enumerable.Range(0, 10).Select(i => $"line{i}").ToArray();
        var endings = Enumerable.Repeat(LineEnding.Lf, 9).Append(LineEnding.None).ToArray();
        var lines = new FakeLines(texts, endings);
        var map = new DocumentTextOffsetMap(lines, checkpointInterval: 3);

        long offset = map.GetOffset(8, 4);

        Assert.Equal(8 * 6 + 4, offset);
        Assert.Equal((8, 4), map.GetPosition(offset));
        Assert.Equal((9, 0), map.GetPosition(9 * 6));
    }

    [Fact]
    public void RejectsInvalidLineAndNegativeOffset()
    {
        var map = new DocumentTextOffsetMap(new FakeLines(new[] { "only" }, new[] { LineEnding.None }));

        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetOffset(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.GetPosition(-1));
    }

    private sealed class FakeLines : ILineSource, ILineEndingSource
    {
        private readonly string[] text;
        private readonly LineEnding[] endings;

        public FakeLines(string[] text, LineEnding[] endings)
        {
            this.text = text;
            this.endings = endings;
        }

        public int LineCount => text.Length;
        public string GetLine(int lineIndex) => text[lineIndex];
        public string GetLineUncached(int lineIndex) => text[lineIndex];
        public LineEnding GetLineEnding(int lineIndex) => endings[lineIndex];
    }
}
