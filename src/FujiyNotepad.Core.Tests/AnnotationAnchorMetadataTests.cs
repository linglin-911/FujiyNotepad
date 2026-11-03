using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class AnnotationAnchorMetadataTests
{
    [Fact]
    public void CreateAndRestore_UsesNormalizedChapterRelativeOffsetsAcrossTitleAndLineEndingChanges()
    {
        Guid chapterId = Guid.NewGuid();
        var oldLines = new TestLines(
            ["第1章 短", "前置内容", "目标摘录", "后置内容", "第2章"],
            [LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.None]);
        var oldMap = new DocumentTextOffsetMap(oldLines);
        long oldStart = oldMap.GetLineStartOffset(2);
        long oldEnd = oldMap.GetOffset(2, "目标摘录".Length);
        long oldChapterEnd = oldMap.GetLineStartOffset(4);
        var oldChapter = new NovelChapter(
            chapterId, "第1章 短", 0, oldChapterEnd, 1, 1, true,
            oldMap.GetLineStartOffset(1), "UNCHANGED-BODY");

        AnnotationAnchorMetadata metadata = AnnotationAnchorMetadataBuilder.Create(
            oldChapter, oldLines, oldMap, oldStart, oldEnd);
        var anchor = new AnnotationAnchor(
            Guid.NewGuid(), Guid.NewGuid(), oldStart, oldEnd, "目标摘录",
            chapterId, false, 1,
            metadata.RelativeStartOffset,
            metadata.RelativeEndOffset,
            metadata.ChapterBodyStartRelativeOffset,
            metadata.ChapterBodySha256,
            metadata.PrefixContext,
            metadata.SuffixContext);

        var currentLines = new TestLines(
            ["第1章 一个明显更长的标题", "前置内容", "目标摘录", "后置内容", "第2章"],
            [LineEnding.CrLf, LineEnding.CrLf, LineEnding.CrLf, LineEnding.CrLf, LineEnding.None]);
        var currentMap = new DocumentTextOffsetMap(currentLines);
        long currentChapterEnd = currentMap.GetLineStartOffset(4);
        var currentChapter = new NovelChapter(
            chapterId, "第1章 一个明显更长的标题", 0, currentChapterEnd, 1, 2, true,
            currentMap.GetLineStartOffset(1), "UNCHANGED-BODY");

        bool restored = AnnotationAnchorMetadataBuilder.TryRestoreUnchangedChapterAnchor(
            anchor, currentChapter, currentLines, currentMap, out AnnotationAnchorQuoteMatch match);

        Assert.True(restored);
        Assert.Equal(currentMap.GetLineStartOffset(2), match.StartOffset);
        Assert.Equal(currentMap.GetOffset(2, "目标摘录".Length), match.EndOffset);
    }

    [Fact]
    public void TryRestoreUnchangedChapterAnchor_RefusesWhenBodyFingerprintChanged()
    {
        Guid chapterId = Guid.NewGuid();
        var lines = new TestLines(
            ["第1章", "正文", "目标摘录", "第2章"],
            [LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.None]);
        var map = new DocumentTextOffsetMap(lines);
        long chapterEnd = map.GetLineStartOffset(3);
        long start = map.GetLineStartOffset(2);
        long end = map.GetOffset(2, "目标摘录".Length);
        var chapter = new NovelChapter(chapterId, "第1章", 0, chapterEnd, 1, 2, true,
            map.GetLineStartOffset(1), "NEW-HASH");
        var anchor = new AnnotationAnchor(
            Guid.NewGuid(), Guid.NewGuid(), start, end, "目标摘录",
            chapterId, false, 1,
            RelativeStartOffset: 5,
            RelativeEndOffset: 10,
            ChapterBodyStartRelativeOffset: 4,
            ChapterBodySha256: "OLD-HASH",
            PrefixContext: "正文\n",
            SuffixContext: "\n");

        Assert.False(AnnotationAnchorMetadataBuilder.TryRestoreUnchangedChapterAnchor(
            anchor, chapter, lines, map, out _));
    }

    [Fact]
    public void ContextMatcher_SelectsTheOccurrenceWithMatchingBeforeAndAfterText()
    {
        Guid chapterId = Guid.NewGuid();
        var lines = new TestLines(
            ["第1章", "前文甲乙丙丁", "目标摘录", "后文甲乙丙丁", "中间内容",
             "前文戊己庚辛", "目标摘录", "后文壬癸子丑", "第2章"],
            Enumerable.Repeat(LineEnding.Lf, 8).Append(LineEnding.None).ToArray());
        var map = new DocumentTextOffsetMap(lines);
        long chapterEnd = map.GetLineStartOffset(8);
        long firstStart = map.GetLineStartOffset(2);
        long firstEnd = map.GetOffset(2, "目标摘录".Length);
        var chapter = new NovelChapter(chapterId, "第1章", 0, chapterEnd, 1, 2, true,
            map.GetLineStartOffset(1), "CHANGED-HASH");
        AnnotationAnchorMetadata original = AnnotationAnchorMetadataBuilder.Create(
            chapter, lines, map, firstStart, firstEnd);
        var anchor = new AnnotationAnchor(
            Guid.NewGuid(), Guid.NewGuid(), firstStart, firstEnd, "目标摘录",
            chapterId, false, 1,
            original.RelativeStartOffset,
            original.RelativeEndOffset,
            original.ChapterBodyStartRelativeOffset,
            "OLD-HASH",
            original.PrefixContext,
            original.SuffixContext);

        AnnotationAnchorQuoteMatch? match = AnnotationAnchorContextMatcher.FindBestMatchInRange(
            lines, map, anchor, chapter);

        Assert.True(match.HasValue);
        Assert.Equal(new AnnotationAnchorQuoteMatch(firstStart, firstEnd), match.Value);
    }

    [Fact]
    public void ContextMatcher_RefusesRepeatedOccurrencesWithIndistinguishableContextAndPosition()
    {
        string repeatedPrefix = new string('前', 80);
        string repeatedSuffix = new string('后', 80);
        Guid chapterId = Guid.NewGuid();
        var lines = new TestLines(
            ["第1章", repeatedPrefix, "目标摘录", repeatedSuffix, "间隔",
             repeatedPrefix, "目标摘录", repeatedSuffix, "第2章"],
            Enumerable.Repeat(LineEnding.Lf, 8).Append(LineEnding.None).ToArray());
        var map = new DocumentTextOffsetMap(lines);
        long chapterEnd = map.GetLineStartOffset(8);
        long firstStart = map.GetLineStartOffset(2);
        long firstEnd = map.GetOffset(2, "目标摘录".Length);
        var chapter = new NovelChapter(chapterId, "第1章", 0, chapterEnd, 1, 2, true,
            map.GetLineStartOffset(1), "CHANGED-HASH");
        AnnotationAnchorMetadata original = AnnotationAnchorMetadataBuilder.Create(
            chapter, lines, map, firstStart, firstEnd);
        var anchor = new AnnotationAnchor(
            Guid.NewGuid(), Guid.NewGuid(), firstStart, firstEnd, "目标摘录",
            chapterId, false, 1,
            original.RelativeStartOffset,
            original.RelativeEndOffset,
            original.ChapterBodyStartRelativeOffset,
            "OLD-HASH",
            original.PrefixContext,
            original.SuffixContext);

        Assert.Null(AnnotationAnchorContextMatcher.FindBestMatchInRange(
            lines, map, anchor, chapter));
    }

    [Fact]
    public void FindMatchesInRange_ReportsWhenTheCandidateListWasTruncated()
    {
        var lines = new TestLines(
            ["第1章", "重复", "重复", "重复", "第2章"],
            [LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.Lf, LineEnding.None]);
        var map = new DocumentTextOffsetMap(lines);
        long end = map.GetLineStartOffset(4);

        AnnotationAnchorQuoteSearchResult result = AnnotationAnchorQuoteLocator.FindMatchesInRange(
            lines, "重复", map, 0, end, maximumMatches: 2);

        Assert.Equal(2, result.Matches.Count);
        Assert.True(result.IsTruncated);
    }

    private sealed class TestLines(string[] values, LineEnding[] endings)
        : ILineSource, ILineEndingSource
    {
        public int LineCount => values.Length;
        public string GetLine(int lineIndex) => values[lineIndex];
        public string GetLineUncached(int lineIndex) => values[lineIndex];
        public LineEnding GetLineEnding(int lineIndex) => endings[lineIndex];
    }
}
