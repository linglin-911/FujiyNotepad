namespace FujiyNotepad.Presentation;

/// <summary>
/// Converts a visual row's original CanvasTextLayout.LayoutBounds into separate text-hit, selection and
/// annotation geometry. LayoutBounds are already expressed in the source layout's DIP coordinate space.
/// </summary>
public static class TextAreaGeometry
{
    public const double SelectionPaddingDip = 1.5;
    public const double AnnotationUnderlineGapDip = 0.25;
    public const double MaximumAnnotationUnderlineThicknessDip = 1.25;
    public const double MinimumAnnotationUnderlineThicknessDip = 0.5;

    public static TextLayoutVerticalGeometry FromLayoutBounds(
        TextLayoutVisualLine row,
        double layoutBoundsTop,
        double layoutBoundsHeight)
    {
        if (!double.IsFinite(row.Top) ||
            !double.IsFinite(row.Height) ||
            row.Height <= 0 ||
            !double.IsFinite(layoutBoundsTop) ||
            !double.IsFinite(layoutBoundsHeight) ||
            layoutBoundsHeight <= 0)
        {
            return new TextLayoutVerticalGeometry(0, 0, 0, 0, double.NaN, 0);
        }

        // LayoutBounds are relative to the original CanvasTextLayout. Do not translate them using caret
        // coordinates or a temporary layout: those values have different semantic roles.
        double textTop = layoutBoundsTop;
        double textBottom = textTop + layoutBoundsHeight;
        double rowBottom = row.Top + row.Height;
        if (!double.IsFinite(textTop) ||
            !double.IsFinite(textBottom) ||
            !double.IsFinite(rowBottom) ||
            textBottom <= textTop)
        {
            return new TextLayoutVerticalGeometry(0, 0, 0, 0, double.NaN, 0);
        }

        // Preserve the complete text layout band. Only decorative padding is constrained to the available
        // space inside this visual row, so selection does not spill into the configured line spacing.
        double topPadding = Math.Min(SelectionPaddingDip, Math.Max(0, textTop - row.Top));
        double bottomPadding = Math.Min(SelectionPaddingDip, Math.Max(0, rowBottom - textBottom));
        double selectionTop = textTop - topPadding;
        double selectionBottom = textBottom + bottomPadding;

        // The annotation stroke sits below the text layout band, inside the remaining lower padding.
        // If the row has insufficient room, omit it rather than drawing through text or into the next row.
        double underlineThickness = Math.Min(
            MaximumAnnotationUnderlineThicknessDip,
            Math.Max(0, bottomPadding - AnnotationUnderlineGapDip));
        if (underlineThickness < MinimumAnnotationUnderlineThicknessDip)
            underlineThickness = 0;

        double underlineY = underlineThickness > 0
            ? textBottom + AnnotationUnderlineGapDip + underlineThickness / 2
            : double.NaN;

        return new TextLayoutVerticalGeometry(
            textTop,
            textBottom,
            selectionTop,
            Math.Max(0, selectionBottom - selectionTop),
            underlineY,
            underlineThickness);
    }
}
