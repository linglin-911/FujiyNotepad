namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Resolves palettes used only by the novel-reading surface. This deliberately does not inspect or follow
    /// the application's Light/Dark theme; the reader theme is a global, independent preference.
    /// </summary>
    public static class ReaderPalette
    {
        private static readonly CanvasColors PaperPalette = new(
            Background: 0xFFFFFCF5u,
            Text: 0xFF302D27u,
            Caret: 0xFF302D27u,
            Selection: 0xFFB9D7EFu,
            MatchHighlight: 0xFFFFE29Au,
            SelectionOccurrence: 0x805B9C75u,
            GutterText: 0xFF928B7Du,
            GutterSeparator: 0xFFE7E0D3u,
            Bookmark: 0xFF397BA8u,
            Whitespace: 0xB27B766Bu,
            TrailingWhitespace: 0xE0B44D3Fu,
            ControlChar: 0xE0B44D3Fu,
            AnnotationMarker: 0xFFC57A20u);

        private static readonly CanvasColors WarmPalette = new(
            Background: 0xFFF2E6D0u,
            Text: 0xFF3D3025u,
            Caret: 0xFF3D3025u,
            Selection: 0xFFC5D9C5u,
            MatchHighlight: 0xFFFFD27Fu,
            SelectionOccurrence: 0x805D8B60u,
            GutterText: 0xFF8D7D68u,
            GutterSeparator: 0xFFDBCEB8u,
            Bookmark: 0xFF557F78u,
            Whitespace: 0xB27C6D5Cu,
            TrailingWhitespace: 0xE0B35A48u,
            ControlChar: 0xE0B35A48u,
            AnnotationMarker: 0xFFB56B2Au);

        private static readonly CanvasColors NightPalette = new(
            Background: 0xFF1D2028u,
            Text: 0xFFE3E5EAu,
            Caret: 0xFFE3E5EAu,
            Selection: 0xFF354D68u,
            MatchHighlight: 0xFF66521Fu,
            SelectionOccurrence: 0x804B9568u,
            GutterText: 0xFF9299A6u,
            GutterSeparator: 0xFF373C47u,
            Bookmark: 0xFF79B8E6u,
            Whitespace: 0xB9C4CAD5u,
            TrailingWhitespace: 0xE0E17B7Bu,
            ControlChar: 0xE0E17B7Bu,
            AnnotationMarker: 0xFFE0A45Cu);

        public static CanvasColors Paper => PaperPalette;
        public static CanvasColors Warm => WarmPalette;
        public static CanvasColors Night => NightPalette;

        /// <summary>
        /// Resolves the selected reader preset. High Contrast is the only system override and takes precedence
        /// over all reader presets. Unknown or missing keys safely fall back to Paper.
        /// </summary>
        public static CanvasColors Resolve(
            string? theme,
            bool isHighContrast,
            HighContrastColors highContrast)
        {
            if (isHighContrast)
            {
                return new CanvasColors(
                    Background: highContrast.Window,
                    Text: highContrast.WindowText,
                    Caret: highContrast.WindowText,
                    Selection: highContrast.Highlight,
                    MatchHighlight: highContrast.Hotlight,
                    SelectionOccurrence: highContrast.Hotlight,
                    GutterText: highContrast.GrayText,
                    GutterSeparator: highContrast.GrayText,
                    Bookmark: highContrast.Hotlight,
                    Whitespace: highContrast.GrayText,
                    TrailingWhitespace: highContrast.Hotlight,
                    ControlChar: highContrast.Hotlight,
                    AnnotationMarker: highContrast.Hotlight);
            }

            return theme?.Trim().ToLowerInvariant() switch
            {
                "warm" => WarmPalette,
                "night" => NightPalette,
                "paper" or null or "" => PaperPalette,
                _ => PaperPalette,
            };
        }
    }
}
