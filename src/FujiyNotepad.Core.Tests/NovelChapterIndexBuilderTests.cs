using FujiyNotepad.Core;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Core.Tests;

public sealed class NovelChapterIndexBuilderTests
{
    [Theory]
    [InlineData("第1章 开始", true)]
    [InlineData("第一章：新的开始", true)]
    [InlineData("第十二章【旧友】", true)]
    [InlineData("第 12 章 标题", true)]
    [InlineData("Chapter 12: A New Start", true)]
    [InlineData("ch. 3 - Return", true)]
    [InlineData("序章", true)]
    [InlineData("番外 2：重逢", true)]
    [InlineData("尾声", true)]
    [InlineData("这是第1章的时候发生的事", false)]
    [InlineData("第1章的时候，天色已晚", false)]
    [InlineData("", false)]
    public void TryParseHeading_RecognizesChapterHeadingLinesOnly(string line, bool expected)
    {
        bool actual = NovelChapterIndexBuilder.TryParseHeading(line, out string title);

        Assert.Equal(expected, actual);
        Assert.Equal(expected ? line.Trim() : "", title);
    }

    [Fact]
    public void Build_CreatesContiguousChapterRangesUsingUtf16AndActualLineEndings()
    {
        string[] text =
        [
            "前言😀",
            "第1章 开始",
            "正文😀",
            "第一章：第二卷的第一章",
            "第二章 继续",
            "尾声",
        ];
        var endings = new[]
        {
            LineEnding.CrLf,
            LineEnding.Lf,
            LineEnding.CrLf,
            LineEnding.Lf,
            LineEnding.CrLf,
            LineEnding.None,
        };
        var lines = new FakeLinesWithEndings(text, endings);

        IReadOnlyList<NovelChapter> chapters = NovelChapterIndexBuilder.Build(lines, documentRevision: 7);

        Assert.Equal(4, chapters.Count);
        Assert.Equal(new[] { "第1章 开始", "第一章：第二卷的第一章", "第二章 继续", "尾声" },
            chapters.Select(chapter => chapter.Title));
        Assert.Equal(new[] { 1, 2, 3, 4 }, chapters.Select(chapter => chapter.ChapterNumber));
        Assert.All(chapters, chapter => Assert.Equal(7, chapter.DocumentRevision));

        long secondHeading = text[0].Length + 2 + text[1].Length + 1 + text[2].Length + 2;
        long thirdHeading = secondHeading + text[3].Length + 1;
        long finalHeading = thirdHeading + text[4].Length + 2;

        Assert.Equal(text[0].Length + 2, chapters[0].StartOffset);
        Assert.Equal(secondHeading, chapters[1].StartOffset);
        Assert.Equal(thirdHeading, chapters[2].StartOffset);
        Assert.Equal(finalHeading, chapters[3].StartOffset);
        Assert.Equal(chapters[1].StartOffset, chapters[0].EndOffset);
        Assert.Equal(chapters[2].StartOffset, chapters[1].EndOffset);
        Assert.Equal(chapters[3].StartOffset, chapters[2].EndOffset);
        Assert.Equal(text.Sum(line => (long)line.Length)
            + endings.Sum(ending => ending switch
            {
                LineEnding.CrLf => 2,
                LineEnding.Lf => 1,
                _ => 0,
            }), chapters[^1].EndOffset);
    }

    [Fact]
    public void Build_ReturnsEmptyWhenNoChapterHeadingsAreFound()
    {
        var lines = new FakeLines("普通正文", "我在第十章的时候想起了往事", "另一段正文");

        Assert.Empty(NovelChapterIndexBuilder.Build(lines));
    }

    [Fact]
    public void Build_ObservesCancellationDuringLargeScans()
    {
        var lines = new FakeLines(Enumerable.Repeat("普通正文", 100).ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NovelChapterIndexBuilder.Build(lines, cancellationToken: cancellation.Token));
    }
}
