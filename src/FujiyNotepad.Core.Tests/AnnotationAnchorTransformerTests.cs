using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class AnnotationAnchorTransformerTests
{
    private static AnnotationAnchor Anchor(long start, long end, bool orphaned = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), start, end, "quote", IsOrphaned: orphaned);

    [Fact]
    public void InsertBeforeAnchor_ShiftsBothBoundaries()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(20, 0, 5));
        Assert.Equal(new AnchorTransformResult(105, 205, false), result);
    }

    [Fact]
    public void InsertAtAnchorStart_IsBeforeAnchoredText()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(100, 0, 7));
        Assert.Equal(new AnchorTransformResult(107, 207, false), result);
    }

    [Fact]
    public void InsertInsideAnchor_ExpandsEndAndAllowsOverlap()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(150, 0, 7));
        Assert.Equal(new AnchorTransformResult(100, 207, false), result);
    }

    [Fact]
    public void InsertAtAnchorEnd_DoesNotExpandAnchor()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(200, 0, 7));
        Assert.Equal(new AnchorTransformResult(100, 200, false), result);
    }

    [Fact]
    public void DeleteBeforeAnchor_ShiftsBothBoundariesLeft()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(20, 10, 0));
        Assert.Equal(new AnchorTransformResult(90, 190, false), result);
    }

    [Fact]
    public void DeleteInsideAnchor_ShrinksRange()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(130, 20, 0));
        Assert.Equal(new AnchorTransformResult(100, 180, false), result);
    }

    [Fact]
    public void ReplaceInsideAnchor_IncludesReplacementText()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(130, 20, 8));
        Assert.Equal(new AnchorTransformResult(100, 188, false), result);
    }

    [Fact]
    public void DeleteWholeAnchor_MarksItOrphaned()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(80, 150, 0));
        Assert.Equal(new AnchorTransformResult(80, 80, true), result);
    }

    [Fact]
    public void ReplaceWholeAnchor_MarksItOrphanedRatherThanPointingAtUnrelatedText()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 200), new DocumentEdit(100, 100, 50));
        Assert.Equal(new AnchorTransformResult(100, 150, true), result);
    }

    [Fact]
    public void ExistingOrphanRemainsOrphanedAndTracksLaterEdits()
    {
        var result = AnnotationAnchorTransformer.Apply(Anchor(100, 100, orphaned: true), new DocumentEdit(10, 0, 4));
        Assert.Equal(new AnchorTransformResult(104, 104, true), result);
    }

    [Fact]
    public void InvalidEditIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnnotationAnchorTransformer.Apply(Anchor(10, 20), new DocumentEdit(-1, 0, 1)));
    }
}
