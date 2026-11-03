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
    private long pendingChapterNavigationOffset = -1;
    private string? pendingChapterNavigationTitle;
    private Guid? selectedChapterAnnotationChapterId;
    private Guid? selectedChapterAnnotationCategoryId;
    private CancellationTokenSource? chapterAnnotationFilterDebounce;
    private IReadOnlyList<NovelChapter> chapterAnnotationChapters = Array.Empty<NovelChapter>();
    private IReadOnlyList<ChapterAnnotationCategory> chapterAnnotationCategories = Array.Empty<ChapterAnnotationCategory>();

    private sealed record ChapterAnnotationEditValues(
        Guid ChapterId, string? Title, string Content, Guid? CategoryId);

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
        {
            chapterAnnotationFilterDebounce?.Cancel();
            return;
        }

        SmallAnnotationsItem.IsChecked = false;
        SmallAnnotationPane.Visibility = Visibility.Collapsed;
        await LoadChapterAnnotationsAsync();
        if (LineIndexer.IsCompleted)
            _ = EnsureChapterIndexAsync();
    }

    private void CloseChapterAnnotations_Click(object sender, RoutedEventArgs e)
    {
        chapterAnnotationFilterDebounce?.Cancel();
        ChapterAnnotationsItem.IsChecked = false;
        ChapterAnnotationPane.Visibility = Visibility.Collapsed;
    }

    private void ChapterAnnotationSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ScheduleChapterAnnotationRefresh();
    }

    private void ChapterAnnotationChapterFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingChapterAnnotationUi)
            return;

        int selectedIndex = ChapterAnnotationChapterFilter.SelectedIndex;
        selectedChapterAnnotationChapterId =
            selectedIndex > 0 && selectedIndex - 1 < chapterAnnotationChapters.Count
                ? chapterAnnotationChapters[selectedIndex - 1].Id
                : null;

        // While the chapter dropdown is open, wheel-scrolling through a large novel should only
        // move the highlighted item. Do not issue a database query for every intermediate selection;
        // refresh once the user closes the dropdown.
        if (!ChapterAnnotationChapterFilter.IsDropDownOpen)
            ScheduleChapterAnnotationRefresh();
    }

    private void ChapterAnnotationChapterFilter_DropDownClosed(
        object sender, object e)
    {
        if (!refreshingChapterAnnotationUi)
            ScheduleChapterAnnotationRefresh();
    }

    private void ChapterAnnotationCategoryFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (refreshingChapterAnnotationUi)
            return;

        int selectedIndex = ChapterAnnotationCategoryFilter.SelectedIndex;
        selectedChapterAnnotationCategoryId =
            selectedIndex > 0 && selectedIndex - 1 < chapterAnnotationCategories.Count
                ? chapterAnnotationCategories[selectedIndex - 1].Id
                : null;
        ScheduleChapterAnnotationRefresh();
    }

    private void ScheduleChapterAnnotationRefresh()
    {
        chapterAnnotationFilterDebounce?.Cancel();
        var cancellation = new CancellationTokenSource();
        chapterAnnotationFilterDebounce = cancellation;
        _ = RefreshChapterAnnotationsAfterDelayAsync(cancellation);
    }

    private async Task RefreshChapterAnnotationsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // Scrolling through a large chapter selector can raise many SelectionChanged events.
            // Wait for the selection to settle before issuing a SQLite query.
            await Task.Delay(180, cancellation.Token);
            await RefreshChapterAnnotationsAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this refresh.
        }
        finally
        {
            if (ReferenceEquals(chapterAnnotationFilterDebounce, cancellation))
                chapterAnnotationFilterDebounce = null;
            cancellation.Dispose();
        }
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
            chapterAnnotationCategories = categories;
            refreshingChapterAnnotationUi = true;
            try
            {
                // Use explicit ComboBoxItem containers throughout. Do not mix ItemsSource with Items,
                // and do not assign null to DisplayMemberPath while rebuilding a live WinUI ComboBox.
                ChapterAnnotationChapterFilter.SelectedIndex = -1;
                ChapterAnnotationChapterFilter.Items.Clear();
                ChapterAnnotationCategoryFilter.SelectedIndex = -1;
                ChapterAnnotationCategoryFilter.Items.Clear();

                ChapterAnnotationChapterFilter.Items.Add(
                    new ComboBoxItem { Content = "全部章节" });
                foreach (NovelChapter chapter in chapters)
                {
                    ChapterAnnotationChapterFilter.Items.Add(new ComboBoxItem
                    {
                        Content = chapter.Title,
                        Tag = chapter.Id,
                    });
                }
                int selectedChapterIndex = selectedChapterAnnotationChapterId is Guid selectedChapterId
                    ? chapters.ToList().FindIndex(chapter => chapter.Id == selectedChapterId) + 1
                    : 0;
                ChapterAnnotationChapterFilter.SelectedIndex =
                    selectedChapterIndex >= 0 ? selectedChapterIndex : 0;
                selectedChapterAnnotationChapterId =
                    (ChapterAnnotationChapterFilter.SelectedItem as ComboBoxItem)?.Tag is Guid chapterId
                        ? chapterId
                        : null;

                ChapterAnnotationCategoryFilter.Items.Add(
                    new ComboBoxItem { Content = "全部分类" });
                foreach (ChapterAnnotationCategory category in categories)
                {
                    ChapterAnnotationCategoryFilter.Items.Add(new ComboBoxItem
                    {
                        Content = category.Name,
                        Tag = category.Id,
                    });
                }
                int selectedCategoryIndex = selectedChapterAnnotationCategoryId is Guid selectedCategoryId
                    ? categories.ToList().FindIndex(category => category.Id == selectedCategoryId) + 1
                    : 0;
                ChapterAnnotationCategoryFilter.SelectedIndex =
                    selectedCategoryIndex >= 0 ? selectedCategoryIndex : 0;
                selectedChapterAnnotationCategoryId =
                    (ChapterAnnotationCategoryFilter.SelectedItem as ComboBoxItem)?.Tag is Guid categoryId
                        ? categoryId
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

    private async Task RefreshChapterAnnotationsAsync(CancellationToken cancellationToken = default)
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
                await store.GetChapterAnnotationSummariesAsync(
                    chapterId, categoryId, searchText, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || requestVersion != chapterAnnotationRefreshVersion
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
                    Text = summary.Chapter.IsActive
                        ? summary.Chapter.Title
                        : $"{summary.Chapter.Title}（原章节已删除）",
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
                    if (summary.Chapter.IsActive)
                    {
                        await NavigateToChapterAsync(summary.Chapter);
                    }
                    else
                    {
                        await ShowNovelProjectMessageAsync(
                            "原章节已不存在",
                            "这条章节批注已保留，但对应章节标题已从原文中删除，无法保证旧位置仍然对应原章节。可以通过“编辑”将它重新指定到现有章节。");
                    }
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
                    bool indexRunning = chapterIndexBuildRunningVersion == chapterIndexBuildVersion;
                    ChapterAnnotationEmptyState.Text = state is null
                        && (!LineIndexer.IsCompleted || indexRunning)
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
        catch (OperationCanceledException)
        {
            // A newer chapter/category/search selection superseded this query.
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

            if (!project.IsDocumentFingerprintCurrent)
            {
                chapterAnnotationChapters = await store.GetChaptersAsync();
                LblStatus.Text = "项目原文与上次保存的指纹不一致，请打开章节批注面板并点击“重新识别”检查变化。";
                if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                    await LoadChapterAnnotationsAsync();
                return;
            }

            if (state is { ParserVersion: NovelChapterIndexBuilder.CurrentParserVersion }
                && state.DocumentRevision == project.Metadata.DocumentRevision)
            {
                chapterAnnotationChapters = await store.GetChaptersAsync();
                bool fingerprintsReady = chapterAnnotationChapters.All(chapter =>
                    chapter.BodyStartOffset >= chapter.StartOffset
                    && chapter.BodyStartOffset <= chapter.EndOffset
                    && !string.IsNullOrWhiteSpace(chapter.BodySha256));
                if (fingerprintsReady)
                {
                    if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                        await LoadChapterAnnotationsAsync();
                    return;
                }

                LblStatus.Text = "当前章节索引缺少正文指纹，请点击“重新识别”升级章节索引。";
                if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                    await LoadChapterAnnotationsAsync();
                return;
            }

            IReadOnlyList<NovelChapter> existing = await store.GetChaptersAsync();
            if (existing.Count > 0)
            {
                // Never replace chapter IDs automatically: chapter annotations depend on them.
                // If this is a legacy index without state metadata, record it only when every row
                // already belongs to the current document revision.
                bool legacyFingerprintMissing = existing.Any(chapter =>
                    chapter.BodyStartOffset < chapter.StartOffset || string.IsNullOrWhiteSpace(chapter.BodySha256));
                if (state is null
                    && !legacyFingerprintMissing
                    && existing.All(chapter => chapter.DocumentRevision == project.Metadata.DocumentRevision))
                {
                    await store.SaveChapterIndexIfEmptyAsync(existing, project.Metadata.DocumentRevision);
                }
                chapterAnnotationChapters = existing;
                if (legacyFingerprintMissing)
                    LblStatus.Text = "旧章节索引缺少正文指纹，请点击“重新识别”完成升级。";
                if (state is not null
                    && (state.DocumentRevision != project.Metadata.DocumentRevision
                        || state.ParserVersion != NovelChapterIndexBuilder.CurrentParserVersion))
                {
                    LblStatus.Text = "原文或章节识别规则已有变化，章节索引可能过期；请点击“重新识别”确认更新。";
                }
                if (ChapterAnnotationPane.Visibility == Visibility.Visible)
                    await LoadChapterAnnotationsAsync();
                return;
            }

            long revision = project.Metadata.DocumentRevision;
            LblStatus.Text = "正在识别章节标题...";
            if (ChapterAnnotationPane.Visibility == Visibility.Visible)
            {
                ChapterAnnotationEmptyState.Text = "正在识别章节，请稍候...";
                ChapterAnnotationEmptyState.Visibility = Visibility.Visible;
            }

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

    private async void RebuildChapterIndex_Click(object sender, RoutedEventArgs e)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        LineProvider? lines = provider;
        if (store is null || project is null || lines is null)
            return;

        if (!LineIndexer.IsCompleted || filteredSource is not null)
        {
            await ShowNovelProjectMessageAsync(
                "暂时无法重新识别章节",
                filteredSource is not null
                    ? "请先退出筛选视图，再重新识别章节。"
                    : "请等待全文行索引完成后，再重新识别章节。");
            return;
        }

        int generation = ++chapterIndexBuildVersion;
        chapterIndexBuildCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        chapterIndexBuildCancellation = cancellation;
        chapterIndexBuildRunningVersion = generation;
        RebuildChapterIndexButton.IsEnabled = false;
        LblStatus.Text = "正在重新扫描章节标题...";

        try
        {
            IReadOnlyList<NovelChapter> existing = await store.GetChaptersAsync();
            bool documentFingerprintChanged = !project.IsDocumentFingerprintCurrent;
            long revision = documentFingerprintChanged
                ? checked(project.Metadata.DocumentRevision + 1)
                : project.Metadata.DocumentRevision;
            IReadOnlyList<NovelChapter> detected = await Task.Run(
                () => NovelChapterIndexBuilder.Build(lines, revision, cancellation.Token),
                cancellation.Token);

            if (!IsCurrentChapterIndexRequest(generation, store, project, lines))
                return;

            NovelChapterIndexReconciliation comparison =
                NovelChapterIndexReconciler.Reconcile(existing, detected);
            var details = new List<string>
            {
                $"原索引章节数：{existing.Count:N0}",
                $"重新识别章节数：{comparison.Chapters.Count:N0}",
                $"新增：{comparison.Added.Count:N0}    删除：{comparison.Removed.Count:N0}    标题变化：{comparison.Renamed.Count:N0}    正文变化：{comparison.ContentChanged.Count:N0}",
                "",
            };

            static void AppendChapterNames(List<string> target, string heading,
                IEnumerable<string> names)
            {
                string[] values = names.Take(15).ToArray();
                if (values.Length == 0)
                    return;
                target.Add(heading);
                target.AddRange(values.Select(value => "  · " + value));
                if (names.Skip(15).Any())
                    target.Add("  · ……其余项目省略");
                target.Add("");
            }

            AppendChapterNames(details, "新增章节：", comparison.Added.Select(chapter => chapter.Title));
            AppendChapterNames(details, "删除章节：", comparison.Removed.Select(chapter => chapter.Title));
            AppendChapterNames(details, "标题变化：", comparison.Renamed.Select(change =>
                $"{change.Previous.Title}  →  {change.Current.Title}"));
            AppendChapterNames(details, "正文内容变化：", comparison.ContentChanged.Select(change =>
                change.Current.Title));

            if (!comparison.HasChanges)
            {
                details.Add("没有发现章节标题的新增、删除、改名或正文变化。");
                details.Add("确认后仍会更新章节范围和索引版本。");
            }

            details.Add("");
            details.Add("安全说明：被删除章节的已有章节批注不会被删除，会保留为“原章节已删除”记录；");
            details.Add("正文指纹变化只代表对应章节需要重新校验小批注锚点；其他章节的锚点不应被连带扫描。");
            details.Add("已有章节批注继续绑定稳定 ChapterId；被删除章节的批注保留在归档章节中。");
            details.Add("确认后才会更新数据库；取消则完全保留当前索引。");

            var dialog = new ContentDialog
            {
                Title = "章节重新识别结果",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = string.Join(Environment.NewLine, details),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "确认更新索引",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || !IsCurrentChapterIndexRequest(generation, store, project, lines))
                return;

            await store.ReplaceChapterIndexAsync(
                comparison.Chapters, revision, NovelChapterIndexBuilder.CurrentParserVersion,
                cancellation.Token);
            if (documentFingerprintChanged)
                await project.RefreshDocumentFingerprintAsync(cancellation.Token);
            if (!IsCurrentChapterIndexRequest(generation, store, project, lines))
                return;

            chapterAnnotationChapters = await store.GetChaptersAsync();
            if (selectedChapterAnnotationChapterId is Guid selected
                && chapterAnnotationChapters.All(chapter => chapter.Id != selected))
            {
                selectedChapterAnnotationChapterId = null;
            }

            await LoadChapterAnnotationsAsync();
            LblStatus.Text = $"章节重新识别完成：当前索引 {chapterAnnotationChapters.Count:N0} 章；已保留被删除章节的历史批注。";
        }
        catch (OperationCanceledException)
        {
            // The project was closed or switched while the index was being rebuilt.
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Re-identify chapter index", ex);
            if (ReferenceEquals(store, activeAnnotationStore))
                await ShowNovelProjectMessageAsync("重新识别章节失败", ex.Message);
        }
        finally
        {
            if (chapterIndexBuildRunningVersion == generation)
                chapterIndexBuildRunningVersion = -1;
            if (ReferenceEquals(chapterIndexBuildCancellation, cancellation))
                chapterIndexBuildCancellation = null;
            cancellation.Dispose();
            if (ReferenceEquals(store, activeAnnotationStore))
                RebuildChapterIndexButton.IsEnabled = true;
        }
    }

    private void CancelChapterIndexBuild()
    {
        pendingChapterNavigationOffset = -1;
        pendingChapterNavigationTitle = null;
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

    private NovelChapter? GetDominantVisibleChapter()
    {
        LineProvider? lines = provider;
        DocumentTextOffsetMap? map = annotationOffsetMap;
        if (lines is null || map is null || filteredSource is not null
            || lines.LineCount == 0 || chapterAnnotationChapters.Count == 0)
            return null;

        (int visibleFirstLine, int visibleLastLine) = View.GetVisibleSourceLineRange();
        int firstLine = Math.Clamp(visibleFirstLine, 0, lines.LineCount - 1);
        int lastLine = Math.Clamp(visibleLastLine, firstLine, lines.LineCount - 1);
        long visibleStart = map.GetLineStartOffset(firstLine);
        long visibleEnd = lastLine + 1 < lines.LineCount
            ? map.GetLineStartOffset(lastLine + 1)
            : map.GetOffset(lastLine, lines.GetLineUncached(lastLine).Length);
        if (visibleEnd <= visibleStart)
            visibleEnd = checked(visibleStart + 1);

        long visibleCenter = visibleStart + (visibleEnd - visibleStart) / 2;
        NovelChapter? best = null;
        long bestOverlap = 0;
        bool bestContainsCenter = false;

        foreach (NovelChapter chapter in chapterAnnotationChapters)
        {
            long overlapStart = Math.Max(visibleStart, chapter.StartOffset);
            long overlapEnd = Math.Min(visibleEnd, chapter.EndOffset);
            long overlap = Math.Max(0, overlapEnd - overlapStart);
            bool containsCenter = visibleCenter >= chapter.StartOffset && visibleCenter < chapter.EndOffset;
            if (overlap > bestOverlap || (overlap == bestOverlap && overlap > 0
                && containsCenter && !bestContainsCenter))
            {
                best = chapter;
                bestOverlap = overlap;
                bestContainsCenter = containsCenter;
            }
        }

        return best;
    }

    private async Task<bool> EnsureChapterIndexAvailableForCreationAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null || provider is null || filteredSource is not null)
            return false;

        chapterAnnotationChapters = await store.GetChaptersAsync();
        NovelChapterIndexState? state = await store.GetChapterIndexStateAsync();

        if (chapterAnnotationChapters.Count == 0 && state is null && LineIndexer.IsCompleted)
        {
            await EnsureChapterIndexAsync();
            chapterAnnotationChapters = await store.GetChaptersAsync();
            state = await store.GetChapterIndexStateAsync();
        }

        if (!LineIndexer.IsCompleted)
        {
            await ShowNovelProjectMessageAsync(
                "章节索引尚未完成",
                "请等待全文索引完成后，再创建章节批注。");
            return false;
        }

        bool missingChapterFingerprints = chapterAnnotationChapters.Any(chapter =>
            chapter.BodyStartOffset < chapter.StartOffset
            || string.IsNullOrWhiteSpace(chapter.BodySha256));
        bool staleIndexState = !project.IsDocumentFingerprintCurrent
            || missingChapterFingerprints
            || (state is not null
                && (state.DocumentRevision != project.Metadata.DocumentRevision
                    || state.ParserVersion != NovelChapterIndexBuilder.CurrentParserVersion));
        bool staleChapterRows = state is null
            && chapterAnnotationChapters.Any(chapter =>
                chapter.DocumentRevision != project.Metadata.DocumentRevision
                || chapter.BodyStartOffset < chapter.StartOffset
                || string.IsNullOrWhiteSpace(chapter.BodySha256));
        if (staleIndexState || staleChapterRows)
        {
            await ShowNovelProjectMessageAsync(
                "章节索引已过期",
                "当前原文或章节识别规则已经发生变化。请先点击右上角“重新识别”，确认更新章节索引后再创建章节批注。");
            return false;
        }

        if (chapterAnnotationChapters.Count == 0)
        {
            await ShowNovelProjectMessageAsync(
                "没有可用章节",
                "当前原文没有识别到章节标题，无法创建章节批注。");
            return false;
        }

        return true;
    }

    private async void CreateChapterAnnotationFromPosition(TextPosition position)
    {
        if (activeAnnotationStore is null || annotationOffsetMap is null
            || provider is null || filteredSource is not null)
            return;

        try
        {
            if (!await EnsureChapterIndexAvailableForCreationAsync())
                return;

            LineProvider lines = provider;
            DocumentTextOffsetMap map = annotationOffsetMap;
            if (position.Line < 0 || position.Line >= lines.LineCount)
            {
                diagnostics.LogSwallowed(
                    "Create chapter annotation from invalid reader position",
                    new ArgumentOutOfRangeException(nameof(position), $"Line {position.Line} is outside the indexed range 0..{lines.LineCount - 1}."));
                await ShowNovelProjectMessageAsync(
                    "无法确定右键位置",
                    "右键位置尚未进入全文索引范围，请稍候等待索引完成后再试。");
                return;
            }

            string clickedLine = lines.GetLineUncached(position.Line);
            int column = Math.Clamp(position.Column, 0, clickedLine.Length);
            long offset = map.GetOffset(position.Line, column);
            NovelChapter? chapter = chapterAnnotationChapters.FirstOrDefault(candidate =>
                candidate.IsActive && candidate.StartOffset <= offset && candidate.EndOffset > offset);
            if (chapter is null)
            {
                await ShowNovelProjectMessageAsync(
                    "当前位置不属于已识别章节",
                    "请将右键位置移动到某个已识别章节的正文范围内；如果章节标题刚刚修改过，请先重新识别章节。");
                return;
            }

            await CreateChapterAnnotationForChapterAsync(chapter);
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Create chapter annotation from reader position", ex);
            await ShowNovelProjectMessageAsync("无法创建章节批注", ex.ToString());
        }
    }

    private async Task CreateChapterAnnotationForChapterAsync(NovelChapter initialChapter)
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        if (store is null)
            return;

        ChapterAnnotationEditValues? values = await ShowChapterAnnotationEditorAsync(
            "新建章节批注",
            initialChapter.Id,
            initialChapter,
            null,
            "",
            selectedChapterAnnotationCategoryId);
        if (values is null || !ReferenceEquals(store, activeAnnotationStore))
            return;

        await store.CreateChapterAnnotationAsync(
            values.ChapterId, values.Content, values.CategoryId, values.Title);
        selectedChapterAnnotationChapterId = values.ChapterId;
        SmallAnnotationsItem.IsChecked = false;
        SmallAnnotationPane.Visibility = Visibility.Collapsed;
        ChapterAnnotationsItem.IsChecked = true;
        ChapterAnnotationPane.Visibility = Visibility.Visible;
        await LoadChapterAnnotationsAsync();
    }

    private async Task NavigateToChapterAsync(NovelChapter chapter)
    {
        if (annotationOffsetMap is null || provider is null || filteredSource is not null)
            return;

        pendingChapterNavigationOffset = chapter.StartOffset;
        pendingChapterNavigationTitle = chapter.Title;
        if (!LineIndexer.IsCompleted && !LineIndexer.CanResolveOffset(chapter.StartOffset))
        {
            LblStatus.Text = $"正在定位章节：{chapter.Title}";
            if (indexingTask.IsCompleted || cancelIndexing?.IsCancellationRequested == true)
                _ = ResumeIndexingForChapterNavigationAsync();
            return;
        }

        try
        {
            ApplyPendingChapterNavigation();
        }
        catch (Exception ex)
        {
            pendingChapterNavigationOffset = -1;
            pendingChapterNavigationTitle = null;
            diagnostics.LogSwallowed("Navigate to chapter", ex);
            await ShowNovelProjectMessageAsync("无法跳转章节", ex.Message);
        }
    }

    private void ApplyPendingChapterNavigation()
    {
        long offset = pendingChapterNavigationOffset;
        if (offset < 0 || annotationOffsetMap is null || provider is null || filteredSource is not null)
            return;

        if (!LineIndexer.CanResolveOffset(offset))
        {
            if (!LineIndexer.IsCompleted)
                return;

            pendingChapterNavigationOffset = -1;
            pendingChapterNavigationTitle = null;
            LblStatus.Text = "章节位置超出当前原文范围，无法跳转。";
            return;
        }

        string? title = pendingChapterNavigationTitle;
        pendingChapterNavigationOffset = -1;
        pendingChapterNavigationTitle = null;
        var (line, _) = annotationOffsetMap.GetPosition(offset);
        View.GoToLineColumn(line, 0);
        View.CenterCaretVertically();
        View.FocusCanvas();
        if (!string.IsNullOrWhiteSpace(title))
            LblStatus.Text = $"已定位：{title}";
    }

    private async Task ResumeIndexingForChapterNavigationAsync()
    {
        if (provider is null || LineIndexer.IsCompleted || pendingChapterNavigationOffset < 0)
            return;

        try
        {
            await StopIndexingAsync();
            if (provider is null || LineIndexer.IsCompleted || pendingChapterNavigationOffset < 0)
                return;

            indexRefreshTimer.Start();
            StartIndexing();
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Resume indexing for chapter navigation", ex);
            LblStatus.Text = $"无法继续索引以定位章节：{ex.Message}";
        }
    }

    private async void NewChapterAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (activeAnnotationStore is null)
            return;

        try
        {
            if (!await EnsureChapterIndexAvailableForCreationAsync())
                return;

            NovelChapter? chapter = GetDominantVisibleChapter();
            if (chapter is null)
            {
                await ShowNovelProjectMessageAsync(
                    "当前可见区域没有已识别章节",
                    "请将阅读位置移动到某个已识别章节的正文范围内。");
                return;
            }

            await CreateChapterAnnotationForChapterAsync(chapter);
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Create chapter annotation from pane", ex);
            await ShowNovelProjectMessageAsync("创建章节批注失败", ex.ToString());
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
                summary.Annotation.ChapterId,
                summary.Chapter,
                summary.Annotation.Title,
                summary.Annotation.Content,
                summary.Annotation.CategoryId);
            if (values is null || !ReferenceEquals(store, activeAnnotationStore))
                return;

            await store.UpdateChapterAnnotationAsync(
                summary.Annotation.Id,
                values.ChapterId,
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
        Guid initialChapterId,
        NovelChapter? initialChapter,
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

        var availableChapters = chapterAnnotationChapters.ToList();
        if (initialChapter is not null
            && availableChapters.All(chapter => chapter.Id != initialChapter.Id))
        {
            availableChapters.Add(initialChapter);
        }
        if (availableChapters.Count == 0)
            throw new InvalidOperationException("没有可供选择的章节。");

        // Use the same explicit item-container pattern as the working category manager.
        // The selected item carries the stable database ID; display text is never used as identity.
        var chapterBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 420,
        };
        foreach (NovelChapter chapter in availableChapters)
        {
            chapterBox.Items.Add(new ComboBoxItem
            {
                Content = chapter.IsActive ? chapter.Title : $"（原章节已删除）{chapter.Title}",
                Tag = chapter.Id,
            });
        }
        int initialChapterIndex = availableChapters.FindIndex(chapter => chapter.Id == initialChapterId);
        chapterBox.SelectedIndex = initialChapterIndex >= 0 ? initialChapterIndex : 0;

        var titleBox = new TextBox
        {
            PlaceholderText = "批注标题（可选）",
            Text = initialTitle ?? "",
        };
        var categoryBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "选择分类",
            MaxDropDownHeight = 420,
        };
        categoryBox.Items.Add(new ComboBoxItem { Content = "未分类" });
        foreach (ChapterAnnotationCategory category in categories)
        {
            categoryBox.Items.Add(new ComboBoxItem
            {
                Content = category.Name,
                Tag = category.Id,
            });
        }
        int initialCategoryIndex = initialCategoryId is Guid selectedCategory
            ? categories.ToList().FindIndex(category => category.Id == selectedCategory) + 1
            : 0;
        categoryBox.SelectedIndex = initialCategoryIndex >= 0 ? initialCategoryIndex : 0;

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
        panel.Children.Add(new TextBlock { Text = "章节" });
        panel.Children.Add(chapterBox);
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
        Guid? selectedChapterId =
            (chapterBox.SelectedItem as ComboBoxItem)?.Tag is Guid chapterId ? chapterId : null;
        NovelChapter? selectedChapter = selectedChapterId is Guid id
            ? availableChapters.FirstOrDefault(chapter => chapter.Id == id)
            : null;
        if (selectedChapter is null)
        {
            await ShowNovelProjectMessageAsync("尚未选择章节", "请选择这条章节批注所属的章节。");
            return null;
        }
        if (string.IsNullOrWhiteSpace(contentBox.Text))
        {
            await ShowNovelProjectMessageAsync("批注内容不能为空", "请输入章节批注内容后再保存。");
            return null;
        }

        Guid? categoryId =
            (categoryBox.SelectedItem as ComboBoxItem)?.Tag is Guid selectedCategoryId
                ? selectedCategoryId
                : null;
        string? title = string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text.Trim();
        return new ChapterAnnotationEditValues(
            selectedChapter.Id, title, contentBox.Text, categoryId);
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
            categoryList.SelectedItem = preferred
                ?? (categoryList.Items.Count > 0 ? categoryList.Items[0] : null);
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
