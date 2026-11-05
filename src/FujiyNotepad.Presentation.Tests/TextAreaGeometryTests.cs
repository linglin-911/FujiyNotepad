using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests;

public sealed class TextAreaGeometryTests
{
    [Fact]
    public void FromLayoutBounds_UsesTheOriginalLayoutCoordinatesWithoutCaretRemapping()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 20, Baseline: 44, Height: 30);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromLayoutBounds(
            row, layoutBoundsTop: 24, layoutBoundsHeight: 15.5);

        Assert.Equal(24, geometry.TextTop);
        Assert.Equal(39.5, geometry.TextBottom);
        Assert.Equal(22.5, geometry.SelectionTop);
        Assert.Equal(18.5, geometry.SelectionHeight);
        Assert.Equal(40.375, geometry.AnnotationUnderlineY);
        Assert.Equal(1.25, geometry.AnnotationUnderlineThickness);
        Assert.True(
            geometry.AnnotationUnderlineY - geometry.AnnotationUnderlineThickness / 2
            > geometry.TextBottom);
        Assert.True(
            geometry.AnnotationUnderlineY + geometry.AnnotationUnderlineThickness / 2
            <= row.Top + row.Height);
    }

    [Fact]
    public void FromLayoutBounds_DoesNotMoveTextBoundsWhenLineSpacingChanges()
    {
        var compactRow = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 30, Height: 25);
        var spaciousRow = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 42, Height: 40);

        TextLayoutVerticalGeometry compact = TextAreaGeometry.FromLayoutBounds(
            compactRow, layoutBoundsTop: 12, layoutBoundsHeight: 14);
        TextLayoutVerticalGeometry spacious = TextAreaGeometry.FromLayoutBounds(
            spaciousRow, layoutBoundsTop: 12, layoutBoundsHeight: 14);

        Assert.Equal(compact.TextTop, spacious.TextTop);
        Assert.Equal(compact.TextBottom, spacious.TextBottom);
        Assert.Equal(compact.SelectionTop, spacious.SelectionTop);
        Assert.Equal(compact.SelectionHeight, spacious.SelectionHeight);
        Assert.Equal(compact.AnnotationUnderlineY, spacious.AnnotationUnderlineY);
    }

    [Fact]
    public void FromLayoutBounds_ClampsDecorativePaddingAndUnderlineToAvailableRowSpace()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 26, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromLayoutBounds(
            row, layoutBoundsTop: 11, layoutBoundsHeight: 18.5);

        Assert.Equal(11, geometry.TextTop);
        Assert.Equal(29.5, geometry.TextBottom);
        Assert.Equal(10, geometry.SelectionTop);
        Assert.Equal(20, geometry.SelectionHeight);
        Assert.Equal(0, geometry.AnnotationUnderlineThickness);
        Assert.False(geometry.HasAnnotationUnderline);
    }

    [Fact]
    public void FromLayoutBounds_NeverClipsTextBoundsThatExtendOutsideTheCaretRowInterval()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 26, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromLayoutBounds(
            row, layoutBoundsTop: 9, layoutBoundsHeight: 22);

        Assert.Equal(9, geometry.TextTop);
        Assert.Equal(31, geometry.TextBottom);
        Assert.Equal(9, geometry.SelectionTop);
        Assert.Equal(22, geometry.SelectionHeight);
        Assert.False(geometry.HasAnnotationUnderline);
    }

    [Fact]
    public void FromLayoutBounds_RejectsInvalidBoundsWithoutInventingAnUnderline()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 26, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromLayoutBounds(
            row, layoutBoundsTop: 0, layoutBoundsHeight: 0);

        Assert.Equal(0, geometry.SelectionHeight);
        Assert.False(geometry.HasAnnotationUnderline);
    }
}
