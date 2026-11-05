namespace FujiyNotepad.Presentation;

/// <summary>
/// Converts a renderer-measured glyph DrawBounds into the shared selection and annotation geometry.
/// The measurement layout may be a temporary single-row layout; its caret origin is aligned to the
/// corresponding visual row in the original multi-row layout before any padding is applied.
/// </summary>
public static class TextAreaGeometry
{
    public const double SelectionPaddingDip = 1.5;
    public const double MaximumAnnotationUnderlineThicknessDip = 1.25;

    public static TextLayoutVerticalGeometry FromDrawBounds(
        TextLayoutVisualLine row,
        double measurementCaretTop,
        double drawTop,
        double drawHeight)
    {
        if (!double.IsFinite(row.Top) ||
            !double.IsFinite(row.Height) ||
            row.Height <= 0 ||
            !double.IsFinite(measurementCaretTop) ||
            !double.IsFinite(drawTop) ||
            !double.IsFinite(drawHeight) ||
            drawHeight <= 0)
        {
            return new TextLayoutVerticalGeometry(0, 0, 0, 0, double.NaN, 0);
        }

        // VisualLine.Top is the original layout's caret-row origin, not a glyph top or baseline.
        // Align the temporary measurement by subtracting its own caret-row origin before adding row.Top.
        double glyphTop = row.Top + drawTop - measurementCaretTop;
        double glyphBottom = glyphTop + drawHeight;
        double rowBottom = row.Top + row.Height;
        if (!double.IsFinite(glyphTop) ||
            !double.IsFinite(glyphBottom) ||
            !double.IsFinite(rowBottom) ||
            glyphBottom <= glyphTop)
        {
            return new TextLayoutVerticalGeometry(0, 0, 0, 0, double.NaN, 0);
        }

        // Never clip measured glyph ink. Decorative padding is reduced when the row has insufficient room,
        // so the selection background does not consume the configured inter-row spacing.
        double topPadding = Math.Min(SelectionPaddingDip, Math.Max(0, glyphTop - row.Top));
        double bottomPadding = Math.Min(SelectionPaddingDip, Math.Max(0, rowBottom - glyphBottom));
        double selectionTop = glyphTop - topPadding;
        double selectionBottom = glyphBottom + bottomPadding;

        // Place the annotation stroke in the center of the available lower padding. If there is no room,
        // omit the stroke instead of drawing through glyph ink or into the next visual row.
        double underlineThickness = Math.Min(MaximumAnnotationUnderlineThicknessDip, bottomPadding);
        double underlineY = underlineThickness > 0
            ? glyphBottom + bottomPadding / 2
            : double.NaN;

        return new TextLayoutVerticalGeometry(
            glyphTop,
            glyphBottom,
            selectionTop,
            Math.Max(0, selectionBottom - selectionTop),
            underlineY,
            underlineThickness);
    }
}
