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
                    double rowHeight = GetEffectiveRowHeight(
                        layout, row, isLastRow: rowIndex == visualRows.Count - 1);
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
                double rowHeight = GetEffectiveRowHeight(layout, row, isLastRow: true);
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

    // The native layout can extend below the baseline interval represented by the visual rows (for example,
    // when fallback glyph metrics or trailing layout bounds are taller than the measured caret-row spacing).
    // Reserve that remaining extent on the final row so the next logical source line never starts inside it.
    private static double GetEffectiveRowHeight(
        ITextLayoutResult layout,
        TextLayoutVisualLine row,
        bool isLastRow)
    {
        double rowHeight = GetSafeRowHeight(row);
        if (!isLastRow || !double.IsFinite(row.Top))
            return rowHeight;

        double layoutHeight = layout.LayoutHeight;
        if (!double.IsFinite(layoutHeight) || layoutHeight <= 0)
            return rowHeight;

        double remainingLayoutHeight = layoutHeight - row.Top;
        return double.IsFinite(remainingLayoutHeight) && remainingLayoutHeight > rowHeight
            ? remainingLayoutHeight
            : rowHeight;
    }

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
        => HitTestCharacter(x, y, out _, out _, out _);

    /// <summary>
    /// Hit-tests the source character under a point and reports whether the pointer actually landed on a
    /// character region. The nearest-boundary position is still returned when it did not. Small-annotation
    /// clicks use the separate selection-rectangle hit test below rather than requiring a glyph hit.
    /// </summary>
    public TextPosition HitTestCharacter(double x, double y, out bool isInside)
        => HitTestCharacter(x, y, out isInside, out _);

    /// <summary>
    /// Also returns the full source range of the hit character/shaping cluster. This matters for ligatures,
    /// combining sequences and other glyphs whose visual hit region can represent multiple UTF-16 units.
    /// </summary>
    public TextPosition HitTestCharacter(
        double x, double y, out bool isInside, out int sourceLength)
        => HitTestCharacter(x, y, out isInside, out sourceLength, out _);

    public TextPosition HitTestCharacter(
        double x, double y, out bool isInside, out int sourceLength, out bool isTrailingHit)
    {
        if (!UsesProportionalLayout)
        {
            isInside = false;
            sourceLength = 0;
            isTrailingHit = false;
            return HitTest(x, y);
        }

        return HitTestProportional(
            x, y, returnCharacter: true, out isInside, out sourceLength, out isTrailingHit);
    }

    private TextPosition HitTestProportional(
        double x, double y, bool returnCharacter,
        out bool isInside, out int sourceLength, out bool isTrailingHit)
    {
        IReadOnlyList<VisibleLine> visible = GetVisibleLinesProportional(hasFocus: false, caretVisible: false);
        if (visible.Count == 0)
        {
            isInside = false;
            sourceLength = 0;
            isTrailingHit = false;
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
        double localX = x - hitRow.TextX;
        double localY = y - layoutTop;
        double hitTestY = localY;

        if (returnCharacter && hitRow.SourceEndIndex > hitRow.SourceStartIndex)
        {
            // Use the actual DrawBounds center of this visual row only to resolve the horizontal hit and
            // DirectWrite leading/trailing side. The real pointer Y is validated against the target
            // character's own shared text rectangle immediately below.
            TextLayoutVerticalGeometry rowGeometry =
                layout.GetVisualRowVerticalGeometry(hitRow.SourceStartIndex);
            if (rowGeometry.GlyphBottom > rowGeometry.GlyphTop)
                hitTestY = rowGeometry.GlyphTop
                           + (rowGeometry.GlyphBottom - rowGeometry.GlyphTop) / 2;
        }

        // Resolve the character from its horizontal position on the row's text band, then validate the
        // actual pointer Y against that character's text rectangle. This prevents line-spacing hits from
        // being mistaken for glyph hits and keeps the same X mapped to the same character while dragging.
        TextLayoutHitTestResult hit = layout.HitTest(localX, hitTestY);

        if (returnCharacter && !hit.IsInside && hitRow.SourceEndIndex > hitRow.SourceStartIndex)
        {
            // The text rectangle is intentionally a little wider than DirectWrite's native region. Recover
            // the first/last character when the pointer lands in that small outer cushion.
            int firstEnd = layout.GetNextCaretIndex(hitRow.SourceStartIndex);
            if (firstEnd > hitRow.SourceStartIndex &&
                IsInsideTextRangeGeometry(
                    layout, hitRow.SourceStartIndex, firstEnd - hitRow.SourceStartIndex, localX, localY))
            {
                hit = new TextLayoutHitTestResult(
                    hitRow.SourceStartIndex, firstEnd - hitRow.SourceStartIndex,
                    isTrailingHit: IsTrailingHalfOfCharacter(
                        layout, hitRow.SourceStartIndex, localX),
                    isInside: true);
            }
            else
            {
                int lastStart = layout.GetPreviousCaretIndex(hitRow.SourceEndIndex);
                if (lastStart < hitRow.SourceEndIndex &&
                    IsInsideTextRangeGeometry(
                        layout, lastStart, hitRow.SourceEndIndex - lastStart, localX, localY))
                {
                    hit = new TextLayoutHitTestResult(
                        lastStart, hitRow.SourceEndIndex - lastStart,
                        isTrailingHit: IsTrailingHalfOfCharacter(layout, lastStart, localX),
                        isInside: true);
                }
            }
        }

        if (returnCharacter)
        {
            bool hitTextArea = hit.IsInside && hit.SourceLength > 0 &&
                IsInsideTextRangeGeometry(layout, hit.SourceStartIndex, hit.SourceLength, localX, localY);

            if (!hitTextArea)
            {
                int boundary = GetTextRowBoundaryForMiss(
                    layout, hitRow, localX, localY, hit.CaretIndex);
                isInside = false;
                sourceLength = 0;
                isTrailingHit = false;
                return new TextPosition(
                    hitRow.LineIndex,
                    Math.Clamp(boundary, hitRow.SourceStartIndex, hitRow.SourceEndIndex));
            }

            isInside = true;
            sourceLength = hit.SourceLength;
            isTrailingHit = hit.IsTrailingHit;
            return new TextPosition(
                hitRow.LineIndex,
                Math.Clamp(hit.SourceStartIndex, 0, layout.SourceText.Length));
        }

        isInside = hit.IsInside && hit.SourceLength > 0;
        sourceLength = isInside ? hit.SourceLength : 0;
        isTrailingHit = hit.IsTrailingHit;
        int sourceIndex = Math.Clamp(hit.CaretIndex, 0, layout.SourceText.Length);
        return new TextPosition(hitRow.LineIndex, sourceIndex);
    }

    private static bool IsTrailingHalfOfCharacter(
        ITextLayoutResult layout,
        int sourceStart,
        double localX)
    {
        TextCaretGeometry leading = layout.GetCaretGeometry(sourceStart);
        TextCaretGeometry trailing = layout.GetCaretGeometry(
            sourceStart, trailingSideOfCharacter: true);
        if (!double.IsFinite(leading.X) ||
            !double.IsFinite(trailing.X) ||
            Math.Abs(trailing.X - leading.X) < 0.001)
        {
            return false;
        }

        double midpoint = (leading.X + trailing.X) / 2;
        return trailing.X > leading.X ? localX >= midpoint : localX <= midpoint;
    }

    private static bool IsInsideTextRangeGeometry(
        ITextLayoutResult layout,
        int sourceStart,
        int sourceLength,
        double x,
        double y)
    {
        const double tolerance = 0.25;
        foreach (TextLayoutRect rectangle in layout.GetRangeGeometry(sourceStart, sourceLength))
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                continue;

            if (x >= rectangle.X - tolerance &&
                x <= rectangle.X + rectangle.Width + tolerance &&
                y >= rectangle.Y - tolerance &&
                y <= rectangle.Y + rectangle.Height + tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetTextRowBoundaryForMiss(
        ITextLayoutResult layout,
        VisibleLine visibleRow,
        double localX,
        double localY,
        int fallbackCaret)
    {
        int rowStart = Math.Clamp(visibleRow.SourceStartIndex, 0, layout.SourceText.Length);
        int rowEnd = Math.Clamp(visibleRow.SourceEndIndex, rowStart, layout.SourceText.Length);
        if (rowEnd <= rowStart)
            return rowStart;

        int firstEnd = layout.GetNextCaretIndex(rowStart);
        if (firstEnd <= rowStart || firstEnd > rowEnd)
            firstEnd = Math.Min(rowEnd, rowStart + 1);

        int lastStart = layout.GetPreviousCaretIndex(rowEnd);
        if (lastStart < rowStart || lastStart >= rowEnd)
            lastStart = Math.Max(rowStart, rowEnd - 1);

        IReadOnlyList<TextLayoutRect> firstRects =
            layout.GetRangeGeometry(rowStart, firstEnd - rowStart);
        IReadOnlyList<TextLayoutRect> lastRects =
            layout.GetRangeGeometry(lastStart, rowEnd - lastStart);

        if (firstRects.Count > 0 && lastRects.Count > 0)
        {
            double firstLeft = firstRects.Min(rectangle => rectangle.X);
            double firstRight = firstRects.Max(rectangle => rectangle.X + rectangle.Width);
            double firstTop = firstRects.Min(rectangle => rectangle.Y);
            double firstBottom = firstRects.Max(rectangle => rectangle.Y + rectangle.Height);

            double lastLeft = lastRects.Min(rectangle => rectangle.X);
            double lastRight = lastRects.Max(rectangle => rectangle.X + rectangle.Width);
            double lastTop = lastRects.Min(rectangle => rectangle.Y);
            double lastBottom = lastRects.Max(rectangle => rectangle.Y + rectangle.Height);

            double textLeft = Math.Min(firstLeft, lastLeft);
            double textRight = Math.Max(firstRight, lastRight);
            double textTop = Math.Min(firstTop, lastTop);
            double textBottom = Math.Max(firstBottom, lastBottom);

            // Leaving the text rectangle vertically selects to the corresponding visual-row boundary.
            // This is what makes a downward drag finish the current row before entering the next one.
            if (localY < textTop)
                return rowStart;
            if (localY > textBottom)
                return rowEnd;

            if (localX < textLeft)
                return rowStart;
            if (localX > textRight)
                return rowEnd;
        }

        return Math.Clamp(fallbackCaret, rowStart, rowEnd);
    }

    /// <summary>
    /// Tests a pointer against the rectangular layout geometry of small-annotation source ranges.
    /// Unlike glyph hit-testing, this intentionally accepts any point inside the range's selection-like
    /// rectangle, including whitespace around the glyph ink.
    /// </summary>
    public bool TryHitTestAnnotationMarkers(
        double x,
        double y,
        IReadOnlyDictionary<int, List<TextAnnotationMarker>> markersByLine,
        out TextPosition position)
        => TryHitTestAnnotationMarkers(x, y, markersByLine, out position, out _, out _);

    public bool TryHitTestAnnotationMarkers(
        double x,
        double y,
        IReadOnlyDictionary<int, List<TextAnnotationMarker>> markersByLine,
        out TextPosition position,
        out bool isTrailingHit)
        => TryHitTestAnnotationMarkers(x, y, markersByLine, out position, out _, out isTrailingHit);

    public bool TryHitTestAnnotationMarkers(
        double x,
        double y,
        IReadOnlyDictionary<int, List<TextAnnotationMarker>> markersByLine,
        out TextPosition position,
        out int sourceLength,
        out bool isTrailingHit)
    {
        position = default;
        sourceLength = 0;
        isTrailingHit = false;
        if (!UsesProportionalLayout || markersByLine.Count == 0
            || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        IReadOnlyList<VisibleLine> visible = GetVisibleLinesProportional(
            hasFocus: false, caretVisible: false);
        VisibleLine hitRow = default;
        bool foundVisibleRow = false;
        foreach (VisibleLine candidate in visible)
        {
            if (y >= candidate.Y
                && y < candidate.Y + candidate.LayoutRowHeight)
            {
                hitRow = candidate;
                foundVisibleRow = true;
                break;
            }
        }

        if (!foundVisibleRow
            || !markersByLine.TryGetValue(hitRow.LineIndex, out List<TextAnnotationMarker>? markers))
        {
            return false;
        }

        ITextLayoutResult layout = GetProportionalLayout(hitRow.LineIndex);
        double layoutTop = hitRow.Y - hitRow.LayoutRowTop;
        double localX = x - hitRow.TextX;
        double localY = y - layoutTop;
        TextLayoutVerticalGeometry rowGeometry =
            layout.GetVisualRowVerticalGeometry(hitRow.SourceStartIndex);
        double hitTestY = rowGeometry.GlyphTop + (rowGeometry.GlyphBottom - rowGeometry.GlyphTop) / 2;
        TextLayoutHitTestResult pointerHit = layout.HitTest(localX, hitTestY);
        int nearestCaret = Math.Clamp(pointerHit.CaretIndex, 0, layout.SourceText.Length);

        int intersectStart = 0;
        int intersectEnd = layout.SourceText.Length;
        bool foundRectangle = false;
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
                double top = rectangle.Y;
                double bottom = rectangle.Y + rectangle.Height;
                if (rectangle.Width <= 0 || bottom <= top
                    || localX < rectangle.X || localX > rectangle.X + rectangle.Width
                    || localY < top || localY >= bottom)
                {
                    continue;
                }

                foundRectangle = true;
                intersectStart = Math.Max(intersectStart, start);
                intersectEnd = Math.Min(intersectEnd, end);

                int lastCaret = Math.Max(start, layout.GetPreviousCaretIndex(end));
                int preferredColumn = pointerHit.IsInside && pointerHit.SourceLength > 0
                    ? pointerHit.SourceStartIndex
                    : nearestCaret;
                int candidateColumn = Math.Clamp(preferredColumn, start, lastCaret);
                int distance = Math.Abs(candidateColumn - nearestCaret);
                if (distance < fallbackDistance)
                {
                    fallbackDistance = distance;
                    fallbackColumn = candidateColumn;
                }
            }
        }

        if (!foundRectangle)
            return false;

        // If the pointer is inside multiple overlapping marker rectangles, prefer a source offset in
        // their common range so the host can reveal all of the overlapping annotations in one click.
        if (intersectEnd > intersectStart)
        {
            int lastCommonCaret = Math.Max(
                intersectStart, layout.GetPreviousCaretIndex(intersectEnd));
            int preferredColumn = pointerHit.IsInside && pointerHit.SourceLength > 0
                ? pointerHit.SourceStartIndex
                : nearestCaret;
            int selectedColumn = Math.Clamp(preferredColumn, intersectStart, lastCommonCaret);
            position = new TextPosition(hitRow.LineIndex, selectedColumn);
            if (pointerHit.IsInside && pointerHit.SourceLength > 0
                && selectedColumn == pointerHit.SourceStartIndex)
            {
                sourceLength = pointerHit.SourceLength;
                isTrailingHit = pointerHit.IsTrailingHit;
            }
            else
            {
                int characterEnd = layout.GetNextCaretIndex(selectedColumn);
                if (characterEnd <= selectedColumn && selectedColumn > 0)
                {
                    selectedColumn = layout.GetPreviousCaretIndex(selectedColumn);
                    position = new TextPosition(hitRow.LineIndex, selectedColumn);
                    characterEnd = layout.GetNextCaretIndex(selectedColumn);
                }

                sourceLength = Math.Max(0, characterEnd - selectedColumn);
                isTrailingHit = IsTrailingHalfOfCharacter(layout, selectedColumn, localX);
            }

            return true;
        }

        if (fallbackColumn < 0)
            return false;

        position = new TextPosition(hitRow.LineIndex, fallbackColumn);
        if (pointerHit.IsInside && pointerHit.SourceLength > 0
            && fallbackColumn == pointerHit.SourceStartIndex)
        {
            sourceLength = pointerHit.SourceLength;
            isTrailingHit = pointerHit.IsTrailingHit;
        }
        else
        {
            int characterEnd = layout.GetNextCaretIndex(fallbackColumn);
            if (characterEnd <= fallbackColumn && fallbackColumn > 0)
            {
                fallbackColumn = layout.GetPreviousCaretIndex(fallbackColumn);
                position = new TextPosition(hitRow.LineIndex, fallbackColumn);
                characterEnd = layout.GetNextCaretIndex(fallbackColumn);
            }

            sourceLength = Math.Max(0, characterEnd - fallbackColumn);
            isTrailingHit = IsTrailingHalfOfCharacter(layout, fallbackColumn, localX);
        }

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
