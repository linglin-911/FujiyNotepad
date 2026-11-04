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
        Assert.Equal(new TextPosition(0, 1), engine.HitTestCharacter(23, 5));
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

        TextPosition position = engine.HitTestCharacter(18, 25, out bool hitCharacter);

        Assert.True(hitCharacter);
        Assert.Equal(new TextPosition(0, 3), position);
    }

    [Fact]
    public void HitTestCharacter_PreservesTheFullSourceRangeOfAShapingCluster()
    {
        var engine = NewEngine(new FakeLines("fi"), viewportWidth: 100, viewportHeight: 40);
        engine.WordWrap = true;

        TextPosition position = engine.HitTestCharacter(
            8, 5, out bool hitCharacter, out int sourceLength);

        Assert.True(hitCharacter);
        Assert.Equal(new TextPosition(0, 0), position);
        Assert.Equal(2, sourceLength);
    }

    [Fact]
    public void HitTestCharacter_DistinguishesTextFromNearestBlankBoundaries()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition beforeText = engine.HitTestCharacter(0, 25, out bool beforeHit);
        TextPosition afterText = engine.HitTestCharacter(100, 25, out bool afterHit);

        Assert.False(beforeHit);
        Assert.False(afterHit);
        Assert.Equal(new TextPosition(0, 2), beforeText);
        Assert.Equal(new TextPosition(0, 4), afterText);

        // Context-menu positioning still uses the nearest source boundary when the pointer is in blank space.
        Assert.Equal(new TextPosition(0, 2), engine.HitTestCharacter(0, 25));
    }

    [Fact]
    public void AnnotationMarkerHitTest_UsesTheWholeSelectionRectangleNotGlyphPixels()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 4) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(25, 19, markers, out TextPosition position);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 1), position);
    }

    [Fact]
    public void AnnotationMarkerHitTest_DoesNotHitOutsideTheAnnotatedRectangle()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 100, viewportHeight: 40);
        var markers = new Dictionary<int, List<TextAnnotationMarker>>
        {
            [0] = new() { new TextAnnotationMarker(0, 1, 4) },
        };

        bool hit = engine.TryHitTestAnnotationMarkers(60, 19, markers, out _);

        Assert.False(hit);
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

        bool hit = engine.TryHitTestAnnotationMarkers(15, 25, markers, out TextPosition position);

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

        bool hit = engine.TryHitTestAnnotationMarkers(35, 5, markers, out TextPosition position);

        Assert.True(hit);
        Assert.Equal(new TextPosition(0, 3), position);
    }

    [Fact]
    public void HitTestCharacter_MapsNextLogicalLineToItsOwnSourcePosition()
    {
        var engine = NewEngine(new FakeLines("ab", "cd"), viewportWidth: 100, viewportHeight: 60);
        engine.WordWrap = true;

        TextPosition position = engine.HitTestCharacter(18, 25, out bool hitCharacter);

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
    public void DoubleClickWordSelection_UsesProportionalHitTestSourcePosition()
    {
        var engine = NewEngine(new FakeLines("abcdef"), viewportWidth: 38, viewportHeight: 60);
        engine.WordWrap = true;

        // Click the middle visual row of a word that spans all three wrapped rows.
        TextPosition clicked = engine.HitTest(25, 25);
        engine.SelectWordAt(clicked);

        Assert.Equal(new TextPosition(0, 0), engine.SelectionStart);
        Assert.Equal(new TextPosition(0, 6), engine.SelectionEnd);
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
            rows.Where(row => row.LineIndex == 0 && row.SourceEndIndex == 6));
        VisibleLine firstRowOfNextLine = rows.First(row => row.LineIndex == 1);

        Assert.Equal(48d, lastRowOfFirstLine.Y + lastRowOfFirstLine.LayoutRowHeight);
        Assert.Equal(48d, firstRowOfNextLine.Y);
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

        public ITextLayoutResult CreateLayout(string sourceText, TextLayoutOptions options)
        {
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
            return new TextCaretGeometry((index - row.SourceStartIndex) * 10, row.Top, row.Height);
        }

        public TextLayoutHitTestResult HitTest(double x, double y)
        {
            TextLayoutVisualLine row = rows.FirstOrDefault(candidate =>
                y >= candidate.Top && y < candidate.Top + candidate.Height);
            if (y < rows[0].Top)
                row = rows[0];
            else if (y >= rows[^1].Top + rows[^1].Height)
                row = rows[^1];

            // Simulate DirectWrite returning no glyph hit in the lower row padding. The selection
            // rectangle still spans the complete visual row and must remain clickable.
            if (x < 0 || y < row.Top || y >= row.Top + row.Height
                || y >= row.Top + row.Height * 0.75)
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
                    result.Add(new TextLayoutRect(
                        (overlapStart - row.SourceStartIndex) * 10,
                        row.Top,
                        (overlapEnd - overlapStart) * 10,
                        row.Height));
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
