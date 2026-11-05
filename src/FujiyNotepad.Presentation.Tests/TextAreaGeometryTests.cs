using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests;

public sealed class TextAreaGeometryTests
{
    [Fact]
    public void FromDrawBounds_AlignsTemporaryMeasurementToTheOriginalVisualRowCaretOrigin()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 20, Baseline: 44, Height: 30);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromDrawBounds(
            row, measurementCaretTop: 3.25, drawTop: 7.25, drawHeight: 15.5);

        Assert.Equal(24, geometry.GlyphTop);
        Assert.Equal(39.5, geometry.GlyphBottom);
        Assert.Equal(22.5, geometry.SelectionTop);
        Assert.Equal(18.5, geometry.SelectionHeight);
        Assert.Equal(40.375, geometry.AnnotationUnderlineY);
        Assert.Equal(1.25, geometry.AnnotationUnderlineThickness);
        Assert.True(
            geometry.AnnotationUnderlineY - geometry.AnnotationUnderlineThickness / 2
            > geometry.GlyphBottom);
        Assert.True(
            geometry.AnnotationUnderlineY + geometry.AnnotationUnderlineThickness / 2
            <= row.Top + row.Height);
    }

    [Fact]
    public void FromDrawBounds_DoesNotChangeGlyphGeometryWhenLineSpacingChanges()
    {
        var compactRow = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 30, Height: 25);
        var spaciousRow = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 42, Height: 40);

        TextLayoutVerticalGeometry compact = TextAreaGeometry.FromDrawBounds(
            compactRow, measurementCaretTop: 2, drawTop: 7, drawHeight: 14);
        TextLayoutVerticalGeometry spacious = TextAreaGeometry.FromDrawBounds(
            spaciousRow, measurementCaretTop: 2, drawTop: 7, drawHeight: 14);

        Assert.Equal(compact.GlyphTop, spacious.GlyphTop);
        Assert.Equal(compact.GlyphBottom, spacious.GlyphBottom);
        Assert.Equal(compact.SelectionTop, spacious.SelectionTop);
        Assert.Equal(compact.SelectionHeight, spacious.SelectionHeight);
        Assert.Equal(compact.AnnotationUnderlineY, spacious.AnnotationUnderlineY);
    }

    [Fact]
    public void FromDrawBounds_ClampsDecorativePaddingAndUnderlineToAvailableRowSpace()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 26, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromDrawBounds(
            row, measurementCaretTop: 0, drawTop: 1, drawHeight: 18.5);

        Assert.Equal(11, geometry.GlyphTop);
        Assert.Equal(29.5, geometry.GlyphBottom);
        Assert.Equal(10, geometry.SelectionTop);
        Assert.Equal(20, geometry.SelectionHeight);
        Assert.Equal(0, geometry.AnnotationUnderlineThickness);
        Assert.False(geometry.HasAnnotationUnderline);
    }

    [Fact]
    public void FromDrawBounds_NeverClipsGlyphInkThatExtendsOutsideTheCaretRowInterval()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 26, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromDrawBounds(
            row, measurementCaretTop: 0, drawTop: -1, drawHeight: 22);

        Assert.Equal(9, geometry.GlyphTop);
        Assert.Equal(31, geometry.GlyphBottom);
        Assert.Equal(9, geometry.SelectionTop);
        Assert.Equal(22, geometry.SelectionHeight);
        Assert.False(geometry.HasAnnotationUnderline);
    }

    [Fact]
    public void FromDrawBounds_RejectsInvalidDrawBoundsWithoutInventingAnUnderline()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 26, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromDrawBounds(
            row, measurementCaretTop: 0, drawTop: 0, drawHeight: 0);

        Assert.Equal(0, geometry.SelectionHeight);
        Assert.False(geometry.HasAnnotationUnderline);
    }
}
