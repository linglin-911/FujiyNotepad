using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests;

public sealed class TextAreaGeometryTests
{
    [Fact]
    public void FromBaseline_CreatesOneCompactTextBandIndependentOfLineSpacing()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 20, Baseline: 44, Height: 30);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromBaseline(row, fontSize: 20);

        Assert.Equal(26, geometry.TextTop);
        Assert.Equal(47, geometry.TextBottom);
        Assert.Equal(21, geometry.TextHeight);
        Assert.True(geometry.HasAnnotationUnderline);
        Assert.Equal(47.75, geometry.AnnotationUnderlineY);
        Assert.Equal(1.0, geometry.AnnotationUnderlineThickness);
    }

    [Fact]
    public void FromBaseline_KeepsTextBandHeightConstantWhenLineSpacingChanges()
    {
        var compactRow = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 25, Height: 25);
        var spaciousRow = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 40, Height: 40);

        TextLayoutVerticalGeometry compact = TextAreaGeometry.FromBaseline(compactRow, fontSize: 20);
        TextLayoutVerticalGeometry spacious = TextAreaGeometry.FromBaseline(spaciousRow, fontSize: 20);

        Assert.Equal(21, compact.TextHeight);
        Assert.Equal(compact.TextHeight, spacious.TextHeight);
        Assert.Equal(compact.TextBottom - compact.TextTop, spacious.TextBottom - spacious.TextTop);
    }

    [Fact]
    public void FromBaseline_AlwaysKeepsAnnotationUnderlineVisibleWhenLineSpacingIsTight()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 18, Height: 10);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromBaseline(row, fontSize: 10);

        Assert.Equal(9, geometry.TextTop);
        Assert.Equal(19.5, geometry.TextBottom);
        Assert.True(geometry.HasAnnotationUnderline);
        Assert.Equal(19, geometry.AnnotationUnderlineY);
        Assert.Equal(1.0, geometry.AnnotationUnderlineThickness);
    }

    [Fact]
    public void FromBaseline_DoesNotClipTheTextBandToTheCaretRow()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: 18, Height: 8);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromBaseline(row, fontSize: 10);

        Assert.Equal(9, geometry.TextTop);
        Assert.Equal(19.5, geometry.TextBottom);
        Assert.True(geometry.HasAnnotationUnderline);
    }

    [Fact]
    public void FromBaseline_RejectsInvalidMetrics()
    {
        var row = new TextLayoutVisualLine(0, 12, Top: 10, Baseline: double.NaN, Height: 20);

        TextLayoutVerticalGeometry geometry = TextAreaGeometry.FromBaseline(row, fontSize: 10);

        Assert.Equal(0, geometry.TextHeight);
        Assert.False(geometry.HasAnnotationUnderline);
    }
}
