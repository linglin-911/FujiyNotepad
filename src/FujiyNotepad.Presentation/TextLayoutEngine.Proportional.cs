using FujiyNotepad.Core;

namespace FujiyNotepad.Presentation;

/// <summary>
/// Proportional-layout path for TextLayoutEngine. The original fixed-cell implementation remains available
/// as a fallback while the reader migrates; all positions produced here remain source-line UTF-16 offsets.
/// </summary>
public sealed partial class TextLayoutEngine
{
    private ITextLayoutProvider? proportionalLayoutProvider;
    private string proportionalFontFamily = "Consolas";
    private double proportionalFontSize = 14;
    private double proportionalLineSpacing = 1.0;
    private double desiredProportionalX = -1;

    private readonly Dictionary<int, ITextLayoutResult> proportionalLayoutCache = new();
    private readonly HashSet<int> activeProportionalLayoutLines = new();
    private TextLayoutOptions? cachedProportionalOptions;

    /// <summary>True when the reader is using the font-aware layout provider instead of fixed display cells.</summary>
    public bool UsesProportionalLayout => proportionalLayoutProvider is not null;

    /// <summary>
    /// Installs the font-aware layout provider and its typography settings. The provider is UI-layer supplied;
    /// the engine itself remains independent of WinUI and native graphics APIs.
    /// </summary>
    public void ConfigureProportionalLayout(
        ITextLayoutProvider? layoutProvider,
        string fontFamily,
        double fontSize,
        double lineSpacingMultiplier = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(lineSpacingMultiplier) || lineSpacingMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineSpacingMultiplier));

        proportionalLayoutProvider = layoutProvider;
        proportionalFontFamily = fontFamily;
        proportionalFontSize = fontSize;
        proportionalLineSpacing = lineSpacingMultiplier;
        desiredColumn = -1;
        desiredProportionalX = -1;
        ClearProportionalLayoutCache();
        RaiseViewChanged();
        RaiseRedraw();
    }

    /// <summary>Returns the original line terminator for whitespace visualization.</summary>
    public LineEnding GetLineEnding(int lineIndex)
    {
        if (provider is null || lineIndex < 0 || lineIndex >= totalLines)
            return LineEnding.None;
        return LineEndingOf(lineIndex);
    }

    /// <summary>
    /// Returns the cached native-backed layout for a source line. The result is owned by the engine and remains
    /// valid until it leaves the active viewport cache or layout settings/provider are changed.
    /// </summary>
    public ITextLayoutResult? GetLayoutForLine(int lineIndex)
    {
        if (proportionalLayoutProvider is null || provider is null ||
            lineIndex < 0 || lineIndex >= totalLines)
        {
            return null;
        }

        return GetProportionalLayout(lineIndex);
    }

    /// <summary>Gets the native source layout's caret geometry without exposing graphics types to Presentation.</summary>
    public TextCaretGeometry GetProportionalCaretGeometry(TextPosition position)
    {
        TextPosition clamped = ClampPosition(position);
        return GetProportionalLayout(clamped.Line).GetCaretGeometry(clamped.Column);
    }

    /// <summary>Releases every cached native layout. Called when the document or typography changes.</summary>
    public void ClearProportionalLayoutCache()
    {
        foreach (ITextLayoutResult result in proportionalLayoutCache.Values)
            result.Dispose();

        proportionalLayoutCache.Clear();
        activeProportionalLayoutLines.Clear();
        cachedProportionalOptions = null;
        desiredProportionalX = -1;
    }

    private TextLayoutOptions CreateProportionalOptions()
    {
        double availableWidth = Math.Max(1, ViewportWidth - TextOriginX - TextPadding);
        return new TextLayoutOptions(
            proportionalFontFamily,
            proportionalFontSize,
            availableWidth,
            wordWrap,
            proportionalLineSpacing,
            tabSize);
    }

    private TextLayoutOptions EnsureProportionalOptions()
    {
        TextLayoutOptions options = CreateProportionalOptions();
        if (cachedProportionalOptions != options)
        {
            ClearProportionalLayoutCache();
            cachedProportionalOptions = options;
        }
        return options;
    }

    private ITextLayoutResult GetProportionalLayout(int lineIndex)
    {
        TextLayoutOptions options = EnsureProportionalOptions();
        activeProportionalLayoutLines.Add(lineIndex);
        if (proportionalLayoutCache.TryGetValue(lineIndex, out ITextLayoutResult? cached))
            return cached;

        string sourceText = provider!.GetLine(lineIndex);
        ITextLayoutResult created = proportionalLayoutProvider!.CreateLayout(sourceText, options);
        proportionalLayoutCache.Add(lineIndex, created);
        return created;
    }

    // Row-count traversal often crosses many off-screen source lines (large wheel/page movements). Do not
    // retain native layouts for those intermediate lines: use an existing cache entry or create/dispose a
    // temporary layout immediately. The destination line is cached separately when its geometry is needed.
    private int GetProportionalRowCount(int lineIndex)
    {
        TextLayoutOptions options = EnsureProportionalOptions();
        if (proportionalLayoutCache.TryGetValue(lineIndex, out ITextLayoutResult? cached))
            return Math.Max(1, cached.VisualLines.Count);

        using ITextLayoutResult temporary = proportionalLayoutProvider!.CreateLayout(
            provider!.GetLine(lineIndex), options);
        return Math.Max(1, temporary.VisualLines.Count);
    }

    private void PruneProportionalLayoutCache()
    {
        foreach (KeyValuePair<int, ITextLayoutResult> pair in proportionalLayoutCache.ToArray())
        {
            if (activeProportionalLayoutLines.Contains(pair.Key))
                continue;

            pair.Value.Dispose();
            proportionalLayoutCache.Remove(pair.Key);
        }

        activeProportionalLayoutLines.Clear();
    }

    private IReadOnlyList<VisibleLine> GetVisibleLinesProportional(bool hasFocus, bool caretVisible)
    {
        var lines = new List<VisibleLine>();
        activeProportionalLayoutLines.Clear();

        if (provider is null || totalLines == 0 || lineHeight <= 0)
        {
            horizontalExtentPx = ViewportWidth;
            PruneProportionalLayoutCache();
            return lines;
        }

        if (wordWrap)
        {
            int firstRowCount = Math.Max(1, GetProportionalLayout(firstVisibleLine).VisualLines.Count);
            firstVisibleRow = Math.Clamp(firstVisibleRow, 0, firstRowCount - 1);
        }

        (TextPosition selectionStart, TextPosition selectionEnd) = NormalizedSelection();
        bool hasSelection = selectionStart != selectionEnd;
        double y = 0;
        double maxWidth = 0;

        if (wordWrap)
        {
            int sourceLine = firstVisibleLine;
            while (sourceLine < totalLines && y < ViewportHeight)
            {
                ITextLayoutResult layout = GetProportionalLayout(sourceLine);
                IReadOnlyList<TextLayoutVisualLine> visualRows = layout.VisualLines;
                if (visualRows.Count == 0)
                {
                    sourceLine++;
                    continue;
                }

                string source = layout.SourceText;
                IReadOnlyList<(int Start, int Length)> lineMatches =
                    highlighter?.Find(source) ?? Array.Empty<(int Start, int Length)>();
                IReadOnlyList<(int Start, int Length)> lineSelectionMatches =
                    selectionHighlighter?.Find(source) ?? Array.Empty<(int Start, int Length)>();
                IReadOnlyList<HighlightSpan> lineRuleHighlights =
                    highlightRules?.Find(source) ?? Array.Empty<HighlightSpan>();
                int firstRow = sourceLine == firstVisibleLine
                    ? Math.Clamp(firstVisibleRow, 0, visualRows.Count - 1)
                    : 0;

                for (int rowIndex = firstRow; rowIndex < visualRows.Count && y < ViewportHeight; rowIndex++)
                {
                    TextLayoutVisualLine row = visualRows[rowIndex];
                    double rowHeight = GetSafeRowHeight(row);
                    int selectedStart = 0;
                    int selectedLength = 0;
                    bool rowHasSelection = false;
                    bool selectionContinues = false;

                    if (hasSelection && sourceLine >= selectionStart.Line && sourceLine <= selectionEnd.Line)
                    {
                        int lineSelectionStart = sourceLine == selectionStart.Line ? selectionStart.Column : 0;
                        int lineSelectionEnd = sourceLine == selectionEnd.Line ? selectionEnd.Column : source.Length;
                        int overlapStart = Math.Max(lineSelectionStart, row.SourceStartIndex);
                        int overlapEnd = Math.Min(lineSelectionEnd, row.SourceEndIndex);

                        bool crossesSoftWrap = lineSelectionStart < row.SourceEndIndex &&
                                                lineSelectionEnd > row.SourceEndIndex;
                        bool crossesSourceLine = sourceLine < selectionEnd.Line &&
                                                 rowIndex == visualRows.Count - 1 &&
                                                 lineSelectionStart <= row.SourceEndIndex;
                        selectionContinues = crossesSoftWrap || crossesSourceLine;

                        if (overlapEnd > overlapStart || selectionContinues)
                        {
                            selectedStart = overlapStart;
                            selectedLength = Math.Max(0, overlapEnd - overlapStart);
                            rowHasSelection = selectedLength > 0 || selectionContinues;
                        }
                    }

                    int caretRow = FindVisualRowForCaret(visualRows, caret.Column);
                    bool hasCaret = hasFocus && caretVisible && caret.Line == sourceLine && caretRow == rowIndex;
                    TextCaretGeometry caretGeometry = hasCaret
                        ? layout.GetCaretGeometry(caret.Column)
                        : default;

                    lines.Add(new VisibleLine
                    {
                        LineIndex = sourceLine,
                        Y = y,
                        Display = source.Substring(row.SourceStartIndex, row.SourceLength),
                        DisplayStartIndex = row.SourceStartIndex,
                        TextX = TextOriginX,
                        HasSelection = rowHasSelection,
                        SelectionStartIndex = selectedStart,
                        SelectionLength = selectedLength,
                        SelectionContinuesPastRow = selectionContinues,
                        HasCaret = hasCaret,
                        CaretX = hasCaret ? TextOriginX + caretGeometry.X : 0,
                        CaretDisplayIndex = hasCaret ? caret.Column : -1,
                        Matches = ClipProportionalHighlights(
                            lineMatches, row.SourceStartIndex, row.SourceEndIndex),
                        SelectionMatches = ClipProportionalHighlights(
                            lineSelectionMatches, row.SourceStartIndex, row.SourceEndIndex, lineMatches),
                        RuleHighlights = ClipProportionalRuleHighlights(
                            lineRuleHighlights, row.SourceStartIndex, row.SourceEndIndex),
                        IsBookmarked = rowIndex == 0 && bookmarks.Count > 0 && bookmarks.Contains(sourceLine),
                        Whitespace = NoWhitespace,
                        IsWrapContinuation = rowIndex > 0,
                        UsesProportionalLayout = true,
                        SourceStartIndex = row.SourceStartIndex,
                        SourceEndIndex = row.SourceEndIndex,
                        LayoutRowTop = row.Top,
                        LayoutRowHeight = rowHeight,
                        CaretSourceIndex = hasCaret ? caret.Column : -1,
                    });

                    y += rowHeight;
                }

                sourceLine++;
            }

            horizontalExtentPx = ViewportWidth;
        }
        else
        {
            int sourceLine = firstVisibleLine;
            while (sourceLine < totalLines && y < ViewportHeight)
            {
                ITextLayoutResult layout = GetProportionalLayout(sourceLine);
                TextLayoutVisualLine row = layout.VisualLines.Count > 0
                    ? layout.VisualLines[0]
                    : new TextLayoutVisualLine(0, layout.SourceText.Length, 0, lineHeight * 0.8, lineHeight);
                double rowHeight = GetSafeRowHeight(row);
                string source = layout.SourceText;
                IReadOnlyList<(int Start, int Length)> lineMatches =
                    highlighter?.Find(source) ?? Array.Empty<(int Start, int Length)>();
                IReadOnlyList<(int Start, int Length)> lineSelectionMatches =
                    selectionHighlighter?.Find(source) ?? Array.Empty<(int Start, int Length)>();
                IReadOnlyList<HighlightSpan> lineRuleHighlights =
                    highlightRules?.Find(source) ?? Array.Empty<HighlightSpan>();
                maxWidth = Math.Max(maxWidth, layout.LayoutWidth);

                int selectedStart = 0;
                int selectedLength = 0;
                bool rowHasSelection = false;
                bool selectionContinues = false;
                if (hasSelection && sourceLine >= selectionStart.Line && sourceLine <= selectionEnd.Line)
                {
                    int lineSelectionStart = sourceLine == selectionStart.Line ? selectionStart.Column : 0;
                    int lineSelectionEnd = sourceLine == selectionEnd.Line ? selectionEnd.Column : source.Length;
                    selectedStart = lineSelectionStart;
                    selectedLength = Math.Max(0, lineSelectionEnd - lineSelectionStart);
                    selectionContinues = sourceLine < selectionEnd.Line;
                    rowHasSelection = selectedLength > 0 || selectionContinues;
                }

                bool hasCaret = hasFocus && caretVisible && caret.Line == sourceLine;
                TextCaretGeometry caretGeometry = hasCaret
                    ? layout.GetCaretGeometry(caret.Column)
                    : default;

                lines.Add(new VisibleLine
                {
                    LineIndex = sourceLine,
                    Y = y,
                    Display = source,
                    DisplayStartIndex = 0,
                    TextX = TextOriginX - horizontalOffset,
                    HasSelection = rowHasSelection,
                    SelectionStartIndex = selectedStart,
                    SelectionLength = selectedLength,
                    SelectionContinuesPastRow = selectionContinues,
                    HasCaret = hasCaret,
                    CaretX = hasCaret ? TextOriginX - horizontalOffset + caretGeometry.X : 0,
                    CaretDisplayIndex = hasCaret ? caret.Column : -1,
                    Matches = ClipProportionalHighlights(lineMatches, 0, source.Length),
                    SelectionMatches = ClipProportionalHighlights(
                        lineSelectionMatches, 0, source.Length, lineMatches),
                    RuleHighlights = ClipProportionalRuleHighlights(lineRuleHighlights, 0, source.Length),
                    IsBookmarked = bookmarks.Count > 0 && bookmarks.Contains(sourceLine),
                    Whitespace = NoWhitespace,
                    UsesProportionalLayout = true,
                    SourceStartIndex = 0,
                    SourceEndIndex = source.Length,
                    LayoutRowTop = row.Top,
                    LayoutRowHeight = rowHeight,
                    CaretSourceIndex = hasCaret ? caret.Column : -1,
                });

                y += rowHeight;
                sourceLine++;
            }

            double newExtent = Math.Max(
                maxWidth + GutterWidthPx + TextPadding * 2,
                ViewportWidth);
            if (Math.Abs(newExtent - horizontalExtentPx) > 0.5)
            {
                horizontalExtentPx = newExtent;
                RaiseViewChanged();
            }

            // A document edit, font change or wider viewport can shrink the measured extent. Never leave the
            // reader horizontally scrolled beyond the new content edge.
            double maximumOffset = Math.Max(0, horizontalExtentPx - ViewportWidth);
            if (horizontalOffset > maximumOffset)
                SetHorizontalOffset(maximumOffset);
        }

        PruneProportionalLayoutCache();
        return lines;
    }

    private static double GetSafeRowHeight(TextLayoutVisualLine row)
        => double.IsFinite(row.Height) && row.Height > 0 ? row.Height : 1;

    private static int FindVisualRowForCaret(IReadOnlyList<TextLayoutVisualLine> rows, int sourceIndex)
    {
        if (rows.Count == 0)
            return 0;

        sourceIndex = Math.Max(0, sourceIndex);
        for (int index = 0; index < rows.Count; index++)
        {
            TextLayoutVisualLine row = rows[index];
            bool isLast = index == rows.Count - 1;
            if (sourceIndex < row.SourceEndIndex || (isLast && sourceIndex <= row.SourceEndIndex))
            {
                if (sourceIndex >= row.SourceStartIndex)
                    return index;
            }
        }

        return rows.Count - 1;
    }

    private (int line, int row) AdvanceProportionalDisplayRows(int line, int row, int delta)
    {
        if (provider is null || totalLines == 0)
            return (0, 0);

        line = Math.Clamp(line, 0, totalLines - 1);
        row = Math.Clamp(row, 0, GetProportionalRowCount(line) - 1);

        while (delta > 0)
        {
            int rowCount = GetProportionalRowCount(line);
            if (row + 1 < rowCount)
            {
                row++;
            }
            else if (line + 1 < totalLines)
            {
                line++;
                row = 0;
            }
            else
            {
                break;
            }

            delta--;
        }

        while (delta < 0)
        {
            if (row > 0)
            {
                row--;
            }
            else if (line > 0)
            {
                line--;
                row = GetProportionalRowCount(line) - 1;
            }
            else
            {
                break;
            }

            delta++;
        }

        return (line, row);
    }

    private void MoveCaretByProportionalDisplayRow(int delta, bool shift)
    {
        ITextLayoutResult currentLayout = GetProportionalLayout(caret.Line);
        int currentRow = FindVisualRowForCaret(currentLayout.VisualLines, caret.Column);

        if (desiredProportionalX < 0)
            desiredProportionalX = currentLayout.GetCaretGeometry(caret.Column).X;

        (int targetLine, int targetRowIndex) =
            AdvanceProportionalDisplayRows(caret.Line, currentRow, delta);

        ITextLayoutResult targetLayout = GetProportionalLayout(targetLine);
        TextLayoutVisualLine targetRow = targetLayout.VisualLines[
            Math.Clamp(targetRowIndex, 0, targetLayout.VisualLines.Count - 1)];
        TextLayoutHitTestResult hit = targetLayout.HitTest(
            desiredProportionalX,
            targetRow.Top + GetSafeRowHeight(targetRow) / 2);

        ApplyCaret(new TextPosition(targetLine, hit.CaretIndex), shift, keepDesired: true);
    }

    /// <summary>Returns the source character under a point, rather than the nearest trailing caret boundary.</summary>
    public TextPosition HitTestCharacter(double x, double y)
        => HitTestCharacter(x, y, out _, out _);

    /// <summary>
    /// Hit-tests the source character under a point and reports whether the pointer actually landed on a
    /// character region. The nearest-boundary position is still returned when it did not; callers that act
    /// on a character (such as clicking an annotation marker) must check <paramref name="isInside"/>.
    /// </summary>
    public TextPosition HitTestCharacter(double x, double y, out bool isInside)
        => HitTestCharacter(x, y, out isInside, out _);

    /// <summary>
    /// Also returns the full source range of the hit character/shaping cluster. This matters for ligatures,
    /// combining sequences and other glyphs whose visual hit region can represent multiple UTF-16 units.
    /// </summary>
    public TextPosition HitTestCharacter(
        double x, double y, out bool isInside, out int sourceLength)
    {
        if (!UsesProportionalLayout)
        {
            isInside = false;
            sourceLength = 0;
            return HitTest(x, y);
        }

        return HitTestProportional(x, y, returnCharacter: true, out isInside, out sourceLength);
    }

    private TextPosition HitTestProportional(
        double x, double y, bool returnCharacter, out bool isInside, out int sourceLength)
    {
        IReadOnlyList<VisibleLine> visible = GetVisibleLinesProportional(hasFocus: false, caretVisible: false);
        if (visible.Count == 0)
        {
            isInside = false;
            sourceLength = 0;
            return new TextPosition(0, 0);
        }

        VisibleLine hitRow = visible[0];
        foreach (VisibleLine candidate in visible)
        {
            hitRow = candidate;
            if (y < candidate.Y + candidate.LayoutRowHeight)
                break;
        }

        ITextLayoutResult layout = GetProportionalLayout(hitRow.LineIndex);
        double layoutTop = hitRow.Y - hitRow.LayoutRowTop;
        TextLayoutHitTestResult hit = layout.HitTest(
            x - hitRow.TextX,
            y - layoutTop);

        isInside = hit.IsInside && hit.SourceLength > 0;
        sourceLength = isInside ? hit.SourceLength : 0;
        int sourceIndex = returnCharacter ? hit.SourceStartIndex : hit.CaretIndex;
        sourceIndex = Math.Clamp(sourceIndex, 0, layout.SourceText.Length);
        return new TextPosition(hitRow.LineIndex, sourceIndex);
    }

    /// <summary>
    /// Tests a pointer against the rectangular layout geometry of small-annotation source ranges.
    /// Unlike glyph hit-testing, this intentionally accepts any point inside the range's selection-like
    /// rectangle, including whitespace around the glyph ink.
    /// </summary>
    public bool TryHitTestAnnotationMarkers(
        int lineIndex,
        IReadOnlyList<TextAnnotationMarker> markers,
        double x,
        double y,
        out TextPosition position)
    {
        position = default;
        if (!UsesProportionalLayout || markers.Count == 0
            || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        IReadOnlyList<VisibleLine> visible = GetVisibleLinesProportional(
            hasFocus: false, caretVisible: false);
        VisibleLine? hitRow = null;
        foreach (VisibleLine candidate in visible)
        {
            if (candidate.LineIndex == lineIndex
                && y >= candidate.Y
                && y < candidate.Y + candidate.LayoutRowHeight)
            {
                hitRow = candidate;
                break;
            }
        }

        if (hitRow is null)
            return false;

        ITextLayoutResult layout = GetProportionalLayout(lineIndex);
        double layoutTop = hitRow.Y - hitRow.LayoutRowTop;
        double localX = x - hitRow.TextX;
        double localY = y - layoutTop;
        TextLayoutHitTestResult pointerHit = layout.HitTest(localX, localY);
        int nearestCaret = Math.Clamp(pointerHit.CaretIndex, 0, layout.SourceText.Length);

        int fallbackColumn = -1;
        int fallbackDistance = int.MaxValue;
        foreach (TextAnnotationMarker marker in markers)
        {
            int start = Math.Max(
                hitRow.SourceStartIndex,
                Math.Clamp(marker.StartColumn, 0, layout.SourceText.Length));
            int end = Math.Min(
                hitRow.SourceEndIndex,
                Math.Clamp(marker.EndColumn, 0, layout.SourceText.Length));
            if (end <= start)
                continue;

            foreach (TextLayoutRect rectangle in layout.GetRangeGeometry(start, end - start))
            {
                double top = Math.Max(hitRow.LayoutRowTop, rectangle.Y);
                double bottom = Math.Min(
                    hitRow.LayoutRowTop + hitRow.LayoutRowHeight,
                    rectangle.Y + rectangle.Height);
                if (rectangle.Width <= 0 || bottom <= top
                    || localX < rectangle.X || localX > rectangle.X + rectangle.Width
                    || localY < top || localY > bottom)
                {
                    continue;
                }

                int lastCaret = Math.Max(start, layout.GetPreviousCaretIndex(end));
                int candidateColumn = Math.Clamp(nearestCaret, start, lastCaret);
                if (nearestCaret >= start && nearestCaret < end)
                {
                    position = new TextPosition(lineIndex, nearestCaret);
                    return true;
                }

                int distance = Math.Abs(candidateColumn - nearestCaret);
                if (distance < fallbackDistance)
                {
                    fallbackDistance = distance;
                    fallbackColumn = candidateColumn;
                }
            }
        }

        if (fallbackColumn < 0)
            return false;

        position = new TextPosition(lineIndex, fallbackColumn);
        return true;
    }

    private static IReadOnlyList<HighlightRect> ClipProportionalHighlights(
        IReadOnlyList<(int Start, int Length)> spans,
        int clipStart,
        int clipEnd,
        IReadOnlyList<(int Start, int Length)>? exclude = null)
    {
        if (spans.Count == 0)
            return NoHighlights;

        var result = new List<HighlightRect>();
        foreach ((int start, int length) in spans)
        {
            if (exclude is not null && Coincides(exclude, start, length))
                continue;

            int end = (int)Math.Clamp((long)start + length, 0L, int.MaxValue);
            int clippedStart = Math.Max(start, clipStart);
            int clippedEnd = Math.Min(end, clipEnd);
            if (clippedEnd > clippedStart)
                result.Add(new HighlightRect(0, 0, clippedStart, clippedEnd - clippedStart));
        }

        return result.Count > 0 ? result : NoHighlights;
    }

    private static IReadOnlyList<RuleHighlightRect> ClipProportionalRuleHighlights(
        IReadOnlyList<HighlightSpan> spans,
        int clipStart,
        int clipEnd)
    {
        if (spans.Count == 0)
            return NoRuleHighlights;

        var result = new List<RuleHighlightRect>();
        foreach (HighlightSpan span in spans)
        {
            int end = (int)Math.Clamp((long)span.Start + span.Length, 0L, int.MaxValue);
            int clippedStart = Math.Max(span.Start, clipStart);
            int clippedEnd = Math.Min(end, clipEnd);
            if (clippedEnd > clippedStart)
                result.Add(new RuleHighlightRect(0, 0, span.Argb, clippedStart, clippedEnd - clippedStart));
        }

        return result.Count > 0 ? result : NoRuleHighlights;
    }

    private bool TryGetProportionalVisualPosition(TextPosition position, out int row)
    {
        row = 0;
        if (proportionalLayoutProvider is null || provider is null ||
            position.Line < 0 || position.Line >= totalLines)
            return false;

        ITextLayoutResult layout = GetProportionalLayout(position.Line);
        row = FindVisualRowForCaret(layout.VisualLines, position.Column);
        return true;
    }
}
