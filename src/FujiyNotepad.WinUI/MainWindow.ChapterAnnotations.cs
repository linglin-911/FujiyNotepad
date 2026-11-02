using FujiyNotepad.Core;
using FujiyNotepad.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FujiyNotepad.WinUI;

public sealed partial class MainWindow
{
    private int chapterIndexBuildVersion;
    private int chapterIndexBuildRunningVersion = -1;
    private CancellationTokenSource? chapterIndexBuildCancellation;
    private int chapterAnnotationRefreshVersion;
    private bool refreshingChapterAnnotationUi;
    private Guid? selectedChapterAnnotationChapterId;
    private Guid? selectedChapterAnnotationCategoryId;
    private IReadOnlyList<NovelChapter> chapterAnnotationChapters = Array.Empty<NovelChapter>();

    private sealed record ChapterAnnotationEditValues(string? Title, string Content, Guid? CategoryId);

    private async void ChapterAnnotations_Click(object sender, RoutedEventArgs e)
    {
        if (activeAnnotationStore is null || activeNovelProject is null)
        {
            ChapterAnnotationsItem.IsChecked = false;
            ChapterAnnotationPane.Visibility = Visibility.Collapsed;
            return;
        }

        bool show = ChapterAnnotationsItem.IsChecked;
        ChapterAnnotationPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        SmallAnnotationsItem.IsChecked = false;
        SmallAnnotationPane.Visibility = Visibility.Collapsed;
        await LoadChapterAnnotationsAsync();
        if (LineIndexer.IsCompleted)
            _ = EnsureChapterIndexAsync();
    }

    private void CloseChapterAnnotations_Click(object sender, RoutedEventArgs e)
    {
        ChapterAnnotationsItem.IsChecked = false;
        ChapterAnnotationPane.Visibility = Visibility.Collapsed;
    }

