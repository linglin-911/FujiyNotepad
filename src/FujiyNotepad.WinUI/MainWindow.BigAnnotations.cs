using FujiyNotepad.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FujiyNotepad.WinUI;

public sealed partial class MainWindow
{
    private IReadOnlyList<BigAnnotationCategory> bigAnnotationCategories = Array.Empty<BigAnnotationCategory>();
    private IReadOnlyList<NovelChapter> editingBigAnnotationChapters = Array.Empty<NovelChapter>();
    private Guid? selectedBigAnnotationCategoryId;
    private Guid? selectedBigAnnotationSmallReferenceCategoryId;
    private Guid? selectedBigAnnotationChapterReferenceCategoryId;
    private bool showBigAnnotationSmallReferences = true;
    private bool showBigAnnotationChapterReferences = true;
    private Guid? editingBigAnnotationId;
    private Guid? resumeBigAnnotationId;
    private bool resumeBigAnnotationEditorPending;
    private string initialBigAnnotationTitle = "";
    private string initialBigAnnotationContent = "";
    private Guid? initialBigAnnotationCategoryId;
    private Guid[] initialBigAnnotationChapterIds = Array.Empty<Guid>();
    private bool refreshingBigAnnotationUi;
    private int bigAnnotationListRefreshVersion;
    private int bigAnnotationReferenceRefreshVersion;
    private CancellationTokenSource? bigAnnotationReferenceRefreshCancellation;
    private double bigAnnotationReferencePaneWidth = 460;

    private async void BigAnnotations_Click(object sender, RoutedEventArgs e)
    {
        if (activeAnnotationStore is null || activeNovelProject is null)
        {
            BigAnnotationsItem.IsChecked = false;
            BigAnnotationWorkspace.Visibility = Visibility.Collapsed;
            ReturnToBigAnnotationButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (!BigAnnotationsItem.IsChecked)
        {
            if (!await CloseBigAnnotationWorkspaceAsync())
                BigAnnotationsItem.IsChecked = true;
            return;
        }

        await ShowBigAnnotationWorkspaceAsync();
    }

    private async Task ShowBigAnnotationWorkspaceAsync()
    {
        if (activeAnnotationStore is null || activeNovelProject is null)
            return;

        ReturnToBigAnnotationButton.Visibility = Visibility.Collapsed;
        CancelSmallAnnotationCardSingleTap();
        SmallAnnotationsItem.IsChecked = false;
        SmallAnnotationPane.Visibility = Visibility.Collapsed;
        ChapterAnnotationsItem.IsChecked = false;
        ChapterAnnotationPane.Visibility = Visibility.Collapsed;
        ChapterDirectoryItem.IsChecked = false;
        ChapterDirectoryPane.Visibility = Visibility.Collapsed;
        chapterAnnotationFilterDebounce?.Cancel();

        BigAnnotationsItem.IsChecked = true;
        BigAnnotationWorkspace.Visibility = Visibility.Visible;
        BigAnnotationListView.Visibility = Visibility.Visible;
        BigAnnotationEditorView.Visibility = Visibility.Collapsed;
        await LoadBigAnnotationListAsync();

        if (resumeBigAnnotationEditorPending && resumeBigAnnotationId is null)
        {
            // A brand-new empty draft has no database ID yet. Its editor controls and chapter
            // range were kept in memory while the reader was shown, so restore that editor directly.
            resumeBigAnnotationEditorPending = false;
            BigAnnotationListView.Visibility = Visibility.Collapsed;
            BigAnnotationEditorView.Visibility = Visibility.Visible;
            await RefreshBigAnnotationReferencesAsync();
            return;
        }

        if (resumeBigAnnotationId is Guid resumeId)
        {
            resumeBigAnnotationId = null;
            resumeBigAnnotationEditorPending = false;
            await OpenBigAnnotationEditorAsync(resumeId);
        }
    }

    private async void ReturnToBigAnnotationWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!resumeBigAnnotationEditorPending
            || activeAnnotationStore is null
            || activeNovelProject is null)
        {
            ReturnToBigAnnotationButton.Visibility = Visibility.Collapsed;
            return;
        }

