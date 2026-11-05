using FujiyNotepad.Presentation;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization;
using Windows.UI;

namespace FujiyNotepad.WinUI
{
    /// <summary>
    /// Edits a temporary ReaderSettings snapshot. It never persists settings directly;
    /// MainWindow owns preview, Apply and Cancel transaction semantics.
    /// </summary>
    public sealed partial class ReaderSettingsDialog : ContentDialog
    {
        private readonly ReaderSettings draftSettings;
        private bool isInitializing = true;
        private double lastExplicitWidthDip;

        public event Action<ReaderSettings>? PreviewChanged;

        public ReaderSettingsDialog(ReaderSettings initialSettings)
        {
            ArgumentNullException.ThrowIfNull(initialSettings);
            InitializeComponent();

            draftSettings = Clone(initialSettings);
            lastExplicitWidthDip = draftSettings.ContentWidthDip > 0
                ? draftSettings.ContentWidthDip
                : ReaderSettings.DefaultContentWidthDip;

            IReadOnlyList<string> fonts = GetInstalledFontFamilies(draftSettings.FontFamily);
            FontFamilyComboBox.ItemsSource = fonts;
            FontFamilyComboBox.SelectedItem = fonts.FirstOrDefault(
                font => string.Equals(font, draftSettings.FontFamily, StringComparison.OrdinalIgnoreCase))
                ?? draftSettings.FontFamily;

            FontSizeSlider.Value = draftSettings.FontSizeDip;
            LineSpacingSlider.Value = draftSettings.LineSpacingMultiplier;
            ContentWidthNumberBox.Value = lastExplicitWidthDip;
            AdaptiveWidthCheckBox.IsChecked = draftSettings.ContentWidthDip == 0;
            ContentWidthNumberBox.IsEnabled = draftSettings.ContentWidthDip != 0;

            PaperThemeRadio.IsChecked = draftSettings.Theme == "Paper";
            WarmThemeRadio.IsChecked = draftSettings.Theme == "Warm";
            NightThemeRadio.IsChecked = draftSettings.Theme == "Night";

            isInitializing = false;
            UpdateDraftFromControls(notifyPreview: false);
            UpdateThemeSwatches();
        }

        /// <summary>Returns a detached copy of the values currently represented by the controls.</summary>
        public ReaderSettings GetSelectedSettings()
        {
            UpdateDraftFromControls(notifyPreview: false);
            return Clone(draftSettings);
        }

        private static ReaderSettings Clone(ReaderSettings source)
        {
            var clone = new ReaderSettings
            {
                FontFamily = source.FontFamily,
                FontSizeDip = source.FontSizeDip,
                LineSpacingMultiplier = source.LineSpacingMultiplier,
                ContentWidthDip = source.ContentWidthDip,
                Theme = source.Theme,
            };
            clone.Normalize();
            return clone;
        }

        private static IReadOnlyList<string> GetInstalledFontFamilies(string currentFamily)
        {
            string[] names;
            try
            {
                names = CanvasTextFormat.GetSystemFontFamilies(ApplicationLanguages.Languages);
            }
            catch
            {
                try
                {
                    names = CanvasTextFormat.GetSystemFontFamilies();
                }
                catch
                {
                    names = Array.Empty<string>();
                }
            }

            var fonts = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Preserve a previously selected value even if its font was removed from this Windows install.
            if (!fonts.Any(name => string.Equals(name, currentFamily, StringComparison.OrdinalIgnoreCase)))
                fonts.Insert(0, currentFamily);

            return fonts;
        }

        private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateDraftFromControls();

        private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
            => UpdateDraftFromControls();

        private void LineSpacingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
            => UpdateDraftFromControls();

        private void ContentWidthNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
            => UpdateDraftFromControls();

        private void AdaptiveWidthCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (isInitializing)
                return;

            ContentWidthNumberBox.IsEnabled = AdaptiveWidthCheckBox.IsChecked != true;
            UpdateDraftFromControls();
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
            => UpdateDraftFromControls();