    private async void ChapterAnnotationSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        await RefreshChapterAnnotationsAsync();
    }

    private async void ChapterAnnotationChapterFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingChapterAnnotationUi)
            return;

        selectedChapterAnnotationChapterId =
            ChapterAnnotationChapterFilter.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
        await RefreshChapterAnnotationsAsync();
    }

    private async void ChapterAnnotationCategoryFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingChapterAnnotationUi)
            return;

        selectedChapterAnnotationCategoryId =
            ChapterAnnotationCategoryFilter.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
        await RefreshChapterAnnotationsAsync();
    }

    private void ChapterAnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateChapterAnnotationActionButtons();
    }

    private void UpdateChapterAnnotationActionButtons()
    {
        bool hasSelection = GetSelectedChapterAnnotationSummary() is not null;
        EditChapterAnnotationButton.IsEnabled = hasSelection;
        DeleteChapterAnnotationButton.IsEnabled = hasSelection;
        NewChapterAnnotationButton.IsEnabled = chapterAnnotationChapters.Count > 0;
    }

    private ChapterAnnotationSummary? GetSelectedChapterAnnotationSummary()
    {
        return ChapterAnnotationList.SelectedItem is ListViewItem { Tag: ChapterAnnotationSummary summary }
            ? summary
            : null;
    }

    private async Task LoadChapterAnnotationsAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null)
            return;

        try
        {
            IReadOnlyList<NovelChapter> chapters = await store.GetChaptersAsync();
            IReadOnlyList<ChapterAnnotationCategory> categories =
                await store.GetChapterAnnotationCategoriesAsync();
            if (!ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
                return;

            chapterAnnotationChapters = chapters;
            refreshingChapterAnnotationUi = true;
            try
            {
                ChapterAnnotationChapterFilter.Items.Clear();
                ChapterAnnotationChapterFilter.Items.Add(new ComboBoxItem { Content = "全部章节" });
                foreach (NovelChapter chapter in chapters)
                {
                    ChapterAnnotationChapterFilter.Items.Add(new ComboBoxItem
                    {
                        Content = $"{chapter.ChapterNumber}. {chapter.Title}",
                        Tag = chapter.Id,
                    });
                }

                int selectedChapterIndex = 0;
                if (selectedChapterAnnotationChapterId is Guid selectedChapter)
                {
                    for (int index = 1; index < ChapterAnnotationChapterFilter.Items.Count; index++)
                    {
                        if (ChapterAnnotationChapterFilter.Items[index] is ComboBoxItem { Tag: Guid id }
                            && id == selectedChapter)
                        {
                            selectedChapterIndex = index;
                            break;
                        }
                    }
                }
                ChapterAnnotationChapterFilter.SelectedIndex = selectedChapterIndex;
                selectedChapterAnnotationChapterId =
                    ChapterAnnotationChapterFilter.SelectedItem is ComboBoxItem { Tag: Guid selectedId }
                        ? selectedId
                        : null;

                ChapterAnnotationCategoryFilter.Items.Clear();
                ChapterAnnotationCategoryFilter.Items.Add(new ComboBoxItem { Content = "全部分类" });
                foreach (ChapterAnnotationCategory category in categories)
                {
                    ChapterAnnotationCategoryFilter.Items.Add(new ComboBoxItem
                    {
                        Content = category.Name,
                        Tag = category.Id,
                    });
                }

                int selectedCategoryIndex = 0;
                if (selectedChapterAnnotationCategoryId is Guid selectedCategory)
                {
                    for (int index = 1; index < ChapterAnnotationCategoryFilter.Items.Count; index++)
                    {
                        if (ChapterAnnotationCategoryFilter.Items[index] is ComboBoxItem { Tag: Guid id }
                            && id == selectedCategory)
                        {
                            selectedCategoryIndex = index;
                            break;
                        }
                    }
                }
                ChapterAnnotationCategoryFilter.SelectedIndex = selectedCategoryIndex;
                selectedChapterAnnotationCategoryId =
                    ChapterAnnotationCategoryFilter.SelectedItem is ComboBoxItem { Tag: Guid selectedId }
                        ? selectedId
                        : null;
            }
            finally
            {
                refreshingChapterAnnotationUi = false;
            }

            UpdateChapterAnnotationActionButtons();
            await RefreshChapterAnnotationsAsync();
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Load chapter annotations", ex);
            ChapterAnnotationEmptyState.Text = $"加载章节批注失败：{ex.Message}";
            ChapterAnnotationEmptyState.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshChapterAnnotationsAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null)
            return;

        int requestVersion = ++chapterAnnotationRefreshVersion;
        Guid? selectedId = GetSelectedChapterAnnotationSummary()?.Annotation.Id;
        Guid? chapterId = selectedChapterAnnotationChapterId;
        Guid? categoryId = selectedChapterAnnotationCategoryId;
        string searchText = ChapterAnnotationSearchBox.Text;

        try
        {
            IReadOnlyList<ChapterAnnotationSummary> summaries =
                await store.GetChapterAnnotationSummariesAsync(chapterId, categoryId, searchText);
            if (requestVersion != chapterAnnotationRefreshVersion
                || !ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
                return;

            ChapterAnnotationList.Items.Clear();
            ListViewItem? selectedItem = null;
            foreach (ChapterAnnotationSummary summary in summaries)
            {
                var content = new StackPanel { Spacing = 4 };
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(summary.Annotation.Title)
                        ? "（未命名章节批注）"
                        : summary.Annotation.Title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"{summary.Chapter.ChapterNumber}. {summary.Chapter.Title}",
                    FontSize = 11,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(new TextBlock
                {
                    Text = CreateChapterAnnotationExcerpt(summary.Annotation.Content, 180),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 4,
                    Opacity = 0.88,
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"{summary.Category?.Name ?? "未分类"}  ·  {summary.Annotation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                    FontSize = 11,
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap,
                });

                var item = new ListViewItem
                {
                    Tag = summary,
                    Content = content,
                    Padding = new Thickness(8, 6, 8, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                item.Tapped += async (_, _) =>
                {
                    ChapterAnnotationList.SelectedItem = item;
                    UpdateChapterAnnotationActionButtons();
                    await NavigateToChapterAsync(summary.Chapter);
                };
                ChapterAnnotationList.Items.Add(item);
                if (selectedId == summary.Annotation.Id)
                    selectedItem = item;
            }

            if (selectedItem is not null)
                ChapterAnnotationList.SelectedItem = selectedItem;

            ChapterAnnotationCount.Text = $"{summaries.Count} 条批注";
            if (summaries.Count == 0)
            {
                if (chapterAnnotationChapters.Count == 0)
                {
                    NovelChapterIndexState? state = await store.GetChapterIndexStateAsync();
                    ChapterAnnotationEmptyState.Text = state is null && !LineIndexer.IsCompleted
                        ? "正在识别章节，请稍候..."
                        : "没有识别到章节标题，或当前筛选条件下没有批注。";
                }
                else
                {
                    ChapterAnnotationEmptyState.Text = "当前没有章节批注。可以点击下方“新建”添加。";
                }
                ChapterAnnotationEmptyState.Visibility = Visibility.Visible;
            }
            else
            {
                ChapterAnnotationEmptyState.Visibility = Visibility.Collapsed;
            }

            UpdateChapterAnnotationActionButtons();
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Refresh chapter annotations", ex);
            ChapterAnnotationEmptyState.Text = $"刷新章节批注失败：{ex.Message}";
            ChapterAnnotationEmptyState.Visibility = Visibility.Visible;
        }
    }

    private static string CreateChapterAnnotationExcerpt(string content, int maxLength)
    {
        string text = content.Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private async Task EnsureChapterIndexAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        LineProvider? lines = provider;
        if (store is null || project is null || lines is null
            || !LineIndexer.IsCompleted || filteredSource is not null)
            return;

        int generation = chapterIndexBuildVersion;
        if (chapterIndexBuildRunningVersion == generation)
            return;
        chapterIndexBuildRunningVersion = generation;
        CancellationTokenSource? buildCancellation = null;

        try
        {
            NovelChapterIndexState? state = await store.GetChapterIndexStateAsync();
            if (!IsCurrentChapterIndexRequest(generation, store, project, lines))
                return;

            if (state is { ParserVersion: NovelChapterIndexBuilder.CurrentParserVersion }
                && state.DocumentRevision == project.Metadata.DocumentRevision)
            {
                chapterAnnotationChapters = await store.GetChaptersAsync();
                if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                    await LoadChapterAnnotationsAsync();
                return;
            }

            IReadOnlyList<NovelChapter> existing = await store.GetChaptersAsync();
            if (existing.Count > 0)
            {
                // Never replace chapter IDs automatically: chapter annotations depend on them.
                chapterAnnotationChapters = existing;
                if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                    await LoadChapterAnnotationsAsync();
                return;
            }

            long revision = project.Metadata.DocumentRevision;
            buildCancellation = new CancellationTokenSource();
            chapterIndexBuildCancellation?.Cancel();
            chapterIndexBuildCancellation = buildCancellation;
            CancellationToken cancellationToken = buildCancellation.Token;
            IReadOnlyList<NovelChapter> chapters = await Task.Run(
                () => NovelChapterIndexBuilder.Build(lines, revision, cancellationToken),
                cancellationToken);
            if (!IsCurrentChapterIndexRequest(generation, store, project, lines))
                return;

            await store.SaveChapterIndexIfEmptyAsync(chapters, revision);
            if (!IsCurrentChapterIndexRequest(generation, store, project, lines))
                return;

            chapterAnnotationChapters = await store.GetChaptersAsync();
            LblStatus.Text = chapterAnnotationChapters.Count > 0
                ? $"章节索引完成：识别到 {chapterAnnotationChapters.Count:N0} 章"
                : "章节索引完成：未识别到符合格式的章节标题";

            if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                await LoadChapterAnnotationsAsync();
        }
        catch (OperationCanceledException)
        {
            // A project/file switch invalidated the scan.
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Build chapter index", ex);
            if (ReferenceEquals(store, activeAnnotationStore))
                LblStatus.Text = $"章节索引失败：{ex.Message}";
        }
        finally
        {
            if (chapterIndexBuildRunningVersion == generation)
                chapterIndexBuildRunningVersion = -1;
            if (buildCancellation is not null)
            {
                if (ReferenceEquals(chapterIndexBuildCancellation, buildCancellation))
                    chapterIndexBuildCancellation = null;
                buildCancellation.Dispose();
            }
        }
    }

    private void CancelChapterIndexBuild()
    {
        chapterIndexBuildVersion++;
        chapterIndexBuildCancellation?.Cancel();
        chapterIndexBuildCancellation = null;
        chapterIndexBuildRunningVersion = -1;
    }

    private bool IsCurrentChapterIndexRequest(
        int generation,
        NovelAnnotationStore store,
        NovelProject project,
        LineProvider lines)
    {
        return generation == chapterIndexBuildVersion
            && ReferenceEquals(store, activeAnnotationStore)
            && ReferenceEquals(project, activeNovelProject)
            && ReferenceEquals(lines, provider);
    }

    private async Task<NovelChapter?> GetCurrentOrSelectedChapterAsync()
    {
        if (selectedChapterAnnotationChapterId is Guid chapterId)
            return chapterAnnotationChapters.FirstOrDefault(chapter => chapter.Id == chapterId);

        if (activeAnnotationStore is not { } store
            || annotationOffsetMap is null
            || filteredSource is not null)
            return null;

        TextPosition caret = View.CaretPosition;
        long offset = annotationOffsetMap.GetOffset(caret.Line, caret.Column);
        return await store.GetChapterAtOffsetAsync(offset);
    }

    private async Task NavigateToChapterAsync(NovelChapter chapter)
    {
        if (annotationOffsetMap is null || provider is null || filteredSource is not null)
            return;

        try
        {
            (int line, _) = annotationOffsetMap.GetPosition(chapter.StartOffset);
            View.GoToLineColumn(line, 0);
            View.CenterCaretVertically();
            View.FocusCanvas();
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Navigate to chapter", ex);
            await ShowNovelProjectMessageAsync("无法跳转章节", ex.Message);
        }
    }

    private async void NewChapterAnnotation_Click(object sender, RoutedEventArgs e)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return;

        try
        {
            NovelChapter? chapter = await GetCurrentOrSelectedChapterAsync();
            if (chapter is null)
            {
                await ShowNovelProjectMessageAsync(
                    "尚未选择章节",
                    "请先在章节下拉框中选择一个章节，或将阅读位置移动到某个章节后再新建章节批注。");
                return;
            }

            ChapterAnnotationEditValues? values = await ShowChapterAnnotationEditorAsync(
                "新建章节批注", null, "", null);
            if (values is null || !ReferenceEquals(store, activeAnnotationStore))
                return;

            await store.CreateChapterAnnotationAsync(
                chapter.Id, values.Content, values.CategoryId, values.Title);
            selectedChapterAnnotationChapterId = chapter.Id;
            await LoadChapterAnnotationsAsync();
        }
        catch (Exception ex)
        {
            await ShowNovelProjectMessageAsync("创建章节批注失败", ex.Message);
        }
    }

    private async void EditSelectedChapterAnnotation_Click(object sender, RoutedEventArgs e)
    {
        ChapterAnnotationSummary? summary = GetSelectedChapterAnnotationSummary();
        NovelAnnotationStore? store = activeAnnotationStore;
        if (summary is null || store is null)
            return;

        try
        {
            ChapterAnnotationEditValues? values = await ShowChapterAnnotationEditorAsync(
                "编辑章节批注",
                summary.Annotation.Title,
                summary.Annotation.Content,
                summary.Annotation.CategoryId);
            if (values is null || !ReferenceEquals(store, activeAnnotationStore))
                return;

            await store.UpdateChapterAnnotationAsync(
                summary.Annotation.Id,
                summary.Annotation.ChapterId,
                values.Content,
                values.CategoryId,
                values.Title);
            await LoadChapterAnnotationsAsync();
        }
        catch (Exception ex)
        {
            await ShowNovelProjectMessageAsync("保存章节批注失败", ex.Message);
        }
    }

    private async void DeleteSelectedChapterAnnotation_Click(object sender, RoutedEventArgs e)
    {
        ChapterAnnotationSummary? summary = GetSelectedChapterAnnotationSummary();
        NovelAnnotationStore? store = activeAnnotationStore;
        if (summary is null || store is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "删除章节批注",
            Content = $"确定删除“{(string.IsNullOrWhiteSpace(summary.Annotation.Title) ? "未命名章节批注" : summary.Annotation.Title)}”吗？",
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
            await store.DeleteChapterAnnotationAsync(summary.Annotation.Id);
            await RefreshChapterAnnotationsAsync();
        }
        catch (Exception ex)
        {
            await ShowNovelProjectMessageAsync("删除章节批注失败", ex.Message);
        }
    }

    private async Task<ChapterAnnotationEditValues?> ShowChapterAnnotationEditorAsync(
        string dialogTitle,
        string? initialTitle,
        string initialContent,
        Guid? initialCategoryId)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return null;

        IReadOnlyList<ChapterAnnotationCategory> categories =
            await store.GetChapterAnnotationCategoriesAsync();
        if (!ReferenceEquals(store, activeAnnotationStore))
            return null;

        var titleBox = new TextBox
        {
            PlaceholderText = "批注标题（可选）",
            Text = initialTitle ?? "",
        };
        var categoryBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "选择分类",
        };
        categoryBox.Items.Add(new ComboBoxItem { Content = "未分类" });
        foreach (ChapterAnnotationCategory category in categories)
            categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });

        categoryBox.SelectedIndex = 0;
        if (initialCategoryId is Guid selectedCategory)
        {
            for (int index = 1; index < categoryBox.Items.Count; index++)
            {
                if (categoryBox.Items[index] is ComboBoxItem { Tag: Guid id } && id == selectedCategory)
                {
                    categoryBox.SelectedIndex = index;
                    break;
                }
            }
        }

        var contentBox = new TextBox
        {
            Text = initialContent,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 240,
            PlaceholderText = "记录本章剧情概括、人物变化、剧情点评或其他分析...",
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(new TextBlock { Text = "标题" });
        panel.Children.Add(titleBox);
        panel.Children.Add(new TextBlock { Text = "分类" });
        panel.Children.Add(categoryBox);
        panel.Children.Add(new TextBlock { Text = "批注内容" });
        panel.Children.Add(new ScrollViewer
        {
            Content = contentBox,
            Height = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var dialog = new ContentDialog
        {
            Title = dialogTitle,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        if (string.IsNullOrWhiteSpace(contentBox.Text))
        {
            await ShowNovelProjectMessageAsync("批注内容不能为空", "请输入章节批注内容后再保存。");
            return null;
        }

        Guid? categoryId = categoryBox.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
        string? title = string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text.Trim();
        return new ChapterAnnotationEditValues(title, contentBox.Text.Trim(), categoryId);
    }

    private async void ManageChapterAnnotationCategories_Click(object sender, RoutedEventArgs e)
    {
        await ShowChapterAnnotationCategoryManagerAsync();
    }

    private async Task ShowChapterAnnotationCategoryManagerAsync()
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
        var confirmDelete = new CheckBox { Content = "确认删除所选分类（批注将变为未分类）" };
        var confirmMerge = new CheckBox { Content = "确认将所选分类合并到目标分类" };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var addButton = new Button { Content = "新建" };
        var renameButton = new Button { Content = "重命名", IsEnabled = false };
        var deleteButton = new Button { Content = "删除", IsEnabled = false };
        var mergeButton = new Button { Content = "合并", IsEnabled = false };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionRow.Children.Add(addButton);
        actionRow.Children.Add(renameButton);
        actionRow.Children.Add(deleteButton);
        actionRow.Children.Add(mergeButton);

        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(categoryList);
        panel.Children.Add(nameBox);
        panel.Children.Add(actionRow);
        panel.Children.Add(targetBox);
        panel.Children.Add(confirmDelete);
        panel.Children.Add(confirmMerge);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "章节批注分类管理",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot,
        };

        ChapterAnnotationCategory? GetSelectedCategory() =>
            categoryList.SelectedItem is ListViewItem { Tag: ChapterAnnotationCategory category }
                ? category
                : null;

        Guid? GetTargetCategoryId() =>
            targetBox.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;

        void UpdateButtons()
        {
            ChapterAnnotationCategory? selected = GetSelectedCategory();
            bool hasName = !string.IsNullOrWhiteSpace(nameBox.Text);
            addButton.IsEnabled = hasName;
            renameButton.IsEnabled = selected is not null && hasName;
            deleteButton.IsEnabled = selected is not null && confirmDelete.IsChecked == true;
            mergeButton.IsEnabled = selected is not null
                && GetTargetCategoryId() is Guid target
                && target != selected.Id
                && confirmMerge.IsChecked == true;
        }

        async Task ReloadCategoriesAsync(Guid? preferredId = null)
        {
            IReadOnlyList<ChapterAnnotationCategory> categories =
                await store.GetChapterAnnotationCategoriesAsync();
            if (!ReferenceEquals(store, activeAnnotationStore))
                return;

            categoryList.Items.Clear();
            targetBox.Items.Clear();
            foreach (ChapterAnnotationCategory category in categories)
            {
                categoryList.Items.Add(new ListViewItem { Content = category.Name, Tag = category });
                targetBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
            }

            ListViewItem? preferred = categoryList.Items
                .OfType<ListViewItem>()
                .FirstOrDefault(item => item.Tag is ChapterAnnotationCategory category
                    && category.Id == preferredId);
            categoryList.SelectedItem = preferred ?? categoryList.Items.FirstOrDefault();
            targetBox.SelectedIndex = targetBox.Items.Count > 0 ? 0 : -1;
            nameBox.Text = GetSelectedCategory()?.Name ?? "";
            confirmDelete.IsChecked = false;
            confirmMerge.IsChecked = false;
            UpdateButtons();
        }

        categoryList.SelectionChanged += (_, _) =>
        {
            nameBox.Text = GetSelectedCategory()?.Name ?? "";
            confirmDelete.IsChecked = false;
            confirmMerge.IsChecked = false;
            UpdateButtons();
        };
        targetBox.SelectionChanged += (_, _) => UpdateButtons();
        nameBox.TextChanged += (_, _) => UpdateButtons();
        confirmDelete.Checked += (_, _) => UpdateButtons();
        confirmDelete.Unchecked += (_, _) => UpdateButtons();
        confirmMerge.Checked += (_, _) => UpdateButtons();
        confirmMerge.Unchecked += (_, _) => UpdateButtons();

        addButton.Click += async (_, _) =>
        {
            try
            {
                Guid id = await store.CreateChapterAnnotationCategoryAsync(nameBox.Text.Trim());
                errorText.Text = "";
                await ReloadCategoriesAsync(id);
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };

        renameButton.Click += async (_, _) =>
        {
            ChapterAnnotationCategory? selected = GetSelectedCategory();
            if (selected is null)
                return;
            try
            {
                await store.UpdateChapterAnnotationCategoryAsync(
                    selected.Id, nameBox.Text.Trim(), selected.Color, selected.SortOrder);
                errorText.Text = "";
                await ReloadCategoriesAsync(selected.Id);
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };

        deleteButton.Click += async (_, _) =>
        {
            ChapterAnnotationCategory? selected = GetSelectedCategory();
            if (selected is null || confirmDelete.IsChecked != true)
                return;
            try
            {
                await store.DeleteChapterAnnotationCategoryAsync(selected.Id);
                errorText.Text = "";
                await ReloadCategoriesAsync();
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };

        mergeButton.Click += async (_, _) =>
        {
            ChapterAnnotationCategory? selected = GetSelectedCategory();
            Guid? targetId = GetTargetCategoryId();
            if (selected is null || targetId is null || confirmMerge.IsChecked != true)
                return;
            try
            {
                await store.MergeChapterAnnotationCategoriesAsync(selected.Id, targetId.Value);
                errorText.Text = "";
                await ReloadCategoriesAsync(targetId.Value);
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
            await LoadChapterAnnotationsAsync();
        }
    }
}
