namespace FujiyNotepad.Presentation
{
    /// <summary>
    /// Global settings that affect only the novel-reading surface. These values are shared by every
    /// novel project and are deliberately separate from application chrome/theme settings.
    /// </summary>
    public sealed class ReaderSettings
    {
        public const string DefaultFontFamily = "Microsoft YaHei";
        public const double DefaultFontSizeDip = 18;
        public const double DefaultLineSpacingMultiplier = 1.4;
        public const double DefaultContentWidthDip = 900;
        public const string DefaultTheme = "Paper";

        public const double MinFontSizeDip = 6;
        public const double MaxFontSizeDip = 72;
        public const double MinLineSpacingMultiplier = 0.8;
        public const double MaxLineSpacingMultiplier = 2.5;
        public const double MinContentWidthDip = 480;
        public const double MaxContentWidthDip = 2400;

        /// <summary>Installed font-family name used only by the reader surface.</summary>
        public string FontFamily { get; set; } = DefaultFontFamily;

        /// <summary>Reader font size in DIPs.</summary>
        public double FontSizeDip { get; set; } = DefaultFontSizeDip;

        /// <summary>Multiplier applied to the font's natural baseline spacing.</summary>
        public double LineSpacingMultiplier { get; set; } = DefaultLineSpacingMultiplier;

        /// <summary>
        /// Maximum reader content width in DIPs. Zero means use the full available reader width.
        /// </summary>
        public double ContentWidthDip { get; set; } = DefaultContentWidthDip;

        /// <summary>Reader-only palette key: Paper, Warm or Night.</summary>
        public string Theme { get; set; } = DefaultTheme;

        /// <summary>
        /// Normalizes persisted or user-edited values before they reach the rendering layer.
        /// Invalid values fall back independently so one bad field does not discard other preferences.
        /// </summary>
        public void Normalize()
        {
            FontFamily = string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 128
                ? DefaultFontFamily
                : FontFamily.Trim();

            if (!double.IsFinite(FontSizeDip) ||
                FontSizeDip < MinFontSizeDip ||
                FontSizeDip > MaxFontSizeDip)
            {
                FontSizeDip = DefaultFontSizeDip;
            }

            if (!double.IsFinite(LineSpacingMultiplier) ||
                LineSpacingMultiplier < MinLineSpacingMultiplier ||
                LineSpacingMultiplier > MaxLineSpacingMultiplier)
            {
                LineSpacingMultiplier = DefaultLineSpacingMultiplier;
            }

            if (!double.IsFinite(ContentWidthDip) ||
                (ContentWidthDip != 0 &&
                 (ContentWidthDip < MinContentWidthDip || ContentWidthDip > MaxContentWidthDip)))
            {
                ContentWidthDip = DefaultContentWidthDip;
            }

            Theme = Theme?.Trim().ToLowerInvariant() switch
            {
                "paper" => "Paper",
                "warm" => "Warm",
                "night" => "Night",
                _ => DefaultTheme,
            };
        }

        /// <summary>
        /// Creates the initial reader preferences from the legacy text-view font and zoom values.
        /// The legacy fields are not application-chrome typography settings.
        /// </summary>
        public static ReaderSettings FromLegacy(string? fontFamily, double fontSizeDip)
        {
            var settings = new ReaderSettings
            {
                FontFamily = fontFamily ?? DefaultFontFamily,
                FontSizeDip = fontSizeDip,
            };
            settings.Normalize();
            return settings;
        }
    }
}
