using FujiyNotepad.Presentation;
using FujiyNotepad.TestSupport;

namespace FujiyNotepad.Presentation.Tests;

public sealed class ProportionalTextLayoutEngineTests
{
    [Fact]
    public void GetVisibleLines_UsesProviderVisualRowsAndPreservesSourceRanges()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 45);
        engine.WordWrap = true;

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(row.UsesProportionalLayout));
        Assert.Equal(new[] { "ab", "cd", "ef" }, rows.Select(row => row.Display));
        Assert.Equal((0, 2), (rows[0].SourceStartIndex, rows[0].SourceEndIndex));
        Assert.Equal((2, 4), (rows[1].SourceStartIndex, rows[1].SourceEndIndex));
        Assert.Equal((4, 6), (rows[2].SourceStartIndex, rows[2].SourceEndIndex));
        Assert.Equal(new[] { 0d, 20d, 40d }, rows.Select(row => row.Y));
    }

    [Fact]
    public void HitTest_UsesProviderCaretGeometryAndCharacterRanges()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 40);
        engine.WordWrap = true;

        Assert.Equal(new TextPosition(0, 2), engine.HitTest(23, 5));
        Assert.Equal(new TextPosition(0, 1), engine.HitTestCharacter(23, 10));
    }

    [Fact]
    public void HitTest_MapsRightClickInBlankAreaToTheNearestSourceBoundary()
    {
        var engine = NewEngine(new FakeLines("chapter title"), viewportWidth: 200, viewportHeight: 40);

        TextPosition position = engine.HitTest(190, 5);

        Assert.Equal(new TextPosition(0, 13), position);
    }

    [Fact]
    public void HitTestCharacter_MapsWrappedVisualRowsBackToOriginalSourceColumns()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition position = engine.HitTestCharacter(18, 30, out bool hitCharacter);

        Assert.True(hitCharacter);
        Assert.Equal(new TextPosition(0, 3), position);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(16, true)]
    public void HitTestCharacter_ReportsWhichHalfOfTheCharacterWasHit(
        double x, bool expectedTrailingHit)
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        TextPosition position = engine.HitTestCharacter(
            x, 10, out bool hitCharacter, out int sourceLength, out bool isTrailingHit);

        Assert.True(hitCharacter);
        Assert.Equal(new TextPosition(0, 0), position);
        Assert.Equal(1, sourceLength);
        Assert.Equal(expectedTrailingHit, isTrailingHit);
    }

    [Fact]
    public void HitTestCharacter_PreservesTheFullSourceRangeOfAShapingCluster()
    {
        var engine = NewEngine(new FakeLines("fi"), viewportWidth: 100, viewportHeight: 40);
        engine.WordWrap = true;

        TextPosition position = engine.HitTestCharacter(
            8, 10, out bool hitCharacter, out int sourceLength);

        Assert.True(hitCharacter);
        Assert.Equal(new TextPosition(0, 0), position);
        Assert.Equal(2, sourceLength);
    }

    [Fact]
    public void HitTestCharacter_DistinguishesTextFromNearestBlankBoundaries()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition beforeText = engine.HitTestCharacter(-5, 30, out bool beforeHit);
        TextPosition afterText = engine.HitTestCharacter(100, 30, out bool afterHit);

        Assert.False(beforeHit);
        Assert.False(afterHit);
        Assert.Equal(new TextPosition(0, 2), beforeText);
        Assert.Equal(new TextPosition(0, 4), afterText);

        // Context-menu positioning still uses the nearest source boundary when the pointer is in blank space.
        Assert.Equal(new TextPosition(0, 2), engine.HitTestCharacter(-5, 30));
    }

    [Fact]
    public void AnnotationMarkerHitTest_UsesTheWholeSelectionRectangleNotGlyphPixels()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 4) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(25, 10, markers, out TextPosition position);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 2), position);
    }

    [Fact]
    public void AnnotationMarkerHitTest_PreservesTheFullShapingClusterAndHitSide()
    {
        var engine = NewEngine(new FakeLines("fi"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 0, 2) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(
            16, 10, markers, out TextPosition position,
            out int sourceLength, out bool isTrailingHit);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 0), position);
        Assert.Equal(2, sourceLength);
        Assert.True(isTrailingHit);
    }

    [Fact]
    public void AnnotationMarkerHitTest_UsesTheExactTextBandWithoutVerticalPadding()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 4) },
        };

        bool inside = engine.TryHitTestAnnotationMarkers(25, 6.5, markers, out TextPosition position);
        bool above = engine.TryHitTestAnnotationMarkers(25, 5.5, markers, out _);

        Assert.True(inside);
        Assert.Equal(new TextPosition(0, 2), position);
        Assert.False(above);
    }

    [Fact]
    public void TextDragEntry_FromBlankSpaceUsesTheFirstTextRectangleBoundaryAsAnchor()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        bool hit = engine.TryHitTestTextRectangleEntry(
            25, 0, 25, 10,
            out TextPosition position,
            out int sourceLength,
            out bool isTrailingHit,
            out double entryX,
            out double entryY);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 1), position);
        Assert.Equal(1, sourceLength);
        Assert.True(isTrailingHit);
        Assert.Equal(25, entryX);
        Assert.Equal(6, entryY);
    }

    [Fact]
    public void TextDragEntry_RecordsTheBoundaryOnASubThresholdPointerMove()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        bool hit = engine.TryHitTestTextRectangleEntry(
            7.5, 10, 8.5, 10,
            out TextPosition position,
            out int sourceLength,
            out bool isTrailingHit,
            out double entryX,
            out double entryY);

        // This one-DIP segment is shorter than the UI drag threshold, but its first text-boundary
        // intersection still has to be remembered until a later movement crosses that threshold.
        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 0), position);
        Assert.Equal(1, sourceLength);
        Assert.False(isTrailingHit);
        Assert.Equal(8, entryX);
        Assert.Equal(10, entryY);
    }

    [Fact]
    public void TextDragEntry_FromHorizontalBlankUsesTheFirstTextRectangleEdge()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        bool hit = engine.TryHitTestTextRectangleEntry(
            0, 10, 25, 10,
            out TextPosition position,
            out int sourceLength,
            out bool isTrailingHit,
            out double entryX,
            out double entryY);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 0), position);
        Assert.Equal(1, sourceLength);
        Assert.False(isTrailingHit);
        Assert.Equal(8, entryX);
        Assert.Equal(10, entryY);
    }

    [Fact]
    public void TextDragEntry_FromInterlineBlankStartsAtTheNextRowBoundary()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        bool hit = engine.TryHitTestTextRectangleEntry(
            25, 18, 25, 30,
            out TextPosition position,
            out int sourceLength,
            out _,
            out double entryX,
            out double entryY);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 3), position);
        Assert.Equal(1, sourceLength);
        Assert.Equal(25, entryX);
        Assert.Equal(26, entryY);
    }

    [Fact]
    public void TextDragEntry_DoesNotCreateAnAnchorWhenTheSegmentMissesEveryTextRectangle()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        bool hit = engine.TryHitTestTextRectangleEntry(
            0, 0, 5, 0,
            out _, out _, out _, out _, out _);

        Assert.False(hit);
    }

    [Fact]
    public void AnnotationMarkerHitTest_DoesNotHitOutsideTheAnnotatedRectangle()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 4) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(60, 10, markers, out _);

        Assert.False(hit);
    }

    [Fact]
    public void AnnotationMarkerHitTest_DoesNotHitInLineSpacing()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 4) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(25, 19, markers, out _);

        Assert.False(hit);
    }

    [Fact]
    public void HitTestCharacter_UsesTheExactHorizontalTextRectangleWithoutPadding()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        TextPosition outside = engine.HitTestCharacter(7.5, 10, out bool outsideHit);
        TextPosition inside = engine.HitTestCharacter(8.5, 10, out bool insideHit);

        Assert.False(outsideHit);
        Assert.True(insideHit);
        Assert.Equal(new TextPosition(0, 0), inside);
        Assert.Equal(new TextPosition(0, 0), outside);
    }

    [Fact]
    public void AnnotationMarkerHitTest_MapsWrappedRowToItsOriginalSourceRange()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 5) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(15, 30, markers, out TextPosition position);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 3), position);
    }

    [Fact]
    public void AnnotationMarkerHitTest_ResolvesOverlappingRangesAtTheSharedSourcePosition()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new()
            {
                new TextAnnotationMarker(0, 1, 4),
                new TextAnnotationMarker(0, 2, 5),
            },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(35, 10, markers, out TextPosition position);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 3), position);
    }

    [Fact]
    public void HitTestCharacter_MapsNextLogicalLineToItsOwnSourcePosition()
    {
        var engine = NewEngine(new FakeLines("ab", "cd"), viewportWidth: 100, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition position = engine.HitTestCharacter(18, 30, out bool hitCharacter);

        Assert.True(hitCharacter);
        Assert.Equal(new TextPosition(1, 1), position);
    }

    [Fact]
    public void PointerDragAcrossSoftWrappedRows_PreservesOriginalSourceSelection()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition start = engine.HitTest(25, 5);
        TextPosition end = engine.HitTest(25, 25);
        engine.PointerPress(start, shift: false);
        engine.PointerDrag(end);

        Assert.Equal(new TextPosition(0, 2), engine.SelectionStart);
        Assert.Equal(new TextPosition(0, 4), engine.SelectionEnd);

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
        Assert.Equal(0, rows[0].SelectionLength);
        Assert.Equal(2, rows[1].SelectionStartIndex);
        Assert.Equal(2, rows[1].SelectionLength);
    }

    [Fact]
    public void PointerDragAcrossLogicalLines_IncludesTheSourceLineBreakInSelection()
    {
        var engine = NewEngine(new FakeLines("ab", "cd"), viewportWidth: 100, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition start = engine.HitTest(25, 5);
        TextPosition end = engine.HitTest(5, 25);
        engine.PointerPress(start, shift: false);
        engine.PointerDrag(end);

        Assert.Equal(new TextPosition(0, 2), engine.SelectionStart);
        Assert.Equal(new TextPosition(1, 0), engine.SelectionEnd);
        Assert.Equal(new SelectionStats(1, 2), engine.GetSelectionStats());
    }

    [Fact]
    public void PointerPressCharacter_DoesNotSelectUntilThePointerMoves()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: false);

        Assert.False(engine.HasSelection);
        Assert.Equal(new TextPosition(0, 1), engine.CaretPosition);
    }

    [Fact]
    public void PointerPressCharacter_UsesTheHitHalfAsTheInitialCaretBoundary()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: false);
        Assert.Equal(new TextPosition(0, 1), engine.CaretPosition);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: true, shift: false);
        Assert.Equal(new TextPosition(0, 2), engine.CaretPosition);
        Assert.False(engine.HasSelection);
    }

    [Fact]
    public void PointerPressCharacter_ShiftClickDoesNotCreateASelection()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: true);

        Assert.False(engine.HasSelection);
        Assert.Equal(new TextPosition(0, 0), engine.CaretPosition);
    }

    [Fact]
    public void PointerDragCharacter_IncludesBothEndpointCharactersWithoutAddingNeighbours()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 3), sourceLength: 1, isTrailingHit: true);

        Assert.Equal(0, engine.SelectionStart.Line);
        Assert.Equal(1, engine.SelectionStart.Column);
        Assert.Equal(0, engine.SelectionEnd.Line);
        Assert.Equal(4, engine.SelectionEnd.Column);
    }

    [Fact]
    public void PointerDragCharacter_IncludesBothEndpointCharactersWhenDraggingBackwards()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 3), sourceLength: 1, isTrailingHit: true, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false);

        Assert.Equal(0, engine.SelectionStart.Line);
        Assert.Equal(1, engine.SelectionStart.Column);
        Assert.Equal(0, engine.SelectionEnd.Line);
        Assert.Equal(4, engine.SelectionEnd.Column);
    }

    [Fact]
    public void PointerDragCharacter_SelectsTheStartingCharacterOnlyAfterCrossingItsMidpoint()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: true);
        Assert.Equal(new TextPosition(0, 1), engine.SelectionStart);
        Assert.Equal(new TextPosition(0, 2), engine.SelectionEnd);

        engine.PointerDragCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false);
        Assert.False(engine.HasSelection);
    }

    [Fact]
    public void PointerDragCharacter_ExcludesTheStartingCharacterWhenDraggingRightFromItsTrailingHalf()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: true, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 2), sourceLength: 1, isTrailingHit: false);
        Assert.False(engine.HasSelection);

        engine.PointerDragCharacter(
            new TextPosition(0, 2), sourceLength: 1, isTrailingHit: true);
        Assert.Equal(new TextPosition(0, 2), engine.SelectionStart);
        Assert.Equal(new TextPosition(0, 3), engine.SelectionEnd);
    }

    [Fact]
    public void PointerDragCharacter_IncludesTheStartingCharacterWhenDraggingLeftAcrossItsMidpoint()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 2), sourceLength: 1, isTrailingHit: true, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 2), sourceLength: 1, isTrailingHit: false);
        Assert.Equal(new TextPosition(0, 2), engine.SelectionStart);
        Assert.Equal(new TextPosition(0, 3), engine.SelectionEnd);

        engine.PointerDragCharacter(
            new TextPosition(0, 2), sourceLength: 1, isTrailingHit: true);
        Assert.False(engine.HasSelection);
    }

    [Fact]
    public void PointerDragCharacter_PreservesTheReportedShapingClusterLength()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 2, isTrailingHit: false, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 4), sourceLength: 1, isTrailingHit: true);

        Assert.Equal(1, engine.SelectionStart.Column);
        Assert.Equal(5, engine.SelectionEnd.Column);
    }

    [Fact]
    public void PointerDragCharacter_ReturningToTheAnchorSelectsOnlyTheAnchorCharacter()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: false);
        engine.PointerDragCharacter(
            new TextPosition(0, 3), sourceLength: 1, isTrailingHit: true);
        engine.PointerDragCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: true);

        Assert.Equal(1, engine.SelectionStart.Column);
        Assert.Equal(2, engine.SelectionEnd.Column);
    }

    [Fact]
    public void PointerDragBoundary_SelectsTheRestOfTheCurrentVisualRow()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);

        engine.PointerPressCharacter(
            new TextPosition(0, 1), sourceLength: 1, isTrailingHit: false, shift: false);
        engine.PointerDragBoundary(new TextPosition(0, 6));

        Assert.Equal(new TextPosition(0, 1), engine.SelectionStart);
        Assert.Equal(new TextPosition(0, 6), engine.SelectionEnd);
    }

    [Fact]
    public void ClearSelection_CollapsesAnExistingSelectionAtTheActiveCaret()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        engine.SelectMatch(0, 1, 4);
        TextPosition activeCaret = engine.CaretPosition;

        Assert.True(engine.HasSelection);
        engine.ClearSelection();

        Assert.False(engine.HasSelection);
        Assert.Equal(activeCaret, engine.CaretPosition);
        Assert.Equal(activeCaret, engine.SelectionStart);
        Assert.Equal(activeCaret, engine.SelectionEnd);
    }

    [Fact]
    public void HitTestCharacter_DoesNotTreatLineSpacingAsTextAndReturnsTheRowEnd()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition position = engine.HitTestCharacter(15, 18, out bool hitCharacter);

        Assert.False(hitCharacter);
        Assert.Equal(new TextPosition(0, 2), position);
    }

    [Fact]
    public void HorizontalNavigation_UsesUnicodeTextElementBoundaries()
    {
        var engine = NewEngine(new FakeLines("A😀B"));
        engine.GoToLineColumn(0, 1);

        engine.HandleKey(NavKey.Right, shift: false);

        Assert.Equal(new TextPosition(0, 3), engine.CaretPosition);
        engine.HandleKey(NavKey.Left, shift: false);
        Assert.Equal(new TextPosition(0, 1), engine.CaretPosition);
    }

    [Fact]
    public void VerticalNavigation_PreservesLayoutXAcrossWrappedRows()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;
        engine.GoToLineColumn(0, 1);

        engine.HandleKey(NavKey.Down, shift: false);

        Assert.Equal(new TextPosition(0, 3), engine.CaretPosition);
    }

    [Fact]
    public void SelectionRenderModel_UsesSourceIndexesAcrossSoftWrapRows()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;
        engine.SelectMatch(0, 1, 4);

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.Equal(new[] { 1, 2, 4 }, rows.Select(row => row.SelectionStartIndex));
        Assert.Equal(new[] { 1, 2, 1 }, rows.Select(row => row.SelectionLength));
        Assert.Equal(new[] { true, true, false }, rows.Select(row => row.SelectionContinuesPastRow));
    }

    [Fact]
    public void NoWrapLayout_UsesMeasuredWidthForHorizontalScrolling()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 40);
        engine.GoToLineColumn(0, 6);

        Assert.True(engine.HorizontalOffset > 0);
        Assert.True(engine.HorizontalExtentPx > engine.ViewportWidth);

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
        Assert.Single(rows);
        Assert.Equal("abcdef", rows[0].Display);
        Assert.Equal((0, 6), (rows[0].SourceStartIndex, rows[0].SourceEndIndex));
    }

    [Fact]
    public void WheelScrolling_AdvancesMeasuredVisualRowsWithinALongSourceLine()
    {
        var engine = NewEngine(new FakeLines("abcdefghijkl"), viewportWidth: 38, viewportHeight: 40);
        engine.WordWrap = true;

        engine.ScrollByWheelDelta(-120);

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.Equal("gh", rows[0].Display);
        Assert.Equal(0, rows[0].LineIndex);
        Assert.True(rows[0].IsWrapContinuation);
    }

    [Fact]
    public void PageNavigation_UsesMeasuredRowHeightInsteadOfFontProbeEstimate()
    {
        var engine = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 60 };
        engine.SetMetrics(10, 10);
        engine.ConfigureProportionalLayout(new FakeProportionalProvider(), "Test", 10);
        engine.SetProvider(new FakeLines("1", "2", "3", "4", "5", "6", "7", "8"));

        Assert.Equal(3, engine.FullyVisibleLineCount);
        engine.HandleKey(NavKey.PageDown, shift: false);

        Assert.Equal(new TextPosition(3, 0), engine.CaretPosition);
    }

    [Fact]
    public void GetVisibleLines_ReservesFullLayoutHeightBeforeNextWrappedSourceLine()
    {
        var engine = NewEngine(
            new FakeLines("abcdef", "next"),
            viewportWidth: 38,
            viewportHeight: 100,
            additionalLayoutHeight: 8);
        engine.WordWrap = true;

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        VisibleLine lastRowOfFirstLine = Assert.Single(
            rows, row => row.LineIndex == 0 && row.SourceEndIndex == 6);
        VisibleLine firstRowOfNextLine = rows.First(row => row.LineIndex == 1);

        Assert.Equal(28d, lastRowOfFirstLine.LayoutRowHeight);
        Assert.Equal(
            lastRowOfFirstLine.Y + lastRowOfFirstLine.LayoutRowHeight,
            firstRowOfNextLine.Y);
    }

    [Fact]
    public void GetVisibleLines_ReservesFullLayoutHeightWhenWordWrapIsDisabled()
    {
        var engine = NewEngine(
            new FakeLines("first", "second"),
            viewportWidth: 200,
            viewportHeight: 100,
            additionalLayoutHeight: 8);
        engine.WordWrap = false;

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.Equal(28d, rows[0].LayoutRowHeight);
        Assert.Equal(28d, rows[1].Y);
    }

    [Fact]
    public void SetViewportAnchor_PreservesSourceColumnWhenWrappingChanges()
    {
        var provider = new FakeProportionalProvider();
        var engine = new TextLayoutEngine { ViewportWidth = 120, ViewportHeight = 20 };
        engine.SetMetrics(10, 20);
        engine.WordWrap = true;
        engine.ConfigureProportionalLayout(provider, "Test", 10);
        engine.SetProvider(new FakeLines("abcdefghij"));

        engine.SetViewportAnchor(new TextPosition(0, 6));
        Assert.Equal(0, Assert.Single(engine.GetVisibleLines(hasFocus: false, caretVisible: false)).SourceStartIndex);

        engine.ViewportWidth = 60;
        engine.SetViewportAnchor(new TextPosition(0, 6));

        VisibleLine first = Assert.Single(engine.GetVisibleLines(hasFocus: false, caretVisible: false));
        Assert.Equal(4, first.SourceStartIndex);
        Assert.Equal(8, first.SourceEndIndex);
    }

    [Fact]
    public void ConfigureProportionalLayout_PassesLineSpacingMultiplierToProvider()
    {
        var provider = new FakeProportionalProvider();
        var engine = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 100 };
        engine.SetMetrics(10, 20);
        engine.ConfigureProportionalLayout(provider, "Test", 10, lineSpacingMultiplier: 1.6);
        engine.SetProvider(new FakeLines("reader line"));

        engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.NotEmpty(provider.Options);
        Assert.All(provider.Options, options => Assert.Equal(1.6, options.LineSpacingMultiplier));
    }

    [Fact]
    public void NoWrapViewportFill_UsesMeasuredRowHeight()
    {
        var engine = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 60 };
        engine.SetMetrics(10, 10);
        engine.ConfigureProportionalLayout(new FakeProportionalProvider(), "Test", 10);
        engine.SetProvider(new FakeLines("1", "2", "3", "4", "5"));

        IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 0d, 20d, 40d }, rows.Select(row => row.Y));
    }

    [Fact]
    public void UpdateTotalLines_DoesNotInvalidateLayoutsWhenCountIsUnchanged()
    {
        var provider = new FakeProportionalProvider();
        var engine = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 20 };
        engine.SetMetrics(10, 20);
        engine.ConfigureProportionalLayout(provider, "Test", 10);
        engine.SetProvider(new FakeLines("one", "two"));

        engine.GetVisibleLines(hasFocus: false, caretVisible: false);
        FakeProportionalResult cached = Assert.IsType<FakeProportionalResult>(provider.Results[0]);

        engine.UpdateTotalLines(2);

        Assert.False(cached.IsDisposed);
    }

    [Fact]
    public void RefreshSourceContent_InvalidatesLayoutsWhenCountIsUnchanged()
    {
        var provider = new FakeProportionalProvider();
        var engine = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 20 };
        engine.SetMetrics(10, 20);
        engine.ConfigureProportionalLayout(provider, "Test", 10);
        engine.SetProvider(new FakeLines("one", "two"));

        engine.GetVisibleLines(hasFocus: false, caretVisible: false);
        FakeProportionalResult cached = Assert.IsType<FakeProportionalResult>(provider.Results[0]);

        engine.RefreshSourceContent();

        Assert.True(cached.IsDisposed);
    }

    [Fact]
    public void LayoutCache_DisposesRowsThatLeaveTheViewport()
    {
        var provider = new FakeProportionalProvider();
        var engine = new TextLayoutEngine { ViewportWidth = 100, ViewportHeight = 20 };
        engine.SetMetrics(10, 20);
        engine.ConfigureProportionalLayout(provider, "Test", 10);
        engine.SetProvider(new FakeLines("one", "two", "three"));

        engine.GetVisibleLines(hasFocus: false, caretVisible: false);
        FakeProportionalResult first = Assert.IsType<FakeProportionalResult>(provider.Results[0]);

        engine.FirstVisibleLine = 1;
        engine.GetVisibleLines(hasFocus: false, caretVisible: false);

        Assert.True(first.IsDisposed);
        Assert.DoesNotContain(provider.Results.Where(result => !result.IsDisposed), result => result.SourceText == "one");
    }

    private static TextLayoutEngine NewEngine(
        FakeLines lines,
        double viewportWidth = 100,
        double viewportHeight = 100,
        double additionalLayoutHeight = 0)
    {
        var engine = new TextLayoutEngine
        {
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
        };
        engine.SetMetrics(10, 20);
        engine.ConfigureProportionalLayout(
            new FakeProportionalProvider(additionalLayoutHeight), "Test", 10);
        engine.SetProvider(lines);
        return engine;
    }

    private sealed class FakeProportionalProvider : ITextLayoutProvider
    {
        private readonly double additionalLayoutHeight;

        public FakeProportionalProvider(double additionalLayoutHeight = 0)
        {
            this.additionalLayoutHeight = additionalLayoutHeight;
        }

        public List<FakeProportionalResult> Results { get; } = new();
        public List<TextLayoutOptions> Options { get; } = new();

        public ITextLayoutResult CreateLayout(string sourceText, TextLayoutOptions options)
        {
            Options.Add(options);
            int maxCharacters = options.WordWrap
                ? Math.Max(1, (int)(options.MaxWidth / 10))
                : Math.Max(1, sourceText.Length);

            var rows = new List<TextLayoutVisualLine>();
            if (sourceText.Length == 0)
            {
                rows.Add(new TextLayoutVisualLine(0, 0, 0, 15, 20));
            }
            else
            {
                int start = 0;
                double top = 0;
                while (start < sourceText.Length)
                {
                    int end = Math.Min(sourceText.Length, start + maxCharacters);
                    rows.Add(new TextLayoutVisualLine(start, end, top, top + 15, 20));
                    start = end;
                    top += 20;
                }
            }

            var result = new FakeProportionalResult(sourceText, rows, additionalLayoutHeight);
            Results.Add(result);
            return result;
        }
    }

    private sealed class FakeProportionalResult : ITextLayoutResult
    {
        private readonly TextElementBoundaryMap boundaries;
        private readonly IReadOnlyList<TextLayoutVisualLine> rows;

        public FakeProportionalResult(
            string sourceText,
            IReadOnlyList<TextLayoutVisualLine> rows,
            double additionalLayoutHeight)
        {
            SourceText = sourceText;
            this.rows = rows;
            boundaries = new TextElementBoundaryMap(sourceText);
            VisualLines = rows;
            LayoutWidth = sourceText.Length * 10;
            double mappedHeight = rows[^1].Top + rows[^1].Height;
            LayoutHeight = Math.Max(mappedHeight, mappedHeight + additionalLayoutHeight);
        }

        public bool IsDisposed { get; private set; }
        public string SourceText { get; }
        public double LayoutWidth { get; }
        public double LayoutHeight { get; }
        public IReadOnlyList<TextLayoutVisualLine> VisualLines { get; }

        public TextCaretGeometry GetCaretGeometry(int sourceUtf16Index, bool trailingSideOfCharacter = false)
        {
            int index = boundaries.SnapToNearestBoundary(sourceUtf16Index);
            TextLayoutVisualLine row = FindRow(index);
            int caretIndex = trailingSideOfCharacter
                ? SourceText == "fi" && index == 0
                    ? 2
                    : boundaries.SnapToNextBoundary(index)
                : index;
            caretIndex = Math.Clamp(caretIndex, row.SourceStartIndex, row.SourceEndIndex);
            return new TextCaretGeometry(
                (caretIndex - row.SourceStartIndex) * 10, row.Top, row.Height);
        }

        public TextLayoutVerticalGeometry GetVisualRowVerticalGeometry(int sourceUtf16Index)
        {
            TextLayoutVisualLine row = FindRow(boundaries.SnapToNearestBoundary(sourceUtf16Index));
            return TextAreaGeometry.FromBaseline(row, fontSize: 10);
        }

        public TextLayoutHitTestResult HitTest(double x, double y)
        {
            TextLayoutVisualLine row = rows.FirstOrDefault(candidate =>
                y >= candidate.Top && y < candidate.Top + candidate.Height);
            if (y < rows[0].Top)
                row = rows[0];
            else if (y >= rows[^1].Top + rows[^1].Height)
                row = rows[^1];

            TextLayoutVerticalGeometry vertical = TextAreaGeometry.FromBaseline(row, fontSize: 10);
            // Simulate DirectWrite returning a glyph hit only inside the same exact text band used by
            // selection painting and annotation hit-testing.
            if (x < 0 || y < vertical.TextTop || y >= vertical.TextBottom)
            {
                return new TextLayoutHitTestResult(row.SourceStartIndex, 0, false, false);
            }

            int offset = Math.Clamp((int)Math.Floor(x / 10), 0, row.SourceLength);
            int index = row.SourceStartIndex + offset;
            if (offset >= row.SourceLength)
                return new TextLayoutHitTestResult(index, 0, false, false);

            bool trailing = x - offset * 10 >= 5;
            int sourceLength = SourceText == "fi" && offset == 0 ? 2 : 1;
            return new TextLayoutHitTestResult(index, sourceLength, trailing, true);
        }

        public IReadOnlyList<TextLayoutRect> GetRangeGeometry(int sourceUtf16Start, int sourceUtf16Length)
        {
            var result = new List<TextLayoutRect>();
            int start = Math.Clamp(sourceUtf16Start, 0, SourceText.Length);
            int end = (int)Math.Clamp((long)start + sourceUtf16Length, 0L, SourceText.Length);
            foreach (TextLayoutVisualLine row in rows)
            {
                int overlapStart = Math.Max(start, row.SourceStartIndex);
                int overlapEnd = Math.Min(end, row.SourceEndIndex);
                if (overlapEnd > overlapStart)
                {
                    TextLayoutVerticalGeometry vertical = TextAreaGeometry.FromBaseline(row, fontSize: 10);
                    result.Add(new TextLayoutRect(
                        (overlapStart - row.SourceStartIndex) * 10,
                        vertical.TextTop,
                        (overlapEnd - overlapStart) * 10,
                        vertical.TextHeight));
                }
            }
            return result;
        }

        public int GetPreviousCaretIndex(int sourceUtf16Index)
            => boundaries.SnapToPreviousBoundary(sourceUtf16Index);

        public int GetNextCaretIndex(int sourceUtf16Index)
            => boundaries.SnapToNextBoundary(sourceUtf16Index);

        public void Dispose() => IsDisposed = true;

        private TextLayoutVisualLine FindRow(int sourceIndex)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                TextLayoutVisualLine row = rows[i];
                if (sourceIndex < row.SourceEndIndex || (i == rows.Count - 1 && sourceIndex <= row.SourceEndIndex))
                    return row;
            }
            return rows[^1];
        }
    }
}
