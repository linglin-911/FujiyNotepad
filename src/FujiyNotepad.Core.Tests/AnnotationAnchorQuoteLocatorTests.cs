using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class AnnotationAnchorQuoteLocatorTests
{
    [Fact]
    public void FindUniqueMatch_NormalizesLineEndingsAndReturnsCurrentRawOffsets()
    {
        var lines = new TestLineSource(
            ["prefix", "first", "second", "suffix"],
            [LineEnding.CrLf, LineEnding.Lf, LineEnding.CrLf, LineEnding.None]);

        AnnotationAnchorQuoteMatch? match =
            AnnotationAnchorQuoteLocator.FindUniqueMatch(lines, "first\r\nsecond");

        Assert.Equal(new AnnotationAnchorQuoteMatch(8, 20), match);
    }

    [Fact]
    public void FindUniqueMatch_FindsQuoteAfterLargeOffsetDrift()
    {
        string[] values = Enumerable.Range(0, 1200)
            .Select(index => $"line-{index:D4}")
            .Append("the saved excerpt")
            .ToArray();
        LineEnding[] endings = Enumerable.Repeat(LineEnding.CrLf, values.Length).ToArray();
        endings[^1] = LineEnding.None;
        var lines = new TestLineSource(values, endings);

        AnnotationAnchorQuoteMatch? match =
            AnnotationAnchorQuoteLocator.FindUniqueMatch(lines, "the saved excerpt");

        long expectedStart = values.Take(values.Length - 1)
            .Sum(line => (long)line.Length + 2);
        Assert.Equal(new AnnotationAnchorQuoteMatch(expectedStart, expectedStart + "the saved excerpt".Length), match);
    }

    [Fact]
    public void FindUniqueMatch_ReturnsNullWhenQuoteIsAmbiguous()
    {
        var lines = new TestLineSource(
            ["same quote", "between", "same quote"],
            [LineEnding.Lf, LineEnding.Lf, LineEnding.None]);

        Assert.Null(AnnotationAnchorQuoteLocator.FindUniqueMatch(lines, "same quote"));
    }

    [Fact]
    public void FindUniqueMatch_ReturnsNullWhenQuoteDoesNotExist()
    {
        var lines = new TestLineSource(
            ["first", "second"],
            [LineEnding.Lf, LineEnding.None]);

        Assert.Null(AnnotationAnchorQuoteLocator.FindUniqueMatch(lines, "missing"));
    }

    private sealed class TestLineSource(string[] values, LineEnding[] endings)
        : ILineSource, ILineEndingSource
    {
        public int LineCount => values.Length;

        public string GetLine(int lineIndex) => values[lineIndex];

        public string GetLineUncached(int lineIndex) => values[lineIndex];

        public LineEnding GetLineEnding(int lineIndex) => endings[lineIndex];
    }
}