        private void UpdateDraftFromControls(bool notifyPreview = true)
        {
            if (isInitializing)
                return;

            if (FontFamilyComboBox.SelectedItem is string selectedFont)
                draftSettings.FontFamily = selectedFont;

            draftSettings.FontSizeDip = Math.Round(FontSizeSlider.Value);
            draftSettings.LineSpacingMultiplier = Math.Round(LineSpacingSlider.Value, 1);

            if (AdaptiveWidthCheckBox.IsChecked == true)
            {
                draftSettings.ContentWidthDip = 0;
            }
            else
            {
                double width = ContentWidthNumberBox.Value;
                if (double.IsFinite(width))
                {
                    lastExplicitWidthDip = Math.Clamp(
                        Math.Round(width),
                        ReaderSettings.MinContentWidthDip,
                        ReaderSettings.MaxContentWidthDip);
                    draftSettings.ContentWidthDip = lastExplicitWidthDip;
                }
            }

            draftSettings.Theme = GetSelectedTheme();
            draftSettings.Normalize();

            UpdateValueLabels();
            UpdateThemeSwatches();
            UpdateSamplePreview();

            if (notifyPreview)
                PreviewChanged?.Invoke(Clone(draftSettings));
        }

        private string GetSelectedTheme()
        {
            if (WarmThemeRadio.IsChecked == true)
                return "Warm";
            if (NightThemeRadio.IsChecked == true)
                return "Night";
            return "Paper";
        }

        private void UpdateValueLabels()
        {
            FontSizeValueText.Text = $"{draftSettings.FontSizeDip:0} DIP";
            LineSpacingValueText.Text = $"{draftSettings.LineSpacingMultiplier:0.0}×";
        }

        private void UpdateThemeSwatches()
        {
            var paper = ReaderPalette.Paper;
            PaperThemeSwatch.Background = new SolidColorBrush(ToColor(paper.Background));
            PaperThemeSwatch.BorderBrush = new SolidColorBrush(ToColor(
                draftSettings.Theme == "Paper" ? paper.AnnotationMarker : paper.GutterSeparator));
            PaperThemeSwatchText.Foreground = new SolidColorBrush(ToColor(paper.Text));
            PaperThemeSwatch.BorderThickness = new Thickness(draftSettings.Theme == "Paper" ? 2 : 1);

            var warm = ReaderPalette.Warm;
            WarmThemeSwatch.Background = new SolidColorBrush(ToColor(warm.Background));
            WarmThemeSwatch.BorderBrush = new SolidColorBrush(ToColor(
                draftSettings.Theme == "Warm" ? warm.AnnotationMarker : warm.GutterSeparator));
            WarmThemeSwatchText.Foreground = new SolidColorBrush(ToColor(warm.Text));
            WarmThemeSwatch.BorderThickness = new Thickness(draftSettings.Theme == "Warm" ? 2 : 1);

            var night = ReaderPalette.Night;
            NightThemeSwatch.Background = new SolidColorBrush(ToColor(night.Background));
            NightThemeSwatch.BorderBrush = new SolidColorBrush(ToColor(
                draftSettings.Theme == "Night" ? night.AnnotationMarker : night.GutterSeparator));
            NightThemeSwatchText.Foreground = new SolidColorBrush(ToColor(night.Text));
            NightThemeSwatch.BorderThickness = new Thickness(draftSettings.Theme == "Night" ? 2 : 1);
        }

        private void UpdateSamplePreview()
        {
            var palette = draftSettings.Theme switch
            {
                "Warm" => ReaderPalette.Warm,
                "Night" => ReaderPalette.Night,
                _ => ReaderPalette.Paper,
            };

            PreviewSurface.Background = new SolidColorBrush(ToColor(palette.Background));
            PreviewSampleText.Foreground = new SolidColorBrush(ToColor(palette.Text));
            PreviewSampleText.FontFamily = new FontFamily(draftSettings.FontFamily);
            PreviewSampleText.FontSize = draftSettings.FontSizeDip;
            PreviewSampleText.LineHeight = Math.Max(
                draftSettings.FontSizeDip,
                draftSettings.FontSizeDip * draftSettings.LineSpacingMultiplier);
        }

        private static Color ToColor(uint argb)
            => Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
    }
}
