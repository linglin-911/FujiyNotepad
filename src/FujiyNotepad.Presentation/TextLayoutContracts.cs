namespace FujiyNotepad.Presentation;

/// <summary>
/// Immutable options for laying out one logical source line. Widths and coordinates use the renderer's
/// logical units (DIPs for the Win2D implementation); text positions always use UTF-16 offsets in the
/// original, unmodified source line.
/// </summary>
public sealed record TextLayoutOptions
{
    public TextLayoutOptions(
        string fontFamily,
        double fontSize,
        double maxWidth,
        bool wordWrap,
        double lineSpacingMultiplier = 1.0,
        int tabSize = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);

        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize), "Font size must be a finite positive value.");

        if (!double.IsFinite(maxWidth) || maxWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "Layout width must be a finite positive value.");

        if (!double.IsFinite(lineSpacingMultiplier) || lineSpacingMultiplier <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(lineSpacingMultiplier), "Line-spacing multiplier must be a finite positive value.");

        if (tabSize < 1)
            throw new ArgumentOutOfRangeException(nameof(tabSize), "Tab size must be at least one space advance.");

        FontFamily = fontFamily;
        FontSize = fontSize;
        MaxWidth = maxWidth;
        WordWrap = wordWrap;
        LineSpacingMultiplier = lineSpacingMultiplier;
        TabSize = tabSize;
    }

    /// <summary>The installed font-family name requested by the reader.</summary>
    public string FontFamily { get; }

    /// <summary>Font size in the renderer's logical units.</summary>
    public double FontSize { get; }

    /// <summary>Maximum layout width in the renderer's logical units.</summary>
    public double MaxWidth { get; }

    /// <summary>Whether the logical source line may produce multiple visual rows.</summary>
    public bool WordWrap { get; }

    /// <summary>
    /// Multiplier applied to the font's natural line spacing. A value of 1.0 means natural spacing;
    /// values above or below 1.0 expand or tighten the baseline distance.
    /// </summary>
    public double LineSpacingMultiplier { get; }

    /// <summary>
    /// Tab-stop width measured in advances of the current font's space character. The layout provider
    /// must measure the active font; it must not assume that every source character has the same width.
    /// </summary>
    public int TabSize { get; }
}

/// <summary>
/// One visual row produced from a logical source line. Source indexes are local UTF-16 offsets into the
/// exact source string passed to the provider. The source range is half-open: [SourceStartIndex, SourceEndIndex).
/// Top, Baseline and Height are relative to the layout result's origin.
/// </summary>
public readonly record struct TextLayoutVisualLine(
    int SourceStartIndex,
    int SourceEndIndex,
    double Top,
    double Baseline,
    double Height)
{
    public int SourceLength => SourceEndIndex - SourceStartIndex;
}

/// <summary>
/// The source-text character or shaping cluster hit by a pointer. SourceStartIndex and SourceLength are
/// UTF-16 code-unit coordinates in the original source string, not indexes into an expanded display string.
/// </summary>
public readonly record struct TextLayoutHitTestResult
{
    public TextLayoutHitTestResult(
        int sourceStartIndex,
        int sourceLength,
        bool isTrailingHit,
        bool isInside)
    {
        if (sourceStartIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceStartIndex));

        if (sourceLength < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLength));

        SourceStartIndex = sourceStartIndex;
        SourceLength = sourceLength;
        IsTrailingHit = isTrailingHit;
        IsInside = isInside;
    }

    public int SourceStartIndex { get; }
    public int SourceLength { get; }
    public bool IsTrailingHit { get; }
    public bool IsInside { get; }

    /// <summary>Source UTF-16 caret boundary; multi-unit clusters advance by their complete source length.</summary>
    public int CaretIndex => IsTrailingHit ? checked(SourceStartIndex + SourceLength) : SourceStartIndex;
}

/// <summary>A caret position and height in layout-local coordinates.</summary>
public readonly record struct TextCaretGeometry(double X, double Y, double Height);

