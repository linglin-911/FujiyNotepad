using FujiyNotepad.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace FujiyNotepad.WinUI;

public sealed partial class MainWindow
{
    private bool annotationPaneChapterManuallySelected;
    private DispatcherQueueTimer? annotationPaneChapterFollowTimer;
    private bool hasLastTextSelectionPoint;
    private Point lastTextSelectionComposerAnchor;
    private Point? pendingSmallComposerAnchor;
    private Popup? selectionActionsPopup;
    private Popup? smallAnnotationComposerPopup;
    private bool hasSmallComposerPassiveState;
    private bool wasReaderInteractionPassiveBeforeSmallComposer;
    private TextBox? smallAnnotationComposerTextBox;
    private TextBlock? smallAnnotationComposerValidation;
    private (long StartOffset, long EndOffset, string Quote)? pendingSmallAnnotationSelection;
    private NovelAnnotationStore? pendingSmallAnnotationStore;
    private NovelProject? pendingSmallAnnotationProject;
    private Guid? pendingSmallAnnotationCategoryId;

    private void SetAnnotationPaneTab(bool showChapterAnnotations)
    {
        if (ChapterAnnotationEditorView.Visibility == Visibility.Visible)
            CloseChapterAnnotationEditor_Click(this, new RoutedEventArgs());

        AnnotationPane.Visibility = Visibility.Visible;
        AnnotationPaneHeader.Visibility = Visibility.Visible;
        AnnotationPaneChapterSelector.Visibility = Visibility.Visible;
        AnnotationPaneTabBar.Visibility = Visibility.Visible;
        SmallAnnotationPane.Visibility = showChapterAnnotations
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChapterAnnotationPane.Visibility = showChapterAnnotations
            ? Visibility.Visible
            : Visibility.Collapsed;
        SmallAnnotationTabButton.IsChecked = !showChapterAnnotations;
        ChapterAnnotationTabButton.IsChecked = showChapterAnnotations;
        SmallAnnotationCount.Visibility = showChapterAnnotations
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChapterAnnotationCount.Visibility = showChapterAnnotations
            ? Visibility.Visible
            : Visibility.Collapsed;
        RebuildChapterIndexButton.Visibility = showChapterAnnotations
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChapterAnnotationListView.Visibility = Visibility.Visible;
        ChapterAnnotationEditorView.Visibility = Visibility.Collapsed;
        SmallAnnotationsItem.IsChecked = !showChapterAnnotations;
        ChapterAnnotationsItem.IsChecked = showChapterAnnotations;
        UpdateReaderToolsRailVisibility();
    }

    private async void SmallAnnotationTab_Click(object sender, RoutedEventArgs e)
    {
        if (activeAnnotationStore is null)
            return;

        SetAnnotationPaneTab(showChapterAnnotations: false);
        await ReloadSmallAnnotationCategoriesAsync();
        await RefreshSmallAnnotationsAsync();
    }

    private async void ChapterAnnotationTab_Click(object sender, RoutedEventArgs e)
    {
        if (activeAnnotationStore is null)
            return;

        SetAnnotationPaneTab(showChapterAnnotations: true);
        await LoadChapterAnnotationsAsync();
        if (LineIndexer.IsCompleted)
            _ = EnsureChapterIndexAsync();
    }

    private void CloseAnnotationPane_Click(object sender, RoutedEventArgs e)
    {
        CloseSelectionActionsPopup();
        CloseSmallAnnotationComposer();
        if (ChapterAnnotationEditorView.Visibility == Visibility.Visible)
            CloseChapterAnnotationEditor_Click(this, new RoutedEventArgs());

        CancelSmallAnnotationCardSingleTap();
        AnnotationPane.Visibility = Visibility.Collapsed;
        SmallAnnotationPane.Visibility = Visibility.Collapsed;
        ChapterAnnotationPane.Visibility = Visibility.Collapsed;
        SmallAnnotationsItem.IsChecked = false;
        ChapterAnnotationsItem.IsChecked = false;
        SmallAnnotationTabButton.IsChecked = false;
        ChapterAnnotationTabButton.IsChecked = false;
        annotationPaneChapterManuallySelected = false;
        annotationPaneChapterFollowTimer?.Stop();
        UpdateReaderToolsRailVisibility();
    }

