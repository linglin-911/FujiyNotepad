using System.Numerics;
using FujiyNotepad.Presentation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace FujiyNotepad.WinUI.Rendering;

/// <summary>
/// Win2D/DirectWrite implementation of the reader's proportional text-layout contract.
/// Create, query and dispose instances on the UI thread. A result owns its native layout and text format.
/// </summary>
internal sealed class Win2DTextLayoutProvider : ITextLayoutProvider
{
    private const double MaxNativeLayoutDimension = 10_000_000;
    private readonly Dictionary<(string FontFamily, float FontSize), float> spaceAdvanceCache = new();

    public ITextLayoutResult CreateLayout(string sourceText, TextLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(options);

        if (sourceText.IndexOf('\r') >= 0 || sourceText.IndexOf('\n') >= 0)
        {
            throw new ArgumentException(
                "CreateLayout accepts one logical source line without its line terminator.",
                nameof(sourceText));
        }

        float fontSize = (float)options.FontSize;
        float lineSpacing = (float)options.LineSpacingMultiplier;
        if (!float.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Font size exceeds the Win2D range.");
        if (!float.IsFinite(lineSpacing) || lineSpacing <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Line spacing exceeds the Win2D range.");

        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var format = new CanvasTextFormat
        {
            FontFamily = options.FontFamily,
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = options.WordWrap ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap,
            LineSpacingMode = CanvasLineSpacingMode.Proportional,
            LineSpacing = lineSpacing,
            LineSpacingBaseline = lineSpacing,
        };

        CanvasTextLayout? layout = null;
        try
        {
            // DirectWrite measures a real space in the selected font. Tabs stay as one source UTF-16
            // character in the layout; no expanded display string or reverse index map is exposed.
            float spaceAdvance = GetSpaceAdvance(device, format);
            double tabStop = spaceAdvance * options.TabSize;
            if (!double.IsFinite(tabStop) || tabStop > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options), "Tab stop exceeds the Win2D range.");

            format.IncrementalTabStop = (float)tabStop;

            float layoutWidth = GetNativeLayoutWidth(sourceText, options);
            float layoutHeight = GetNativeLayoutHeight(sourceText, options);

            layout = new CanvasTextLayout(device, sourceText, format, layoutWidth, layoutHeight);
            return new Win2DTextLayoutResult(sourceText, layout, format, options.FontSize, options.LineSpacingMultiplier);
        }
        catch
        {
            layout?.Dispose();
            format.Dispose();
            throw;
        }
    }

    private float GetSpaceAdvance(CanvasDevice device, CanvasTextFormat format)
    {
        var key = (format.FontFamily, format.FontSize);
        if (spaceAdvanceCache.TryGetValue(key, out float cached))
            return cached;

        float measured = MeasureSpaceAdvance(device, format);
        if (spaceAdvanceCache.Count >= 32)
            spaceAdvanceCache.Clear();

        spaceAdvanceCache[key] = measured;
        return measured;
    }

    private static float MeasureSpaceAdvance(CanvasDevice device, CanvasTextFormat format)
    {
        using var spaceLayout = new CanvasTextLayout(device, " ", format, 64, 64)
        {
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        float advance = (float)spaceLayout.LayoutBoundsIncludingTrailingWhitespace.Width;
        if (!float.IsFinite(advance) || advance <= 0)
        {
            Vector2 start = spaceLayout.GetCaretPosition(0, trailingSideOfCharacter: false);
            Vector2 end = spaceLayout.GetCaretPosition(1, trailingSideOfCharacter: false);
            advance = Math.Abs(end.X - start.X);
        }

        if (!float.IsFinite(advance) || advance <= 0)
            return Math.Max(float.Epsilon, format.FontSize * 0.25f);

        return advance;
    }

    private static float GetNativeLayoutWidth(string sourceText, TextLayoutOptions options)
    {
        double requestedWidth = options.MaxWidth;
        if (!options.WordWrap)
        {
            // No-wrap layouts must be wider than the source so DirectWrite can report the true line
            // extent and the canvas can horizontally scroll without clipping the final characters.
            double estimatedContentWidth = (sourceText.Length + 1d) * options.FontSize * 2d + 64d;
            requestedWidth = Math.Max(requestedWidth, estimatedContentWidth);
        }

        return (float)Math.Clamp(requestedWidth, 1d, MaxNativeLayoutDimension);
    }

    private static float GetNativeLayoutHeight(string sourceText, TextLayoutOptions options)
    {
        double minimumHeight = Math.Min(options.FontSize * 2d, MaxNativeLayoutDimension);
        if (!options.WordWrap)
        {
            // A no-wrap logical source line always occupies one visual row, regardless of its character count.
            return (float)Math.Clamp(options.FontSize * 4d, minimumHeight, MaxNativeLayoutDimension);
        }

        // In the worst case a narrow viewport can place each UTF-16 code unit on its own visual row.
        // The existing LineProvider caps a logical source line, so this remains a bounded allocation.
        double estimatedRows = Math.Max(4d, sourceText.Length + 1d);
        double height = options.FontSize * estimatedRows * Math.Max(1d, options.LineSpacingMultiplier) * 1.5d;
        return (float)Math.Clamp(height, minimumHeight, MaxNativeLayoutDimension);
    }
}

/// <summary>
/// A source-index-preserving wrapper around one native CanvasTextLayout. The engine and TextCanvas share this
/// result for drawing, hit-testing, caret placement, selection and annotation geometry.
/// </summary>
internal sealed class Win2DTextLayoutResult : ITextLayoutResult
{
    private readonly CanvasTextLayout layout;
    private readonly CanvasTextFormat format;
    private readonly TextElementBoundaryMap boundaryMap;
    private readonly int[] trailingWhitespaceCounts;
    private bool disposed;

    internal Win2DTextLayoutResult(
        string sourceText,
        CanvasTextLayout layout,
        CanvasTextFormat format,
        double fontSize,
        double lineSpacingMultiplier)
    {
        SourceText = sourceText;
        this.layout = layout;
        this.format = format;
        boundaryMap = new TextElementBoundaryMap(sourceText);

        (TextLayoutVisualLine[] visualLines, int[] trailingCounts, double height) =
            BuildVisualLineMap(sourceText, layout, fontSize, lineSpacingMultiplier, boundaryMap);
        VisualLines = Array.AsReadOnly(visualLines);
        trailingWhitespaceCounts = trailingCounts;
        LayoutHeight = height;

        Rect bounds = layout.LayoutBoundsIncludingTrailingWhitespace;
        LayoutWidth = Math.Max(0, bounds.Width);
    }

    /// <summary>The native layout used by the Win2D renderer. Valid until this result is disposed.</summary>
    internal CanvasTextLayout NativeLayout
    {
        get
        {
            ThrowIfDisposed();
            return layout;
        }
    }

    public string SourceText { get; }

    public double LayoutWidth { get; }

    public double LayoutHeight { get; }

    public IReadOnlyList<TextLayoutVisualLine> VisualLines { get; }

    public TextCaretGeometry GetCaretGeometry(int sourceUtf16Index, bool trailingSideOfCharacter = false)
    {
        ThrowIfDisposed();

        int index = boundaryMap.SnapToNearestBoundary(sourceUtf16Index);
        Vector2 position = layout.GetCaretPosition(index, trailingSideOfCharacter);
        TextLayoutVisualLine row = FindVisualLineForCaret(index);

        // At a soft-wrap boundary DirectWrite can expose the trailing edge of the previous row.
        // The source-index row map is authoritative for vertical placement; keep X from DirectWrite.
        double y = float.IsFinite(position.Y) &&
                   position.Y >= row.Top && position.Y < row.Top + row.Height
            ? position.Y
            : row.Top;
        return new TextCaretGeometry(position.X, y, row.Height);
    }

    public TextLayoutHitTestResult HitTest(double x, double y)
    {
        ThrowIfDisposed();

        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "Hit-test coordinates must be finite.");

        if (layout.HitTest((float)x, (float)y, out CanvasTextLayoutRegion region, out bool trailing))
        {
            int rawStart = Math.Clamp(region.CharacterIndex, 0, SourceText.Length);
            int rawLength = Math.Clamp(region.CharacterCount, 0, SourceText.Length - rawStart);
            (int start, int length) = boundaryMap.ExpandRange(rawStart, rawLength);
            return new TextLayoutHitTestResult(start, length, trailing, isInside: true);
        }

        int boundary = FindNearestBoundary(x, y);
        return new TextLayoutHitTestResult(boundary, 0, isTrailingHit: false, isInside: false);
    }

    public IReadOnlyList<TextLayoutRect> GetRangeGeometry(int sourceUtf16Start, int sourceUtf16Length)
    {
        ThrowIfDisposed();

        if (sourceUtf16Length <= 0 || SourceText.Length == 0)
            return Array.Empty<TextLayoutRect>();

        var expandedRange = boundaryMap.ExpandRange(sourceUtf16Start, sourceUtf16Length);
        int start = expandedRange.Start;
        int end = start + expandedRange.Length;
        if (end <= start)
            return Array.Empty<TextLayoutRect>();

        var rectangles = new List<TextLayoutRect>();
        foreach (CanvasTextLayoutRegion region in layout.GetCharacterRegions(start, end - start))
        {
            Rect bounds = region.LayoutBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                rectangles.Add(new TextLayoutRect(
                    bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }
        }

        // DirectWrite's character-region ink/layout bounds omit trailing whitespace. Add the advance
        // rectangle for any selected trailing spaces or tabs on each wrapped visual row.
        int firstRow = FindFirstVisualLineEndingAfter(start);
        for (int i = firstRow; i < VisualLines.Count; i++)
        {
            TextLayoutVisualLine row = VisualLines[i];
            if (row.SourceStartIndex >= end)
                break;

            int trailingCount = trailingWhitespaceCounts[i];
            if (trailingCount <= 0)
                continue;

            int trailingStart = Math.Max(row.SourceStartIndex, row.SourceEndIndex - trailingCount);
            int overlapStart = Math.Max(start, trailingStart);
            int overlapEnd = Math.Min(end, row.SourceEndIndex);
            if (overlapEnd <= overlapStart)
                continue;

            int lastElementStart = boundaryMap.SnapToPreviousBoundary(overlapEnd);
            Vector2 first = layout.GetCaretPosition(overlapStart, trailingSideOfCharacter: false);
            Vector2 last = layout.GetCaretPosition(lastElementStart, trailingSideOfCharacter: true);
            double left = Math.Min(first.X, last.X);
            double width = Math.Abs(last.X - first.X);
            if (width > 0)
            {
                rectangles.Add(new TextLayoutRect(left, row.Top, width, row.Height));
            }
        }

        return rectangles;
    }

    public int GetPreviousCaretIndex(int sourceUtf16Index)
    {
        ThrowIfDisposed();
        return boundaryMap.SnapToPreviousBoundary(sourceUtf16Index);
    }

    public int GetNextCaretIndex(int sourceUtf16Index)
    {
        ThrowIfDisposed();
        return boundaryMap.SnapToNextBoundary(sourceUtf16Index);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        layout.Dispose();
        format.Dispose();
    }

    private TextLayoutVisualLine FindVisualLineForCaret(int sourceUtf16Index)
    {
        if (VisualLines.Count == 0)
            return new TextLayoutVisualLine(0, SourceText.Length, 0, 0, 0);

        if (sourceUtf16Index >= SourceText.Length)
            return VisualLines[^1];

        int low = 0;
        int high = VisualLines.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            TextLayoutVisualLine row = VisualLines[middle];

            if (sourceUtf16Index < row.SourceStartIndex)
                high = middle - 1;
            else if (sourceUtf16Index >= row.SourceEndIndex)
                low = middle + 1;
            else
                return row;
        }

        return VisualLines[Math.Clamp(low, 0, VisualLines.Count - 1)];
    }

    private int FindFirstVisualLineEndingAfter(int sourceUtf16Index)
    {
        int low = 0;
        int high = VisualLines.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (VisualLines[middle].SourceEndIndex <= sourceUtf16Index)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private TextLayoutVisualLine FindVisualLineNearestY(double y)
    {
        int low = 0;
        int high = VisualLines.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            TextLayoutVisualLine row = VisualLines[middle];

            if (y < row.Top)
                high = middle - 1;
            else if (y >= row.Top + row.Height)
                low = middle + 1;
            else
                return row;
        }

        if (high < 0)
            return VisualLines[0];
        if (low >= VisualLines.Count)
            return VisualLines[^1];

        TextLayoutVisualLine previous = VisualLines[high];
        TextLayoutVisualLine next = VisualLines[low];
        double previousDistance = Math.Abs(y - (previous.Top + previous.Height));
        double nextDistance = Math.Abs(next.Top - y);
        return previousDistance <= nextDistance ? previous : next;
    }

    private int FindNearestBoundary(double x, double y)
    {
        TextLayoutVisualLine row = VisualLines.Count == 0
            ? new TextLayoutVisualLine(0, SourceText.Length, 0, 0, 0)
            : FindVisualLineNearestY(y);

        Vector2 start = layout.GetCaretPosition(row.SourceStartIndex, trailingSideOfCharacter: false);
        int lastElementStart = boundaryMap.SnapToPreviousBoundary(row.SourceEndIndex);
        Vector2 end = row.SourceEndIndex > row.SourceStartIndex
            ? layout.GetCaretPosition(lastElementStart, trailingSideOfCharacter: true)
            : start;
        return Math.Abs(x - start.X) <= Math.Abs(x - end.X)
            ? row.SourceStartIndex
            : row.SourceEndIndex;
    }

    private const float CaretRowChangeTolerance = 0.5f;

    private static (TextLayoutVisualLine[] Lines, int[] TrailingCounts, double Height) BuildVisualLineMap(
        string sourceText,
        CanvasTextLayout layout,
        double fontSize,
        double lineSpacingMultiplier,
        TextElementBoundaryMap boundaryMap)
    {
        // CanvasTextLayout.LineMetrics returns CanvasLineMetrics[], whose WinRT struct contains a Boolean
        // field. The CsWinRT projection used by the current WinAppSDK cannot marshal this non-blittable
        // struct array and throws NotSupportedException at runtime. Use scalar LineCount and
        // GetCaretPosition calls instead; Vector2 is blittable and safe across this projection.
        double fallbackHeight = Math.Max(1, fontSize * 1.2 * lineSpacingMultiplier);
        int expectedLineCount = Math.Max(1, layout.LineCount);
        var rowStarts = new List<(int SourceIndex, double Top)>(expectedLineCount);

        if (sourceText.Length == 0)
        {
            double emptyHeight = Math.Max(
                fallbackHeight,
                GetPositiveLayoutHeight(layout));
            return (
                new[] { new TextLayoutVisualLine(0, 0, 0, emptyHeight * 0.8, emptyHeight) },
                new[] { 0 },
                emptyHeight);
        }

        float firstTop = GetCharacterRowTop(layout, 0);
        if (!float.IsFinite(firstTop))
            firstTop = 0;

        rowStarts.Add((0, firstTop));

        // Find each soft-wrap boundary with a binary search over valid Unicode text-element boundaries.
        // Source order is monotonic in visual-row Y even when the glyph order within a row is bidirectional.
        // This avoids one WinRT call per character for long (up to 64 KiB) source lines.
        for (int row = 1; row < expectedLineCount; row++)
        {
            (int currentStart, double currentTop) = rowStarts[^1];
            int nextStart = FindFirstBoundaryOnNextVisualRow(
                sourceText, boundaryMap, layout, currentStart, currentTop);

            if (nextStart <= currentStart || nextStart >= sourceText.Length)
                break;

            float nextTop = GetCharacterRowTop(layout, nextStart);
            if (!float.IsFinite(nextTop) || nextTop <= currentTop + CaretRowChangeTolerance)
                break;

            rowStarts.Add((nextStart, nextTop));
        }

        // Some DirectWrite caret positions at soft-wrap boundaries are ambiguous. If binary search did not
        // recover the scalar LineCount, scan grapheme starts as a correctness fallback.
        if (rowStarts.Count != expectedLineCount)
        {
            var scannedStarts = ScanVisualLineStarts(
                sourceText, boundaryMap, layout, firstTop, expectedLineCount);
            if (scannedStarts.Count > rowStarts.Count)
                rowStarts = scannedStarts;
        }

        if (rowStarts.Count > expectedLineCount)
            rowStarts.RemoveRange(expectedLineCount, rowStarts.Count - expectedLineCount);

        double originTop = rowStarts[0].Top;
        var rows = new List<TextLayoutVisualLine>(rowStarts.Count);
        var trailingCounts = new List<int>(rowStarts.Count);

        for (int i = 0; i < rowStarts.Count; i++)
        {
            (int sourceStart, double rawTop) = rowStarts[i];
            int sourceEnd = i + 1 < rowStarts.Count
                ? rowStarts[i + 1].SourceIndex
                : sourceText.Length;
            sourceEnd = Math.Clamp(sourceEnd, sourceStart, sourceText.Length);

            double top = Math.Max(0, rawTop - originTop);
            double height;
            if (i + 1 < rowStarts.Count)
            {
                height = rowStarts[i + 1].Top - rawTop;
            }
            else if (i > 0)
            {
                // The final row has no following caret Y from which to derive its height. Reuse the
                // preceding row's actual DirectWrite baseline distance; proportional spacing is uniform
                // for the active format and source line.
                height = rawTop - rowStarts[i - 1].Top;
            }
            else
            {
                height = Math.Max(fallbackHeight, GetPositiveLayoutHeight(layout));
            }

            if (!double.IsFinite(height) || height <= 0)
                height = fallbackHeight;

            rows.Add(new TextLayoutVisualLine(
                sourceStart,
                sourceEnd,
                top,
                top + height * 0.8,
                height));
            trailingCounts.Add(CountTrailingWhitespace(sourceText, sourceStart, sourceEnd - sourceStart));
        }

        if (rows.Count == 0)
        {
            double height = Math.Max(fallbackHeight, GetPositiveLayoutHeight(layout));
            rows.Add(new TextLayoutVisualLine(0, sourceText.Length, 0, height * 0.8, height));
            trailingCounts.Add(CountTrailingWhitespace(sourceText));
        }

        TextLayoutVisualLine last = rows[^1];
        double totalHeight = last.Top + last.Height;
        return (
            rows.ToArray(),
            trailingCounts.ToArray(),
            Math.Max(totalHeight, GetPositiveLayoutHeight(layout)));
    }

    private static float GetCharacterRowTop(CanvasTextLayout layout, int sourceIndex)
    {
        // At a soft-wrap boundary DirectWrite may expose the previous row for one caret affinity.
        // Taking the greater Y of the leading and trailing positions identifies the row containing
        // the character at sourceIndex without requesting any WinRT struct arrays.
        Vector2 leading = layout.GetCaretPosition(sourceIndex, trailingSideOfCharacter: false);
        Vector2 trailing = layout.GetCaretPosition(sourceIndex, trailingSideOfCharacter: true);
        return Math.Max(leading.Y, trailing.Y);
    }

    private static int FindFirstBoundaryOnNextVisualRow(
        string sourceText,
        TextElementBoundaryMap boundaryMap,
        CanvasTextLayout layout,
        int currentStart,
        double currentTop)
    {
        int low = currentStart + 1;
        int high = sourceText.Length - 1;
        int result = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int candidate = boundaryMap.SnapToBoundaryAtOrAfter(middle);
            if (candidate >= sourceText.Length)
            {
                high = middle - 1;
                continue;
            }

            float candidateTop = GetCharacterRowTop(layout, candidate);
            if (float.IsFinite(candidateTop) && candidateTop > currentTop + CaretRowChangeTolerance)
            {
                result = candidate;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return result;
    }

    private static List<(int SourceIndex, double Top)> ScanVisualLineStarts(
        string sourceText,
        TextElementBoundaryMap boundaryMap,
        CanvasTextLayout layout,
        float firstTop,
        int maximumRows)
    {
        var starts = new List<(int SourceIndex, double Top)>(maximumRows)
        {
            (0, float.IsFinite(firstTop) ? firstTop : 0)
        };

        for (int index = boundaryMap.SnapToNextBoundary(0);
             index < sourceText.Length && starts.Count < maximumRows;)
        {
            float top = GetCharacterRowTop(layout, index);
            if (float.IsFinite(top) && top > starts[^1].Top + CaretRowChangeTolerance)
                starts.Add((index, top));

            int next = boundaryMap.SnapToNextBoundary(index);
            if (next <= index)
                break;
            index = next;
        }

        return starts;
    }

    private static double GetPositiveLayoutHeight(CanvasTextLayout layout)
    {
        double height = layout.LayoutBoundsIncludingTrailingWhitespace.Height;
        return double.IsFinite(height) && height > 0 ? height : 0;
    }

    private static int CountTrailingWhitespace(string sourceText)
    {
        return CountTrailingWhitespace(sourceText, 0, sourceText.Length);
    }

    private static int CountTrailingWhitespace(string sourceText, int start, int length)
    {
        int count = 0;
        for (int i = start + length - 1; i >= start && char.IsWhiteSpace(sourceText[i]); i--)
            count++;
        return count;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
