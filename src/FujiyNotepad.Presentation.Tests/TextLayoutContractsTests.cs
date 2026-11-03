using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests;

public sealed class TextLayoutContractsTests
{
    [Fact]
    public void Options_PreserveReaderTypographyAndViewportSettings()
    {
        var options = new TextLayoutOptions(
            "Microsoft YaHei",
            fontSize: 18,
            maxWidth: 720,
            wordWrap: true,
            lineSpacingMultiplier: 1.6,
            tabSize: 4);

        Assert.Equal("Microsoft YaHei", options.FontFamily);
        Assert.Equal(18, options.FontSize);
        Assert.Equal(720, options.MaxWidth);
        Assert.True(options.WordWrap);
        Assert.Equal(1.6, options.LineSpacingMultiplier);
        Assert.Equal(4, options.TabSize);
    }

    [Fact]
    public void Options_RejectBlankFontFamily()
    {
        Assert.Throws<ArgumentException>(() =>
            new TextLayoutOptions(" ", 18, 720, wordWrap: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Options_RejectInvalidFontSize(double fontSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TextLayoutOptions("Microsoft YaHei", fontSize, 720, wordWrap: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Options_RejectInvalidLayoutWidth(double maxWidth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TextLayoutOptions("Microsoft YaHei", 18, maxWidth, wordWrap: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void Options_RejectInvalidLineSpacing(double lineSpacingMultiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TextLayoutOptions(
                "Microsoft YaHei", 18, 720, wordWrap: true,
                lineSpacingMultiplier: lineSpacingMultiplier));
    }

    [Fact]
    public void Options_RejectTabSizeBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TextLayoutOptions("Microsoft YaHei", 18, 720, wordWrap: true, tabSize: 0));
    }

    [Fact]
    public void HitTest_CaretIndexUsesWholeUtf16ClusterLength()
    {
        // In "A😀B", the supplementary character occupies UTF-16 indexes [1, 3).
        var leading = new TextLayoutHitTestResult(
            sourceStartIndex: 1, sourceLength: 2, isTrailingHit: false, isInside: true);
        var trailing = new TextLayoutHitTestResult(
            sourceStartIndex: 1, sourceLength: 2, isTrailingHit: true, isInside: true);

        Assert.Equal(1, leading.CaretIndex);
        Assert.Equal(3, trailing.CaretIndex);
    }

    [Fact]
    public void HitTest_AllowsWhitespaceHitAtEndOfSource()
    {
        var hit = new TextLayoutHitTestResult(
            sourceStartIndex: 5, sourceLength: 0, isTrailingHit: false, isInside: false);

        Assert.Equal(5, hit.CaretIndex);
        Assert.False(hit.IsInside);
    }

    [Fact]
    public void VisualLine_UsesHalfOpenUtf16SourceRange()
    {
        var row = new TextLayoutVisualLine(
            SourceStartIndex: 2,
            SourceEndIndex: 6,
            Top: 24,
            Baseline: 18,
            Height: 28);

        Assert.Equal(4, row.SourceLength);
    }
}
