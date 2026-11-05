using System.Numerics;
using FujiyNotepad.Core;
using FujiyNotepad.Presentation;
using FujiyNotepad.WinUI.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace FujiyNotepad.WinUI.Controls
{
    /// <summary>
    /// A from-scratch, read-only text surface for arbitrarily large files. It draws only visible lines
    /// (read on demand via <see cref="LineProvider"/>) onto a Win2D <see cref="CanvasControl"/>, tracks scrolling
    /// in source-line units with a visual-row offset when wrapping is enabled, and supports drag-only character
    /// selection, copy and keyboard navigation. Text shaping and visual-row geometry come from DirectWrite.
    ///
    /// This control is the WinUI shell: it owns the canvas, focus, pointer capture, clipboard access, font metrics
    /// and draw calls. Scroll/selection math and the per-line render model live in the framework-independent
    /// <see cref="TextLayoutEngine"/>, so that logic can be unit-tested on a normal test host. The reader does
    /// not display an insertion caret or offer double-click word selection.
    /// </summary>
    public sealed partial class TextCanvas : UserControl
    {
        private readonly TextLayoutEngine engine = new();
        private bool hasLoggedFirstDraw;
        private bool hasLoggedFirstContentDraw;
        private bool hasLoggedFirstLineCountUpdate;
        private bool isUpdatingTotalLines;
        private readonly CanvasControl canvas;
        private readonly CanvasTextFormat textFormat;
        private readonly Win2DTextLayoutProvider proportionalLayoutProvider = new();
        private readonly HashSet<int> drawnProportionalLines = new();

        // Text layouts are retained only for the current viewport. Drawing, selection geometry and pointer
        // hit-testing all query the same DirectWrite layout instead of estimating glyph widths.
        private readonly Dictionary<TextLayoutKey, CanvasTextLayout> textLayoutCache = new();
        private readonly HashSet<TextLayoutKey> activeTextLayouts = new();
        private readonly record struct TextLayoutKey(int LineIndex, int DisplayStartIndex, int DisplayLength);

        private double charWidth;
        private double lineHeight;
        private double lineHeightRaw;     // unsnapped line height straight from the font probe
        private double lastMetricsDpi;    // DPI used for the last pixel-snap; re-snap when it changes
        private bool metricsValid;

        // Reader-palette colors for the Win2D surface. They are independent from the application's ActualTheme.
        private Color backgroundColor = Colors.White;
        private Color textColor = Colors.Black;
        private Color selectionColor = Color.FromArgb(255, 0xAD, 0xD6, 0xFF);
        private Color matchHighlightColor = Color.FromArgb(255, 0xFF, 0xD5, 0x4F);
        private Color selectionOccurrenceColor = Color.FromArgb(0x80, 0x3F, 0xB5, 0x7F);
        private Color gutterTextColor = Color.FromArgb(255, 0x88, 0x88, 0x88);
        private Color gutterSeparatorColor = Color.FromArgb(255, 0xE0, 0xE0, 0xE0);
        private Color bookmarkColor = Color.FromArgb(255, 0x1A, 0x7F, 0xD6);
        private Color whitespaceColor = Color.FromArgb(210, 0x5A, 0x5A, 0x5A);
        private Color trailingWhitespaceColor = Color.FromArgb(235, 0xD0, 0x30, 0x30);
        private Color controlCharColor = Color.FromArgb(235, 0xD0, 0x30, 0x30);
        private Color annotationMarkerColor = Color.FromArgb(255, 0xC5, 0x7A, 0x20);
        private double readerLineSpacingMultiplier = ReaderSettings.DefaultLineSpacingMultiplier;
        private double readerContentWidthDip = ReaderSettings.DefaultContentWidthDip;
        private string readerTheme = ReaderSettings.DefaultTheme;

        // Detects Windows High Contrast so ApplyReaderPalette can switch the surface to the system HC palette. Kept
        // alive as a field so its HighContrastChanged event keeps firing.
        private AccessibilitySettings? accessibility;

        // Right margin (px) between a line number and the gutter's edge; mirrors the engine's gutter padding.
        private const double GutterRightMargin = 6d;

        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer autoScrollTimer;

        private bool isSelecting;
        private bool hasStartedDragSelection;
        private bool hasSelectionAnchor;
        private bool isReaderInteractionPassive;
        private const double DragSelectionThresholdDip = 4d;
        private Point lastPointer;
        private Point pointerDownPosition;
        private TextPosition contextMenuPosition;
        private bool hasPointerContextMenuPosition;
        private readonly Dictionary<int, List<TextAnnotationMarker>> annotationMarkersByLine = new();

        public event Action? ViewChanged;
        public event Action? ReaderPaletteChanged;
        public event Action? ReaderThemeChanged;
        public event Action? ContentWidthChanged;
        public event Action<TextPosition>? CaretChanged;
        public event Action? CreateSmallAnnotationRequested;
        public event Action? AddSelectionToSmallAnnotationRequested;
        public event Action<TextPosition>? CreateChapterAnnotationRequested;
        public event Action<TextPosition>? AnnotationMarkerClicked;

        private MenuFlyoutItem? createSmallAnnotationItem;
        private MenuFlyoutItem? addToSmallAnnotationItem;
        private MenuFlyoutItem? createChapterAnnotationItem;

        public TextCanvas()
        {
            textFormat = new CanvasTextFormat
            {
                FontFamily = ReaderSettings.DefaultFontFamily,
                FontSize = (float)ReaderSettings.DefaultFontSizeDip,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            canvas = new CanvasControl();
            canvas.Draw += OnDraw;

            // The reader has no visible insertion caret. The engine still tracks source positions internally
            // for drag selection, keyboard navigation, copying and accessibility.
            Content = canvas;

            // Repeats the drag while the pointer is held past the top/bottom edge during a selection.
            autoScrollTimer = DispatcherQueue.CreateTimer();
            autoScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
            autoScrollTimer.Tick += AutoScrollTick;

            engine.ViewChanged += () =>
            {
                if (isUpdatingTotalLines)
                    LogDiagnostic("TextCanvas: engine ViewChanged entered during UpdateTotalLines");
                ViewChanged?.Invoke();
                if (isUpdatingTotalLines)
                    LogDiagnostic("TextCanvas: engine ViewChanged completed during UpdateTotalLines");
            };
            engine.CaretChanged += pos =>
            {
                CaretChanged?.Invoke(pos);
                NotifyAccessibilityCaretLineChanged(pos);
            };
            engine.RedrawRequested += () =>
            {
                if (isUpdatingTotalLines)
                    LogDiagnostic("TextCanvas: engine RedrawRequested entered during UpdateTotalLines");
                canvas.Invalidate();
                if (isUpdatingTotalLines)
                    LogDiagnostic("TextCanvas: engine RedrawRequested completed during UpdateTotalLines");
            };
            engine.ConfigureProportionalLayout(
                proportionalLayoutProvider, textFormat.FontFamily, textFormat.FontSize, readerLineSpacingMultiplier);

            IsTabStop = true;
            UseSystemFocusVisuals = false;
            IsDoubleTapEnabled = false;

            // Show the text (I-beam) cursor while hovering the surface, like a real text control.
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerWheelChanged += OnPointerWheelChanged;
            KeyDown += OnKeyDown;
            // Focus remains available for keyboard shortcuts, but it never paints an insertion caret.
            SizeChanged += OnSizeChanged;

            // Reader colors are selected only by ReaderSettings.Theme, never by the application's Light/Dark theme.
            ApplyReaderPalette();
            Loaded += (_, _) => ApplyReaderPalette();

            // Override the selected reader palette when the user toggles Windows High Contrast or switches
            // HC scheme. It can fire off the UI thread, so marshal back before repainting.
            // Construction can throw if the thread has no view, so guard it and fall back to the palette.
            try
            {
                accessibility = new AccessibilitySettings();
                accessibility.HighContrastChanged += (_, _) => DispatcherQueue?.TryEnqueue(ApplyReaderPalette);
            }
            catch
            {
                // High Contrast detection unavailable; the selected reader preset remains active.
            }

            BuildContextMenu();
        }

        // A right-click / long-press / Shift+F10 context menu for the read-only surface.
        private void BuildContextMenu()
        {
            var copyItem = new MenuFlyoutItem { Text = "Copy", KeyboardAcceleratorTextOverride = "Ctrl+C" };
            copyItem.Click += (_, _) => CopySelection();

            var selectAllItem = new MenuFlyoutItem { Text = "Select All", KeyboardAcceleratorTextOverride = "Ctrl+A" };
            selectAllItem.Click += (_, _) => SelectAllText();

            createSmallAnnotationItem = new MenuFlyoutItem
            {
                Text = "新建小批注...",
                Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            };
            createSmallAnnotationItem.Click += (_, _) => CreateSmallAnnotationRequested?.Invoke();

            addToSmallAnnotationItem = new MenuFlyoutItem
            {
                Text = "添加到现有小批注...",
                Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            };
            addToSmallAnnotationItem.Click += (_, _) => AddSelectionToSmallAnnotationRequested?.Invoke();

            createChapterAnnotationItem = new MenuFlyoutItem
            {
                Text = "新建章节批注...",
                Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            };
            createChapterAnnotationItem.Click += (_, _) =>
            {
                TextPosition position = hasPointerContextMenuPosition
                    ? contextMenuPosition
                    : engine.CaretPosition;
                hasPointerContextMenuPosition = false;
                CreateChapterAnnotationRequested?.Invoke(position);
            };

            var flyout = new MenuFlyout();
            flyout.Items.Add(copyItem);
            flyout.Items.Add(selectAllItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(createChapterAnnotationItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(createSmallAnnotationItem);
            flyout.Items.Add(addToSmallAnnotationItem);
            // Copy and small-annotation commands are only meaningful when text is selected.
            // Chapter annotations are tied to the clicked chapter, so they do not require a selection.
            flyout.Opening += (_, _) =>
            {
                if (!hasPointerContextMenuPosition)
                    contextMenuPosition = engine.CaretPosition;
                copyItem.IsEnabled = engine.HasSelection;
                if (createSmallAnnotationItem is not null)
                    createSmallAnnotationItem.IsEnabled = engine.HasSelection;
                if (addToSmallAnnotationItem is not null)
                    addToSmallAnnotationItem.IsEnabled = engine.HasSelection;
            };
            flyout.Closed += (_, _) => hasPointerContextMenuPosition = false;

            ContextFlyout = flyout;
        }

        // Push the live control size + measured font metrics into the engine before any op that depends on
        // them (clamping, hit-testing, the render model).
        private void Ready()
        {
            if (isUpdatingTotalLines)
                LogDiagnostic("TextCanvas.UpdateTotalLines: Ready before EnsureMetrics");
            EnsureMetrics();
            if (isUpdatingTotalLines)
                LogDiagnostic("TextCanvas.UpdateTotalLines: Ready after EnsureMetrics");
            SyncViewport();
            if (isUpdatingTotalLines)
                LogDiagnostic("TextCanvas.UpdateTotalLines: Ready after SyncViewport");
        }

        private void SyncViewport()
        {
            engine.ViewportWidth = ActualWidth;
            engine.ViewportHeight = ActualHeight;
        }

        // Capture against the engine's previous viewport width before SyncViewport invalidates/reflows it.
        // The returned position is always an original-source UTF-16 line/column, never a screen coordinate.
        private void OnSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            bool widthChanged = Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5;
            TextPosition? viewportAnchor = widthChanged ? CaptureViewportAnchor() : null;

            SyncViewport();
            if (viewportAnchor is TextPosition anchor)
                engine.SetViewportAnchor(anchor);
            else
                ViewChanged?.Invoke();

            canvas.Invalidate();
        }

        #region Public surface for the host

        public int FirstVisibleLine
        {
            get => engine.FirstVisibleLine;
            set { Ready(); engine.FirstVisibleLine = value; }
        }

        public double HorizontalOffset
        {
            get => engine.HorizontalOffset;
            set { Ready(); engine.HorizontalOffset = value; }
        }

        public int TotalLines => engine.TotalLines;
        public int MaxFirstLine { get { Ready(); return engine.MaxFirstLine; } }

        /// <summary>
        /// Returns the first and last source lines represented in the viewport. Unlike adding the
        /// visible row count to FirstVisibleLine, this remains accurate when word-wrap displays
        /// multiple visual rows from one very long source line.
        /// </summary>
        public (int FirstLine, int LastLine) GetVisibleSourceLineRange()
        {
            Ready();
            IReadOnlyList<VisibleLine> visible = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
            if (visible.Count == 0)
            {
                int line = Math.Clamp(engine.FirstVisibleLine, 0, Math.Max(0, engine.TotalLines - 1));
                return (line, line);
            }

            return (visible.Min(line => line.LineIndex), visible.Max(line => line.LineIndex));
        }
        public TextPosition CaretPosition => engine.CaretPosition;

        // ----- Accessibility: expose file content to screen readers via a UIA peer (issue #75) -----

        // The caret line last announced to assistive tech; -1 = none yet. Used to re-announce only on a real
        // line change (not when the caret moves left/right within the same line).
        private int lastAccessibilityLine = -1;

        // A custom AutomationPeer (instead of the default UserControl peer) so Narrator / NVDA can read the
        // file's text — the Win2D surface paints text directly and otherwise exposes none.
        protected override AutomationPeer OnCreateAutomationPeer() => new TextCanvasAutomationPeer(this);

        /// <summary>The text a screen reader should read: the raw source of the caret line (bounded; issue #75).</summary>
        internal string GetAccessibleText() => engine.GetCaretLineText();

        /// <summary>The 1-based caret line and column, for the peer's position announcement.</summary>
        internal (int Line, int Column) GetAccessibleCaretPosition()
        {
            TextPosition c = engine.CaretPosition;
            return (c.Line + 1, c.Column + 1);
        }

        // Raises a UIA value-changed event when the caret moves to a different line, so a screen reader reads
        // the new line as the user navigates. No-op unless an automation client (the peer) is attached.
        private void NotifyAccessibilityCaretLineChanged(TextPosition pos)
        {
            if (pos.Line == lastAccessibilityLine)
            {
                return;
            }
            lastAccessibilityLine = pos.Line;

            if (FrameworkElementAutomationPeer.FromElement(this) is TextCanvasAutomationPeer peer)
            {
                peer.NotifyCaretLineChanged();
            }
        }

        public TextPosition SelectionStart => engine.SelectionStart;
        public TextPosition SelectionEnd => engine.SelectionEnd;

        /// <summary>Returns the selected source text with its original line terminators for annotation quotes.</summary>
        public string? GetSelectedTextForAnnotation() => engine.GetSelectedTextForAnnotation();

        /// <summary>Shows or hides the novel-project annotation commands in the text context menu.</summary>
        public void SetAnnotationCommandsEnabled(bool enabled)
        {
            if (createSmallAnnotationItem is not null)
                createSmallAnnotationItem.Visibility = enabled
                    ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            if (addToSmallAnnotationItem is not null)
                addToSmallAnnotationItem.Visibility = enabled
                    ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            if (createChapterAnnotationItem is not null)
                createChapterAnnotationItem.Visibility = enabled
                    ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        /// <summary>Replaces the source ranges whose text should receive small-annotation markers.</summary>
        public void SetAnnotationMarkers(IEnumerable<TextAnnotationMarker> markers)
        {
            annotationMarkersByLine.Clear();

            foreach (IGrouping<int, TextAnnotationMarker> group in markers
                         .Where(marker => marker.Line >= 0 && marker.StartColumn >= 0
                                          && marker.EndColumn > marker.StartColumn)
                         .GroupBy(marker => marker.Line))
            {
                var merged = new List<TextAnnotationMarker>();
                foreach (TextAnnotationMarker marker in group
                             .OrderBy(marker => marker.StartColumn)
                             .ThenBy(marker => marker.EndColumn))
                {
                    if (merged.Count == 0 || marker.StartColumn > merged[^1].EndColumn)
                    {
                        merged.Add(marker);
                    }
                    else
                    {
                        TextAnnotationMarker previous = merged[^1];
                        merged[^1] = previous with
                        {
                            EndColumn = Math.Max(previous.EndColumn, marker.EndColumn),
                        };
                    }
                }

                annotationMarkersByLine[group.Key] = merged;
            }

            canvas.Invalidate();
        }

        /// <summary>The selected text when it lies on a single source line; otherwise null (issue #130).</summary>
        public string? GetSelectedTextOnSingleLine() => engine.GetSelectedTextOnSingleLine();

        /// <summary>The size (characters and lines) of the current selection, for the status bar.</summary>
        public SelectionStats GetSelectionStats() => engine.GetSelectionStats();

        /// <summary>The time delta between the first and last selected lines' leading timestamps, or null.</summary>
        public TimeSpan? GetSelectionTimestampDelta() => engine.GetSelectionTimestampDelta();

        public int TabSize
        {
            get => engine.TabSize;
            set
            {
                if (engine.TabSize != value)
                {
                    engine.TabSize = value;
                    ClearTextLayoutCache();
                }
            }
        }

        // Font size bounds and the "100% zoom" baseline (the default reader size). Zoom changes the font size.
        private const double BaseFontSize = ReaderSettings.DefaultFontSizeDip;
        private const double MinFontSize = ReaderSettings.MinFontSizeDip;
        private const double MaxFontSize = ReaderSettings.MaxFontSizeDip;
        private const double FontSizeStep = 1;

        /// <summary>The font family used to render text. Changing it re-measures the font metrics.</summary>
        public string FontFamilyName
        {
            get => textFormat.FontFamily;
            set
            {
                if (!string.IsNullOrEmpty(value) && !string.Equals(value, textFormat.FontFamily, StringComparison.Ordinal))
                {
                    textFormat.FontFamily = value;
                    RefreshFont();
                }
            }
        }

        /// <summary>The reader font size in DIPs (clamped). Zoom is expressed through this. Re-measures the metrics.</summary>
        public double FontSizePoints
        {
            get => textFormat.FontSize;
            set
            {
                float clamped = (float)Math.Clamp(value, MinFontSize, MaxFontSize);
                if (Math.Abs(clamped - textFormat.FontSize) > 0.01f)
                {
                    textFormat.FontSize = clamped;
                    RefreshFont();
                }
            }
        }

        /// <summary>The active reader line-spacing multiplier.</summary>
        public double LineSpacingMultiplier => readerLineSpacingMultiplier;

        /// <summary>The maximum reader text-surface width in DIPs; zero means use the available width.</summary>
        public double ContentWidthDip => readerContentWidthDip;

        /// <summary>The effective reader background, including the Windows High Contrast override.</summary>
        public Color ReaderBackgroundColor => backgroundColor;

        /// <summary>The active reader-only theme key.</summary>
        public string ReaderTheme => readerTheme;

        /// <summary>
        /// Applies a snapshot of global reader settings. Typography changes share one layout refresh;
        /// content-width changes notify the centered viewport host; palette changes repaint only the reader.
        /// The Night preset separately asks the window host to apply a temporary app-chrome Dark override.
        /// </summary>
        public void ApplyReaderSettings(ReaderSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var normalized = new ReaderSettings
            {
                FontFamily = settings.FontFamily,
                FontSizeDip = settings.FontSizeDip,
                LineSpacingMultiplier = settings.LineSpacingMultiplier,
                ContentWidthDip = settings.ContentWidthDip,
                Theme = settings.Theme,
            };
            normalized.Normalize();

            bool typographyChanged =
                !string.Equals(textFormat.FontFamily, normalized.FontFamily, StringComparison.Ordinal) ||
                Math.Abs(textFormat.FontSize - normalized.FontSizeDip) > 0.01 ||
                Math.Abs(readerLineSpacingMultiplier - normalized.LineSpacingMultiplier) > 0.001;
            bool contentWidthChanged = Math.Abs(readerContentWidthDip - normalized.ContentWidthDip) > 0.01;
            bool paletteChanged = !string.Equals(readerTheme, normalized.Theme, StringComparison.Ordinal);

            textFormat.FontFamily = normalized.FontFamily;
            textFormat.FontSize = (float)normalized.FontSizeDip;
            readerLineSpacingMultiplier = normalized.LineSpacingMultiplier;
            readerContentWidthDip = normalized.ContentWidthDip;
            readerTheme = normalized.Theme;

            if (typographyChanged)
                RefreshFont();

            if (contentWidthChanged)
                ContentWidthChanged?.Invoke();

            if (paletteChanged)
            {
                ApplyReaderPalette();
                ReaderThemeChanged?.Invoke();
            }
        }

        /// <summary>The current zoom as a percentage of the default font size (100% = the baseline size).</summary>
        public int ZoomPercent => (int)Math.Round(textFormat.FontSize / BaseFontSize * 100);

        public void ZoomIn() => FontSizePoints = textFormat.FontSize + FontSizeStep;

        public void ZoomOut() => FontSizePoints = textFormat.FontSize - FontSizeStep;

        public void ResetZoom() => FontSizePoints = BaseFontSize;

        /// <summary>Raised when reader typography changes, so the host can persist settings and update status.</summary>
        public event Action? FontChanged;

        // Capture the first visible original-source UTF-16 position while the engine still has its old layout.
        // This is shared by typography refresh and viewport-width changes.
        private TextPosition? CaptureViewportAnchor()
        {
            if (engine.TotalLines <= 0)
                return null;

            IReadOnlyList<VisibleLine> visible = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
            if (visible.Count == 0)
                return null;

            VisibleLine first = visible[0];
            return new TextPosition(
                first.LineIndex,
                first.UsesProportionalLayout ? first.SourceStartIndex : 0);
        }

        // Re-measure the active font, update the proportional layout provider and gutter metrics, then repaint.
        private void RefreshFont()
        {
            // Capture before invalidating layouts; a font/size/line-spacing change may alter soft-wrap boundaries.
            TextPosition? viewportAnchor = CaptureViewportAnchor();

            ClearTextLayoutCache();
            metricsValid = false;
            EnsureMetrics();
            engine.ConfigureProportionalLayout(
                proportionalLayoutProvider, textFormat.FontFamily, textFormat.FontSize, readerLineSpacingMultiplier);

            if (viewportAnchor is TextPosition anchor)
                engine.SetViewportAnchor(anchor);

            ViewChanged?.Invoke();
            canvas?.Invalidate();
            FontChanged?.Invoke();
        }

        public void SetProvider(ILineSource? newProvider)
        {
            Ready();
            annotationMarkersByLine.Clear();
            ClearTextLayoutCache();
            lastAccessibilityLine = -1; // a new file: re-announce the caret line even if it lands on line 0 again
            engine.SetProvider(newProvider);
        }

        public void UpdateTotalLines(int count)
        {
            bool logStages = !hasLoggedFirstLineCountUpdate;
            if (logStages)
            {
                hasLoggedFirstLineCountUpdate = true;
                LogDiagnostic(
                    $"TextCanvas.UpdateTotalLines: entered; current={engine.TotalLines:N0}; requested={count:N0}");
            }

            if (count == engine.TotalLines)
            {
                if (logStages)
                    LogDiagnostic("TextCanvas.UpdateTotalLines: unchanged line count; returning");
                return;
            }

            isUpdatingTotalLines = logStages;
            try
            {
                if (logStages)
                    LogDiagnostic("TextCanvas.UpdateTotalLines: before Ready");
                Ready();
                if (logStages)
                    LogDiagnostic("TextCanvas.UpdateTotalLines: after Ready; before ClearTextLayoutCache");

                ClearTextLayoutCache();
                if (logStages)
                    LogDiagnostic("TextCanvas.UpdateTotalLines: after ClearTextLayoutCache; before engine.UpdateTotalLines");

                engine.UpdateTotalLines(count);
                if (logStages)
                    LogDiagnostic($"TextCanvas.UpdateTotalLines: engine.UpdateTotalLines completed; total={engine.TotalLines:N0}");
            }
            finally
            {
                isUpdatingTotalLines = false;
                if (logStages)
                    LogDiagnostic("TextCanvas.UpdateTotalLines: finished and diagnostic stage flag cleared");
            }
        }

        /// <summary>Notifies the reader that the existing line source changed without a line-count change.</summary>
        public void RefreshSourceContent()
        {
            Ready();
            ClearTextLayoutCache();
            engine.RefreshSourceContent();
        }

        public void GoToLine(int lineIndex)
        {
            Ready();
            engine.GoToLine(lineIndex);
        }

        public void GoToLineColumn(int lineIndex, int column)
        {
            Ready();
            engine.GoToLineColumn(lineIndex, column);
        }

        /// <summary>Centers the current caret position vertically in the reader viewport.</summary>
        public void CenterCaretVertically()
        {
            Ready();
            engine.CenterCaretVertically();
        }

        public void SelectMatch(int lineIndex, int startColumn, int length)
        {
            Ready();
            engine.SelectMatch(lineIndex, startColumn, length);
        }

        /// <summary>Sets (or clears, with null) the highlighter that paints every Find match in the viewport.</summary>
        public void SetHighlighter(ILineHighlighter? highlighter) => engine.SetHighlighter(highlighter);

        public void SetSelectionHighlighter(ILineHighlighter? highlighter) => engine.SetSelectionHighlighter(highlighter);

        /// <summary>Sets (or clears, with null) the persistent highlight rules painted across the whole file.</summary>
        public void SetHighlightRules(HighlightRuleSet? rules) => engine.SetHighlightRules(rules);

        /// <summary>Toggles a bookmark on the caret line; returns true if it is now bookmarked.</summary>
        public bool ToggleBookmark() => engine.ToggleBookmarkAtCaret();

        /// <summary>Moves the caret to the next bookmarked line (wrapping).</summary>
        public void GoToNextBookmark() => engine.GoToNextBookmark();

        /// <summary>Moves the caret to the previous bookmarked line (wrapping).</summary>
        public void GoToPreviousBookmark() => engine.GoToPreviousBookmark();

        /// <summary>Removes every bookmark.</summary>
        public void ClearBookmarks() => engine.ClearBookmarks();

        /// <summary>Whether any line is bookmarked.</summary>
        public bool HasBookmarks => engine.HasBookmarks;

        /// <summary>The bookmarked line indices, for the scrollbar marker margin.</summary>
        public IReadOnlyCollection<int> BookmarkLines => engine.BookmarkLines;

        /// <summary>Whether the line-number gutter is shown.</summary>
        public bool ShowLineNumbers
        {
            get => engine.ShowLineNumbers;
            set => engine.ShowLineNumbers = value;
        }

        public bool ShowWhitespace
        {
            get => engine.ShowWhitespace;
            set => engine.ShowWhitespace = value;
        }

        /// <summary>Word wrap (issue #31): wrap long lines to the viewport width instead of scrolling sideways.
        /// Re-syncs the scrollbars (the horizontal one is hidden while wrapping) and repaints.</summary>
        public bool WordWrap
        {
            get => engine.WordWrap;
            set
            {
                if (engine.WordWrap == value)
                {
                    return;
                }
                Ready();
                engine.WordWrap = value;
                ViewChanged?.Invoke();
                canvas?.Invalidate();
            }
        }

        /// <summary>
        /// When true, the reader surface remains wheel-scrollable but does not accept text selection,
        /// annotation-marker clicks, context menus, double-tap selection, or keyboard navigation.
        /// Used while a floating reader-settings panel is open.
        /// </summary>
        public bool IsReaderInteractionPassive
        {
            get => isReaderInteractionPassive;
            set
            {
                if (isReaderInteractionPassive == value)
                    return;

                isReaderInteractionPassive = value;
                if (value)
                {
                    isSelecting = false;
                    autoScrollTimer.Stop();
                    ReleasePointerCaptures();
                    hasPointerContextMenuPosition = false;
                }
            }
        }

        public void FocusCanvas() => Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

        /// <summary>Copies the current selection to the clipboard (no-op when nothing is selected).</summary>
        public void CopySelection()
        {
            string? text = engine.BuildCopyText();
            if (text != null)
            {
                CopyToClipboard(text);
            }
        }

        /// <summary>Copies the selected lines (or the caret's line) prefixed with their 1-based line numbers.</summary>
        public void CopySelectionWithLineNumbers()
        {
            string? text = engine.BuildCopyTextWithLineNumbers();
            if (text != null)
            {
                CopyToClipboard(text);
            }
        }

        /// <summary>Selects the whole document.</summary>
        public void SelectAllText()
        {
            Ready();
            engine.HandleKey(NavKey.SelectAll, false);
            FocusCanvas();
        }

        #endregion

        #region Pointer

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsReaderInteractionPassive)
            {
                e.Handled = true;
                return;
            }

            var pointer = e.GetCurrentPoint(this);
            // Keep the text position under a right-click for the chapter-annotation context command.
            if (!pointer.Properties.IsLeftButtonPressed)
            {
                if (pointer.Properties.IsRightButtonPressed)
                {
                    Ready();
                    // Chapter creation is based on the source position nearest the pointer, not on
                    // whether the pointer landed on a glyph. Blank space to the right of a line therefore
                    // still resolves to that line's end and its owning chapter.
                    contextMenuPosition = HitTestText(pointer.Position.X, pointer.Position.Y);
                    hasPointerContextMenuPosition = true;
                }
                return;
            }

            Focus(Microsoft.UI.Xaml.FocusState.Pointer);
            Ready();
            engine.ClearSelection();
            lastPointer = e.GetCurrentPoint(this).Position;
            pointerDownPosition = lastPointer;
            TextPosition hit = HitTestTextCharacter(
                lastPointer.X, lastPointer.Y,
                out bool hitCharacter, out int hitSourceLength, out bool isTrailingHit);

            hasSelectionAnchor = hitCharacter && hitSourceLength > 0;
            if (hasSelectionAnchor)
            {
                bool shift = IsDown(VirtualKey.Shift);
                engine.PointerPressCharacter(hit, hitSourceLength, isTrailingHit, shift);
            }

            // Capture even when the initial point is blank. If a later drag segment first enters a text
            // rectangle, that rectangle boundary becomes the effective selection anchor.
            hasStartedDragSelection = false;
            isSelecting = true;
            CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isSelecting)
            {
                return;
            }
            Point previousPointer = lastPointer;
            lastPointer = e.GetCurrentPoint(this).Position;
            if (!hasStartedDragSelection)
            {
                double deltaX = lastPointer.X - pointerDownPosition.X;
                double deltaY = lastPointer.Y - pointerDownPosition.Y;
                if (Math.Abs(deltaX) <= DragSelectionThresholdDip
                    && Math.Abs(deltaY) <= DragSelectionThresholdDip)
                {
                    return;
                }

                hasStartedDragSelection = true;
            }

            Ready();
            if (!hasSelectionAnchor)
            {
                if (!engine.TryHitTestTextRectangleEntry(
                        previousPointer.X, previousPointer.Y,
                        lastPointer.X, lastPointer.Y,
                        out TextPosition entry,
                        out int entrySourceLength,
                        out bool entryTrailingHit,
                        out _, out _))
                {
                    // Do not create an anchor from blank space. Keep waiting until the pointer trajectory
                    // crosses a real text rectangle; the engine returns the first boundary point A.
                    UpdateAutoScroll();
                    return;
                }

                engine.PointerPressCharacter(
                    entry, entrySourceLength, entryTrailingHit, shift: false);
                hasSelectionAnchor = true;
            }

            engine.ScrollForPointer(lastPointer.Y);
            TextPosition hit = HitTestTextCharacter(
                lastPointer.X, lastPointer.Y,
                out bool hitCharacter, out int hitSourceLength, out bool isTrailingHit);
            if (hitCharacter && hitSourceLength > 0)
                engine.PointerDragCharacter(hit, hitSourceLength, isTrailingHit);
            else
                engine.PointerDragBoundary(hit);
            UpdateAutoScroll();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!isSelecting)
                return;

            Point released = e.GetCurrentPoint(this).Position;
            bool hadSelectionAnchor = hasSelectionAnchor;
            bool isClick = !hasStartedDragSelection
                           && Math.Abs(released.X - pointerDownPosition.X) <= DragSelectionThresholdDip
                           && Math.Abs(released.Y - pointerDownPosition.Y) <= DragSelectionThresholdDip;

            isSelecting = false;
            hasStartedDragSelection = false;
            hasSelectionAnchor = false;
            autoScrollTimer.Stop();
            ReleasePointerCapture(e.Pointer);

            if (isClick && hadSelectionAnchor)
            {
                Ready();
                if (engine.TryHitTestAnnotationMarkers(
                        released.X, released.Y, annotationMarkersByLine, out TextPosition position))
                {
                    AnnotationMarkerClicked?.Invoke(position);
                }
                else if (!engine.UsesProportionalLayout)
                {
                    // Keep the legacy fixed-cell renderer working while using its full row hit region.
                    TextPosition legacyPosition = HitTestTextCharacter(
                        released.X, released.Y, out bool hitCharacter, out int hitSourceLength);
                    if (hitCharacter && HasAnnotationMarkerAt(legacyPosition, hitSourceLength))
                        AnnotationMarkerClicked?.Invoke(legacyPosition);
                }
            }
        }

        private bool HasAnnotationMarkerAt(TextPosition position, int sourceLength)
        {
            if (sourceLength <= 0
                || !annotationMarkersByLine.TryGetValue(position.Line, out List<TextAnnotationMarker>? markers))
            {
                return false;
            }

            // A DirectWrite hit can represent a complete shaping cluster (for example, a ligature or
            // combining sequence). Treat the hit and annotation as overlapping source ranges, not just
            // as a single UTF-16 starting column.
            long hitEnd = (long)position.Column + sourceLength;
            foreach (TextAnnotationMarker marker in markers)
            {
                if (position.Column < marker.EndColumn && hitEnd > marker.StartColumn)
                    return true;
            }

            return false;
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            Ready();
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            if (IsReaderInteractionPassive)
            {
                // Wheel scrolling remains available, but Ctrl+wheel must not change reader settings
                // behind the open panel.
                if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
                    engine.ScrollByWheelDelta(delta);
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
            {
                // Ctrl + wheel zooms (changes the font size) instead of scrolling.
                if (delta > 0)
                {
                    ZoomIn();
                }
                else if (delta < 0)
                {
                    ZoomOut();
                }
            }
            else
            {
                engine.ScrollByWheelDelta(delta);
            }
            e.Handled = true;
        }

        // While the pointer is held past the top/bottom edge during a selection, keep scrolling (and
        // extending the selection) on a timer even if the pointer stops moving.
        private void UpdateAutoScroll()
        {
            if (isSelecting && hasStartedDragSelection && hasSelectionAnchor
                && (lastPointer.Y < 0 || lastPointer.Y > ActualHeight))
            {
                autoScrollTimer.Start();
            }
            else
            {
                autoScrollTimer.Stop();
            }
        }

        private void AutoScrollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (!isSelecting || !hasStartedDragSelection || !hasSelectionAnchor
                || (lastPointer.Y >= 0 && lastPointer.Y <= ActualHeight))
            {
                autoScrollTimer.Stop();
                return;
            }
            Ready();
            engine.ScrollForPointer(lastPointer.Y);
            TextPosition hit = HitTestTextCharacter(
                lastPointer.X, lastPointer.Y,
                out bool hitCharacter, out int hitSourceLength, out bool isTrailingHit);
            if (hitCharacter && hitSourceLength > 0)
                engine.PointerDragCharacter(hit, hitSourceLength, isTrailingHit);
            else
                engine.PointerDragBoundary(hit);
        }

        #endregion

        #region Keyboard

        private static bool IsDown(VirtualKey k) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        // Translates a physical key (plus the Ctrl state) into a UI-agnostic engine command, or null when
        // the key isn't a navigation/selection/copy shortcut the canvas handles.
        private static NavKey? MapKey(VirtualKey key, bool ctrl) => key switch
        {
            VirtualKey.Left => NavKey.Left,
            VirtualKey.Right => NavKey.Right,
            VirtualKey.Up => NavKey.Up,
            VirtualKey.Down => NavKey.Down,
            VirtualKey.PageUp => NavKey.PageUp,
            VirtualKey.PageDown => NavKey.PageDown,
            VirtualKey.Home => ctrl ? NavKey.DocumentStart : NavKey.LineStart,
            VirtualKey.End => ctrl ? NavKey.DocumentEnd : NavKey.LineEnd,
            VirtualKey.A when ctrl => NavKey.SelectAll,
            VirtualKey.C when ctrl => NavKey.Copy,
            _ => null,
        };

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (IsReaderInteractionPassive)
            {
                e.Handled = true;
                return;
            }

            bool ctrl = IsDown(VirtualKey.Control);
            NavKey? nav = MapKey(e.Key, ctrl);
            if (nav is null)
            {
                return;
            }

            Ready();
            bool shift = IsDown(VirtualKey.Shift);
            KeyResult result = engine.HandleKey(nav.Value, shift);
            if (result.CopyText != null)
            {
                CopyToClipboard(result.CopyText);
            }
            if (result.Handled)
            {
                e.Handled = true;
            }
        }

        #endregion

        #region Copy

        private static void CopyToClipboard(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch (Exception)
            {
                // The clipboard can be transiently locked by another process; copying is best-effort.
            }
        }

        #endregion

        #region Metrics + rendering

        private void EnsureMetrics()
        {
            double dpi = canvas.Dpi > 0 ? canvas.Dpi : 96.0;
            if (metricsValid && dpi == lastMetricsDpi)
            {
                return;
            }

            if (!metricsValid)
            {
                try
                {
                    CanvasDevice device = CanvasDevice.GetSharedDevice();
                    using var probe = new CanvasTextLayout(device, new string('0', 10), textFormat, 0, 0);
                    charWidth = probe.LayoutBounds.Width / 10.0;
                    lineHeightRaw = probe.LayoutBounds.Height;
                    // Only latch the metrics once a real probe succeeds, so a transient device failure on
                    // the first call falls back for this draw but is recomputed on the next.
                    if (charWidth > 0 && lineHeightRaw > 0)
                    {
                        metricsValid = true;
                    }
                }
                catch (Exception)
                {
                    charWidth = 0;
                    lineHeightRaw = 0;
                }

                if (charWidth <= 0)
                {
                    charWidth = 8.0;
                }
                if (lineHeightRaw <= 0)
                {
                    lineHeightRaw = 18.0;
                }
            }

            // Snap the line height to a whole *device* pixel so every line's top lands on the physical pixel
            // grid and the text stays crisp at fractional DPI (e.g. 150%). This is no longer what prevents the
            // historical caret-blink jitter — the caret is now a composition overlay, so a blink never redraws
            // the text at all (see the caret overlay remarks) — but keeping tops on the grid avoids sub-pixel
            // softening. The snapping math is the pure, unit-tested PixelSnap.
            double dpiScale = dpi / 96.0;
            lineHeight = PixelSnap.SnapToDevicePixels(lineHeightRaw, dpiScale);
            lastMetricsDpi = dpi;

            engine.SetMetrics(charWidth, lineHeight);
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            bool logFirstDraw = !hasLoggedFirstDraw;
            if (logFirstDraw)
            {
                hasLoggedFirstDraw = true;
                LogDiagnostic(
                    $"TextCanvas: first Draw event entered; engineLines={engine.TotalLines:N0}; " +
                    $"canvas={canvas.ActualWidth:N0}x{canvas.ActualHeight:N0}; dpi={canvas.Dpi:N0}");
            }

            bool logFirstContentDraw = engine.TotalLines > 0 && !hasLoggedFirstContentDraw;
            if (logFirstContentDraw)
            {
                hasLoggedFirstContentDraw = true;
                LogDiagnostic($"TextCanvas: first content draw entered; totalLines={engine.TotalLines:N0}; viewport={ActualWidth:N0}x{ActualHeight:N0}");
            }

            try
            {
                CanvasDrawingSession ds = args.DrawingSession;
                ds.Clear(backgroundColor);
                if (logFirstDraw)
                    LogDiagnostic("TextCanvas: first Draw drawing session acquired and background cleared");

            if (logFirstContentDraw)
                LogDiagnostic("TextCanvas: first content draw before EnsureMetrics");
            EnsureMetrics();
            if (logFirstContentDraw)
                LogDiagnostic($"TextCanvas: first content draw metrics ready; charWidth={charWidth:F2}; lineHeight={lineHeight:F2}; dpi={canvas.Dpi:F0}");

            SyncViewport();
            if (logFirstContentDraw)
                LogDiagnostic($"TextCanvas: first content draw viewport synced; viewport={ActualWidth:F0}x{ActualHeight:F0}");

            // The legacy per-row cache is retained only for the fallback path. Proportional layouts are
            // owned and bounded by TextLayoutEngine, keyed by original source line and typography options.
            activeTextLayouts.Clear();
            drawnProportionalLines.Clear();

            double dpiScale = (canvas.Dpi > 0 ? canvas.Dpi : 96.0) / 96.0;
            if (logFirstContentDraw)
                LogDiagnostic($"TextCanvas: first content draw before GetVisibleLines; firstLine={engine.FirstVisibleLine}; totalLines={engine.TotalLines:N0}; wordWrap={engine.WordWrap}");
            IReadOnlyList<VisibleLine> lines = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
            if (logFirstContentDraw)
                LogDiagnostic($"TextCanvas: first content draw GetVisibleLines completed; visibleRows={lines.Count}; proportionalRows={lines.Count(line => line.UsesProportionalLayout)}");

            // Paint every proportional background before drawing any glyphs. A single source-line layout can
            // produce several visible rows; painting a later row's highlight after drawing the complete layout
            // would cover its text. This first pass keeps the z-order correct for all wrapped rows.
            foreach (VisibleLine line in lines)
            {
                if (!line.UsesProportionalLayout)
                    continue;

                ITextLayoutResult? sourceLayout = engine.GetLayoutForLine(line.LineIndex);
                if (sourceLayout is null)
                    continue;

                float layoutTopPx = (float)PixelSnap.SnapToDevicePixels(
                    line.Y - line.LayoutRowTop, dpiScale);
                DrawProportionalRowHighlights(ds, sourceLayout, line, layoutTopPx);
            }

            foreach (VisibleLine line in lines)
            {
                float rowTopPx = (float)PixelSnap.SnapToDevicePixels(line.Y, dpiScale);
                ITextLayoutResult? sourceLayout = line.UsesProportionalLayout
                    ? engine.GetLayoutForLine(line.LineIndex)
                    : null;
                CanvasTextLayout? layout = line.UsesProportionalLayout
                    ? (sourceLayout as Win2DTextLayoutResult)?.NativeLayout
                    : GetTextLayout(line);

                double layoutTop = line.UsesProportionalLayout
                    ? line.Y - line.LayoutRowTop
                    : line.Y;
                float layoutTopPx = (float)PixelSnap.SnapToDevicePixels(layoutTop, dpiScale);

                // Proportional backgrounds were painted in the pre-pass. Keep the original ordering for the
                // legacy renderer, where each visible row owns an independent layout object.
                if (sourceLayout is null)
                {
                    if (line.RuleHighlights is { Count: > 0 })
                    {
                        foreach (RuleHighlightRect highlight in line.RuleHighlights)
                        {
                            if (layout is not null && highlight.StartIndex >= 0 && highlight.Length > 0)
                            {
                                DrawTextRangeHighlight(
                                    ds, layout, line, highlight.StartIndex, highlight.Length,
                                    ToColor(highlight.Argb), rowTopPx);
                            }
                            else
                            {
                                ds.FillRectangle(
                                    (float)highlight.X, rowTopPx, (float)highlight.Width,
                                    (float)lineHeight, ToColor(highlight.Argb));
                            }
                        }
                    }

                    if (line.Matches is { Count: > 0 })
                    {
                        foreach (HighlightRect highlight in line.Matches)
                        {
                            if (layout is not null && highlight.StartIndex >= 0 && highlight.Length > 0)
                            {
                                DrawTextRangeHighlight(
                                    ds, layout, line, highlight.StartIndex, highlight.Length,
                                    matchHighlightColor, rowTopPx);
                            }
                            else
                            {
                                ds.FillRectangle(
                                    (float)highlight.X, rowTopPx, (float)highlight.Width,
                                    (float)lineHeight, matchHighlightColor);
                            }
                        }
                    }

                    if (line.SelectionMatches is { Count: > 0 })
                    {
                        foreach (HighlightRect highlight in line.SelectionMatches)
                        {
                            if (layout is not null && highlight.StartIndex >= 0 && highlight.Length > 0)
                            {
                                DrawTextRangeHighlight(
                                    ds, layout, line, highlight.StartIndex, highlight.Length,
                                    selectionOccurrenceColor, rowTopPx);
                            }
                            else
                            {
                                ds.FillRectangle(
                                    (float)highlight.X, rowTopPx, (float)highlight.Width,
                                    (float)lineHeight, selectionOccurrenceColor);
                            }
                        }
                    }

                    if (line.HasSelection)
                    {
                        if (layout is not null)
                        {
                            DrawTextRangeHighlight(
                                ds, layout, line, line.SelectionStartIndex, line.SelectionLength,
                                selectionColor, rowTopPx);
                            if (line.SelectionContinuesPastRow)
                            {
                                Vector2 end = layout.GetCaretPosition(
                                    line.Display.Length, trailingSideOfCharacter: false);
                                ds.FillRectangle(
                                    (float)(line.TextX + end.X), rowTopPx,
                                    (float)Math.Max(1.0, charWidth), (float)lineHeight, selectionColor);
                            }
                        }
                        else
                        {
                            ds.FillRectangle(
                                (float)line.SelectionX, rowTopPx,
                                (float)line.SelectionWidth, (float)lineHeight, selectionColor);
                        }
                    }
                }

                // A proportional layout represents the complete original logical line. Draw it once at the
                // correct layout origin; the canvas clips off-screen rows when the viewport starts mid-line.
                if (layout is not null)
                {
                    if (!line.UsesProportionalLayout || drawnProportionalLines.Add(line.LineIndex))
                    {
                        ds.DrawTextLayout(
                            layout,
                            (float)line.TextX,
                            line.UsesProportionalLayout ? layoutTopPx : rowTopPx,
                            textColor);
                    }
                }
                else
                {
                    ds.DrawText(line.Display, new Vector2((float)line.TextX, rowTopPx), textColor, textFormat);
                }

                if (sourceLayout is not null)
                {
                    DrawProportionalAnnotationMarkers(ds, sourceLayout, line, layoutTopPx, dpiScale);
                    if (engine.ShowWhitespace)
                        DrawProportionalWhitespaceMarkers(ds, sourceLayout, line, layoutTopPx, dpiScale);
                }
                else
                {
                    DrawAnnotationMarkers(ds, layout, line, rowTopPx);
                    if (line.Whitespace is { Count: > 0 })
                        DrawWhitespaceMarkers(ds, line, rowTopPx);
                }

            }

            PruneTextLayoutCache();
            DrawGutter(ds, sender, lines);

                if (logFirstContentDraw)
                {
                    int proportionalRows = lines.Count(line => line.UsesProportionalLayout);
                    LogDiagnostic($"TextCanvas: first content draw completed; visibleRows={lines.Count}; proportionalRows={proportionalRows}");
                }
            }
            catch (Exception ex)
            {
                LogDiagnosticException("TextCanvas.OnDraw", ex);
                throw;
            }
        }

        private static void LogDiagnostic(string message)
        {
            try
            {
                CrashLogger.Default().Trace(message);
            }
            catch
            {
                // Diagnostics must never interfere with rendering.
            }
        }

        private static void LogDiagnosticException(string operation, Exception exception)
        {
            try
            {
                CrashLogger logger = CrashLogger.Default();
                logger.Trace($"{operation}: FAILED");
                logger.Log(exception);
            }
            catch
            {
                // Diagnostics must never interfere with rendering.
            }
        }

        private void DrawProportionalRowHighlights(
            CanvasDrawingSession ds,
            ITextLayoutResult layout,
            VisibleLine line,
            float layoutTopPx)
        {
            if (line.RuleHighlights is { Count: > 0 })
            {
                foreach (RuleHighlightRect highlight in line.RuleHighlights)
                {
                    if (highlight.StartIndex >= 0 && highlight.Length > 0)
                    {
                        DrawSourceTextRangeHighlight(
                            ds, layout, line, highlight.StartIndex, highlight.Length,
                            ToColor(highlight.Argb), layoutTopPx);
                    }
                }
            }

            if (line.Matches is { Count: > 0 })
            {
                foreach (HighlightRect highlight in line.Matches)
                {
                    if (highlight.StartIndex >= 0 && highlight.Length > 0)
                    {
                        DrawSourceTextRangeHighlight(
                            ds, layout, line, highlight.StartIndex, highlight.Length,
                            matchHighlightColor, layoutTopPx);
                    }
                }
            }

            if (line.SelectionMatches is { Count: > 0 })
            {
                foreach (HighlightRect highlight in line.SelectionMatches)
                {
                    if (highlight.StartIndex >= 0 && highlight.Length > 0)
                    {
                        DrawSourceTextRangeHighlight(
                            ds, layout, line, highlight.StartIndex, highlight.Length,
                            selectionOccurrenceColor, layoutTopPx);
                    }
                }
            }

            if (line.HasSelection)
            {
                DrawSourceTextRangeHighlight(
                    ds, layout, line, line.SelectionStartIndex, line.SelectionLength,
                    selectionColor, layoutTopPx);
                if (line.SelectionContinuesPastRow)
                {
                    DrawSourceSelectionContinuation(
                        ds, layout, line, selectionColor, layoutTopPx);
                }
            }
        }

        private CanvasTextLayout? GetTextLayout(VisibleLine line)
        {
            var key = new TextLayoutKey(line.LineIndex, line.DisplayStartIndex, line.Display.Length);
            activeTextLayouts.Add(key);
            if (textLayoutCache.TryGetValue(key, out CanvasTextLayout? cached))
            {
                return cached;
            }

            try
            {
                // Reading mode produces short visual rows; the larger estimate also supports long unwrapped
                // lines without allowing DirectWrite to wrap them a second time.
                double estimatedWidth = Math.Max(
                    ActualWidth + engine.HorizontalOffset + 64,
                    line.Display.Length * Math.Max(charWidth, textFormat.FontSize) * 2 + 64);
                float layoutWidth = (float)Math.Clamp(estimatedWidth, 256, 10_000_000);
                float layoutHeight = (float)Math.Max(lineHeight * 2, textFormat.FontSize * 4);
                var layout = new CanvasTextLayout(
                    CanvasDevice.GetSharedDevice(), line.Display, textFormat, layoutWidth, layoutHeight)
                {
                    WordWrapping = CanvasWordWrapping.NoWrap,
                };
                textLayoutCache.Add(key, layout);
                return layout;
            }
            catch (Exception)
            {
                // Device loss or an invalid native layout must not make the read-only viewer unusable.
                ClearTextLayoutCache();
                return null;
            }
        }

        private void DrawSourceTextRangeHighlight(
            CanvasDrawingSession ds,
            ITextLayoutResult layout,
            VisibleLine line,
            int sourceStart,
            int sourceLength,
            Color color,
            float layoutTop)
        {
            if (sourceLength <= 0)
                return;

            // VisibleLine source ranges are already clipped to this visual row. Paint the exact shared
            // text rectangle; the same rectangle is used for pointer hit-testing and annotation clicks.
            foreach (TextLayoutRect rectangle in layout.GetRangeGeometry(sourceStart, sourceLength))
            {
                if (rectangle.Height <= 0 || rectangle.Width <= 0)
                    continue;

                // layoutTop is the same snapped origin used by DrawTextLayout. Do not snap the summed Y again:
                // doing so changes the relative offset between glyphs and their background at fractional DPI.
                float y = layoutTop + (float)rectangle.Y;
                ds.FillRectangle(
                    (float)(line.TextX + rectangle.X),
                    y,
                    (float)rectangle.Width,
                    (float)rectangle.Height,
                    color);
            }
        }

        private void DrawSourceSelectionContinuation(
            CanvasDrawingSession ds,
            ITextLayoutResult layout,
            VisibleLine line,
            Color color,
            float layoutTop)
        {
            if (line.SourceEndIndex <= line.SourceStartIndex)
                return;

            int lastCharacter = layout.GetPreviousCaretIndex(line.SourceEndIndex);
            TextCaretGeometry leading = layout.GetCaretGeometry(lastCharacter);
            TextCaretGeometry trailing = layout.GetCaretGeometry(
                lastCharacter, trailingSideOfCharacter: true);
            double advance = Math.Abs(trailing.X - leading.X);
            if (!double.IsFinite(advance) || advance <= 0)
                advance = Math.Max(1, textFormat.FontSize * 0.25);

            IReadOnlyList<TextLayoutRect> textGeometry = layout.GetRangeGeometry(
                lastCharacter, line.SourceEndIndex - lastCharacter);
            if (textGeometry.Count == 0)
                return;

            TextLayoutRect textRectangle = textGeometry[0];
            float y = layoutTop + (float)textRectangle.Y;
            ds.FillRectangle(
                (float)(line.TextX + trailing.X),
                y,
                (float)advance,
                (float)textRectangle.Height,
                color);
        }

        private void DrawProportionalAnnotationMarkers(
            CanvasDrawingSession ds,
            ITextLayoutResult layout,
            VisibleLine line,
            float layoutTop,
            double dpiScale)
        {
            if (!annotationMarkersByLine.TryGetValue(line.LineIndex, out List<TextAnnotationMarker>? markers))
                return;

            foreach (TextAnnotationMarker marker in markers)
            {
                int start = Math.Max(
                    line.SourceStartIndex, Math.Clamp(marker.StartColumn, 0, layout.SourceText.Length));
                int end = Math.Min(
                    line.SourceEndIndex, Math.Clamp(marker.EndColumn, start, layout.SourceText.Length));
                if (end <= start)
                    continue;

                TextLayoutVerticalGeometry vertical = layout.GetVisualRowVerticalGeometry(start);
                if (!vertical.HasAnnotationUnderline)
                    continue;

                // The underline is anchored to the same baseline-derived text band used by selection and hit-testing.
                float y = layoutTop + (float)vertical.AnnotationUnderlineY;
                float strokeThickness = (float)vertical.AnnotationUnderlineThickness;

                foreach (TextLayoutRect rectangle in layout.GetRangeGeometry(start, end - start))
                {
                    if (rectangle.Height <= 0 || rectangle.Width <= 0)
                        continue;

                    float left = (float)(line.TextX + rectangle.X);
                    float right = left + (float)rectangle.Width;
                    const float dashLength = 3.5f;
                    const float dashGap = 2.5f;
                    for (float x = left; x < right; x += dashLength + dashGap)
                    {
                        ds.DrawLine(
                            x, y, Math.Min(x + dashLength, right),
                            y, annotationMarkerColor, strokeThickness);
                    }
                }
            }
        }

        private void DrawProportionalWhitespaceMarkers(
            CanvasDrawingSession ds,
            ITextLayoutResult layout,
            VisibleLine line,
            float layoutTop,
            double dpiScale)
        {
            string source = layout.SourceText;
            int lastSignificant = -1;
            for (int index = 0; index < source.Length; index++)
            {
                char value = source[index];
                if (value != ' ' && value != '\t')
                    lastSignificant = index;
            }

            int start = Math.Clamp(line.SourceStartIndex, 0, source.Length);
            int end = Math.Clamp(line.SourceEndIndex, start, source.Length);
            double rowStart = line.LayoutRowTop;
            double rowEnd = rowStart + line.LayoutRowHeight;

            for (int index = start; index < end; index++)
            {
                char value = source[index];
                WhitespaceKind? kind = value switch
                {
                    ' ' => WhitespaceKind.Space,
                    '\t' => WhitespaceKind.Tab,
                    _ when char.IsControl(value) => WhitespaceKind.Control,
                    _ => null,
                };
                if (kind is null)
                    continue;

                IReadOnlyList<TextLayoutRect> geometry = layout.GetRangeGeometry(index, 1);
                if (geometry.Count == 0)
                {
                    TextCaretGeometry leading = layout.GetCaretGeometry(index);
                    TextCaretGeometry trailing = layout.GetCaretGeometry(index, trailingSideOfCharacter: true);
                    geometry = new[]
                    {
                        new TextLayoutRect(
                            Math.Min(leading.X, trailing.X),
                            rowStart,
                            Math.Abs(trailing.X - leading.X),
                            line.LayoutRowHeight),
                    };
                }

                WhitespaceKind markerKind = kind.Value;
                bool isTrailing = (markerKind == WhitespaceKind.Space || markerKind == WhitespaceKind.Tab)
                                  && index > lastSignificant;
                Color color = isTrailing ? trailingWhitespaceColor : whitespaceColor;

                foreach (TextLayoutRect rectangle in geometry)
                {
                    double top = Math.Max(rowStart, rectangle.Y);
                    double bottom = Math.Min(rowEnd, rectangle.Y + rectangle.Height);
                    if (bottom <= top || rectangle.Width <= 0)
                        continue;

                    float x = (float)(line.TextX + rectangle.X);
                    float y = (float)PixelSnap.SnapToDevicePixels(layoutTop + top, dpiScale);
                    float width = (float)rectangle.Width;
                    float height = (float)(bottom - top);
                    float midY = y + height / 2f;

                    switch (markerKind)
                    {
                        case WhitespaceKind.Space:
                            float radius = Math.Max(1.5f, width * 0.14f);
                            ds.FillEllipse(x + width / 2f, midY, radius, radius, color);
                            break;

                        case WhitespaceKind.Tab:
                            float x0 = x + 2f;
                            float x1 = x + width - 2f;
                            if (x1 > x0)
                            {
                                ds.DrawLine(x0, midY, x1, midY, color, 1.5f);
                                ds.DrawLine(x1 - 4f, midY - 4f, x1, midY, color, 1.5f);
                                ds.DrawLine(x1 - 4f, midY + 4f, x1, midY, color, 1.5f);
                            }
                            break;

                        case WhitespaceKind.Control:
                            ds.DrawRectangle(x + 1f, y + 2f, Math.Max(1, width - 2f),
                                Math.Max(1, height - 4f), controlCharColor, 1.5f);
                            break;
                    }
                }
            }

            if (line.SourceEndIndex == source.Length)
            {
                LineEnding ending = engine.GetLineEnding(line.LineIndex);
                if (ending == LineEnding.Lf || ending == LineEnding.CrLf)
                {
                    TextCaretGeometry caret = layout.GetCaretGeometry(source.Length);
                    float x = (float)(line.TextX + caret.X);
                    float y = (float)PixelSnap.SnapToDevicePixels(layoutTop + line.LayoutRowTop, dpiScale);
                    DrawReturnMark(ds, x, y, (float)Math.Max(4, charWidth),
                        (float)line.LayoutRowHeight, ending == LineEnding.CrLf);
                }
            }
        }

        private void DrawAnnotationMarkers(
            CanvasDrawingSession ds, CanvasTextLayout? layout, VisibleLine line, float top)
        {
            if (layout is null || !annotationMarkersByLine.TryGetValue(
                    line.LineIndex, out List<TextAnnotationMarker>? markers))
            {
                return;
            }

            float y = top + (float)Math.Max(1, lineHeight - 1.5);
            foreach (TextAnnotationMarker marker in markers)
            {
                int startIndex = engine.GetDisplayIndexForColumn(line.LineIndex, marker.StartColumn)
                                 - line.DisplayStartIndex;
                int endIndex = engine.GetDisplayIndexForColumn(line.LineIndex, marker.EndColumn)
                               - line.DisplayStartIndex;
                startIndex = Math.Clamp(startIndex, 0, line.Display.Length);
                endIndex = Math.Clamp(endIndex, 0, line.Display.Length);
                if (endIndex <= startIndex)
                    continue;

                Vector2 start = layout.GetCaretPosition(startIndex, trailingSideOfCharacter: false);
                Vector2 end = layout.GetCaretPosition(endIndex, trailingSideOfCharacter: false);
                float left = (float)(line.TextX + Math.Min(start.X, end.X));
                float right = (float)(line.TextX + Math.Max(start.X, end.X));
                const float dashLength = 3.5f;
                const float dashGap = 2.5f;

                for (float x = left; x < right; x += dashLength + dashGap)
                {
                    ds.DrawLine(x, y, Math.Min(x + dashLength, right), y, annotationMarkerColor, 1.25f);
                }
            }
        }

        private void DrawTextRangeHighlight(
            CanvasDrawingSession ds,
            CanvasTextLayout layout,
            VisibleLine line,
            int startIndex,
            int length,
            Color color,
            float top)
        {
            if (length <= 0 || line.Display.Length == 0)
            {
                return;
            }

            startIndex = Math.Clamp(startIndex, 0, line.Display.Length);
            int endIndex = Math.Clamp(startIndex + length, startIndex, line.Display.Length);
            if (endIndex == startIndex)
            {
                return;
            }

            // Caret positions include trailing whitespace, unlike glyph ink bounds, so copied spaces and
            // selected spaces receive the same accurate background as visible glyphs.
            Vector2 start = layout.GetCaretPosition(startIndex, trailingSideOfCharacter: false);
            Vector2 end = layout.GetCaretPosition(endIndex, trailingSideOfCharacter: false);
            float left = Math.Min(start.X, end.X);
            float right = Math.Max(start.X, end.X);
            if (right > left)
            {
                ds.FillRectangle((float)(line.TextX + left), top, right - left, (float)lineHeight, color);
            }
        }

        private TextPosition HitTestText(double x, double y)
        {
            if (engine.UsesProportionalLayout)
                return engine.HitTest(x, y);

            IReadOnlyList<VisibleLine> lines = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
            if (lines.Count == 0)
            {
                return engine.HitTest(x, y);
            }

            VisibleLine hitLine = lines[0];
            foreach (VisibleLine candidate in lines)
            {
                if (y >= candidate.Y && y < candidate.Y + lineHeight)
                {
                    hitLine = candidate;
                    break;
                }
                if (y >= candidate.Y)
                {
                    hitLine = candidate;
                }
            }

            if (hitLine.Display.Length == 0)
            {
                return engine.PositionFromDisplayIndex(hitLine.LineIndex, hitLine.DisplayStartIndex, trailing: false);
            }

            CanvasTextLayout? layout = GetTextLayout(hitLine);
            if (layout == null)
            {
                return engine.HitTest(x, y);
            }

            float localX = (float)(x - hitLine.TextX);
            // Y has already selected the visual row; use its vertical center so clicks near the top or
            // bottom padding of a row still hit the glyphs on that row.
            float localY = (float)Math.Max(0, lineHeight / 2.0);
            if (layout.HitTest(localX, localY, out CanvasTextLayoutRegion region, out bool trailing))
            {
                int localIndex = Math.Clamp(region.CharacterIndex, 0, hitLine.Display.Length);
                int characterCount = Math.Max(1, region.CharacterCount);
                return engine.PositionFromDisplayIndex(
                    hitLine.LineIndex,
                    hitLine.DisplayStartIndex + localIndex,
                    trailing,
                    characterCount);
            }

            // Clicks before or after the painted text still place the caret at the nearest line boundary.
            int boundary = localX <= layout.GetCaretPosition(0, trailingSideOfCharacter: false).X
                ? 0
                : hitLine.Display.Length;
            return engine.PositionFromDisplayIndex(
                hitLine.LineIndex, hitLine.DisplayStartIndex + boundary, trailing: false);
        }

        // Legacy fixed-cell fallback only: returns the source column of the glyph under the pointer,
        // not the nearest caret boundary. The proportional reader uses selection-like range rectangles.
        private TextPosition HitTestTextCharacter(double x, double y)
            => HitTestTextCharacter(x, y, out _, out _, out _);

        private TextPosition HitTestTextCharacter(
            double x, double y, out bool hitCharacter, out int hitSourceLength)
            => HitTestTextCharacter(x, y, out hitCharacter, out hitSourceLength, out _);

        // Marker activation is stricter than context-menu positioning: a nearest-boundary result from a
        // click in the blank area before/after a line must not activate an annotation on the boundary glyph.
        private TextPosition HitTestTextCharacter(
            double x, double y,
            out bool hitCharacter,
            out int hitSourceLength,
            out bool isTrailingHit)
        {
            hitCharacter = false;
            hitSourceLength = 0;
            isTrailingHit = false;
            if (engine.UsesProportionalLayout)
            {
                TextPosition position = engine.HitTestCharacter(
                    x, y, out hitCharacter, out hitSourceLength, out isTrailingHit);
                if (!hitCharacter &&
                    engine.TryHitTestAnnotationMarkers(
                        x, y, annotationMarkersByLine,
                        out TextPosition markerPosition,
                        out int markerSourceLength,
                        out bool markerTrailingHit))
                {
                    hitSourceLength = markerSourceLength;
                    hitCharacter = hitSourceLength > 0;
                    isTrailingHit = markerTrailingHit;
                    if (hitCharacter)
                        return markerPosition;
                }

                return position;
            }

            IReadOnlyList<VisibleLine> lines = engine.GetVisibleLines(hasFocus: false, caretVisible: false);
            if (lines.Count == 0)
            {
                hitCharacter = false;
                hitSourceLength = 0;
                isTrailingHit = false;
                return engine.HitTest(x, y);
            }

            VisibleLine hitLine = lines[0];
            foreach (VisibleLine candidate in lines)
            {
                if (y >= candidate.Y && y < candidate.Y + lineHeight)
                {
                    hitLine = candidate;
                    break;
                }

                if (y >= candidate.Y)
                    hitLine = candidate;
            }

            if (hitLine.Display.Length == 0)
            {
                hitCharacter = false;
                hitSourceLength = 0;
                isTrailingHit = false;
                return engine.PositionFromDisplayIndex(
                    hitLine.LineIndex, hitLine.DisplayStartIndex, trailing: false);
            }

            CanvasTextLayout? layout = GetTextLayout(hitLine);
            if (layout is null)
            {
                hitCharacter = false;
                hitSourceLength = 0;
                isTrailingHit = false;
                return HitTestText(x, y);
            }

            float localX = (float)(x - hitLine.TextX);
            float localY = (float)Math.Max(0, lineHeight / 2.0);
            if (layout.HitTest(localX, localY, out CanvasTextLayoutRegion region, out isTrailingHit))
            {
                hitCharacter = true;
                int localIndex = Math.Clamp(region.CharacterIndex, 0, hitLine.Display.Length);
                int displayCharacterCount = Math.Max(1, region.CharacterCount);
                int sourceDisplayIndex = hitLine.DisplayStartIndex + localIndex;
                TextPosition start = engine.PositionFromDisplayIndex(
                    hitLine.LineIndex,
                    sourceDisplayIndex,
                    trailing: false,
                    displayCharacterCount: displayCharacterCount);
                TextPosition end = engine.PositionFromDisplayIndex(
                    hitLine.LineIndex,
                    sourceDisplayIndex + displayCharacterCount,
                    trailing: false);
                hitSourceLength = Math.Max(1, end.Column - start.Column);
                return start;
            }

            hitCharacter = false;
            hitSourceLength = 0;
            isTrailingHit = false;
            int boundary = localX <= layout.GetCaretPosition(0, trailingSideOfCharacter: false).X
                ? 0
                : hitLine.Display.Length;
            return engine.PositionFromDisplayIndex(
                hitLine.LineIndex, hitLine.DisplayStartIndex + boundary, trailing: false);
        }

        private void PruneTextLayoutCache()
        {
            foreach (var pair in textLayoutCache.ToArray())
            {
                if (!activeTextLayouts.Contains(pair.Key))
                {
                    pair.Value.Dispose();
                    textLayoutCache.Remove(pair.Key);
                }
            }
            activeTextLayouts.Clear();
        }

        private void ClearTextLayoutCache()
        {
            foreach (CanvasTextLayout layout in textLayoutCache.Values)
            {
                layout.Dispose();
            }
            textLayoutCache.Clear();
            activeTextLayouts.Clear();
        }

        // Overlays markers for spaces (dot), tabs (arrow) and other control chars (box) when "Show Whitespace"
        // is on; trailing space/tab runs use a stronger (reddish) colour so they stand out. <paramref name="top"/>
        // is the line's pixel-snapped top (so markers align with the snapped text).
        private void DrawWhitespaceMarkers(CanvasDrawingSession ds, VisibleLine line, float top)
        {
            float cw = (float)charWidth;
            float h = (float)lineHeight;
            float midY = top + h / 2f;

            foreach (WhitespaceMarker m in line.Whitespace)
            {
                float x = (float)line.TextX + m.Column * cw;
                Windows.UI.Color color = m.Trailing ? trailingWhitespaceColor : whitespaceColor;

                switch (m.Kind)
                {
                    case WhitespaceKind.Space:
                        float r = Math.Max(1.5f, cw * 0.14f);
                        ds.FillEllipse(x + cw / 2f, midY, r, r, color);
                        break;

                    case WhitespaceKind.Tab:
                        float w = m.Width * cw;
                        float x0 = x + 2f;
                        float x1 = x + w - 2f;
                        if (x1 > x0)
                        {
                            ds.DrawLine(x0, midY, x1, midY, color, 1.5f);
                            ds.DrawLine(x1 - 4f, midY - 4f, x1, midY, color, 1.5f); // arrowhead
                            ds.DrawLine(x1 - 4f, midY + 4f, x1, midY, color, 1.5f);
                        }
                        break;

                    case WhitespaceKind.Control:
                        ds.DrawRectangle(x + 1f, top + 2f, cw - 2f, h - 4f, controlCharColor, 1.5f);
                        break;

                    case WhitespaceKind.Lf:
                        DrawReturnMark(ds, x, top, cw, h, crlf: false);
                        break;

                    case WhitespaceKind.CrLf:
                        DrawReturnMark(ds, x, top, cw, h, crlf: true);
                        break;
                }
            }
        }

        // Draws a faint "return" mark at the end of a line for its terminator: a down-then-left arrow for LF,
        // with a small leading CR tick for CRLF so the two endings are distinguishable (e.g. in a Mixed file).
        private void DrawReturnMark(CanvasDrawingSession ds, float x, float yTop, float cw, float h, bool crlf)
        {
            Windows.UI.Color color = whitespaceColor;
            float baseX = x;
            if (crlf)
            {
                // CR tick: a short vertical bar just before the LF arrow.
                ds.FillRectangle(baseX + 1f, yTop + h * 0.30f, 2f, h * 0.40f, color);
                baseX += 4f;
            }

            float riserX = baseX + cw * 0.55f;
            float topY = yTop + h * 0.28f;
            float botY = yTop + h * 0.60f;
            float leftX = baseX + cw * 0.18f;

            ds.DrawLine(riserX, topY, riserX, botY, color, 1.5f);          // vertical riser
            ds.DrawLine(riserX, botY, leftX, botY, color, 1.5f);           // base, leftward
            ds.DrawLine(leftX, botY, leftX + 4f, botY - 4f, color, 1.5f);  // arrowhead
            ds.DrawLine(leftX, botY, leftX + 4f, botY + 4f, color, 1.5f);
        }

        // Draws the line-number gutter on top of the text (its opaque background masks any text scrolled under
        // it, since the gutter itself stays fixed while the text scrolls horizontally).
        private void DrawGutter(CanvasDrawingSession ds, CanvasControl sender, IReadOnlyList<VisibleLine> lines)
        {
            double gutterWidth = engine.GutterWidthPx;
            if (gutterWidth <= 0)
            {
                return;
            }

            float w = (float)gutterWidth;
            float h = (float)sender.Size.Height;
            ds.FillRectangle(0, 0, w, h, backgroundColor);
            ds.DrawLine(w - 0.5f, 0, w - 0.5f, h, gutterSeparatorColor, 1f);

            foreach (VisibleLine line in lines)
            {
                if (line.IsWrapContinuation)
                {
                    continue; // the line number and bookmark tick belong only to a wrapped line's first row
                }

                if (line.IsBookmarked)
                {
                    // A small dot near the gutter's left edge (numbers are right-aligned, so this stays clear).
                    double markerRowHeight = line.UsesProportionalLayout ? line.LayoutRowHeight : lineHeight;
                    float r = (float)Math.Min(markerRowHeight * 0.18, 4.0);
                    float cx = r + 1.5f;
                    float cy = (float)(line.Y + markerRowHeight / 2);
                    ds.FillEllipse(cx, cy, r, r, bookmarkColor);
                }

                string number = (line.LineIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                float x = (float)(gutterWidth - GutterRightMargin - number.Length * charWidth);
                ds.DrawText(number, new Vector2(x, (float)line.Y), gutterTextColor, textFormat);
            }
        }

        // Unpacks a 0xAARRGGBB highlight-rule colour into a Win2D Color.
        private static Color ToColor(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        // Resolves the reader-only palette and the live Windows High-Contrast override. Application theme
        // changes never influence this selection; the reader follows ReaderSettings.Theme exclusively.
        private void ApplyReaderPalette()
        {
            bool isHighContrast = false;
            try { isHighContrast = accessibility?.HighContrast == true; } catch { /* fall back to reader preset */ }

            HighContrastColors hc = isHighContrast ? ReadHighContrastColors() : default;
            CanvasColors c = ReaderPalette.Resolve(readerTheme, isHighContrast, hc);

            backgroundColor = ToColor(c.Background);
            textColor = ToColor(c.Text);
            selectionColor = ToColor(c.Selection);
            matchHighlightColor = ToColor(c.MatchHighlight);
            selectionOccurrenceColor = ToColor(c.SelectionOccurrence);
            gutterTextColor = ToColor(c.GutterText);
            gutterSeparatorColor = ToColor(c.GutterSeparator);
            bookmarkColor = ToColor(c.Bookmark);
            whitespaceColor = ToColor(c.Whitespace);
            trailingWhitespaceColor = ToColor(c.TrailingWhitespace);
            controlCharColor = ToColor(c.ControlChar);
            annotationMarkerColor = ToColor(c.AnnotationMarker);

            canvas?.Invalidate();
            ReaderPaletteChanged?.Invoke();
        }

        // Reads the live Windows High-Contrast system colours for ReaderPalette to map onto the surface.
        private static HighContrastColors ReadHighContrastColors() => new(
            Window: SysColor(COLOR_WINDOW),
            WindowText: SysColor(COLOR_WINDOWTEXT),
            Highlight: SysColor(COLOR_HIGHLIGHT),
            GrayText: SysColor(COLOR_GRAYTEXT),
            Hotlight: SysColor(COLOR_HOTLIGHT));

        // Reads a Win32 system colour (which Windows swaps to the High-Contrast scheme when HC is on) and
        // packs its 0x00BBGGRR COLORREF into an opaque 0xAARRGGBB value.
        private static uint SysColor(int index)
        {
            uint colorRef = GetSysColor(index);
            uint r = colorRef & 0xFF, g = (colorRef >> 8) & 0xFF, b = (colorRef >> 16) & 0xFF;
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        private const int COLOR_WINDOW = 5;
        private const int COLOR_WINDOWTEXT = 8;
        private const int COLOR_HIGHLIGHT = 13;
        private const int COLOR_GRAYTEXT = 17;
        private const int COLOR_HOTLIGHT = 26;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetSysColor(int nIndex);

        #endregion
    }
}
