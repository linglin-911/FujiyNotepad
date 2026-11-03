using System.Text;
using FujiyNotepad.Core;
using FujiyNotepad.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;

namespace FujiyNotepad.WinUI
{
    public sealed partial class MainWindow
    {
        private int smallAnnotationRefreshVersion;
        private DispatcherQueueTimer? smallAnnotationCardTapTimer;
        private NovelAnnotationSummary? pendingSmallAnnotationCardTap;
        private Dictionary<Guid, string> smallAnnotationCategoryNames = new();
        private Guid? selectedSmallAnnotationCategoryId;

        private sealed record SmallAnnotationEditValues(string? Title, string Content, Guid? CategoryId);

        private async void SmallAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (activeAnnotationStore is null || activeNovelProject is null)
            {
                SmallAnnotationsItem.IsChecked = false;
                SmallAnnotationPane.Visibility = Visibility.Collapsed;
                return;
            }

            bool show = SmallAnnotationsItem.IsChecked;
            SmallAnnotationPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                CancelSmallAnnotationCardSingleTap();

            if (show)
            {
                ChapterDirectoryItem.IsChecked = false;
                ChapterDirectoryPane.Visibility = Visibility.Collapsed;
                ChapterAnnotationsItem.IsChecked = false;
                ChapterAnnotationPane.Visibility = Visibility.Collapsed;
                await ReloadSmallAnnotationCategoriesAsync();
                await RefreshSmallAnnotationsAsync();
            }
        }

        private void CloseSmallAnnotations_Click(object sender, RoutedEventArgs e)
        {
            CancelSmallAnnotationCardSingleTap();
            SmallAnnotationsItem.IsChecked = false;
            SmallAnnotationPane.Visibility = Visibility.Collapsed;
        }