/// <summary>A layout-local rectangle. A source range may produce multiple rectangles.</summary>
public readonly record struct TextLayoutRect(double X, double Y, double Width, double Height);

/// <summary>
/// Shared vertical geometry for one visual row. Glyph bounds come from the renderer's actual draw bounds;
/// selection bounds add only the padding available inside that visual row. The annotation underline is
/// placed within the remaining lower padding and is disabled when no safe space exists.
/// </summary>
public readonly record struct TextLayoutVerticalGeometry(
    double GlyphTop,
    double GlyphBottom,
    double SelectionTop,
    double SelectionHeight,
    double AnnotationUnderlineY,
    double AnnotationUnderlineThickness)
{
    public bool HasAnnotationUnderline =>
        double.IsFinite(AnnotationUnderlineY) &&
        double.IsFinite(AnnotationUnderlineThickness) &&
        AnnotationUnderlineThickness > 0;
}

/// <summary>
/// Creates disposable text-layout results for individual logical source lines. The implementation belongs
/// in the UI/rendering layer; Presentation deliberately has no WinUI, Win2D or DirectWrite dependency.
/// </summary>
public interface ITextLayoutProvider
{
    /// <summary>
    /// Creates a layout for one original decoded source line with its line terminator removed. Empty lines
    /// are valid. Any visual projection, including tab expansion, must be mapped back to source UTF-16 indexes.
    /// </summary>
    ITextLayoutResult CreateLayout(string sourceText, TextLayoutOptions options);
}

/// <summary>
/// A disposable layout of one logical source line. It may contain several visual rows when wrapping is enabled.
/// All indexes are zero-based UTF-16 offsets into SourceText; all geometry is local to the layout origin.
/// Implementations backed by native graphics resources may be UI-thread-affine.
/// </summary>
public interface ITextLayoutResult : IDisposable
{
    string SourceText { get; }
    double LayoutWidth { get; }
    /// <summary>Full vertical extent from the layout origin through its bottom edge, in renderer logical units.</summary>
    double LayoutHeight { get; }
    /// <summary>
    /// Visual rows in source order. Their combined vertical coverage must reach LayoutHeight; the final row
    /// may include trailing layout space below its baseline interval.
    /// </summary>
    IReadOnlyList<TextLayoutVisualLine> VisualLines { get; }

    /// <summary>
    /// Returns caret geometry for a source UTF-16 position. When trailingSideOfCharacter is false, the
    /// index is the leading caret boundary; when true, it identifies the source character/cluster whose
    /// trailing caret edge is requested. Pass SourceText.Length with false for the end-of-line boundary.
    /// </summary>
    TextCaretGeometry GetCaretGeometry(int sourceUtf16Index, bool trailingSideOfCharacter = false);

    /// <summary>Hit-tests layout-local coordinates and returns an original-source UTF-16 range.</summary>
    TextLayoutHitTestResult HitTest(double x, double y);

    /// <summary>
    /// Returns one or more rectangles for an original-source UTF-16 range. Advance whitespace is included,
    /// so selection backgrounds and annotation markers cover spaces as well as visible glyph ink.
    /// The vertical band is shared with GetVisualRowVerticalGeometry for the corresponding visual row.
    /// </summary>
    IReadOnlyList<TextLayoutRect> GetRangeGeometry(int sourceUtf16Start, int sourceUtf16Length);

    /// <summary>
    /// Returns actual glyph bounds, the padded selection band and a safe annotation underline position for
    /// the visual row containing the supplied original-source UTF-16 index.
    /// </summary>
    TextLayoutVerticalGeometry GetVisualRowVerticalGeometry(int sourceUtf16Index);

    /// <summary>Returns the previous valid caret stop before the supplied source UTF-16 boundary.</summary>
    int GetPreviousCaretIndex(int sourceUtf16Index);

    /// <summary>Returns the next valid caret stop after the supplied source UTF-16 boundary.</summary>
    int GetNextCaretIndex(int sourceUtf16Index);
}
