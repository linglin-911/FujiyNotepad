namespace FujiyNotepad.Presentation;

/// <summary>
/// Builds the reader's single authoritative text rectangle band from a visual row's baseline.
/// The band is intentionally independent of line spacing: line spacing controls the distance between
/// baselines, not the height of the selectable text rectangle.
/// </summary>
public static class TextAreaGeometry
{
    // Matches the proven Win2D reader approach used by Uviewer: a compact band around the text baseline,
    // rather than the full LayoutBounds line box (which expands with line spacing).
    public const double TextBandHeightEm = 1.05;
    public const double TextBandTopOffsetEm = 0.90;

    public const double AnnotationUnderlineOffsetDip = 0.75;
    public const double AnnotationUnderlineThicknessDip = 1.0;

    public static TextLayoutVerticalGeometry FromBaseline(
        TextLayoutVisualLine row,
        double fontSize)
    {
        if (!double.IsFinite(row.Top) ||
            !double.IsFinite(row.Baseline) ||
            !double.IsFinite(row.Height) ||
            row.Height <= 0 ||
            !double.IsFinite(fontSize) ||
            fontSize <= 0)
        {
            return new TextLayoutVerticalGeometry(0, 0, double.NaN, 0);
        }

        double textTop = row.Baseline - fontSize * TextBandTopOffsetEm;
        double textHeight = fontSize * TextBandHeightEm;
        double textBottom = textTop + textHeight;
        double rowBottom = row.Top + row.Height;
        if (!double.IsFinite(textTop) ||
            !double.IsFinite(textBottom) ||
            !double.IsFinite(rowBottom) ||
            textBottom <= textTop)
        {
            return new TextLayoutVerticalGeometry(0, 0, double.NaN, 0);
        }

        // Prefer the small gap below the text band. When tight line spacing leaves no room, keep the
        // underline visible at the band's lower edge instead of silently suppressing the annotation.
        double availableLowerGap = Math.Max(0, rowBottom - textBottom);
        double underlineY = availableLowerGap >= AnnotationUnderlineThicknessDip
            ? textBottom + Math.Min(
                AnnotationUnderlineOffsetDip,
                availableLowerGap / 2)
            : textBottom - AnnotationUnderlineThicknessDip / 2;

        return new TextLayoutVerticalGeometry(
            textTop,
            textBottom,
            underlineY,
            AnnotationUnderlineThicknessDip);
    }
}