        private async void SmallAnnotationSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            await RefreshSmallAnnotationsAsync();
        }

        private async void SmallAnnotationCategoryFilter_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (refreshingSmallAnnotationUi)
                return;

            selectedSmallAnnotationCategoryId =
                SmallAnnotationCategoryFilter.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
            await RefreshSmallAnnotationsAsync();
        }

        private void SmallAnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSmallAnnotationActionButtons();
        }

        private void UpdateSmallAnnotationActionButtons()
        {
            DeleteSmallAnnotationButton.IsEnabled = GetSelectedSmallAnnotationSummary() is not null;
        }

        private void QueueSmallAnnotationCardSingleTap(NovelAnnotationSummary summary)
        {
            pendingSmallAnnotationCardTap = summary;
            smallAnnotationCardTapTimer ??= CreateSmallAnnotationCardTapTimer();
            smallAnnotationCardTapTimer.Stop();
            smallAnnotationCardTapTimer.Start();
        }

        private DispatcherQueueTimer CreateSmallAnnotationCardTapTimer()
        {
            DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.IsRepeating = false;
            timer.Tick += async (sender, _) =>
            {
                sender.Stop();
                NovelAnnotationSummary? summary = pendingSmallAnnotationCardTap;
                pendingSmallAnnotationCardTap = null;
                if (summary is not null)
                    await NavigateToFirstSmallAnnotationAnchorAsync(summary);
            };
            return timer;
        }

        private void CancelSmallAnnotationCardSingleTap()
        {
            smallAnnotationCardTapTimer?.Stop();
            pendingSmallAnnotationCardTap = null;
        }

        private NovelAnnotationSummary? GetSelectedSmallAnnotationSummary()
        {
            return SmallAnnotationList.SelectedItem is ListViewItem { Tag: NovelAnnotationSummary summary }
                ? summary
                : null;
        }

        private Guid? GetSelectedSmallAnnotationCategoryId() => selectedSmallAnnotationCategoryId;

        private async Task ReloadSmallAnnotationCategoriesAsync(Guid? preferredCategoryId = null)
        {
            NovelAnnotationStore? store = activeAnnotationStore;
            if (store is null)
                return;

            try
            {
                IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
                if (!ReferenceEquals(store, activeAnnotationStore))
                    return;

                Guid? categoryId = preferredCategoryId ?? selectedSmallAnnotationCategoryId;
                if (categoryId is Guid requested
                    && !categories.Any(category => category.Id == requested))
                {
                    categoryId = null;
                }

                refreshingSmallAnnotationUi = true;
                try
                {
                    smallAnnotationCategoryNames = categories.ToDictionary(
                        category => category.Id, category => category.Name);

                    SmallAnnotationCategoryFilter.Items.Clear();
                    SmallAnnotationCategoryFilter.Items.Add(new ComboBoxItem { Content = "全部分类" });
                    foreach (AnnotationCategory category in categories)
                    {
                        SmallAnnotationCategoryFilter.Items.Add(new ComboBoxItem
                        {
                            Content = category.Name,
                            Tag = category.Id,
                        });
                    }

                    SmallAnnotationCategoryFilter.SelectedIndex = categoryId is Guid selected
                        ? categories.ToList().FindIndex(category => category.Id == selected) + 1
                        : 0;
                    selectedSmallAnnotationCategoryId = categoryId;
                }
                finally
                {
                    refreshingSmallAnnotationUi = false;
                }
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(store, activeAnnotationStore))
                    await ShowMessageAsync("无法加载小批注分类", ex.Message);
            }
        }

        private async Task RefreshSmallAnnotationsAsync(Guid? preferredAnnotationId = null)
        {
            CancelSmallAnnotationCardSingleTap();
            NovelAnnotationStore? store = activeAnnotationStore;
            if (store is null)
                return;

            int requestVersion = ++smallAnnotationRefreshVersion;
            Guid? selectedAnnotationId = preferredAnnotationId
                ?? GetSelectedSmallAnnotationSummary()?.Annotation.Id;
            Guid? categoryId = selectedSmallAnnotationCategoryId;
            string searchText = SmallAnnotationSearchBox.Text;

            try
            {
                IReadOnlyList<NovelAnnotationSummary> summaries =
                    await store.GetAnnotationSummariesAsync(categoryId, searchText);

                if (requestVersion != smallAnnotationRefreshVersion
                    || !ReferenceEquals(store, activeAnnotationStore))
                {
                    return;
                }

                IReadOnlyDictionary<Guid, IReadOnlyList<AnnotationAnchor>> anchorsByAnnotation =
                    await store.GetAnchorsForAnnotationsAsync(summaries.Select(item => item.Annotation.Id));
                if (requestVersion != smallAnnotationRefreshVersion
                    || !ReferenceEquals(store, activeAnnotationStore))
                {
                    return;
                }

                refreshingSmallAnnotationUi = true;
                try
                {
                    SmallAnnotationList.Items.Clear();
                    ListViewItem? itemToSelect = null;
                    foreach (NovelAnnotationSummary summary in summaries)
                    {
                        ListViewItem item = BuildSmallAnnotationListItem(
                            summary, smallAnnotationCategoryNames,
                            anchorsByAnnotation.TryGetValue(summary.Annotation.Id, out IReadOnlyList<AnnotationAnchor>? anchors)
                                ? anchors : Array.Empty<AnnotationAnchor>());
                        SmallAnnotationList.Items.Add(item);
                        if (summary.Annotation.Id == selectedAnnotationId)
                            itemToSelect = item;
                    }

                    SmallAnnotationCount.Text = $"{summaries.Count} 条批注";
                    SmallAnnotationEmptyState.Text = summaries.Count > 0
                        ? ""
                        : !string.IsNullOrWhiteSpace(searchText)
                            ? "没有找到匹配的小批注。"
                            : categoryId is not null
                                ? "该分类下暂无小批注。"
                                : "暂无小批注。";
                    SmallAnnotationEmptyState.Visibility = summaries.Count == 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    SmallAnnotationList.SelectedItem = itemToSelect;
                }
                finally
                {
                    refreshingSmallAnnotationUi = false;
                }

                UpdateSmallAnnotationActionButtons();
            }
            catch (Exception ex)
            {
                if (requestVersion == smallAnnotationRefreshVersion
                    && ReferenceEquals(store, activeAnnotationStore))
                {
                    await ShowMessageAsync("无法加载小批注", ex.Message);
                }
            }
        }

        private ListViewItem BuildSmallAnnotationListItem(
            NovelAnnotationSummary summary,
            IReadOnlyDictionary<Guid, string> categoryNames,
            IReadOnlyList<AnnotationAnchor>? anchors = null)
        {
            NovelAnnotation annotation = summary.Annotation;
            string title = string.IsNullOrWhiteSpace(annotation.Title) ? "（未命名批注）" : annotation.Title;
            string category = annotation.CategoryId is Guid categoryId
                && categoryNames.TryGetValue(categoryId, out string? categoryName)
                    ? categoryName
                    : "未分类";
            int activeAnchors = Math.Max(0, summary.AnchorCount - summary.OrphanedAnchorCount);

            ListViewItem? parentItem = null;
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = CreateSmallAnnotationExcerpt(annotation.Content, 90),
                FontSize = 12,
                Opacity = 0.82,
                TextWrapping = TextWrapping.Wrap,
            });

            if (anchors is { Count: > 0 })
            {
                int occurrence = 0;
                foreach (AnnotationAnchor anchor in anchors)
                {
                    occurrence++;
                    bool versionMatches = activeNovelProject is { } project
                        && anchor.DocumentRevision == project.Metadata.DocumentRevision;
                    bool canAttemptNavigation = !anchor.IsOrphaned;
                    string status = anchor.IsOrphaned
                        ? "（已失效）"
                        : versionMatches ? "" : "（位置待校验，点击尝试修复）";
                    var quoteButton = new Button
                    {
                        Tag = anchor,
                        IsEnabled = canAttemptNavigation,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(4, 2, 4, 2),
                        Content = new TextBlock
                        {
                            Text = $"原文 {occurrence}：{CreateSmallAnnotationExcerpt(anchor.Quote, 100)}{status}",
                            FontSize = 11,
                            Opacity = 0.86,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    };
                    quoteButton.Tapped += (_, args) =>
                    {
                        args.Handled = true;
                        if (parentItem is not null)
                        {
                            SmallAnnotationList.SelectedItem = parentItem;
                            UpdateSmallAnnotationActionButtons();
                        }
                    };
                    quoteButton.Click += async (_, _) =>
                    {
                        if (parentItem is not null)
                        {
                            SmallAnnotationList.SelectedItem = parentItem;
                            UpdateSmallAnnotationActionButtons();
                        }
                        await NavigateToSmallAnnotationAnchorAsync(anchor);
                    };
                    quoteButton.DoubleTapped += (_, args) => args.Handled = true;
                    content.Children.Add(quoteButton);
                }
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(summary.FirstActiveAnchorQuote)
                        ? "原文：暂无有效关联"
                        : $"原文：{CreateSmallAnnotationExcerpt(summary.FirstActiveAnchorQuote, 100)}",
                    FontSize = 11,
                    Opacity = 0.82,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = $"{category}  ·  有效位置 {activeAnchors}/{summary.AnchorCount}"
                    + (summary.OrphanedAnchorCount > 0 ? $"  ·  失效 {summary.OrphanedAnchorCount}" : "")
                    + $"  ·  {annotation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
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
            parentItem = item;
            item.Tapped += (_, args) =>
            {
                if (args.Handled)
                    return;

                SmallAnnotationList.SelectedItem = item;
                UpdateSmallAnnotationActionButtons();
                QueueSmallAnnotationCardSingleTap(summary);
            };
            item.DoubleTapped += async (_, args) =>
            {
                args.Handled = true;
                CancelSmallAnnotationCardSingleTap();
                SmallAnnotationList.SelectedItem = item;
                UpdateSmallAnnotationActionButtons();
                await ShowSmallAnnotationDetailsAsync(summary);
            };
            return item;
        }

        private static string CreateSmallAnnotationExcerpt(string text, int maxLength)
        {
            if (text.Length <= maxLength)
                return text;

            return text[..maxLength] + "…";
        }

        private async Task NavigateToFirstSmallAnnotationAnchorAsync(NovelAnnotationSummary summary)
        {
            NovelAnnotationStore? store = activeAnnotationStore;
            NovelProject? project = activeNovelProject;
            if (store is null || project is null)
                return;

            try
            {
                IReadOnlyList<AnnotationAnchor> anchors =
                    await store.GetAnchorsForAnnotationAsync(summary.Annotation.Id);
                if (!ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                // A non-orphaned anchor from an older revision may still be recoverable when the
                // saved quote survives and moved only because nearby text was inserted/deleted.
                AnnotationAnchor? first = anchors.FirstOrDefault(anchor => !anchor.IsOrphaned);

                if (first is null)
                {
                    await ShowMessageAsync("无法定位原文", "这条小批注目前没有可用的原文关联位置。");
                    return;
                }

                await NavigateToSmallAnnotationAnchorAsync(first);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("无法定位原文", ex.Message);
            }
        }


        private async Task RefreshVisibleSmallAnnotationMarkersAsync(bool force = false)
        {
            NovelAnnotationStore? store = activeAnnotationStore;
            NovelProject? project = activeNovelProject;
            DocumentTextOffsetMap? map = annotationOffsetMap;
            LineProvider? lines = provider;

            if (store is null || project is null || map is null || lines is null
                || filteredSource is not null || lines.LineCount == 0)
            {
                annotationMarkerRefreshCancellation?.Cancel();
                annotationMarkerRefreshCancellation = null;
                annotationMarkerRefreshVersion++;
                lastAnnotationMarkerStartLine = -1;
                lastAnnotationMarkerEndLine = -1;
                View.SetAnnotationMarkers(Array.Empty<TextAnnotationMarker>());
                return;
            }

            int firstLine = Math.Clamp(View.FirstVisibleLine, 0, lines.LineCount - 1);
            int lastLine = Math.Min(
                lines.LineCount - 1,
                firstLine + Math.Max(1, View.FullyVisibleLineCount) + 2);

            if (!force && firstLine == lastAnnotationMarkerStartLine
                && lastLine == lastAnnotationMarkerEndLine)
            {
                return;
            }

            lastAnnotationMarkerStartLine = firstLine;
            lastAnnotationMarkerEndLine = lastLine;
            annotationMarkerRefreshCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            annotationMarkerRefreshCancellation = cancellation;
            CancellationToken cancellationToken = cancellation.Token;
            int requestVersion = ++annotationMarkerRefreshVersion;

            try
            {
                // Offset-map lookups may have to decode lines from the nearest sparse checkpoint. Keep
                // that work and the visible-anchor query off the UI thread. A newer viewport cancels
                // the old query so rapid scrolling cannot accumulate obsolete database work.
                List<TextAnnotationMarker> markers = await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long startOffset = map.GetLineStartOffset(firstLine);
                    long endOffset = lastLine + 1 < lines.LineCount
                        ? map.GetLineStartOffset(lastLine + 1)
                        : map.GetOffset(lastLine, lines.GetLineUncached(lastLine).Length);
                    if (endOffset <= startOffset)
                        endOffset = checked(startOffset + 1);

                    IReadOnlyList<AnnotationAnchor> anchors =
                        await store.GetAnchorsIntersectingRangeAsync(startOffset, endOffset, cancellationToken);
                    var result = new List<TextAnnotationMarker>();

                    foreach (AnnotationAnchor anchor in anchors)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (anchor.IsOrphaned
                            || anchor.DocumentRevision != project.Metadata.DocumentRevision)
                        {
                            continue;
                        }

                        try
                        {
                            (int Line, int Column) start = map.GetPosition(anchor.StartOffset);
                            (int Line, int Column) end = map.GetPosition(anchor.EndOffset);
                            if (start.Line < 0 || end.Line < start.Line
                                || end.Line >= lines.LineCount)
                            {
                                continue;
                            }

                            string currentQuote = ExtractSmallAnnotationRange(
                                lines, new TextPosition(start.Line, start.Column),
                                new TextPosition(end.Line, end.Column));
                            if (!string.Equals(currentQuote, anchor.Quote, StringComparison.Ordinal))
                                continue;

                            int rangeStartLine = Math.Max(firstLine, start.Line);
                            int rangeEndLine = Math.Min(lastLine, end.Line);
                            for (int lineIndex = rangeStartLine; lineIndex <= rangeEndLine; lineIndex++)
                            {
                                int lineLength = lines.GetLineUncached(lineIndex).Length;
                                int startColumn = lineIndex == start.Line ? start.Column : 0;
                                int endColumn = lineIndex == end.Line ? end.Column : lineLength;
                                startColumn = Math.Clamp(startColumn, 0, lineLength);
                                endColumn = Math.Clamp(endColumn, 0, lineLength);

                                if (endColumn > startColumn)
                                    result.Add(new TextAnnotationMarker(lineIndex, startColumn, endColumn));
                            }
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // The saved anchor is outside the currently indexed text; never draw a
                            // marker at an unverified location.
                        }
                        catch (InvalidOperationException)
                        {
                            // The offset map is not ready for this anchor yet.
                        }
                    }

                    return result;
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested
                    || requestVersion != annotationMarkerRefreshVersion
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject)
                    || !ReferenceEquals(lines, provider)
                    || filteredSource is not null)
                {
                    return;
                }

                View.SetAnnotationMarkers(markers);
            }
            catch (OperationCanceledException)
            {
                // A newer viewport or project change made this marker request obsolete.
            }
            catch (Exception)
            {
                // A transient index/database read failure must not interrupt reading or clear the last
                // successfully-rendered marker set. A later viewport change retries the query.
            }
            finally
            {
                if (ReferenceEquals(annotationMarkerRefreshCancellation, cancellation))
                    annotationMarkerRefreshCancellation = null;
                cancellation.Dispose();
            }
        }

        private async void OnAnnotationMarkerClicked(TextPosition position)
        {
            NovelAnnotationStore? store = activeAnnotationStore;
            NovelProject? project = activeNovelProject;
            DocumentTextOffsetMap? map = annotationOffsetMap;
            if (store is null || project is null || map is null || filteredSource is not null)
                return;

            try
            {
                long offset = map.GetOffset(position.Line, position.Column);
                IReadOnlyList<AnnotationAnchor> directAnchors = await store.GetAnchorsAtOffsetAsync(offset);
                if (!ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                directAnchors = directAnchors.Where(anchor =>
                    !anchor.IsOrphaned
                    && anchor.DocumentRevision == project.Metadata.DocumentRevision
                    && TryResolveSmallAnnotationAnchor(anchor, out _)).ToList();
                if (directAnchors.Count == 0)
                    return;

                // A click anywhere inside a larger annotated range should also reveal annotations whose
                // source ranges are fully contained by that larger range. This makes nested notes discoverable
                // even when the click lands outside the smaller quote itself.
                long queryStart = directAnchors.Min(anchor => anchor.StartOffset);
                long queryEnd = directAnchors.Max(anchor => anchor.EndOffset);
                IReadOnlyList<AnnotationAnchor> intersecting =
                    await store.GetAnchorsIntersectingRangeAsync(queryStart, queryEnd);
                IReadOnlyList<AnnotationAnchor> anchors = intersecting
                    .Where(candidate =>
                        !candidate.IsOrphaned
                        && candidate.DocumentRevision == project.Metadata.DocumentRevision
                        && directAnchors.Any(parent =>
                            candidate.StartOffset >= parent.StartOffset
                            && candidate.EndOffset <= parent.EndOffset)
                        && TryResolveSmallAnnotationAnchor(candidate, out _))
                    .GroupBy(candidate => candidate.Id)
                    .Select(group => group.First())
                    .OrderBy(candidate => candidate.StartOffset)
                    .ThenByDescending(candidate => candidate.EndOffset)
                    .ToList();
                if (anchors.Count == 0)
                    return;

                HashSet<Guid> annotationIds = anchors.Select(anchor => anchor.AnnotationId).ToHashSet();
                long matchedStart = anchors.Min(anchor => anchor.StartOffset);
                long matchedEnd = anchors.Max(anchor => anchor.EndOffset);
                IReadOnlyList<NovelAnnotation> notes =
                    await store.GetAnnotationsIntersectingRangeAsync(matchedStart, matchedEnd);
                notes = notes.Where(note => annotationIds.Contains(note.Id)).ToList();
                if (notes.Count > 0
                    && ReferenceEquals(store, activeAnnotationStore)
                    && ReferenceEquals(project, activeNovelProject))
                {
                    await ShowSmallAnnotationMatchesAtPositionAsync(notes, anchors);
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("无法读取原文批注", ex.Message);
            }
        }

        private async Task ShowSmallAnnotationMatchesAtPositionAsync(
            IReadOnlyList<NovelAnnotation> notes,
            IReadOnlyList<AnnotationAnchor> anchors)
        {
            var content = new StackPanel { Spacing = 12, MinWidth = 420 };
            content.Children.Add(new TextBlock
            {
                Text = $"此处原文关联了 {notes.Count} 条小批注",
                FontSize = 12,
                Opacity = 0.72,
            });

            foreach (NovelAnnotation note in notes)
            {
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(note.Title) ? "（未命名批注）" : note.Title,
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(new TextBlock
                {
                    Text = note.Content,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                foreach (AnnotationAnchor anchor in anchors.Where(anchor => anchor.AnnotationId == note.Id))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = $"关联原文：{CreateSmallAnnotationExcerpt(anchor.Quote, 240)}",
                        FontSize = 12,
                        Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    });
                }

                content.Children.Add(new Border
                {
                    Height = 1,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(80, 128, 128, 128)),
                });
            }

            var dialog = new ContentDialog
            {
                Title = "原文小批注",
                Content = new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                CloseButtonText = "关闭",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async Task<(long StartOffset, long EndOffset, string Quote)?> GetCurrentSelectionAnchorAsync()
        {
            if (activeNovelProject is null || activeAnnotationStore is null
                || annotationOffsetMap is null || provider is null)
            {
                await ShowMessageAsync("无法创建批注", "请先打开一个小说项目。");
                return null;
            }

            if (filteredSource is not null)
            {
                await ShowMessageAsync("无法创建批注", "请先退出筛选视图，再从原文中选择需要批注的内容。");
                return null;
            }

            TextPosition startPosition = View.SelectionStart;
            TextPosition endPosition = View.SelectionEnd;
            if (startPosition == endPosition)
            {
                await ShowMessageAsync("无法创建批注", "请先在阅读器中选中一段原文。");
                return null;
            }

            string? quote = View.GetSelectedTextForAnnotation();
            if (string.IsNullOrEmpty(quote))
            {
                await ShowMessageAsync("无法创建批注", "没有获取到有效的选中原文。");
                return null;
            }

            try
            {
                long startOffset = annotationOffsetMap.GetOffset(startPosition.Line, startPosition.Column);
                long endOffset = annotationOffsetMap.GetOffset(endPosition.Line, endPosition.Column);
                if (endOffset <= startOffset || endOffset - startOffset != quote.Length)
                {
                    await ShowMessageAsync(
                        "无法创建批注",
                        "选中文字与原文定位信息不一致。请重新选择这段文字后再试。");
                    return null;
                }

                return (startOffset, endOffset, quote);
            }
            catch (ArgumentOutOfRangeException)
            {
                await ShowMessageAsync(
                    "无法创建批注",
                    "选中位置尚未建立完整索引。请等待阅读器索引完成后再试。");
                return null;
            }
        }

        private async void CreateSmallAnnotationFromSelection()
        {
            var selected = await GetCurrentSelectionAnchorAsync();
            if (selected is null || activeAnnotationStore is not { } store
                || activeNovelProject is not { } project)
            {
                return;
            }

            try
            {
                SmallAnnotationEditValues? values = await ShowSmallAnnotationEditorAsync(
                    "新建小批注", null, "", null, selected.Value.Quote);
                if (values is null
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                NovelChapter? chapter = await store.GetChapterAtOffsetAsync(selected.Value.StartOffset);
                Guid annotationId = await store.CreateAnnotationWithAnchorAsync(
                    values.Content,
                    selected.Value.StartOffset,
                    selected.Value.EndOffset,
                    selected.Value.Quote,
                    categoryId: values.CategoryId,
                    title: values.Title,
                    chapterId: chapter?.Id,
                    documentRevision: project.Metadata.DocumentRevision);

                SmallAnnotationsItem.IsChecked = true;
                SmallAnnotationPane.Visibility = Visibility.Visible;
                selectedSmallAnnotationCategoryId = values.CategoryId;
                await ReloadSmallAnnotationCategoriesAsync(values.CategoryId);
                await RefreshSmallAnnotationsAsync(annotationId);
                await RefreshVisibleSmallAnnotationMarkersAsync(force: true);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("创建小批注失败", ex.Message);
            }
        }

        private async void AddSelectionToExistingSmallAnnotation()
        {
            var selected = await GetCurrentSelectionAnchorAsync();
            if (selected is null || activeAnnotationStore is not { } store
                || activeNovelProject is not { } project)
            {
                return;
            }

            try
            {
                NovelAnnotationSummary? target = await SelectExistingSmallAnnotationAsync(store);
                if (target is null
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                if (await store.HasAnchorAsync(
                    target.Annotation.Id, selected.Value.StartOffset, selected.Value.EndOffset))
                {
                    await ShowMessageAsync("没有添加", "这段原文已经关联到该批注，无需重复添加。");
                    return;
                }

                NovelChapter? chapter = await store.GetChapterAtOffsetAsync(selected.Value.StartOffset);
                await store.AddAnchorAsync(
                    target.Annotation.Id,
                    selected.Value.StartOffset,
                    selected.Value.EndOffset,
                    selected.Value.Quote,
                    chapterId: chapter?.Id,
                    documentRevision: project.Metadata.DocumentRevision);

                SmallAnnotationsItem.IsChecked = true;
                SmallAnnotationPane.Visibility = Visibility.Visible;
                selectedSmallAnnotationCategoryId = target.Annotation.CategoryId;
                await ReloadSmallAnnotationCategoriesAsync(target.Annotation.CategoryId);
                await RefreshSmallAnnotationsAsync(target.Annotation.Id);
                await RefreshVisibleSmallAnnotationMarkersAsync(force: true);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("添加原文关联失败", ex.Message);
            }
        }

        private async Task<SmallAnnotationEditValues?> ShowSmallAnnotationEditorAsync(
            string dialogTitle,
            string? initialTitle,
            string initialContent,
            Guid? initialCategoryId,
            string? quotePreview,
            IReadOnlyList<AnnotationAnchor>? anchors = null,
            bool readOnlyInitially = false)
        {
            if (activeAnnotationStore is not { } store)
                return null;

            IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
            var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
            ContentDialog? dialog = null;

            void AddFieldHeader(string label, Button? penButton)
            {
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (penButton is not null)
                {
                    Grid.SetColumn(penButton, 1);
                    header.Children.Add(penButton);
                }
                panel.Children.Add(header);
            }

            Button CreatePenButton(Action enableField)
            {
                var button = new Button
                {
                    Content = new SymbolIcon(Symbol.Edit),
                    Padding = new Thickness(6, 2, 6, 2),
                    MinWidth = 32,
                    MinHeight = 28,
                };
                button.Click += (_, _) =>
                {
                    enableField();
                    button.Visibility = Visibility.Collapsed;
                    if (dialog is not null)
                        dialog.IsPrimaryButtonEnabled = true;
                };
                return button;
            }

            var titleBox = new TextBox
            {
                PlaceholderText = "为这条批注起一个简短标题",
                Text = initialTitle ?? "",
                IsReadOnly = readOnlyInitially,
            };
            AddFieldHeader("标题（可选）",
                readOnlyInitially ? CreatePenButton(() =>
                {
                    titleBox.IsReadOnly = false;
                    titleBox.Focus(FocusState.Programmatic);
                }) : null);
            panel.Children.Add(titleBox);

            var categoryBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = !readOnlyInitially,
            };
            categoryBox.Items.Add(new ComboBoxItem { Content = "未分类" });
            foreach (AnnotationCategory category in categories)
                categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });

            categoryBox.SelectedIndex = 0;
            if (initialCategoryId is Guid selectedCategoryId)
            {
                for (int i = 0; i < categoryBox.Items.Count; i++)
                {
                    if (categoryBox.Items[i] is ComboBoxItem { Tag: Guid matchedCategoryId }
                        && matchedCategoryId == selectedCategoryId)
                    {
                        categoryBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            AddFieldHeader("分类",
                readOnlyInitially ? CreatePenButton(() =>
                {
                    categoryBox.IsEnabled = true;
                    categoryBox.Focus(FocusState.Programmatic);
                }) : null);
            panel.Children.Add(categoryBox);

            var contentBox = new TextBox
            {
                Text = initialContent,
                PlaceholderText = "记录你的观察、分析或写作素材……",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 150,
                Height = 190,
                MaxHeight = 260,
                IsReadOnly = readOnlyInitially,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(contentBox, ScrollBarVisibility.Auto);
            AddFieldHeader("批注内容",
                readOnlyInitially ? CreatePenButton(() =>
                {
                    contentBox.IsReadOnly = false;
                    contentBox.Focus(FocusState.Programmatic);
                }) : null);
            panel.Children.Add(contentBox);

            if (quotePreview is not null)
            {
                panel.Children.Add(new TextBlock { Text = "关联原文（只读）" });
                panel.Children.Add(new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = quotePreview,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                    Height = 180,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                });
            }

            if (anchors is not null)
            {
                panel.Children.Add(new TextBlock { Text = $"关联原文（{anchors.Count} 处）" });
                var anchorPanel = new StackPanel { Spacing = 4 };
                NovelProject? project = activeNovelProject;
                int occurrence = 0;
                foreach (AnnotationAnchor anchor in anchors)
                {
                    occurrence++;
                    bool eligible = !anchor.IsOrphaned;
                    bool versionMatches = project is not null
                        && anchor.DocumentRevision == project.Metadata.DocumentRevision;
                    var anchorButton = new Button
                    {
                        IsEnabled = eligible,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(6, 4, 6, 4),
                        Content = new TextBlock
                        {
                            Text = $"原文 {occurrence}：{CreateSmallAnnotationExcerpt(anchor.Quote, 220)}"
                                + (anchor.IsOrphaned ? "（已失效）" : versionMatches ? "" : "（位置待校验，点击尝试修复）"),
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                        },
                    };
                    anchorButton.Click += async (_, _) =>
                    {
                        await NavigateToSmallAnnotationAnchorAsync(anchor);
                    };
                    anchorPanel.Children.Add(anchorButton);
                }

                if (anchors.Count == 0)
                {
                    anchorPanel.Children.Add(new TextBlock
                    {
                        Text = "这条批注目前没有关联原文位置。",
                        Opacity = 0.7,
                    });
                }

                panel.Children.Add(new ScrollViewer
                {
                    Content = anchorPanel,
                    Height = 180,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                });
            }

            var validationText = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(validationText);

            dialog = new ContentDialog
            {
                Title = dialogTitle,
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 620,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
                PrimaryButtonText = "保存",
                CloseButtonText = readOnlyInitially ? "关闭" : "取消",
                DefaultButton = readOnlyInitially ? ContentDialogButton.Close : ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = !readOnlyInitially,
                XamlRoot = Content.XamlRoot,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(contentBox.Text))
                {
                    args.Cancel = true;
                    validationText.Text = "批注内容不能为空。";
                    contentBox.Focus(FocusState.Programmatic);
                }
                else
                {
                    validationText.Text = "";
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;

            Guid? categoryId = categoryBox.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
            string? title = string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text.Trim();
            return new SmallAnnotationEditValues(title, contentBox.Text, categoryId);
        }

        private async Task<NovelAnnotationSummary?> SelectExistingSmallAnnotationAsync(NovelAnnotationStore store)
        {
            IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
            Dictionary<Guid, string> categoryNames = categories.ToDictionary(
                category => category.Id, category => category.Name);

            var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
            var searchBox = new TextBox { PlaceholderText = "搜索批注标题或内容……" };
            var resultCount = new TextBlock { Text = "正在加载……", FontSize = 12, Opacity = 0.7 };
            var list = new ListView
            {
                Height = 320,
                SelectionMode = ListViewSelectionMode.Single,
            };
            panel.Children.Add(searchBox);
            panel.Children.Add(resultCount);
            panel.Children.Add(list);

            var dialog = new ContentDialog
            {
                Title = "添加到现有小批注",
                Content = panel,
                PrimaryButtonText = "添加关联",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Content.XamlRoot,
            };

            int requestVersion = 0;
            async Task RefreshChoicesAsync()
            {
                int currentVersion = ++requestVersion;
                IReadOnlyList<NovelAnnotationSummary> summaries =
                    await store.GetAnnotationSummariesAsync(searchText: searchBox.Text);
                if (currentVersion != requestVersion || !ReferenceEquals(store, activeAnnotationStore))
                    return;

                list.Items.Clear();
                foreach (NovelAnnotationSummary summary in summaries)
                    list.Items.Add(BuildSmallAnnotationListItem(summary, categoryNames));

                resultCount.Text = summaries.Count == 0
                    ? "没有找到匹配的批注。"
                    : $"找到 {summaries.Count} 条批注，请选择要追加原文的位置。";
                dialog.IsPrimaryButtonEnabled = false;
            }

            searchBox.TextChanged += async (_, _) => await RefreshChoicesAsync();
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled =
                    list.SelectedItem is ListViewItem { Tag: NovelAnnotationSummary };
            };

            await RefreshChoicesAsync();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;

            return list.SelectedItem is ListViewItem { Tag: NovelAnnotationSummary summary }
                ? summary
                : null;
        }

        private async Task ShowSmallAnnotationDetailsAsync(NovelAnnotationSummary summary)
        {
            NovelAnnotationStore? store = activeAnnotationStore;
            NovelProject? project = activeNovelProject;
            if (store is null || project is null)
                return;

            try
            {
                IReadOnlyList<AnnotationAnchor> anchors =
                    await store.GetAnchorsForAnnotationAsync(summary.Annotation.Id);
                if (!ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                SmallAnnotationEditValues? values = await ShowSmallAnnotationEditorAsync(
                    "小批注",
                    summary.Annotation.Title,
                    summary.Annotation.Content,
                    summary.Annotation.CategoryId,
                    quotePreview: null,
                    anchors: anchors,
                    readOnlyInitially: true);

                if (values is null
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                await store.UpdateAnnotationAsync(
                    summary.Annotation.Id, values.Content, values.CategoryId, values.Title);
                selectedSmallAnnotationCategoryId = values.CategoryId;
                await ReloadSmallAnnotationCategoriesAsync(values.CategoryId);
                await RefreshSmallAnnotationsAsync(summary.Annotation.Id);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("无法打开或保存小批注", ex.Message);
            }
        }

        private async Task NavigateToSmallAnnotationAnchorAsync(AnnotationAnchor anchor)
        {
            NovelProject? project = activeNovelProject;
            NovelAnnotationStore? store = activeAnnotationStore;
            if (project is null || store is null
                || annotationOffsetMap is null || provider is null)
            {
                await ShowMessageAsync("无法定位原文", "当前没有打开对应的小说项目。");
                return;
            }

            if (anchor.IsOrphaned)
            {
                await ShowMessageAsync("无法定位原文", "这条原文关联已经失效，不能直接跳转。");
                return;
            }

            if (filteredSource is not null)
                ExitFilterToFullView();

            bool refreshAnnotationUi = false;
            bool anchorResolved = TryResolveSmallAnnotationAnchor(anchor, out TextPosition position);
            if (!anchorResolved && !LineIndexer.IsCompleted)
            {
                // A project reopened from disk may not have indexed the saved anchor yet. Resume a stopped
                // indexer if necessary, then wait asynchronously rather than resolving to the current frontier.
                if (StartIndexingItem.IsEnabled)
                {
                    indexRefreshTimer.Start();
                    StartIndexing();
                }

                await indexingTask;
                if (!ReferenceEquals(project, activeNovelProject)
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || provider is null || annotationOffsetMap is null)
                    return;

                View.UpdateTotalLines(provider.LineCount);
                SyncScrollBars();
                anchorResolved = TryResolveSmallAnnotationAnchor(anchor, out position);
            }

            // A document edit can leave an anchor's quote at exactly the same offset. In that case
            // the range is already verified; advance only its revision instead of leaving it stale
            // (which would hide its marker and disable its source button indefinitely).
            if (anchorResolved && anchor.DocumentRevision != project.Metadata.DocumentRevision)
            {
                Guid? chapterId = null;
                NovelChapterIndexState? indexState = await store.GetChapterIndexStateAsync();
                if (indexState is { } currentIndex
                    && currentIndex.DocumentRevision == project.Metadata.DocumentRevision
                    && currentIndex.ParserVersion == NovelChapterIndexBuilder.CurrentParserVersion)
                {
                    chapterId = (await store.GetChapterAtOffsetAsync(anchor.StartOffset))?.Id;
                }

                bool revisionUpdated = await store.TryRepairAnchorAsync(
                    anchor,
                    anchor.StartOffset,
                    anchor.EndOffset,
                    chapterId,
                    project.Metadata.DocumentRevision);
                if (!revisionUpdated
                    || !ReferenceEquals(project, activeNovelProject)
                    || !ReferenceEquals(store, activeAnnotationStore))
                {
                    await ShowMessageAsync(
                        "批注位置需要刷新",
                        "原文位置已核对，但批注记录在此期间发生变化。请刷新批注列表后重新尝试。");
                    return;
                }

                anchor = anchor with
                {
                    ChapterId = chapterId,
                    DocumentRevision = project.Metadata.DocumentRevision,
                };
                refreshAnnotationUi = true;
                LblStatus.Text = "已核对原文位置，并同步小批注的文档版本。";
            }

            if (!anchorResolved)
            {
                // A small edit before the quote changes its absolute offset. External editors may
                // also normalize mixed/LF line endings to CRLF, shifting every later offset. Auto-repair
                // only when the normalized saved excerpt occurs exactly once in the complete document.
                AnnotationAnchor? repaired = await TryRepairSmallAnnotationAnchorAsync(anchor, project, store);
                if (repaired is not null
                    && ReferenceEquals(project, activeNovelProject)
                    && ReferenceEquals(store, activeAnnotationStore)
                    && TryResolveSmallAnnotationAnchor(repaired, out position))
                {
                    anchor = repaired;
                    refreshAnnotationUi = true;
                    LblStatus.Text = "已根据唯一匹配的原文摘录修复一处小批注位置。";
                }
                else
                {
                    LblStatus.Text = "原文摘录没有找到唯一匹配，未自动修改批注关联。";
                    string reason = !LineIndexer.IsCompleted
                        ? "全文索引尚未到达该位置。请稍后等待索引完成，再重新尝试定位。"
                        : anchor.ChapterId is not null
                            ? "这条小批注已经绑定具体章节，但该章节索引可能已过期/归档，或原文摘录在本章内存在多个匹配。为避免跳到其他章节中相同的文字，系统不会跨章节猜测位置。请先重新识别章节；如果本章内摘录重复，还需要通过上下文确认。"
                            : "这条旧版小批注没有章节关联。系统已忽略 CRLF/LF 换行差异并进行全文查找，但没有找到唯一且可验证的位置，因此没有自动修改关联。若摘录本身已被改写或在全文中重复出现，需要人工处理。";
                    await ShowMessageAsync("无法准确定位原文", reason);
                    return;
                }
            }

            // Navigate as soon as the anchor is verified. Rebuilding the full annotation list
            // and refreshing visible markers are secondary UI work and must not delay the jump.
            View.GoToLineColumn(position.Line, position.Column);
            View.CenterCaretVertically();
            SyncScrollBars();
            View.FocusCanvas();

            if (refreshAnnotationUi)
            {
                _ = RefreshSmallAnnotationsAsync(anchor.AnnotationId);
                _ = RefreshVisibleSmallAnnotationMarkersAsync(force: true);
            }
        }

        private async Task<AnnotationAnchor?> TryRepairSmallAnnotationAnchorAsync(
            AnnotationAnchor anchor,
            NovelProject project,
            NovelAnnotationStore store)
        {
            LineProvider? lines = provider;
            DocumentTextOffsetMap? map = annotationOffsetMap;
            if (lines is null || map is null || lines.LineCount == 0
                || string.IsNullOrEmpty(anchor.Quote) || anchor.IsOrphaned)
            {
                return null;
            }

            try
            {
                AnnotationAnchorQuoteMatch? match;
                Guid? repairedChapterId = anchor.ChapterId;
                if (anchor.ChapterId is Guid savedChapterId)
                {
                    // A chapter-bound anchor must never be relocated by matching text in another chapter.
                    // Use its saved chapter range only when that range has been updated to the current revision.
                    NovelChapter? chapter = await store.GetChapterByIdAsync(savedChapterId);
                    NovelChapterIndexState? indexState = await store.GetChapterIndexStateAsync();
                    if (chapter is null || !chapter.IsActive
                        || chapter.DocumentRevision != project.Metadata.DocumentRevision
                        || (indexState is { } savedIndex
                            && savedIndex.ParserVersion != NovelChapterIndexBuilder.CurrentParserVersion))
                    {
                        return null;
                    }

                    LblStatus.Text = "正在对应章节内查找原文摘录...";
                    match = await Task.Run(() =>
                        AnnotationAnchorQuoteLocator.FindUniqueMatchInRange(
                            lines, anchor.Quote, map, chapter.StartOffset, chapter.EndOffset));
                }
                else
                {
                    // Legacy anchors without a chapter association need one full-document lookup.
                    // Once matched, they are assigned to a chapter so later repairs stay chapter-scoped.
                    LblStatus.Text = "正在全文查找旧版小批注摘录...";
                    match = await Task.Run(
                        () => AnnotationAnchorQuoteLocator.FindUniqueMatch(lines, anchor.Quote));
                }

                if (match is null
                    || !ReferenceEquals(project, activeNovelProject)
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(lines, provider)
                    || !ReferenceEquals(map, annotationOffsetMap))
                {
                    return null;
                }

                long repairedStart = match.Value.StartOffset;
                long repairedEnd = match.Value.EndOffset;
                var (startLine, startColumn) = map.GetPosition(repairedStart);
                var (endLine, endColumn) = map.GetPosition(repairedEnd);
                string currentQuote = ExtractSmallAnnotationRange(
                    lines,
                    new TextPosition(startLine, startColumn),
                    new TextPosition(endLine, endColumn));
                if (!string.Equals(
                    AnnotationAnchorQuoteLocator.NormalizeLineEndings(currentQuote),
                    AnnotationAnchorQuoteLocator.NormalizeLineEndings(anchor.Quote),
                    StringComparison.Ordinal))
                {
                    return null;
                }

                if (repairedChapterId is null)
                {
                    NovelChapterIndexState? indexState = await store.GetChapterIndexStateAsync();
                    if (indexState is { ParserVersion: NovelChapterIndexBuilder.CurrentParserVersion })
                    {
                        NovelChapter? matchedChapter = await store.GetChapterAtOffsetAsync(repairedStart);
                        if (matchedChapter is { } currentChapter
                            && currentChapter.DocumentRevision == project.Metadata.DocumentRevision)
                        {
                            repairedChapterId = currentChapter.Id;
                        }
                    }
                }

                bool updated = await store.TryRepairAnchorAsync(
                    anchor,
                    repairedStart,
                    repairedEnd,
                    repairedChapterId,
                    project.Metadata.DocumentRevision);
                if (!updated)
                    return null;

                return anchor with
                {
                    StartOffset = repairedStart,
                    EndOffset = repairedEnd,
                    ChapterId = repairedChapterId,
                    DocumentRevision = project.Metadata.DocumentRevision,
                    IsOrphaned = false,
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostics.LogSwallowed("Repair small annotation anchor", ex);
                return null;
            }
        }

        private bool TryResolveSmallAnnotationAnchor(AnnotationAnchor anchor, out TextPosition position)
        {
            position = default;
            DocumentTextOffsetMap? map = annotationOffsetMap;
            LineProvider? currentProvider = provider;
            if (map is null || currentProvider is null || currentProvider.LineCount == 0)
                return false;

            try
            {
                (int Line, int Column) start = map.GetPosition(anchor.StartOffset);
                (int Line, int Column) end = map.GetPosition(anchor.EndOffset);
                if (start.Line < 0 || end.Line < start.Line
                    || start.Line >= currentProvider.LineCount || end.Line >= currentProvider.LineCount)
                {
                    return false;
                }

                if (map.GetOffset(start.Line, start.Column) != anchor.StartOffset
                    || map.GetOffset(end.Line, end.Column) != anchor.EndOffset)
                {
                    return false;
                }

                string currentQuote = ExtractSmallAnnotationRange(
                    currentProvider, new TextPosition(start.Line, start.Column),
                    new TextPosition(end.Line, end.Column));
                if (!string.Equals(
                    AnnotationAnchorQuoteLocator.NormalizeLineEndings(currentQuote),
                    AnnotationAnchorQuoteLocator.NormalizeLineEndings(anchor.Quote),
                    StringComparison.Ordinal))
                {
                    return false;
                }

                position = new TextPosition(start.Line, start.Column);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static string ExtractSmallAnnotationRange(
            LineProvider lines, TextPosition start, TextPosition end)
        {
            if (end < start)
                return string.Empty;

            var builder = new StringBuilder();
            for (int lineIndex = start.Line; lineIndex <= end.Line; lineIndex++)
            {
                string line = lines.GetLineUncached(lineIndex);
                int from = lineIndex == start.Line ? Math.Clamp(start.Column, 0, line.Length) : 0;
                int to = lineIndex == end.Line
                    ? Math.Clamp(end.Column, from, line.Length)
                    : line.Length;
                builder.Append(line.AsSpan(from, to - from));

                if (lineIndex < end.Line)
                {
                    builder.Append(lines.GetLineEnding(lineIndex) switch
                    {
                        LineEnding.CrLf => "\r\n",
                        LineEnding.Lf => "\n",
                        _ => "\n",
                    });
                }
            }

            return builder.ToString();
        }

        private async void DeleteSelectedSmallAnnotation_Click(object sender, RoutedEventArgs e)
        {
            NovelAnnotationSummary? summary = GetSelectedSmallAnnotationSummary();
            if (summary is null || activeAnnotationStore is not { } store)
                return;

            var dialog = new ContentDialog
            {
                Title = "删除小批注",
                Content = $"确定删除“{(string.IsNullOrWhiteSpace(summary.Annotation.Title) ? "未命名批注" : summary.Annotation.Title)}”吗？\n\n"
                    + $"这将同时删除它关联的 {summary.AnchorCount} 处原文位置，且无法撤销。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || !ReferenceEquals(store, activeAnnotationStore))
            {
                return;
            }

            try
            {
                await store.DeleteAnnotationAsync(summary.Annotation.Id);
                await RefreshSmallAnnotationsAsync();
                await RefreshVisibleSmallAnnotationMarkersAsync(force: true);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("删除小批注失败", ex.Message);
            }
        }

        private async void ManageSmallAnnotationCategories_Click(object sender, RoutedEventArgs e)
        {
            await ShowSmallAnnotationCategoryManagerAsync();
        }

        private async Task ShowSmallAnnotationCategoryManagerAsync()
        {
            if (activeAnnotationStore is not { } store)
                return;

            var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
            var categoryList = new ListView
            {
                Height = 170,
                SelectionMode = ListViewSelectionMode.Single,
            };
            var nameBox = new TextBox { PlaceholderText = "输入分类名称（用于新建或重命名）" };
            var targetCategoryBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "选择合并后的目标分类",
            };
            var confirmDelete = new CheckBox
            {
                Content = "确认删除所选分类（批注会保留并变为未分类）",
            };
            var confirmMerge = new CheckBox
            {
                Content = "确认将所选分类合并到目标分类",
            };
            var errorText = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            };

            var addButton = new Button { Content = "新建分类" };
            var renameButton = new Button { Content = "重命名" };
            var deleteButton = new Button { Content = "删除分类", IsEnabled = false };
            var mergeButton = new Button { Content = "合并分类", IsEnabled = false };

            var nameButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nameButtons.Children.Add(addButton);
            nameButtons.Children.Add(renameButton);
            var mergeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            mergeButtons.Children.Add(deleteButton);
            mergeButtons.Children.Add(mergeButton);

            panel.Children.Add(new TextBlock { Text = "现有分类" });
            panel.Children.Add(categoryList);
            panel.Children.Add(nameBox);
            panel.Children.Add(nameButtons);
            panel.Children.Add(targetCategoryBox);
            panel.Children.Add(confirmDelete);
            panel.Children.Add(confirmMerge);
            panel.Children.Add(mergeButtons);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "小批注分类管理",
                Content = panel,
                CloseButtonText = "完成",
                XamlRoot = Content.XamlRoot,
            };

            AnnotationCategory? GetSelectedCategory()
            {
                return categoryList.SelectedItem is ListViewItem { Tag: AnnotationCategory category }
                    ? category
                    : null;
            }

            Guid? GetTargetCategoryId()
            {
                return targetCategoryBox.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
            }

            void UpdateButtons()
            {
                AnnotationCategory? selected = GetSelectedCategory();
                bool hasName = !string.IsNullOrWhiteSpace(nameBox.Text);
                addButton.IsEnabled = hasName;
                renameButton.IsEnabled = selected is not null && hasName;
                deleteButton.IsEnabled = selected is not null && confirmDelete.IsChecked == true;
                mergeButton.IsEnabled = selected is not null
                    && GetTargetCategoryId() is Guid targetId
                    && targetId != selected.Id
                    && confirmMerge.IsChecked == true;
            }

            void RebuildTargetCategories(IReadOnlyList<AnnotationCategory> categories)
            {
                targetCategoryBox.Items.Clear();
                AnnotationCategory? selected = GetSelectedCategory();
                foreach (AnnotationCategory category in categories)
                {
                    if (selected?.Id == category.Id)
                        continue;

                    targetCategoryBox.Items.Add(new ComboBoxItem
                    {
                        Content = category.Name,
                        Tag = category.Id,
                    });
                }

                targetCategoryBox.SelectedIndex = targetCategoryBox.Items.Count > 0 ? 0 : -1;
            }

            async Task ReloadCategoriesAsync(Guid? preferredCategoryId = null)
            {
                IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
                if (!ReferenceEquals(store, activeAnnotationStore))
                    return;

                categoryList.Items.Clear();
                foreach (AnnotationCategory category in categories)
                {
                    categoryList.Items.Add(new ListViewItem
                    {
                        Content = category.Name,
                        Tag = category,
                    });
                }

                ListViewItem? selectedItem = null;
                foreach (object item in categoryList.Items)
                {
                    if (item is ListViewItem { Tag: AnnotationCategory category }
                        && category.Id == preferredCategoryId)
                    {
                        selectedItem = (ListViewItem)item;
                        break;
                    }
                }

                categoryList.SelectedItem = selectedItem
                    ?? (categoryList.Items.Count > 0 ? categoryList.Items[0] : null);

                AnnotationCategory? selected = GetSelectedCategory();
                nameBox.Text = selected?.Name ?? "";
                RebuildTargetCategories(categories);
                confirmDelete.IsChecked = false;
                confirmMerge.IsChecked = false;
                UpdateButtons();
            }

            categoryList.SelectionChanged += async (_, _) =>
            {
                IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
                AnnotationCategory? selected = GetSelectedCategory();
                nameBox.Text = selected?.Name ?? "";
                RebuildTargetCategories(categories);
                UpdateButtons();
            };
            targetCategoryBox.SelectionChanged += (_, _) => UpdateButtons();
            nameBox.TextChanged += (_, _) => UpdateButtons();
            confirmDelete.Checked += (_, _) => UpdateButtons();
            confirmDelete.Unchecked += (_, _) => UpdateButtons();
            confirmMerge.Checked += (_, _) => UpdateButtons();
            confirmMerge.Unchecked += (_, _) => UpdateButtons();

            addButton.Click += async (_, _) =>
            {
                string name = nameBox.Text.Trim();
                if (name.Length == 0)
                    return;

                try
                {
                    Guid id = await store.CreateCategoryAsync(name);
                    errorText.Text = "";
                    nameBox.Text = "";
                    await ReloadCategoriesAsync(id);
                }
                catch (Exception ex)
                {
                    errorText.Text = ex.Message;
                }
            };

            renameButton.Click += async (_, _) =>
            {
                AnnotationCategory? selected = GetSelectedCategory();
                string name = nameBox.Text.Trim();
                if (selected is null || name.Length == 0)
                    return;

                try
                {
                    await store.UpdateCategoryAsync(selected.Id, name, selected.Color, selected.SortOrder);
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
                AnnotationCategory? selected = GetSelectedCategory();
                if (selected is null || confirmDelete.IsChecked != true)
                    return;

                try
                {
                    await store.DeleteCategoryAsync(selected.Id);
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
                AnnotationCategory? selected = GetSelectedCategory();
                Guid? targetId = GetTargetCategoryId();
                if (selected is null || targetId is null || confirmMerge.IsChecked != true)
                    return;

                try
                {
                    await store.MergeCategoriesAsync(selected.Id, targetId.Value);
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
                await ReloadSmallAnnotationCategoriesAsync(selectedSmallAnnotationCategoryId);
                await RefreshSmallAnnotationsAsync();
            }
        }
    }
}