    private void AnnotationPaneChapterSelector_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingChapterAnnotationUi
            || ChapterAnnotationEditorView.Visibility == Visibility.Visible)
        {
            return;
        }

        if ((AnnotationPaneChapterSelector.SelectedItem as ComboBoxItem)?.Tag is not Guid chapterId)
            return;

        selectedChapterAnnotationChapterId = chapterId;
        annotationPaneChapterManuallySelected = true;

        if (AnnotationPaneChapterSelector.IsDropDownOpen)
            return;

        RefreshAnnotationPaneForSelectedChapter();
    }

    private void AnnotationPaneChapterSelector_DropDownClosed(
        object sender, object e)
    {
        if (refreshingChapterAnnotationUi)
            return;

        RefreshAnnotationPaneForSelectedChapter();
    }

    private void RefreshAnnotationPaneForSelectedChapter()
    {
        if (ChapterAnnotationPane.Visibility == Visibility.Visible)
            ScheduleChapterAnnotationRefresh();
        else if (SmallAnnotationPane.Visibility == Visibility.Visible)
            _ = RefreshSmallAnnotationsAsync();
    }

    private void SetAnnotationPaneChapter(Guid chapterId, bool pinToChapter)
    {
        selectedChapterAnnotationChapterId = chapterId;
        if (pinToChapter)
            annotationPaneChapterManuallySelected = true;

        int index = -1;
        for (int i = 0; i < AnnotationPaneChapterSelector.Items.Count; i++)
        {
            if (AnnotationPaneChapterSelector.Items[i] is ComboBoxItem { Tag: Guid id }
                && id == chapterId)
            {
                index = i;
                break;
            }
        }

        if (index < 0 || AnnotationPaneChapterSelector.SelectedIndex == index)
            return;

        refreshingChapterAnnotationUi = true;
        try
        {
            AnnotationPaneChapterSelector.SelectedIndex = index;
        }
        finally
        {
            refreshingChapterAnnotationUi = false;
        }
    }

    private void ScheduleAnnotationPaneChapterFollow()
    {
        if (AnnotationPane.Visibility != Visibility.Visible
            || annotationPaneChapterManuallySelected
            || ChapterAnnotationEditorView.Visibility == Visibility.Visible)
        {
            return;
        }

        annotationPaneChapterFollowTimer ??= CreateAnnotationPaneChapterFollowTimer();
        annotationPaneChapterFollowTimer.Stop();
        annotationPaneChapterFollowTimer.Start();
    }

    private DispatcherQueueTimer CreateAnnotationPaneChapterFollowTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await UpdateAnnotationPaneChapterFromViewportAsync();
        return timer;
    }

    private async Task UpdateAnnotationPaneChapterFromViewportAsync()
    {
        if (AnnotationPane.Visibility != Visibility.Visible
            || annotationPaneChapterManuallySelected
            || ChapterAnnotationEditorView.Visibility == Visibility.Visible)
        {
            return;
        }

        NovelChapter? chapter = GetDominantVisibleChapter();
        if (chapter is null || chapter.Id == selectedChapterAnnotationChapterId)
            return;

        SetAnnotationPaneChapter(chapter.Id, pinToChapter: false);
        if (ChapterAnnotationPane.Visibility == Visibility.Visible)
            await RefreshChapterAnnotationsAsync();
        else if (SmallAnnotationPane.Visibility == Visibility.Visible)
            await RefreshSmallAnnotationsAsync();
    }

    private void OnTextSelectionCompleted(Point menuAnchor, Point composerAnchor)
    {
        // Keep the freshly created text selection while replacing any stale editor/menu.
        if (smallAnnotationComposerPopup?.IsOpen == true)
            return;

        CloseSmallAnnotationComposer(clearSelection: false);
        pendingSmallComposerAnchor = null;
        CloseSelectionActionsPopup();
        lastTextSelectionComposerAnchor = composerAnchor;
        hasLastTextSelectionPoint = true;

        Brush menuBackground = GetSelectionMenuBackground();
        Brush menuForeground = new SolidColorBrush(Colors.White);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };

        var copyContent = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        copyContent.Children.Add(new Viewbox
        {
            Width = 19,
            Height = 19,
            Child = new SymbolIcon
            {
                Symbol = Symbol.Copy,
                Foreground = menuForeground,
            },
        });
        copyContent.Children.Add(new TextBlock
        {
            Text = "复制",
            FontSize = 12,
            Foreground = menuForeground,
        });
        var copyButton = new Button
        {
            Content = copyContent,
            Width = 76,
            Height = 58,
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Foreground = menuForeground,
            UseSystemFocusVisuals = false,
        };
        copyButton.Click += (_, _) =>
        {
            View.CopySelection();
            View.ClearTextSelection();
            hasLastTextSelectionPoint = false;
            pendingSmallComposerAnchor = null;
            CloseSelectionActionsPopup();
            View.FocusCanvas();
        };
        actions.Children.Add(copyButton);

        if (activeAnnotationStore is not null
            && activeNovelProject is not null
            && filteredSource is null)
        {
            var smallContent = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            smallContent.Children.Add(new Viewbox
            {
                Width = 19,
                Height = 19,
                Child = new SymbolIcon
                {
                    Symbol = Symbol.Edit,
                    Foreground = menuForeground,
                },
            });
            smallContent.Children.Add(new TextBlock
            {
                Text = "小批",
                FontSize = 12,
                Foreground = menuForeground,
            });
            var smallButton = new Button
            {
                Content = smallContent,
                Width = 76,
                Height = 58,
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                Foreground = menuForeground,
                UseSystemFocusVisuals = false,
            };
            smallButton.Click += (_, _) =>
            {
                pendingSmallComposerAnchor = lastTextSelectionComposerAnchor;
                CloseSelectionActionsPopup();
                CreateSmallAnnotationFromSelection();
            };
            actions.Children.Add(smallButton);
        }

        const double menuBodyHeight = 66d;
        const double arrowHeight = 7d;
        double menuWidth = actions.Children.Count * 76d
                           + Math.Max(0, actions.Children.Count - 1) * 2d
                           + 10d;
        double menuHeight = menuBodyHeight + arrowHeight;

        // The two anchors describe the visible selection highlight itself:
        // menuAnchor is the top of the first selected row; composerAnchor is the bottom
        // of the last selected row. They are intentionally separate for multi-row selections.
        GeneralTransform viewToRoot = View.TransformToVisual(RootGrid);
        Point readerOrigin = viewToRoot.TransformPoint(new Point(0, 0));
        Point firstRowAnchorInRoot = viewToRoot.TransformPoint(menuAnchor);
        Point lastRowAnchorInRoot = viewToRoot.TransformPoint(composerAnchor);
        double readerWidth = Math.Max(1d, View.ActualWidth);
        double readerHeight = Math.Max(1d, View.ActualHeight);

        double leftMin = readerOrigin.X + 8d;
        double leftMax = readerOrigin.X + readerWidth - menuWidth - 8d;
        if (leftMax < leftMin)
            leftMax = leftMin;
        double topMin = readerOrigin.Y + 8d;
        double topMax = readerOrigin.Y + readerHeight - menuHeight - 8d;
        if (topMax < topMin)
            topMax = topMin;

        // Keep the menu above the first selected row whenever it fits.
        // At the top edge, flip it below the LAST selected row so it never covers the first row.
        bool placeBelowSelection = firstRowAnchorInRoot.Y - menuHeight < topMin;
        Point placementAnchor = placeBelowSelection
            ? lastRowAnchorInRoot
            : firstRowAnchorInRoot;
        // Leave a visible gap between the selection highlight and the arrow tip in either direction.
        const double arrowGap = 10d;
        double menuLeft = Math.Clamp(placementAnchor.X - menuWidth / 2d, leftMin, leftMax);
        double desiredMenuTop = placeBelowSelection
            ? placementAnchor.Y + arrowGap
            : firstRowAnchorInRoot.Y - menuHeight - arrowGap;
        double menuTop = Math.Clamp(desiredMenuTop, topMin, topMax);

        var menuRoot = new Grid
        {
            Width = menuWidth,
            Height = menuHeight,
        };
        menuRoot.RowDefinitions.Add(new RowDefinition
        {
            Height = placeBelowSelection ? new GridLength(arrowHeight) : GridLength.Auto,
        });
        menuRoot.RowDefinitions.Add(new RowDefinition
        {
            Height = placeBelowSelection ? GridLength.Auto : new GridLength(arrowHeight),
        });

        var menuBody = new Border
        {
            Width = menuWidth,
            Height = menuBodyHeight,
            Padding = new Thickness(5, 4, 5, 4),
            CornerRadius = new CornerRadius(22),
            Background = menuBackground,
            BorderThickness = new Thickness(0),
            Child = actions,
        };
        var menuArrow = new Polygon
        {
            Points = placeBelowSelection
                ? new PointCollection
                {
                    new Point(0, arrowHeight),
                    new Point(14, arrowHeight),
                    new Point(7, 0),
                }
                : new PointCollection
                {
                    new Point(0, 0),
                    new Point(14, 0),
                    new Point(7, arrowHeight),
                },
            Fill = menuBackground,
            Width = 14,
            Height = arrowHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = placeBelowSelection ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        Grid.SetRow(menuBody, placeBelowSelection ? 1 : 0);
        Grid.SetRow(menuArrow, placeBelowSelection ? 0 : 1);
        menuRoot.Children.Add(menuBody);
        menuRoot.Children.Add(menuArrow);

        var selectionPopup = new Popup
        {
            XamlRoot = RootGrid.XamlRoot,
            Child = menuRoot,
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = true,
            HorizontalOffset = menuLeft,
            VerticalOffset = menuTop,
        };
        selectionActionsPopup = selectionPopup;
        selectionPopup.Closed += (_, _) =>
        {
            if (ReferenceEquals(selectionActionsPopup, selectionPopup))
            {
                selectionActionsPopup = null;
                if (pendingSmallComposerAnchor is null)
                    hasLastTextSelectionPoint = false;
            }
        };
        selectionPopup.IsOpen = true;
    }

    private void CloseSelectionActionsPopup()
    {
        Popup? popup = selectionActionsPopup;
        if (popup?.IsOpen == true)
            popup.IsOpen = false;
        else if (ReferenceEquals(selectionActionsPopup, popup))
            selectionActionsPopup = null;
    }

    private Task OpenSmallAnnotationComposerAsync(
        (long StartOffset, long EndOffset, string Quote) selected)
    {
        if (activeAnnotationStore is not { } store || activeNovelProject is not { } project)
            return Task.CompletedTask;

        Point anchor = pendingSmallComposerAnchor
            ?? (hasLastTextSelectionPoint
                ? lastTextSelectionComposerAnchor
                : new Point(Math.Max(24, View.ActualWidth / 2), Math.Max(24, View.ActualHeight / 3)));
        pendingSmallComposerAnchor = null;
        CloseSmallAnnotationComposer(clearSelection: false);
        pendingSmallAnnotationSelection = selected;
        pendingSmallAnnotationStore = store;
        pendingSmallAnnotationProject = project;
        pendingSmallAnnotationCategoryId = selectedSmallAnnotationCategoryId;

        Brush popupBackground = new SolidColorBrush(Color.FromArgb(255, 48, 48, 48));
        Brush popupForeground = new SolidColorBrush(Colors.White);
        double readerWidth = View.ActualWidth > 0 ? View.ActualWidth : 720d;
        double readerHeight = View.ActualHeight > 0 ? View.ActualHeight : 640d;
        double popupWidth = Math.Min(660d, Math.Max(1d, readerWidth - 16d));
        double popupHeight = Math.Min(328d, Math.Max(1d, readerHeight - 16d));
        double layoutWidth = Math.Max(1d, popupWidth - 32d);
        double layoutHeight = Math.Max(1d, popupHeight - 28d);
        double editorMinHeight = Math.Max(1d, Math.Min(150d, layoutHeight - 80d));

        var contentBox = new TextBox
        {
            Style = (Style)RootGrid.Resources["AnnotationComposerTextBoxStyle"],
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = editorMinHeight,
            FontSize = 16,
            PlaceholderText = "写下这一刻的想法……",
            PlaceholderForeground = new SolidColorBrush(Color.FromArgb(165, 255, 255, 255)),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Foreground = popupForeground,
            Padding = new Thickness(0, 7, 0, 7),
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = true,
        };
        smallAnnotationComposerTextBox = contentBox;

        smallAnnotationComposerValidation = new TextBlock
        {
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(255, 196, 63, 63)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var closeGlyph = new Viewbox
        {
            Width = 12,
            Height = 12,
            Child = new SymbolIcon
            {
                Symbol = Symbol.Cancel,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 45, 48, 52)),
            },
        };
        var closeButton = new Button
        {
            Content = closeGlyph,
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(255, 232, 234, 237)),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ToolTipService.SetToolTip(closeButton, "关闭");
        closeButton.Click += (_, _) => CloseSmallAnnotationComposer();

        var saveButton = new Button
        {
            Content = "保存",
            MinWidth = 82,
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 13,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(255, 43, 132, 222)),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Colors.White),
            UseSystemFocusVisuals = false,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        contentBox.TextChanged += (_, _) =>
        {
            bool hasContent = !string.IsNullOrWhiteSpace(contentBox.Text);
            saveButton.IsEnabled = hasContent;
            saveButton.Background = new SolidColorBrush(
                hasContent
                    ? Color.FromArgb(255, 43, 132, 222)
                    : Color.FromArgb(255, 76, 80, 86));
        };
        saveButton.Click += async (_, _) => await SaveSmallAnnotationComposerAsync();

        var editorArea = new Grid { ColumnSpacing = 8 };
        editorArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        editorArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var editorIcon = new Viewbox
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new SymbolIcon
            {
                Symbol = Symbol.Edit,
                Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            },
        };
        Grid.SetColumn(editorIcon, 0);
        Grid.SetColumn(contentBox, 1);
        editorArea.Children.Add(editorIcon);
        editorArea.Children.Add(contentBox);

        var layout = new Grid
        {
            RowSpacing = 4,
            Width = layoutWidth,
            Height = layoutHeight,
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(closeButton, 0);
        Grid.SetRow(editorArea, 1);
        Grid.SetRow(smallAnnotationComposerValidation, 2);
        Grid.SetRow(saveButton, 3);
        layout.Children.Add(closeButton);
        layout.Children.Add(editorArea);
        layout.Children.Add(smallAnnotationComposerValidation);
        layout.Children.Add(saveButton);

        var card = new Border
        {
            Width = popupWidth,
            Height = popupHeight,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(24),
            Background = popupBackground,
            BorderThickness = new Thickness(0),
            Child = layout,
        };

        // Popup offsets are relative to the XamlRoot. Transform the last selected-row anchor
        // from TextCanvas coordinates into RootGrid coordinates, then clamp the card to the
        // reader viewport. If it would extend below the reader, bottom-align it inside the viewport.
        GeneralTransform viewToRoot = View.TransformToVisual(RootGrid);
        Point readerOrigin = viewToRoot.TransformPoint(new Point(0, 0));
        Point anchorInRoot = viewToRoot.TransformPoint(anchor);
        double leftMin = readerOrigin.X + 8d;
        double leftMax = readerOrigin.X + readerWidth - popupWidth - 8d;
        if (leftMax < leftMin)
            leftMax = leftMin;
        double topMin = readerOrigin.Y + 8d;
        double topMax = readerOrigin.Y + readerHeight - popupHeight - 8d;
        if (topMax < topMin)
            topMax = topMin;

        double popupLeft = Math.Clamp(anchorInRoot.X - popupWidth / 2d, leftMin, leftMax);
        double popupTop = Math.Clamp(anchorInRoot.Y + 12d, topMin, topMax);

        var popup = new Popup
        {
            XamlRoot = RootGrid.XamlRoot,
            Child = card,
            IsLightDismissEnabled = false,
            ShouldConstrainToRootBounds = true,
            HorizontalOffset = popupLeft,
            VerticalOffset = popupTop,
        };
        smallAnnotationComposerPopup = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(smallAnnotationComposerPopup, popup))
            {
                smallAnnotationComposerPopup = null;
                if (hasSmallComposerPassiveState)
                {
                    View.IsReaderInteractionPassive = wasReaderInteractionPassiveBeforeSmallComposer;
                    hasSmallComposerPassiveState = false;
                }
                View.ClearTextSelection();
                View.FocusCanvas();
                smallAnnotationComposerTextBox = null;
                smallAnnotationComposerValidation = null;
                pendingSmallAnnotationSelection = null;
                pendingSmallAnnotationStore = null;
                pendingSmallAnnotationProject = null;
                pendingSmallAnnotationCategoryId = null;
                hasLastTextSelectionPoint = false;
            }
        };

        contentBox.GotFocus += (_, _) =>
            LogDiagnostic("SmallAnnotationComposer: editor GotFocus");
        contentBox.LostFocus += (_, _) =>
            LogDiagnostic("SmallAnnotationComposer: editor LostFocus");

        // Popup.Opened is raised after the Popup child is attached to the live XamlRoot.
        // Request focus once, on the next dispatcher turn, so the native caret starts blinking
        // as soon as the editor appears. LostFocus is intentionally NOT auto-corrected:
        // the pointer policy below decides whether focus should remain or be released.
        popup.Opened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(smallAnnotationComposerTextBox, contentBox)
                && ReferenceEquals(smallAnnotationComposerPopup, popup)
                && popup.IsOpen)
            {
                bool focused = contentBox.Focus(FocusState.Programmatic);
                LogDiagnostic($"SmallAnnotationComposer: initial editor focus accepted={focused}");
            }
        });

        // Clicking the card outside the editable surface releases focus after the current
        // pointer event. Enabled buttons keep their normal pointer/click behavior.
        card.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, e) =>
            {
                if (IsPointerWithin(contentBox, e)
                    || IsPointerWithin(closeButton, e)
                    || (saveButton.IsEnabled && IsPointerWithin(saveButton, e)))
                {
                    return;
                }

                QueueSmallComposerFocusRelease(popup, contentBox);
            }),
            true);

        wasReaderInteractionPassiveBeforeSmallComposer = View.IsReaderInteractionPassive;
        hasSmallComposerPassiveState = true;
        View.IsReaderInteractionPassive = true;
        popup.IsOpen = true;
        return Task.CompletedTask;
    }

    private async Task SaveSmallAnnotationComposerAsync()
    {
        if (smallAnnotationComposerTextBox is not { } contentBox
            || pendingSmallAnnotationSelection is not { } selected
            || pendingSmallAnnotationStore is not { } store
            || pendingSmallAnnotationProject is not { } project)
        {
            return;
        }

        Guid? categoryId = pendingSmallAnnotationCategoryId;
        string content = contentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            if (smallAnnotationComposerValidation is not null)
            {
                smallAnnotationComposerValidation.Text = "批注内容不能为空。";
                smallAnnotationComposerValidation.Visibility = Visibility.Visible;
            }
            contentBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!ReferenceEquals(store, activeAnnotationStore)
            || !ReferenceEquals(project, activeNovelProject)
            || provider is null || annotationOffsetMap is null)
        {
            CloseSmallAnnotationComposer();
            return;
        }

        try
        {
            NovelChapterIndexState? indexState = await store.GetChapterIndexStateAsync();
            NovelChapter? chapter = await store.GetChapterAtOffsetAsync(selected.StartOffset);
            if (chapter is null || !chapter.IsActive
                || chapter.DocumentRevision != project.Metadata.DocumentRevision
                || indexState is null
                || indexState.DocumentRevision != project.Metadata.DocumentRevision
                || indexState.ParserVersion != NovelChapterIndexBuilder.CurrentParserVersion
                || selected.EndOffset > chapter.EndOffset)
            {
                throw new InvalidOperationException(
                    "当前章节索引尚未就绪，或所选原文没有完整落在一个有效章节内。请等待章节索引完成，并确保选区不跨越章节。");
            }

            var selectionCheck = new AnnotationAnchor(
                Guid.Empty, Guid.Empty,
                selected.StartOffset, selected.EndOffset,
                selected.Quote, chapter.Id,
                DocumentRevision: project.Metadata.DocumentRevision);
            if (!TryResolveSmallAnnotationAnchor(selectionCheck, out _))
                throw new InvalidOperationException("打开批注输入框期间，原文选区发生了变化。请重新选择原文后再创建批注。");

            LineProvider linesForMetadata = provider;
            DocumentTextOffsetMap mapForMetadata = annotationOffsetMap;
            AnnotationAnchorMetadata metadata = await Task.Run(() =>
                AnnotationAnchorMetadataBuilder.Create(
                    chapter, linesForMetadata, mapForMetadata,
                    selected.StartOffset, selected.EndOffset));

            if (!ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
            {
                CloseSmallAnnotationComposer();
                return;
            }

            Guid annotationId = await store.CreateAnnotationWithAnchorAsync(
                content,
                selected.StartOffset,
                selected.EndOffset,
                selected.Quote,
                categoryId: categoryId,
                title: null,
                chapterId: chapter.Id,
                documentRevision: project.Metadata.DocumentRevision,
                anchorMetadata: metadata);

            if (!ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
            {
                CloseSmallAnnotationComposer();
                return;
            }

            CloseSmallAnnotationComposer();
            AnnotationPane.Visibility = Visibility.Visible;
            SetAnnotationPaneTab(showChapterAnnotations: false);
            SetAnnotationPaneChapter(chapter.Id, pinToChapter: true);
            await LoadChapterAnnotationsAsync();
            await ReloadSmallAnnotationCategoriesAsync(categoryId);
            await RefreshSmallAnnotationsAsync(annotationId);
            await RefreshVisibleSmallAnnotationMarkersAsync(force: true);
            UpdateReaderToolsRailVisibility();
        }
        catch (Exception ex)
        {
            if (smallAnnotationComposerValidation is not null)
            {
                smallAnnotationComposerValidation.Text = ex.Message;
                smallAnnotationComposerValidation.Visibility = Visibility.Visible;
            }
        }
    }

    private void QueueSmallComposerFocusRelease(Popup popup, TextBox contentBox)
    {
        // Run after the pointer event so an X/Save button can complete its normal Click action.
        // If the click landed on a non-focusable surface, explicitly move focus to the passive
        // reader host; this hides the TextBox caret without making the reader interactive.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(smallAnnotationComposerPopup, popup)
                || !popup.IsOpen
                || !ReferenceEquals(smallAnnotationComposerTextBox, contentBox)
                || contentBox.FocusState == FocusState.Unfocused)
            {
                return;
            }

            bool moved = View.FocusPassiveReaderSurface();
            LogDiagnostic($"SmallAnnotationComposer: outside click released editor focus; readerFocusAccepted={moved}");
        });
    }

    private void QueueSmallComposerFocusReleaseForPointer(PointerRoutedEventArgs e)
    {
        Popup? popup = smallAnnotationComposerPopup;
        TextBox? contentBox = smallAnnotationComposerTextBox;
        if (popup?.IsOpen != true || contentBox is null || IsPointerWithin(contentBox, e))
            return;

        // Popup children normally use their own routed tree. If this event also reaches RootGrid,
        // leave clicks inside the card to the card handler so action buttons are not interrupted.
        if (popup.Child is FrameworkElement popupChild && IsPointerWithin(popupChild, e))
            return;

        QueueSmallComposerFocusRelease(popup, contentBox);
    }

    private void CloseSmallAnnotationComposer(bool clearSelection = true)
    {
        Popup? popup = smallAnnotationComposerPopup;
        smallAnnotationComposerPopup = null;
        if (popup?.IsOpen == true)
            popup.IsOpen = false;

        if (hasSmallComposerPassiveState)
        {
            View.IsReaderInteractionPassive = wasReaderInteractionPassiveBeforeSmallComposer;
            hasSmallComposerPassiveState = false;
        }

        smallAnnotationComposerTextBox = null;
        smallAnnotationComposerValidation = null;
        pendingSmallAnnotationSelection = null;
        pendingSmallAnnotationStore = null;
        pendingSmallAnnotationProject = null;
        pendingSmallAnnotationCategoryId = null;
        pendingSmallComposerAnchor = null;
        hasLastTextSelectionPoint = false;

        if (clearSelection)
        {
            View.ClearTextSelection();
            View.FocusCanvas();
        }
    }

    private Brush GetSelectionMenuBackground()
    {
        Color readerBackground = View.ReaderBackgroundColor;
        double luminance = (0.2126 * readerBackground.R
                            + 0.7152 * readerBackground.G
                            + 0.0722 * readerBackground.B) / 255d;
        bool readerIsDark = luminance < 0.2d;

        return new SolidColorBrush(
            readerIsDark
                ? Color.FromArgb(255, 48, 48, 48)
                : Color.FromArgb(255, 20, 20, 20));
    }

}
