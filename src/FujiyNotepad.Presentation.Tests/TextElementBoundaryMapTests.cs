using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests;

public sealed class TextElementBoundaryMapTests
{
    [Fact]
    public void Boundaries_DoNotSplitSupplementaryCharacters()
    {
        var map = new TextElementBoundaryMap("A😀B");

        Assert.Equal(1, map.SnapToPreviousBoundary(2));
        Assert.Equal(3, map.SnapToNextBoundary(2));
        Assert.Equal(1, map.SnapToNearestBoundary(2));
    }

    [Fact]
    public void Boundaries_KeepCombiningSequenceTogether()
    {
        var map = new TextElementBoundaryMap("e\u0301x");

        Assert.Equal(0, map.SnapToPreviousBoundary(1));
        Assert.Equal(2, map.SnapToNextBoundary(1));
        Assert.Equal((0, 2), map.ExpandRange(1, 1));
    }

    [Fact]
    public void ExpandRange_ExpandsPartialSurrogateRangeToWholeCharacter()
    {
        var map = new TextElementBoundaryMap("A😀B");

        Assert.Equal((1, 2), map.ExpandRange(2, 1));
    }

    [Fact]
    public void PreviousAndNext_ReturnAdjacentValidBoundaries()
    {
        var map = new TextElementBoundaryMap("甲😀乙");

        Assert.Equal(1, map.SnapToPreviousBoundary(3));
        Assert.Equal(3, map.SnapToNextBoundary(3));
        Assert.Equal(5, map.SnapToNextBoundary(4));
    }

    [Fact]
    public void EmptySource_HasSingleZeroBoundary()
    {
        var map = new TextElementBoundaryMap(string.Empty);

        Assert.Equal(0, map.SnapToPreviousBoundary(0));
        Assert.Equal(0, map.SnapToNextBoundary(0));
        Assert.Equal((0, 0), map.ExpandRange(0, 10));
    }

    [Fact]
    public void ExpandRange_ClampsOverflowingEndWithoutIntegerOverflow()
    {
        var map = new TextElementBoundaryMap("abc");

        Assert.Equal((1, 2), map.ExpandRange(1, int.MaxValue));
    }
}
