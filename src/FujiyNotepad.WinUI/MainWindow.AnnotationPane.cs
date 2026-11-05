using FujiyNotepad.Core;
using Microsoft.UI.Dispatching;
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
    private Point lastTextSelectionPoint;
    private Point? pendingSmallComposerPosition;
    private Flyout? selectionActionsFlyout;
    private Flyout? smallAnnotationComposerFlyout;
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
        selectionActionsFlyout?.Hide();
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

    private void OnTextSelectionCompleted(Point anchor)
    {
        CloseSmallAnnotationComposer();
        pendingSmallComposerPosition = null;
        selectionActionsFlyout?.Hide();
        lastTextSelectionPoint = anchor;
        hasLastTextSelectionPoint = true;

        Brush menuBackground = GetAnnotationFlyoutBackground();
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
        copyContent.Children.Add(new SymbolIcon
        {
            Symbol = Symbol.Copy,
            FontSize = 19,
            Foreground = menuForeground,
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
            selectionActionsFlyout?.Hide();
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
            smallContent.Children.Add(new SymbolIcon
            {
                Symbol = Symbol.Edit,
                FontSize = 19,
                Foreground = menuForeground,
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
                pendingSmallComposerPosition = lastTextSelectionPoint;
                selectionActionsFlyout?.Hide();
                CreateSmallAnnotationFromSelection();
            };
            actions.Children.Add(smallButton);
        }

        var menuRoot = new Grid();
        menuRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        menuRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });
        var menuBody = new Border
        {
            Padding = new Thickness(5, 4, 5, 4),
            CornerRadius = new CornerRadius(22),
            Background = menuBackground,
            BorderThickness = new Thickness(0),
            Child = actions,
        };
        var menuArrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(0, 0),
                new Point(14, 0),
                new Point(7, 7),
            },
            Fill = menuBackground,
            Width = 14,
            Height = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        Grid.SetRow(menuBody, 0);
        Grid.SetRow(menuArrow, 1);
        menuRoot.Children.Add(menuBody);
        menuRoot.Children.Add(menuArrow);

        var selectionFlyout = new Flyout
        {
            Content = menuRoot,
            FlyoutPresenterStyle = CreateAnnotationFlyoutPresenterStyle(),
        };
        selectionActionsFlyout = selectionFlyout;
        selectionFlyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(selectionActionsFlyout, selectionFlyout))
                selectionActionsFlyout = null;
            if (pendingSmallComposerPosition is null)
                hasLastTextSelectionPoint = false;
        };
        selectionFlyout.ShowAt(View, new FlyoutShowOptions
        {
            Position = anchor,
            Placement = FlyoutPlacementMode.Top,
        });
    }

    private Task OpenSmallAnnotationComposerAsync(
        (long StartOffset, long EndOffset, string Quote) selected)
    {
        if (activeAnnotationStore is not { } store || activeNovelProject is not { } project)
            return Task.CompletedTask;

        Point position = pendingSmallComposerPosition
            ?? (hasLastTextSelectionPoint
                ? lastTextSelectionPoint
                : new Point(Math.Max(24, View.ActualWidth / 2), Math.Max(24, View.ActualHeight / 3)));
        pendingSmallComposerPosition = null;
        CloseSmallAnnotationComposer();
        pendingSmallAnnotationSelection = selected;
        pendingSmallAnnotationStore = store;
        pendingSmallAnnotationProject = project;
        pendingSmallAnnotationCategoryId = selectedSmallAnnotationCategoryId;

        Brush popupBackground = GetAnnotationFlyoutBackground();
        Brush popupForeground = new SolidColorBrush(Colors.White);
        var contentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 136,
            MaxHeight = 220,
            FontSize = 16,
            PlaceholderText = "写下这一刻的想法……",
            PlaceholderForeground = new SolidColorBrush(Color.FromArgb(165, 255, 255, 255)),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Foreground = popupForeground,
            Padding = new Thickness(2, 8, 2, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
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

        var closeButton = new Button
        {
            Content = "×",
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            FontSize = 20,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(255, 232, 234, 237)),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 45, 48, 52)),
            UseSystemFocusVisuals = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ToolTipService.SetToolTip(closeButton, "关闭");
        closeButton.Click += (_, _) => CloseSmallAnnotationComposer();

        var saveButton = new Button
        {
            Content = "保存",
            MinWidth = 100,
            Height = 40,
            Padding = new Thickness(18, 0, 18, 0),
            FontSize = 14,
            CornerRadius = new CornerRadius(20),
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

        var layout = new Grid { RowSpacing = 6, MinWidth = 360, MaxWidth = 440 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(closeButton, 0);
        Grid.SetRow(contentBox, 1);
        Grid.SetRow(smallAnnotationComposerValidation, 2);
        Grid.SetRow(saveButton, 3);
        layout.Children.Add(closeButton);
        layout.Children.Add(contentBox);
        layout.Children.Add(smallAnnotationComposerValidation);
        layout.Children.Add(saveButton);

        var flyout = new Flyout
        {
            Content = new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(24),
                Background = popupBackground,
                BorderThickness = new Thickness(0),
                Child = layout,
            },
            FlyoutPresenterStyle = CreateAnnotationFlyoutPresenterStyle(),
        };
        smallAnnotationComposerFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(smallAnnotationComposerFlyout, flyout))
            {
                smallAnnotationComposerFlyout = null;
                smallAnnotationComposerTextBox = null;
                smallAnnotationComposerValidation = null;
                pendingSmallAnnotationSelection = null;
                pendingSmallAnnotationStore = null;
                pendingSmallAnnotationProject = null;
                pendingSmallAnnotationCategoryId = null;
                hasLastTextSelectionPoint = false;
            }
        };

        flyout.ShowAt(View, new FlyoutShowOptions
        {
            Position = position,
            Placement = FlyoutPlacementMode.Bottom,
        });
        contentBox.Focus(FocusState.Programmatic);
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

    private void CloseSmallAnnotationComposer()
    {
        Flyout? flyout = smallAnnotationComposerFlyout;
        smallAnnotationComposerFlyout = null;
        flyout?.Hide();
        smallAnnotationComposerTextBox = null;
        smallAnnotationComposerValidation = null;
        pendingSmallAnnotationSelection = null;
        pendingSmallAnnotationStore = null;
        pendingSmallAnnotationProject = null;
        pendingSmallAnnotationCategoryId = null;
        pendingSmallComposerPosition = null;
        hasLastTextSelectionPoint = false;
    }

    private Brush GetAnnotationFlyoutBackground()
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

    private static Style CreateAnnotationFlyoutPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Colors.Transparent)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Colors.Transparent)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(0)));
        return style;
    }
}
