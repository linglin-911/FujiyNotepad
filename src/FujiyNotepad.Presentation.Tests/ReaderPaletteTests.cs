namespace FujiyNotepad.Presentation.Tests
{
    public class ReaderPaletteTests
    {
        private static readonly HighContrastColors HighContrast = new(
            Window: 0xFF110000u,
            WindowText: 0xFF220000u,
            Highlight: 0xFF330000u,
            GrayText: 0xFF440000u,
            Hotlight: 0xFF550000u);

        [Fact]
        public void Resolve_Paper_ReturnsPaperPalette()
        {
            Assert.Equal(ReaderPalette.Paper,
                ReaderPalette.Resolve("Paper", isHighContrast: false, HighContrast));
        }

        [Fact]
        public void Resolve_Warm_ReturnsWarmPalette()
        {
            Assert.Equal(ReaderPalette.Warm,
                ReaderPalette.Resolve("Warm", isHighContrast: false, HighContrast));
        }

        [Fact]
        public void Resolve_Night_ReturnsNightPalette()
        {
            Assert.Equal(ReaderPalette.Night,
                ReaderPalette.Resolve("Night", isHighContrast: false, HighContrast));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unknown")]
        [InlineData("FollowApp")]
        public void Resolve_UnknownOrMissingTheme_FallsBackToPaper(string? theme)
        {
            Assert.Equal(ReaderPalette.Paper,
                ReaderPalette.Resolve(theme, isHighContrast: false, HighContrast));
        }

        [Theory]
        [InlineData("Paper")]
        [InlineData("Warm")]
        [InlineData("Night")]
        public void Resolve_HighContrastOverridesEveryReaderTheme(string theme)
        {
            CanvasColors colors = ReaderPalette.Resolve(theme, isHighContrast: true, HighContrast);

            Assert.Equal(HighContrast.Window, colors.Background);
            Assert.Equal(HighContrast.WindowText, colors.Text);
            Assert.Equal(HighContrast.WindowText, colors.Caret);
            Assert.Equal(HighContrast.Highlight, colors.Selection);
            Assert.Equal(HighContrast.Hotlight, colors.MatchHighlight);
            Assert.Equal(HighContrast.Hotlight, colors.SelectionOccurrence);
            Assert.Equal(HighContrast.GrayText, colors.GutterText);
            Assert.Equal(HighContrast.GrayText, colors.GutterSeparator);
            Assert.Equal(HighContrast.Hotlight, colors.Bookmark);
            Assert.Equal(HighContrast.GrayText, colors.Whitespace);
            Assert.Equal(HighContrast.Hotlight, colors.TrailingWhitespace);
            Assert.Equal(HighContrast.Hotlight, colors.ControlChar);
            Assert.Equal(HighContrast.Hotlight, colors.AnnotationMarker);
        }

        [Fact]
        public void ReaderPresets_AreIndependentAndDistinct()
        {
            Assert.NotEqual(ReaderPalette.Paper, ReaderPalette.Warm);
            Assert.NotEqual(ReaderPalette.Paper, ReaderPalette.Night);
            Assert.NotEqual(ReaderPalette.Warm, ReaderPalette.Night);
        }
    }
}
