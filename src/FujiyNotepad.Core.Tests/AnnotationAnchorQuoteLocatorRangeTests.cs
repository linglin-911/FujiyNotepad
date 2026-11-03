using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class AnnotationAnchorQuoteLocatorRangeTests
{
    [Fact]
    public void FindUniqueMatchInRange_IgnoresTheSameQuoteInOtherChapters()
    {
        var lines = new TestLineSource(
            ["第1章", "相同的原文摘录", "第2章", "相同的原文摘录", "末尾"],
            [LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.None]);
        var map = new DocumentTextOffsetMap(lines);
        long chapterStart = map.GetLineStartOffset(2);
        long chapterEnd = map.GetOffset(4, lines.GetLineUncached(4).Length);

        AnnotationAnchorQuoteMatch? match = AnnotationAnchorQuoteLocator.FindUniqueMatchInRange(
            lines, "相同的原文摘录", map, chapterStart, chapterEnd);

        long expectedStart = map.GetLineStartOffset(3);
        long expectedEnd = map.GetOffset(3, "相同的原文摘录".Length);
        Assert.Equal(new AnnotationAnchorQuoteMatch(expectedStart, expectedEnd), match);
        Assert.Null(AnnotationAnchorQuoteLocator.FindUniqueMatch(lines, "相同的原文摘录"));
    }

    [Fact]
    public void FindUniqueMatchInRange_ReturnsNullWhenQuoteRepeatsInsideTheSameChapter()
    {
        var lines = new TestLineSource(
            ["第1章", "重复摘录", "中间内容", "重复摘录", "第2章"],
            [LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.None]);
        var map = new DocumentTextOffsetMap(lines);
        long chapterStart = map.GetLineStartOffset(0);
        long chapterEnd = map.GetLineStartOffset(4);

        Assert.Null(AnnotationAnchorQuoteLocator.FindUniqueMatchInRange(
            lines, "重复摘录", map, chapterStart, chapterEnd));
    }

    [Fact]
    public void FindUniqueMatchInRange_NormalizesLineEndingsAndReturnsActualOffsets()
    {
        var lines = new TestLineSource(
            ["第1章", "第一行", "第二行", "第2章", "第一行", "第二行"],
            [LineEnding.CrLf, LineEnding.CrLf, LineEnding.CrLf,
             LineEnding.CrLf, LineEnding.CrLf, LineEnding.None]);
        var map = new DocumentTextOffsetMap(lines);
        long chapterStart = map.GetLineStartOffset(3);
        long chapterEnd = map.GetOffset(5, lines.GetLineUncached(5).Length);

        AnnotationAnchorQuoteMatch? match = AnnotationAnchorQuoteLocator.FindUniqueMatchInRange(
            lines, "第一行\n第二行", map, chapterStart, chapterEnd);

        long expectedStart = map.GetLineStartOffset(4);
        long expectedEnd = map.GetOffset(5, "第二行".Length);
        Assert.Equal(new AnnotationAnchorQuoteMatch(expectedStart, expectedEnd), match);
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
