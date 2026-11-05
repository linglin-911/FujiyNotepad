using FujiyNotepad.Presentation;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Presentation.Tests;

public sealed class TextLayoutEngineAnnotationMarkerTests
{
    [Fact]
    public void GetDisplayIndexForColumn_MapsSourceColumnsAcrossTabsAndWideCharacters()
    {
        var engine = new TextLayoutEngine();
        engine.SetProvider(new FakeLines("A\t中B"));

        Assert.Equal(1, engine.GetDisplayIndexForColumn(0, 1));
        Assert.Equal(4, engine.GetDisplayIndexForColumn(0, 2));
        Assert.Equal(5, engine.GetDisplayIndexForColumn(0, 3));
        Assert.Equal(6, engine.GetDisplayIndexForColumn(0, 4));
    }

    [Fact]
    public void CenterCaretVertically_PlacesTargetLineNearViewportMiddle()
    {
        var engine = new TextLayoutEngine();
        engine.SetProvider(new FakeLines(Enumerable.Range(0, 30).Select(index => $"line {index}").ToArray()));
        engine.SetMetrics(charWidthPx: 8, lineHeightPx: 10);
        engine.ViewportHeight = 50;

        engine.GoToLineColumn(10, 0);
        engine.CenterCaretVertically();

        Assert.Equal(8, engine.FirstVisibleLine);
        Assert.Equal(10, engine.CaretPosition.Line);
    }

    [Fact]
    public void GetDisplayIndexForColumn_RejectsLineOutsideCurrentDocument()
    {
        var engine = new TextLayoutEngine();
        engine.SetProvider(new FakeLines("one"));

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.GetDisplayIndexForColumn(1, 0));
    }
}