        await ShowBigAnnotationWorkspaceAsync();
    }

    private async void CloseBigAnnotationWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!await CloseBigAnnotationWorkspaceAsync())
            return;
    }

    private async Task<bool> CloseBigAnnotationWorkspaceAsync()
    {
        if (BigAnnotationEditorView.Visibility == Visibility.Visible
            && !await TryLeaveBigAnnotationEditorAsync())
            return false;

        resumeBigAnnotationId = null;
        resumeBigAnnotationEditorPending = false;
        ReturnToBigAnnotationButton.Visibility = Visibility.Collapsed;
        ResetBigAnnotationEditorState();
        HideBigAnnotationWorkspace();
        return true;
    }

    private void HideBigAnnotationWorkspace()
    {
        BigAnnotationWorkspace.Visibility = Visibility.Collapsed;
        BigAnnotationsItem.IsChecked = false;
    }

    private void ResetBigAnnotationWorkspaceForProjectClose()
    {
        bigAnnotationReferenceRefreshCancellation?.Cancel();
        bigAnnotationReferenceRefreshCancellation = null;
        bigAnnotationListRefreshVersion++;
        bigAnnotationReferenceRefreshVersion++;
        bigAnnotationCategories = Array.Empty<BigAnnotationCategory>();
        selectedBigAnnotationCategoryId = null;
        resumeBigAnnotationId = null;
        resumeBigAnnotationEditorPending = false;
        ReturnToBigAnnotationButton.Visibility = Visibility.Collapsed;
        refreshingBigAnnotationUi = true;
        try
        {
            BigAnnotationSearchBox.Text = "";
            BigAnnotationReferenceSearchBox.Text = "";
            BigAnnotationCategoryFilter.Items.Clear();
            BigAnnotationCategoryFilter.SelectedIndex = -1;
            BigAnnotationEditorCategoryFilter.Items.Clear();
            BigAnnotationSmallCategoryFilter.Items.Clear();
            BigAnnotationChapterCategoryFilter.Items.Clear();
            showBigAnnotationSmallReferences = true;
            showBigAnnotationChapterReferences = true;
            BigAnnotationSmallVisibilityToggle.IsOn = true;
            BigAnnotationChapterVisibilityToggle.IsOn = true;
            BigAnnotationSmallCategoryFilter.Visibility = Visibility.Visible;
            BigAnnotationChapterCategoryFilter.Visibility = Visibility.Visible;
            BigAnnotationEditorTitleBox.Text = "";
            BigAnnotationEditorContentBox.Text = "";
        }
        finally
        {
            refreshingBigAnnotationUi = false;
        }
        ResetBigAnnotationEditorState();
        BigAnnotationWorkspace.Visibility = Visibility.Collapsed;
        BigAnnotationsItem.IsChecked = false;
        BigAnnotationListView.Visibility = Visibility.Visible;
        BigAnnotationEditorView.Visibility = Visibility.Collapsed;
        BigAnnotationList.Items.Clear();
        BigAnnotationReferenceGroups.Children.Clear();
        BigAnnotationCount.Text = "0 条大批注";
        BigAnnotationReferenceCount.Text = "";
        BigAnnotationEditorStatus.Text = "";
        BigAnnotationEmptyState.Text = "";
        BigAnnotationEmptyState.Visibility = Visibility.Collapsed;
    }

    private void ResetBigAnnotationEditorState()
    {
        resumeBigAnnotationEditorPending = false;
        editingBigAnnotationId = null;
        editingBigAnnotationChapters = Array.Empty<NovelChapter>();
        initialBigAnnotationTitle = "";
        initialBigAnnotationContent = "";
        initialBigAnnotationCategoryId = null;
        initialBigAnnotationChapterIds = Array.Empty<Guid>();
        selectedBigAnnotationSmallReferenceCategoryId = null;
        selectedBigAnnotationChapterReferenceCategoryId = null;
        BigAnnotationEditorView.Visibility = Visibility.Collapsed;
        BigAnnotationListView.Visibility = Visibility.Visible;
        BigAnnotationReferenceGroups.Children.Clear();
    }

    private async Task LoadBigAnnotationListAsync(Guid? preferredId = null)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null)
            return;

        try
        {
            IReadOnlyList<BigAnnotationCategory> categories = await store.GetBigAnnotationCategoriesAsync();
            if (!ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
                return;

            bigAnnotationCategories = categories;
            refreshingBigAnnotationUi = true;
            try
            {
                BigAnnotationCategoryFilter.SelectedIndex = -1;
                BigAnnotationCategoryFilter.Items.Clear();
                BigAnnotationCategoryFilter.Items.Add(new ComboBoxItem { Content = "全部分类" });
                foreach (BigAnnotationCategory category in categories)
                {
                    BigAnnotationCategoryFilter.Items.Add(new ComboBoxItem
                    {
                        Content = category.Name,
                        Tag = category.Id,
                    });
                }

                int selectedIndex = selectedBigAnnotationCategoryId is Guid selectedId
                    ? categories.ToList().FindIndex(category => category.Id == selectedId) + 1
                    : 0;
                BigAnnotationCategoryFilter.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                selectedBigAnnotationCategoryId = GetSelectedComboGuid(BigAnnotationCategoryFilter);
            }
            finally
            {
                refreshingBigAnnotationUi = false;
            }

            await RefreshBigAnnotationListAsync(preferredId);
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Load big annotations", ex);
            BigAnnotationEmptyState.Text = $"加载大批注失败：{ex.Message}";
            BigAnnotationEmptyState.Visibility = Visibility.Visible;
        }
    }

    private async void BigAnnotationSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (refreshingBigAnnotationUi)
            return;
        await RefreshBigAnnotationListAsync();
    }

    private void BigAnnotationCategoryFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingBigAnnotationUi)
            return;
        selectedBigAnnotationCategoryId = GetSelectedComboGuid(BigAnnotationCategoryFilter);
        _ = RefreshBigAnnotationListAsync();
    }

    private async Task RefreshBigAnnotationListAsync(Guid? preferredId = null)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null)
            return;

        int version = ++bigAnnotationListRefreshVersion;
        Guid? selectedId = preferredId ?? GetSelectedBigAnnotationSummary()?.Annotation.Id;
        try
        {
            IReadOnlyList<BigAnnotationSummary> summaries =
                await store.GetBigAnnotationSummariesAsync(
                    selectedBigAnnotationCategoryId, BigAnnotationSearchBox.Text);
            if (version != bigAnnotationListRefreshVersion
                || !ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
                return;

            BigAnnotationList.Items.Clear();
            foreach (BigAnnotationSummary summary in summaries)
                BigAnnotationList.Items.Add(BuildBigAnnotationListItem(summary));

            BigAnnotationCount.Text = $"{summaries.Count} 条大批注";
            BigAnnotationEmptyState.Text = summaries.Count == 0
                ? "还没有大批注。点击“新建”并选择连续章节范围。"
                : "";
            BigAnnotationEmptyState.Visibility = summaries.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

            if (selectedId is Guid id)
            {
                foreach (object item in BigAnnotationList.Items)
                {
                    if (item is ListViewItem { Tag: BigAnnotationSummary summary }
                        && summary.Annotation.Id == id)
                    {
                        BigAnnotationList.SelectedItem = item;
                        break;
                    }
                }
            }
            UpdateBigAnnotationListButtons();
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Refresh big annotations", ex);
            BigAnnotationEmptyState.Text = $"刷新大批注失败：{ex.Message}";
            BigAnnotationEmptyState.Visibility = Visibility.Visible;
        }
    }

    private static ListViewItem BuildBigAnnotationListItem(BigAnnotationSummary summary)
    {
        string title = string.IsNullOrWhiteSpace(summary.Annotation.Title)
            ? "未命名大批注" : summary.Annotation.Title;
        string range = summary.ChapterCount == 0
            ? "没有关联章节"
            : $"{summary.FirstChapterTitle} — {summary.LastChapterTitle}（{summary.ChapterCount} 章）";
        string category = summary.Category?.Name ?? "未分类";
        string excerpt = summary.Annotation.Content.Length > 160
            ? summary.Annotation.Content[..160] + "…"
            : summary.Annotation.Content;

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{range} · {category}",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(excerpt))
        {
            panel.Children.Add(new TextBlock
            {
                Text = excerpt,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                Opacity = 0.8,
            });
        }

        return new ListViewItem
        {
            Content = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                Child = panel,
            },
            Tag = summary,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private void BigAnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateBigAnnotationListButtons();

    private BigAnnotationSummary? GetSelectedBigAnnotationSummary() =>
        BigAnnotationList.SelectedItem is ListViewItem { Tag: BigAnnotationSummary summary }
            ? summary : null;

    private void UpdateBigAnnotationListButtons()
    {
        bool selected = GetSelectedBigAnnotationSummary() is not null;
        OpenBigAnnotationButton.IsEnabled = selected;
        DeleteBigAnnotationButton.IsEnabled = selected;
    }

    private async void NewBigAnnotation_Click(object sender, RoutedEventArgs e)
    {
        await BeginNewBigAnnotationAsync();
    }

    private async Task BeginNewBigAnnotationAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null || !await EnsureChapterIndexAvailableForCreationAsync())
            return;

        IReadOnlyList<NovelChapter> chapters = await store.GetChaptersAsync();
        if (chapters.Count < 2)
        {
            await ShowNovelProjectMessageAsync(
                "无法创建大批注",
                "大批注必须关联至少两个连续章节。当前项目识别到的有效章节不足。");
            return;
        }

        IReadOnlyList<NovelChapter>? range =
            await ShowBigAnnotationChapterRangePickerAsync(chapters, null);
        if (range is null || !ReferenceEquals(store, activeAnnotationStore))
            return;

        await StartBigAnnotationEditorAsync(null, range);
    }

    private async void OpenSelectedBigAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedBigAnnotationSummary() is { } summary)
            await OpenBigAnnotationEditorAsync(summary.Annotation.Id);
    }

    private async Task OpenBigAnnotationEditorAsync(Guid annotationId)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return;

        BigAnnotation? annotation = await store.GetBigAnnotationAsync(annotationId);
        IReadOnlyList<NovelChapter> chapters = await store.GetBigAnnotationChaptersAsync(annotationId);
        if (!ReferenceEquals(store, activeAnnotationStore))
            return;
        if (annotation is null)
        {
            await ShowNovelProjectMessageAsync("大批注不存在", "这条大批注可能已被删除，请刷新列表。");
            return;
        }

        if (chapters.Count < 2)
        {
            await ShowNovelProjectMessageAsync("章节关联异常", "这条大批注没有完整的多章节关联，暂时无法打开编辑工作区。");
            return;
        }

        await StartBigAnnotationEditorAsync(annotation, chapters);
    }

    private async Task StartBigAnnotationEditorAsync(
        BigAnnotation? annotation, IReadOnlyList<NovelChapter> chapters)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return;

        IReadOnlyList<BigAnnotationCategory> bigCategories = await store.GetBigAnnotationCategoriesAsync();
        IReadOnlyList<AnnotationCategory> smallCategories = await store.GetCategoriesAsync();
        IReadOnlyList<ChapterAnnotationCategory> chapterCategories =
            await store.GetChapterAnnotationCategoriesAsync();
        if (!ReferenceEquals(store, activeAnnotationStore))
            return;

        bigAnnotationCategories = bigCategories;
        editingBigAnnotationId = annotation?.Id;
        editingBigAnnotationChapters = chapters.ToArray();
        initialBigAnnotationTitle = annotation?.Title ?? "";
        initialBigAnnotationContent = annotation?.Content ?? "";
        initialBigAnnotationCategoryId = annotation?.CategoryId;
        initialBigAnnotationChapterIds = chapters.Select(chapter => chapter.Id).ToArray();

        refreshingBigAnnotationUi = true;
        try
        {
            BigAnnotationEditorTitleBox.Text = annotation?.Title ?? "";
            BigAnnotationEditorContentBox.Text = annotation?.Content ?? "";
            PopulateCategoryCombo(BigAnnotationEditorCategoryFilter, bigCategories,
                "未分类", annotation?.CategoryId);
            PopulateCategoryCombo(BigAnnotationSmallCategoryFilter, smallCategories,
                "小批：全部分类", null);
            PopulateCategoryCombo(BigAnnotationChapterCategoryFilter, chapterCategories,
                "章批：全部分类", null);
            showBigAnnotationSmallReferences = true;
            showBigAnnotationChapterReferences = true;
            BigAnnotationSmallVisibilityToggle.IsOn = true;
            BigAnnotationChapterVisibilityToggle.IsOn = true;
            BigAnnotationSmallCategoryFilter.Visibility = Visibility.Visible;
            BigAnnotationChapterCategoryFilter.Visibility = Visibility.Visible;
            BigAnnotationReferenceSearchBox.Text = "";
        }
        finally
        {
            refreshingBigAnnotationUi = false;
        }

        selectedBigAnnotationSmallReferenceCategoryId = null;
        selectedBigAnnotationChapterReferenceCategoryId = null;
        BigAnnotationEditorHeader.Text = annotation is null ? "新建大批注" : "编辑大批注";
        BigAnnotationEditorChapterRange.Text = FormatBigAnnotationChapterRange(chapters);
        BigAnnotationEditorStatus.Text = "";
        BigAnnotationReferenceColumn.Width = new GridLength(bigAnnotationReferencePaneWidth);
        BigAnnotationListView.Visibility = Visibility.Collapsed;
        BigAnnotationEditorView.Visibility = Visibility.Visible;
        await RefreshBigAnnotationReferencesAsync();
    }

    private static void PopulateCategoryCombo<TCategory>(
        ComboBox combo, IReadOnlyList<TCategory> categories, string allLabel, Guid? selectedId)
        where TCategory : class
    {
        combo.SelectedIndex = -1;
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = allLabel });
        foreach (TCategory category in categories)
        {
            Guid categoryIdValue;
            string name;
            switch (category)
            {
                case BigAnnotationCategory big:
                    categoryIdValue = big.Id;
                    name = big.Name;
                    break;
                case AnnotationCategory small:
                    categoryIdValue = small.Id;
                    name = small.Name;
                    break;
                case ChapterAnnotationCategory chapter:
                    categoryIdValue = chapter.Id;
                    name = chapter.Name;
                    break;
                default:
                    continue;
            }
            combo.Items.Add(new ComboBoxItem { Content = name, Tag = categoryIdValue });
        }

        int selectedIndex = selectedId is Guid selectedCategoryId
            ? categories.ToList().FindIndex(category => category switch
            {
                BigAnnotationCategory big => big.Id == selectedCategoryId,
                AnnotationCategory small => small.Id == selectedCategoryId,
                ChapterAnnotationCategory chapter => chapter.Id == selectedCategoryId,
                _ => false,
            }) + 1
            : 0;
        combo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
    }

    private async Task<IReadOnlyList<NovelChapter>?> ShowBigAnnotationChapterRangePickerAsync(
        IReadOnlyList<NovelChapter> chapters, IReadOnlyList<NovelChapter>? initialRange)
    {
        List<NovelChapter> ordered = chapters.Where(chapter => chapter.IsActive)
            .OrderBy(chapter => chapter.StartOffset)
            .ThenBy(chapter => chapter.ChapterNumber)
            .ToList();
        if (ordered.Count < 2)
            return null;

        var startBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var endBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (NovelChapter chapter in ordered)
        {
            startBox.Items.Add(new ComboBoxItem { Content = chapter.Title, Tag = chapter.Id });
            endBox.Items.Add(new ComboBoxItem { Content = chapter.Title, Tag = chapter.Id });
        }

        int startIndex = initialRange is { Count: > 0 }
            ? ordered.FindIndex(chapter => chapter.Id == initialRange[0].Id)
            : 0;
        int endIndex = initialRange is { Count: > 1 }
            ? ordered.FindIndex(chapter => chapter.Id == initialRange[^1].Id)
            : 1;
        startBox.SelectedIndex = startIndex >= 0 ? startIndex : 0;
        endBox.SelectedIndex = endIndex > startBox.SelectedIndex ? endIndex : Math.Min(startBox.SelectedIndex + 1, ordered.Count - 1);

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = "选择大批注覆盖的起始章节和结束章节。系统会自动包含两者之间的全部章节，不能跳过中间章节。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = "起始章节" });
        panel.Children.Add(startBox);
        panel.Children.Add(new TextBlock { Text = "结束章节" });
        panel.Children.Add(endBox);

        var dialog = new ContentDialog
        {
            Title = "选择连续章节范围",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        Guid? startId = GetSelectedComboGuid(startBox);
        Guid? endId = GetSelectedComboGuid(endBox);
        int selectedStart = startId is Guid first
            ? ordered.FindIndex(chapter => chapter.Id == first) : -1;
        int selectedEnd = endId is Guid last
            ? ordered.FindIndex(chapter => chapter.Id == last) : -1;
        if (selectedStart < 0 || selectedEnd <= selectedStart)
        {
            await ShowNovelProjectMessageAsync(
                "章节范围无效", "结束章节必须位于起始章节之后，且大批注至少覆盖两个章节。");
            return null;
        }

        return ordered.Skip(selectedStart).Take(selectedEnd - selectedStart + 1).ToArray();
    }

    private async void AdjustBigAnnotationRange_Click(object sender, RoutedEventArgs e)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return;
        IReadOnlyList<NovelChapter> chapters = await store.GetChaptersAsync();
        if (chapters.Count < 2)
        {
            await ShowNovelProjectMessageAsync("无法调整章节范围", "当前项目有效章节不足两个。");
            return;
        }

        IReadOnlyList<NovelChapter>? range =
            await ShowBigAnnotationChapterRangePickerAsync(chapters, editingBigAnnotationChapters);
        if (range is null || !ReferenceEquals(store, activeAnnotationStore))
            return;

        editingBigAnnotationChapters = range;
        BigAnnotationEditorChapterRange.Text = FormatBigAnnotationChapterRange(range);
        BigAnnotationEditorStatus.Text = "章节范围已修改，保存后生效。";
        await RefreshBigAnnotationReferencesAsync();
    }

    private static string FormatBigAnnotationChapterRange(IReadOnlyList<NovelChapter> chapters) =>
        chapters.Count == 0
            ? "未关联章节"
            : $"{chapters[0].Title} — {chapters[^1].Title}（共 {chapters.Count} 章）";

    private async void SaveBigAnnotation_Click(object sender, RoutedEventArgs e)
    {
        await SaveBigAnnotationAsync();
    }

    private bool IsBigAnnotationEditorDirty()
    {
        if (BigAnnotationEditorView.Visibility != Visibility.Visible)
            return false;

        string title = BigAnnotationEditorTitleBox.Text ?? "";
        string body = BigAnnotationEditorContentBox.Text ?? "";
        Guid? categoryId = GetSelectedComboGuid(BigAnnotationEditorCategoryFilter);
        Guid[] chapterIds = editingBigAnnotationChapters.Select(chapter => chapter.Id).ToArray();
        return !string.Equals(title, initialBigAnnotationTitle, StringComparison.Ordinal)
            || !string.Equals(body, initialBigAnnotationContent, StringComparison.Ordinal)
            || categoryId != initialBigAnnotationCategoryId
            || !chapterIds.SequenceEqual(initialBigAnnotationChapterIds);
    }

    private async Task<bool> SaveBigAnnotationAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return false;

        string title = BigAnnotationEditorTitleBox.Text ?? "";
        string body = BigAnnotationEditorContentBox.Text ?? "";
        Guid? categoryId = GetSelectedComboGuid(BigAnnotationEditorCategoryFilter);
        Guid[] chapterIds = editingBigAnnotationChapters.Select(chapter => chapter.Id).ToArray();

        if (editingBigAnnotationId is null
            && string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(body)
            && categoryId is null)
        {
            return true;
        }

        if (chapterIds.Length < 2)
        {
            await ShowNovelProjectMessageAsync("章节范围无效", "大批注必须关联至少两个连续章节。");
            return false;
        }

        try
        {
            if (editingBigAnnotationId is Guid id)
            {
                IReadOnlyList<Guid>? changedRange =
                    chapterIds.SequenceEqual(initialBigAnnotationChapterIds) ? null : chapterIds;
                if (!await store.UpdateBigAnnotationAsync(id, title, body, categoryId, changedRange))
                {
                    await ShowNovelProjectMessageAsync("保存失败", "这条大批注可能已被删除，请返回列表刷新。");
                    return false;
                }
            }
            else
            {
                editingBigAnnotationId = await store.CreateBigAnnotationAsync(
                    title, body, categoryId, chapterIds);
            }

            if (!ReferenceEquals(store, activeAnnotationStore))
                return false;

            initialBigAnnotationTitle = title;
            initialBigAnnotationContent = body;
            initialBigAnnotationCategoryId = categoryId;
            initialBigAnnotationChapterIds = chapterIds;
            BigAnnotationEditorHeader.Text = "编辑大批注";
            BigAnnotationEditorStatus.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
            await RefreshBigAnnotationListAsync(editingBigAnnotationId);
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Save big annotation", ex);
            await ShowNovelProjectMessageAsync("保存大批注失败", ex.Message);
            return false;
        }
    }

    private async Task<bool> TryLeaveBigAnnotationEditorAsync()
    {
        if (!IsBigAnnotationEditorDirty())
            return true;

        var dialog = new ContentDialog
        {
            Title = "大批注尚有未保存的修改",
            Content = "是否保存当前标题、分类、章节范围和正文？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "放弃修改",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => await SaveBigAnnotationAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async void BackToBigAnnotationList_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryLeaveBigAnnotationEditorAsync())
            return;

        Guid? preferredId = editingBigAnnotationId;
        ResetBigAnnotationEditorState();
        await LoadBigAnnotationListAsync(preferredId);
    }

    private async void DeleteSelectedBigAnnotation_Click(object sender, RoutedEventArgs e)
    {
        BigAnnotationSummary? summary = GetSelectedBigAnnotationSummary();
        NovelAnnotationStore? store = activeAnnotationStore;
        if (summary is null || store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "删除大批注",
            Content = $"确定删除“{(string.IsNullOrWhiteSpace(summary.Annotation.Title) ? "未命名大批注" : summary.Annotation.Title)}”吗？关联章节记录也会一并删除，但不会影响小批注和章节批注。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !ReferenceEquals(store, activeAnnotationStore))
            return;

        try
        {
            await store.DeleteBigAnnotationAsync(summary.Annotation.Id);
            await RefreshBigAnnotationListAsync();
        }
        catch (Exception ex)
        {
            await ShowNovelProjectMessageAsync("删除大批注失败", ex.Message);
        }
    }

    private void BigAnnotationSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double maximum = Math.Max(300, BigAnnotationEditorColumns.ActualWidth - 420);
        bigAnnotationReferencePaneWidth = Math.Clamp(
            BigAnnotationReferenceColumn.ActualWidth + e.HorizontalChange, 280, maximum);
        BigAnnotationReferenceColumn.Width = new GridLength(bigAnnotationReferencePaneWidth);
    }

    private void BigAnnotationReferenceSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!refreshingBigAnnotationUi)
            ScheduleBigAnnotationReferenceRefresh();
    }

    private void BigAnnotationSmallVisibility_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshingBigAnnotationUi)
            return;

        showBigAnnotationSmallReferences = sender is ToggleSwitch toggle && toggle.IsOn;
        BigAnnotationSmallCategoryFilter.Visibility = showBigAnnotationSmallReferences
            ? Visibility.Visible : Visibility.Collapsed;
        ScheduleBigAnnotationReferenceRefresh();
    }

    private void BigAnnotationChapterVisibility_Toggled(object sender, RoutedEventArgs e)
    {
        if (refreshingBigAnnotationUi)
            return;

        showBigAnnotationChapterReferences = sender is ToggleSwitch toggle && toggle.IsOn;
        BigAnnotationChapterCategoryFilter.Visibility = showBigAnnotationChapterReferences
            ? Visibility.Visible : Visibility.Collapsed;
        ScheduleBigAnnotationReferenceRefresh();
    }

    private void BigAnnotationSmallCategoryFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingBigAnnotationUi)
            return;
        selectedBigAnnotationSmallReferenceCategoryId = GetSelectedComboGuid(BigAnnotationSmallCategoryFilter);
        ScheduleBigAnnotationReferenceRefresh();
    }

    private void BigAnnotationChapterCategoryFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingBigAnnotationUi)
            return;
        selectedBigAnnotationChapterReferenceCategoryId = GetSelectedComboGuid(BigAnnotationChapterCategoryFilter);
        ScheduleBigAnnotationReferenceRefresh();
    }

    private void ScheduleBigAnnotationReferenceRefresh()
    {
        bigAnnotationReferenceRefreshCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        bigAnnotationReferenceRefreshCancellation = cancellation;
        _ = RefreshBigAnnotationReferencesAfterDelayAsync(cancellation);
    }

    private async Task RefreshBigAnnotationReferencesAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            await RefreshBigAnnotationReferencesAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer search or category selection superseded this request.
        }
        finally
        {
            if (ReferenceEquals(bigAnnotationReferenceRefreshCancellation, cancellation))
                bigAnnotationReferenceRefreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RefreshBigAnnotationReferencesAsync(CancellationToken cancellationToken = default)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null || editingBigAnnotationChapters.Count == 0)
            return;

        int version = ++bigAnnotationReferenceRefreshVersion;
        Guid[] chapterIds = editingBigAnnotationChapters.Select(chapter => chapter.Id).ToArray();
        Guid? smallCategory = showBigAnnotationSmallReferences
            ? selectedBigAnnotationSmallReferenceCategoryId : null;
        Guid? chapterCategory = showBigAnnotationChapterReferences
            ? selectedBigAnnotationChapterReferenceCategoryId : null;
        bool includeSmall = showBigAnnotationSmallReferences;
        bool includeChapter = showBigAnnotationChapterReferences;
        string search = BigAnnotationReferenceSearchBox.Text ?? "";

        try
        {
            Task<IReadOnlyList<BigAnnotationSmallReference>> smallTask = includeSmall
                ? store.GetBigAnnotationSmallReferencesAsync(chapterIds, smallCategory, search, cancellationToken)
                : Task.FromResult<IReadOnlyList<BigAnnotationSmallReference>>(Array.Empty<BigAnnotationSmallReference>());
            Task<IReadOnlyList<ChapterAnnotationSummary>> chapterTask = includeChapter
                ? store.GetBigAnnotationChapterReferencesAsync(chapterIds, chapterCategory, search, cancellationToken)
                : Task.FromResult<IReadOnlyList<ChapterAnnotationSummary>>(Array.Empty<ChapterAnnotationSummary>());
            await Task.WhenAll(smallTask, chapterTask);

            if (cancellationToken.IsCancellationRequested
                || version != bigAnnotationReferenceRefreshVersion
                || !ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
                return;

            BuildBigAnnotationReferenceGroups(smallTask.Result, chapterTask.Result);
        }
        catch (OperationCanceledException)
        {
            // A newer filter request superseded this one.
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Load big annotation references", ex);
            BigAnnotationReferenceGroups.Children.Clear();
            BigAnnotationReferenceGroups.Children.Add(new TextBlock
            {
                Text = $"加载参考资料失败：{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void BuildBigAnnotationReferenceGroups(
        IReadOnlyList<BigAnnotationSmallReference> smallReferences,
        IReadOnlyList<ChapterAnnotationSummary> chapterReferences)
    {
        BigAnnotationReferenceGroups.Children.Clear();

        // A small annotation is rendered only once, under the chapter containing its first
        // selected anchor. Its expanded details still list every selected-chapter location.
        var smallByChapter = smallReferences
            .Where(reference => reference.Anchors.Count > 0)
            .GroupBy(reference => reference.Anchors[0].Chapter.Id)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var chapterByChapter = chapterReferences
            .GroupBy(reference => reference.Chapter.Id)
            .ToDictionary(group => group.Key, group => group.ToArray());

        BigAnnotationReferenceCount.Text =
            $"小批 {smallReferences.Count} / 章批 {chapterReferences.Count}";

        int visibleChapterCount = 0;
        foreach (NovelChapter chapter in editingBigAnnotationChapters)
        {
            smallByChapter.TryGetValue(chapter.Id, out BigAnnotationSmallReference[]? smallItems);
            chapterByChapter.TryGetValue(chapter.Id, out ChapterAnnotationSummary[]? chapterItems);
            smallItems ??= Array.Empty<BigAnnotationSmallReference>();
            chapterItems ??= Array.Empty<ChapterAnnotationSummary>();

            // Do not render empty chapters after the current type/category/search filters.
            if (smallItems.Length == 0 && chapterItems.Length == 0)
                continue;

            visibleChapterCount++;
            var details = new StackPanel { Spacing = 3, Visibility = Visibility.Visible };
            foreach (BigAnnotationSmallReference reference in smallItems)
                details.Children.Add(BuildBigAnnotationSmallReferenceCard(reference));
            foreach (ChapterAnnotationSummary reference in chapterItems)
                details.Children.Add(BuildBigAnnotationChapterReferenceCard(reference));

            var chapterArrow = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
            var chapterHeaderContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chapterHeaderContent.Children.Add(chapterArrow);
            chapterHeaderContent.Children.Add(new TextBlock
            {
                Text = chapter.Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var chapterToggle = new Button
            {
                Content = chapterHeaderContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(2, 4, 2, 4),
                MinHeight = 28,
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            var branch = new Border
            {
                Margin = new Thickness(11, 0, 0, 2),
                Padding = new Thickness(10, 2, 0, 2),
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
                    { Opacity = 0.4 },
                Child = details,
            };
            chapterToggle.Click += (_, _) =>
            {
                bool expand = details.Visibility != Visibility.Visible;
                details.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
                branch.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
                chapterArrow.Glyph = expand ? "\uE70D" : "\uE76C";
            };

            var headerRow = new Grid { ColumnSpacing = 6 };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.Children.Add(chapterToggle);

            var chapterCount = new TextBlock
            {
                Text = $"小批 {smallItems.Length} / 章批 {chapterItems.Length}",
                Opacity = 0.65,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(chapterCount, 1);
            headerRow.Children.Add(chapterCount);

            // Chapter navigation is useful when chapter annotations exist; small-annotation
            // excerpts below are themselves clickable navigation targets.
            if (chapterItems.Length > 0)
            {
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var jumpChapterButton = new Button
                {
                    Content = "跳转",
                    Padding = new Thickness(3, 1, 3, 1),
                    MinHeight = 24,
                    VerticalAlignment = VerticalAlignment.Center,
                    BorderThickness = new Thickness(0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Opacity = 0.6,
                    FontSize = 12,
                };
                jumpChapterButton.Click += async (_, _) =>
                    await NavigateFromBigAnnotationChapterReferenceAsync(chapter);
                Grid.SetColumn(jumpChapterButton, 2);
                headerRow.Children.Add(jumpChapterButton);
            }

            var group = new StackPanel { Spacing = 2 };
            group.Children.Add(headerRow);
            group.Children.Add(branch);
            BigAnnotationReferenceGroups.Children.Add(group);
        }

        if (visibleChapterCount == 0)
        {
            BigAnnotationReferenceGroups.Children.Add(new TextBlock
            {
                Text = "当前筛选条件下没有参考批注。",
                Opacity = 0.65,
                Margin = new Thickness(8, 12, 8, 8),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private Border BuildBigAnnotationSmallReferenceCard(BigAnnotationSmallReference reference)
    {
        string? title = string.IsNullOrWhiteSpace(reference.Annotation.Title)
            ? null : reference.Annotation.Title.Trim();
        string category = reference.Category?.Name ?? "未分类";
        var details = new StackPanel { Spacing = 5, Visibility = Visibility.Visible };
        details.Children.Add(new TextBlock
        {
            Text = reference.Annotation.Content,
            TextWrapping = TextWrapping.Wrap,
        });

        // Categories are metadata, not tree nodes: keep the label beneath the note and above its sources.
        details.Children.Add(BuildBigAnnotationCategoryBadge(category));

        for (int index = 0; index < reference.Anchors.Count; index++)
        {
            BigAnnotationSmallAnchorReference anchorReference = reference.Anchors[index];
            details.Children.Add(new TextBlock
            {
                Text = $"原文{index + 1} · {anchorReference.Chapter.Title}",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // The quoted source text itself is the navigation target; there is no extra "定位" button.
            var quoteButton = new Button
            {
                Content = new TextBlock
                {
                    Text = anchorReference.Anchor.Quote,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 24,
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            AnnotationAnchor capturedAnchor = anchorReference.Anchor;
            quoteButton.Click += async (_, _) =>
                await NavigateFromBigAnnotationAnchorAsync(capturedAnchor);
            details.Children.Add(quoteButton);
        }

        var arrow = new FontIcon { Glyph = "\uE70D", FontSize = 11 };
        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerContent.Children.Add(arrow);
        if (title is not null)
        {
            headerContent.Children.Add(new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        headerContent.Children.Add(new TextBlock
        {
            Text = $"{reference.Anchors.Count} 处原文",
            Opacity = 0.65,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var header = new Button
        {
            Content = headerContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(2, 3, 2, 3),
            MinHeight = 26,
            BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        header.Click += (_, _) =>
        {
            bool expand = details.Visibility != Visibility.Visible;
            details.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            arrow.Glyph = expand ? "\uE70D" : "\uE76C";
        };

        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(12, 1, 0, 3) };
        panel.Children.Add(header);
        panel.Children.Add(details);
        return new Border { Child = panel };
    }

    private static Border BuildBigAnnotationCategoryBadge(string category)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = category,
                FontSize = 11,
                Opacity = 0.8,
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 1, 0, 3),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
                { Opacity = 0.25 },
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
                { Opacity = 0.08 },
        };
    }

    private Border BuildBigAnnotationChapterReferenceCard(ChapterAnnotationSummary reference)
    {
        string? title = string.IsNullOrWhiteSpace(reference.Annotation.Title)
            ? null : reference.Annotation.Title.Trim();
        string category = reference.Category?.Name ?? "未分类";
        var details = new StackPanel { Spacing = 5, Visibility = Visibility.Visible };
        details.Children.Add(new TextBlock
        {
            Text = reference.Annotation.Content,
            TextWrapping = TextWrapping.Wrap,
        });
        details.Children.Add(BuildBigAnnotationCategoryBadge(category));

        var arrow = new FontIcon { Glyph = "\uE70D", FontSize = 11 };
        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerContent.Children.Add(arrow);
        if (title is not null)
        {
            headerContent.Children.Add(new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        else
        {
            headerContent.Children.Add(new TextBlock
            {
                Text = "章节批注",
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var header = new Button
        {
            Content = headerContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(2, 3, 2, 3),
            MinHeight = 26,
            BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        header.Click += (_, _) =>
        {
            bool expand = details.Visibility != Visibility.Visible;
            details.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            arrow.Glyph = expand ? "\uE70D" : "\uE76C";
        };

        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(12, 1, 0, 3) };
        panel.Children.Add(header);
        panel.Children.Add(details);
        return new Border { Child = panel };
    }

    private async Task NavigateFromBigAnnotationAnchorAsync(AnnotationAnchor anchor)
    {
        if (IsBigAnnotationEditorDirty() && !await SaveBigAnnotationAsync())
            return;

        resumeBigAnnotationId = editingBigAnnotationId;
        resumeBigAnnotationEditorPending = true;
        HideBigAnnotationWorkspace();
        ReturnToBigAnnotationButton.Visibility = Visibility.Visible;
        await NavigateToSmallAnnotationAnchorAsync(anchor);
    }

    private async Task NavigateFromBigAnnotationChapterReferenceAsync(NovelChapter chapter)
    {
        if (!chapter.IsActive)
        {
            await ShowNovelProjectMessageAsync(
                "原章节已删除",
                "这条章节批注仍然保留，但所属章节已经归档。为避免跳转到错误位置，系统不会自动定位。");
            return;
        }

        if (IsBigAnnotationEditorDirty() && !await SaveBigAnnotationAsync())
            return;

        resumeBigAnnotationId = editingBigAnnotationId;
        resumeBigAnnotationEditorPending = true;
        HideBigAnnotationWorkspace();
        ReturnToBigAnnotationButton.Visibility = Visibility.Visible;
        if (filteredSource is not null)
            ExitFilterToFullView();
        await NavigateToChapterAsync(chapter);
        View.FocusCanvas();
    }

    private static Guid? GetSelectedComboGuid(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag is Guid id ? id : null;

    private async void ManageBigAnnotationCategories_Click(object sender, RoutedEventArgs e)
    {
        await ShowBigAnnotationCategoryManagerAsync();
    }

    private async Task ShowBigAnnotationCategoryManagerAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return;

        var categoryList = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 170 };
        var nameBox = new TextBox { PlaceholderText = "输入分类名称" };
        var targetBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "选择合并目标分类",
        };
        var confirmDelete = new CheckBox { Content = "确认删除所选分类" };
        var confirmMerge = new CheckBox { Content = "确认将所选分类合并到目标分类" };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var addButton = new Button { Content = "新建" };
        var renameButton = new Button { Content = "重命名", IsEnabled = false };
        var deleteButton = new Button { Content = "删除", IsEnabled = false };
        var mergeButton = new Button { Content = "合并", IsEnabled = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(addButton);
        actions.Children.Add(renameButton);
        actions.Children.Add(deleteButton);
        actions.Children.Add(mergeButton);

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(categoryList);
        panel.Children.Add(nameBox);
        panel.Children.Add(actions);
        panel.Children.Add(targetBox);
        panel.Children.Add(confirmDelete);
        panel.Children.Add(confirmMerge);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "大批注分类管理",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot,
        };

        BigAnnotationCategory? GetSelectedCategory() =>
            categoryList.SelectedItem is ListViewItem { Tag: BigAnnotationCategory category }
                ? category : null;
        Guid? GetTargetCategoryId() => GetSelectedComboGuid(targetBox);

        void UpdateActions()
        {
            BigAnnotationCategory? selected = GetSelectedCategory();
            renameButton.IsEnabled = selected is not null && !string.IsNullOrWhiteSpace(nameBox.Text);
            deleteButton.IsEnabled = selected is not null && confirmDelete.IsChecked == true;
            mergeButton.IsEnabled = selected is not null
                && GetTargetCategoryId() is Guid target
                && target != selected.Id
                && confirmMerge.IsChecked == true;
        }

        async Task ReloadCategoriesAsync(Guid? preferredId = null)
        {
            IReadOnlyList<BigAnnotationCategory> categories = await store.GetBigAnnotationCategoriesAsync();
            if (!ReferenceEquals(store, activeAnnotationStore))
                return;

            categoryList.Items.Clear();
            targetBox.Items.Clear();
            foreach (BigAnnotationCategory category in categories)
            {
                categoryList.Items.Add(new ListViewItem { Content = category.Name, Tag = category });
                targetBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
            }

            if (preferredId is Guid id)
            {
                foreach (object item in categoryList.Items)
                {
                    if (item is ListViewItem { Tag: BigAnnotationCategory category } && category.Id == id)
                    {
                        categoryList.SelectedItem = item;
                        break;
                    }
                }
            }
            targetBox.SelectedIndex = targetBox.Items.Count > 1 ? 1 : targetBox.Items.Count == 1 ? 0 : -1;
            UpdateActions();
        }

        categoryList.SelectionChanged += (_, _) =>
        {
            BigAnnotationCategory? selected = GetSelectedCategory();
            nameBox.Text = selected?.Name ?? "";
            confirmDelete.IsChecked = false;
            confirmMerge.IsChecked = false;
            UpdateActions();
        };
        nameBox.TextChanged += (_, _) => UpdateActions();
        targetBox.SelectionChanged += (_, _) => UpdateActions();
        confirmDelete.Checked += (_, _) => UpdateActions();
        confirmDelete.Unchecked += (_, _) => UpdateActions();
        confirmMerge.Checked += (_, _) => UpdateActions();
        confirmMerge.Unchecked += (_, _) => UpdateActions();

        addButton.Click += async (_, _) =>
        {
            try
            {
                await store.CreateBigAnnotationCategoryAsync(nameBox.Text);
                nameBox.Text = "";
                await ReloadCategoriesAsync();
                errorText.Text = "";
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };
        renameButton.Click += async (_, _) =>
        {
            if (GetSelectedCategory() is not BigAnnotationCategory selected)
                return;
            try
            {
                await store.UpdateBigAnnotationCategoryAsync(selected.Id, nameBox.Text);
                await ReloadCategoriesAsync(selected.Id);
                errorText.Text = "";
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };
        deleteButton.Click += async (_, _) =>
        {
            if (GetSelectedCategory() is not BigAnnotationCategory selected || confirmDelete.IsChecked != true)
                return;
            try
            {
                await store.DeleteBigAnnotationCategoryAsync(selected.Id);
                nameBox.Text = "";
                await ReloadCategoriesAsync();
                errorText.Text = "";
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };
        mergeButton.Click += async (_, _) =>
        {
            if (GetSelectedCategory() is not BigAnnotationCategory selected
                || GetTargetCategoryId() is not Guid target
                || confirmMerge.IsChecked != true)
                return;
            try
            {
                await store.MergeBigAnnotationCategoriesAsync(selected.Id, target);
                await ReloadCategoriesAsync(target);
                errorText.Text = "";
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };

        await ReloadCategoriesAsync();
        await dialog.ShowAsync();
        if (ReferenceEquals(store, activeAnnotationStore))
        {
            selectedBigAnnotationCategoryId = null;
            await LoadBigAnnotationListAsync();
        }
    }
}
