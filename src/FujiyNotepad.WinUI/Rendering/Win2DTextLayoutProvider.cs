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

    public ITextLayoutResult CreateLayout(string sourceText, TextLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(options);

        if (sourceText.IndexOfAny(new[] { '\r', '\n' }) >= 0)
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
            float spaceAdvance = MeasureSpaceAdvance(device, format);
            double tabStop = spaceAdvance * options.TabSize;
            if (!double.IsFinite(tabStop) || tabStop > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options), "Tab stop exceeds the Win2D range.");

            format.IncrementalTabStop = (float)tabStop;

            float layoutWidth = GetNativeLayoutWidth(sourceText, options);
            float layoutHeight = GetNativeLayoutHeight(sourceText, options);

            layout = new CanvasTextLayout(device, sourceText, format, layoutWidth, layoutHeight);
            return new Win2DTextLayoutResult(sourceText, layout, format, options.FontSize);
        }
        catch
        {
            layout?.Dispose();
            format.Dispose();
            throw;
        }
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
        // In the worst case a narrow viewport can place each UTF-16 code unit on its own visual row.
        // The existing LineProvider caps a logical source line, so this remains a bounded allocation.
        double estimatedRows = Math.Max(4d, sourceText.Length + 1d);
        double height = options.FontSize * estimatedRows * Math.Max(1d, options.LineSpacingMultiplier) * 1.5d;
        double minimumHeight = Math.Min(options.FontSize * 2d, MaxNativeLayoutDimension);
        return (float)Math.Clamp(height, minimumHeight, MaxNativeLayoutDimension);
    }
}

/// <summary>
/// A source-index-preserving wrapper around one native CanvasTextLayout. It is intentionally separate from
/// TextCanvas so the layout backend can be verified before the engine and reader rendering path are migrated.
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
        double fontSize)
    {
        SourceText = sourceText;
        this.layout = layout;
        this.format = format;
        boundaryMap = new TextElementBoundaryMap(sourceText);

        (TextLayoutVisualLine[] visualLines, int[] trailingCounts, double height) =
            BuildVisualLineMap(sourceText, layout, fontSize);
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

        double y = float.IsFinite(position.Y) ? position.Y : row.Top;
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

        int start = Math.Clamp(sourceUtf16Start, 0, SourceText.Length);
        int end = (int)Math.Clamp((long)start + sourceUtf16Length, 0L, SourceText.Length);
        var expandedRange = boundaryMap.ExpandRange(start, end - start);
        start = expandedRange.Start;
        end = start + expandedRange.Length;
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

    private static (TextLayoutVisualLine[] Lines, int[] TrailingCounts, double Height) BuildVisualLineMap(
        string sourceText,
        CanvasTextLayout layout,
        double fontSize)
    {
        CanvasLineMetrics[] metrics = layout.LineMetrics;
        if (metrics.Length == 0)
        {
            double fallbackHeight = Math.Max(1, fontSize * 1.2);
            return (
                new[] { new TextLayoutVisualLine(0, sourceText.Length, 0, fallbackHeight * 0.8, fallbackHeight) },
                new[] { CountTrailingWhitespace(sourceText) },
                fallbackHeight);
        }

        var rows = new List<TextLayoutVisualLine>(metrics.Length);
        var trailingCounts = new List<int>(metrics.Length);
        int sourceOffset = 0;
        double top = 0;

        for (int i = 0; i < metrics.Length; i++)
        {
            CanvasLineMetrics metric = metrics[i];
            int available = Math.Max(0, sourceText.Length - sourceOffset);
            int contentLength = Math.Clamp(
                metric.CharacterCount - metric.TerminalNewlineCount,
                0,
                available);

            if (i == metrics.Length - 1 && sourceOffset + contentLength < sourceText.Length)
                contentLength = available;

            int end = sourceOffset + contentLength;
            double height = metric.Height > 0 && float.IsFinite(metric.Height)
                ? metric.Height
                : Math.Max(1, fontSize * 1.2);
            double baseline = metric.Baseline > 0 && float.IsFinite(metric.Baseline)
                ? metric.Baseline
                : height * 0.8;

            rows.Add(new TextLayoutVisualLine(sourceOffset, end, top, top + baseline, height));
            trailingCounts.Add(Math.Clamp(metric.TrailingWhitespaceCount, 0, contentLength));

            sourceOffset = end;
            top += height;
        }

        if (rows.Count == 0)
        {
            double fallbackHeight = Math.Max(1, fontSize * 1.2);
            rows.Add(new TextLayoutVisualLine(0, sourceText.Length, 0, fallbackHeight * 0.8, fallbackHeight));
            trailingCounts.Add(CountTrailingWhitespace(sourceText));
            top = fallbackHeight;
        }
        else if (sourceOffset < sourceText.Length)
        {
            TextLayoutVisualLine last = rows[^1];
            rows[^1] = new TextLayoutVisualLine(
                last.SourceStartIndex,
                sourceText.Length,
                last.Top,
                last.Baseline,
                last.Height);
            trailingCounts[^1] = Math.Min(
                sourceText.Length - last.SourceStartIndex,
                Math.Max(trailingCounts[^1], CountTrailingWhitespace(sourceText)));
        }

        // DirectWrite normally returns a line metric for an empty line. Keep the contract deterministic even
        // if a future backend/version reports no visual rows for an empty source string.
        if (sourceText.Length == 0 && rows.Count == 0)
        {
            double fallbackHeight = Math.Max(1, fontSize * 1.2);
            rows.Add(new TextLayoutVisualLine(0, 0, 0, fallbackHeight * 0.8, fallbackHeight));
            trailingCounts.Add(0);
            top = fallbackHeight;
        }

        return (rows.ToArray(), trailingCounts.ToArray(), Math.Max(top, layout.LayoutBounds.Height));
    }

    private static int CountTrailingWhitespace(string sourceText)
    {
        int count = 0;
        for (int i = sourceText.Length - 1; i >= 0 && char.IsWhiteSpace(sourceText[i]); i--)
            count++;
        return count;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
