using System.Globalization;
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
        Assert.DoesNotContain(0, provider.Results.Where(result => !result.IsDisposed).Select(result => result.LineIndex));
    }

    private static TextLayoutEngine NewEngine(
        FakeLines lines,
        double viewportWidth = 100,
        double viewportHeight = 100)
    {
        var engine = new TextLayoutEngine
        {
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
        };
        engine.SetMetrics(10, 20);
        engine.ConfigureProportionalLayout(new FakeProportionalProvider(), "Test", 10);
        engine.SetProvider(lines);
        return engine;
    }

    private sealed class FakeProportionalProvider : ITextLayoutProvider
    {
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

            var result = new FakeProportionalResult(sourceText, rows);
            Results.Add(result);
            return result;
        }
    }

    private sealed class FakeProportionalResult : ITextLayoutResult
    {
        private readonly TextElementBoundaryMap boundaries;
        private readonly IReadOnlyList<TextLayoutVisualLine> rows;

        public FakeProportionalResult(string sourceText, IReadOnlyList<TextLayoutVisualLine> rows)
        {
            SourceText = sourceText;
            this.rows = rows;
            boundaries = new TextElementBoundaryMap(sourceText);
            VisualLines = rows;
            LayoutWidth = sourceText.Length * 10;
            LayoutHeight = rows.Count * 20;
        }

        public int LineIndex { get; set; } = -1;
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

            int offset = Math.Clamp((int)Math.Floor(Math.Max(0, x) / 10), 0, row.SourceLength);
            int index = row.SourceStartIndex + offset;
            if (offset >= row.SourceLength)
                return new TextLayoutHitTestResult(index, 0, false, false);

            bool trailing = x - offset * 10 >= 5;
            return new TextLayoutHitTestResult(index, 1, trailing, true);
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
