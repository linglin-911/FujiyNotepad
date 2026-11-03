using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class DocumentDiffMapperTests
{
    [Fact]
    public void InsertBeforeAnchor_ShiftsItByInsertedLength()
    {
        var hunks = new[] { new DocumentDiffHunk(0, 0, 0, 5, DocumentDiffKind.Inserted) };
        AnchorRemapResult result = DocumentDiffMapper.MapRange(20, 30, hunks);
        Assert.Equal((25L, 35L), (result.StartOffset, result.EndOffset));
        Assert.Equal(AnchorRemapStatus.Mapped, result.Status);
    }

    [Fact]
    public void InsertAtAnchorStart_IsBeforeAnchor_ButInsertAtEndIsAfterAnchor()
    {
        // Old range is empty; new range spans the three inserted characters [position, position + 3).
        var atStart = new[] { new DocumentDiffHunk(10, 10, 10, 13, DocumentDiffKind.Inserted) };
        var atEnd = new[] { new DocumentDiffHunk(20, 20, 20, 23, DocumentDiffKind.Inserted) };

        AnchorRemapResult startResult = DocumentDiffMapper.MapRange(10, 20, atStart);
        AnchorRemapResult endResult = DocumentDiffMapper.MapRange(10, 20, atEnd);

        Assert.Equal((13L, 23L), (startResult.StartOffset, startResult.EndOffset));
        Assert.Equal((10L, 20L), (endResult.StartOffset, endResult.EndOffset));
        Assert.Equal(AnchorRemapStatus.Mapped, startResult.Status);
        Assert.Equal(AnchorRemapStatus.Mapped, endResult.Status);
    }

    [Fact]
    public void ChangeInsideAnchor_RequiresQuoteAndContextReanchoring()
    {
        var hunks = new[] { new DocumentDiffHunk(14, 17, 14, 18, DocumentDiffKind.Replaced) };
        AnchorRemapResult result = DocumentDiffMapper.MapRange(10, 20, hunks);

        Assert.Equal(AnchorRemapStatus.NeedsReview, result.Status);
        Assert.Contains("re-anchoring", result.Reason);
    }

    [Fact]
    public void CompleteDeletion_OrphansAnchorButChapterRequiresReview()
    {
        var hunks = new[] { new DocumentDiffHunk(10, 30, 10, 10, DocumentDiffKind.Deleted) };
        AnchorRemapResult anchor = DocumentDiffMapper.MapRange(12, 20, hunks);
        AnchorRemapResult chapter = DocumentDiffMapper.MapChapterRange(12, 20, hunks);

        Assert.Equal(AnchorRemapStatus.Orphaned, anchor.Status);
        Assert.Equal(AnchorRemapStatus.NeedsReview, chapter.Status);
    }

    [Fact]
    public void OverlappingAnchorsMapIndependentlyThroughSameDiff()
    {
        var hunks = new[] { new DocumentDiffHunk(0, 0, 0, 4, DocumentDiffKind.Inserted) };
        AnchorRemapResult broad = DocumentDiffMapper.MapRange(10, 40, hunks);
        AnchorRemapResult nested = DocumentDiffMapper.MapRange(20, 25, hunks);
        AnchorRemapResult crossing = DocumentDiffMapper.MapRange(35, 50, hunks);

        Assert.Equal((14L, 44L), (broad.StartOffset, broad.EndOffset));
        Assert.Equal((24L, 29L), (nested.StartOffset, nested.EndOffset));
        Assert.Equal((39L, 54L), (crossing.StartOffset, crossing.EndOffset));
    }

    [Fact]
    public void MultipleChangesAccumulateOffsetDeltas()
    {
        var hunks = new[]
        {
            new DocumentDiffHunk(5, 8, 5, 10, DocumentDiffKind.Replaced),
            new DocumentDiffHunk(20, 25, 22, 22, DocumentDiffKind.Deleted)
        };

        AnchorRemapResult result = DocumentDiffMapper.MapRange(30, 40, hunks);
        Assert.Equal((27L, 37L), (result.StartOffset, result.EndOffset));
        Assert.Equal(AnchorRemapStatus.Mapped, result.Status);
    }

    [Fact]
    public void RejectsOverlappingOrOutOfOrderDiffHunks()
    {
        var hunks = new[]
        {
            new DocumentDiffHunk(10, 20, 10, 20, DocumentDiffKind.Replaced),
            new DocumentDiffHunk(15, 25, 20, 30, DocumentDiffKind.Replaced)
        };
        Assert.Throws<ArgumentException>(() => DocumentDiffMapper.MapRange(30, 40, hunks));
    }
}
