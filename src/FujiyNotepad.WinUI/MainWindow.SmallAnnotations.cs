using System.Text;
using FujiyNotepad.Core;
using FujiyNotepad.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FujiyNotepad.WinUI
{
    public sealed partial class MainWindow
    {
        private int smallAnnotationRefreshVersion;
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
            if (show)
            {
                await ReloadSmallAnnotationCategoriesAsync();
                await RefreshSmallAnnotationsAsync();
            }
        }

        private void CloseSmallAnnotations_Click(object sender, RoutedEventArgs e)
        {
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

        // A single click on a note navigates to its first valid source occurrence. The full note and
        // all occurrences remain available from the separate Details action.
        private async void SmallAnnotationList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ListViewItem { Tag: NovelAnnotationSummary summary })
                await NavigateToFirstSmallAnnotationAnchorAsync(summary);
        }

        private async void ShowSelectedSmallAnnotationDetails_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedSmallAnnotationSummary() is { } summary)
                await ShowSmallAnnotationDetailsAsync(summary);
        }

        private void UpdateSmallAnnotationActionButtons()
        {
            bool hasSelection = GetSelectedSmallAnnotationSummary() is not null;
            ShowSmallAnnotationDetailsButton.IsEnabled = hasSelection;
            EditSmallAnnotationButton.IsEnabled = hasSelection;
            DeleteSmallAnnotationButton.IsEnabled = hasSelection;
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

                refreshingSmallAnnotationUi = true;
                try
                {
                    SmallAnnotationList.Items.Clear();
                    ListViewItem? itemToSelect = null;
                    foreach (NovelAnnotationSummary summary in summaries)
                    {
                        ListViewItem item = BuildSmallAnnotationListItem(summary, smallAnnotationCategoryNames);
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

        private static ListViewItem BuildSmallAnnotationListItem(
            NovelAnnotationSummary summary, IReadOnlyDictionary<Guid, string> categoryNames)
        {
            NovelAnnotation annotation = summary.Annotation;
            string title = string.IsNullOrWhiteSpace(annotation.Title) ? "（未命名批注）" : annotation.Title;
            string category = annotation.CategoryId is Guid categoryId
                && categoryNames.TryGetValue(categoryId, out string? categoryName)
                    ? categoryName
                    : "未分类";
            int activeAnchors = Math.Max(0, summary.AnchorCount - summary.OrphanedAnchorCount);

            var content = new StackPanel { Spacing = 3 };
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
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(summary.FirstActiveAnchorQuote)
                    ? "原文：暂无有效关联"
                    : $"原文：{CreateSmallAnnotationExcerpt(summary.FirstActiveAnchorQuote, 72)}",
                FontSize = 11,
                Opacity = 0.82,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{category}  ·  有效位置 {activeAnchors}/{summary.AnchorCount}"
                    + (summary.OrphanedAnchorCount > 0 ? $"  ·  失效 {summary.OrphanedAnchorCount}" : "")
                    + $"  ·  {annotation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                FontSize = 11,
                Opacity = 0.68,
                TextWrapping = TextWrapping.Wrap,
            });

            return new ListViewItem
            {
                Tag = summary,
                Content = content,
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
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

                AnnotationAnchor? first = anchors.FirstOrDefault(anchor =>
                    !anchor.IsOrphaned
                    && anchor.DocumentRevision == project.Metadata.DocumentRevision);

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
            int requestVersion = ++annotationMarkerRefreshVersion;

            try
            {
                long startOffset = map.GetLineStartOffset(firstLine);
                long endOffset = lastLine + 1 < lines.LineCount
                    ? map.GetLineStartOffset(lastLine + 1)
                    : map.GetOffset(lastLine, lines.GetLineUncached(lastLine).Length);
                if (endOffset <= startOffset)
                    endOffset = checked(startOffset + 1);

                IReadOnlyList<AnnotationAnchor> anchors =
                    await store.GetAnchorsIntersectingRangeAsync(startOffset, endOffset);

                if (requestVersion != annotationMarkerRefreshVersion
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject)
                    || !ReferenceEquals(lines, provider)
                    || filteredSource is not null)
                {
                    return;
                }

                var markers = new List<TextAnnotationMarker>();
                foreach (AnnotationAnchor anchor in anchors)
                {
                    if (anchor.IsOrphaned
                        || anchor.DocumentRevision != project.Metadata.DocumentRevision)
                    {
                        continue;
                    }

                    (int Line, int Column) start = map.GetPosition(anchor.StartOffset);
                    (int Line, int Column) end = map.GetPosition(anchor.EndOffset);
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
                            markers.Add(new TextAnnotationMarker(lineIndex, startColumn, endColumn));
                    }
                }

                View.SetAnnotationMarkers(markers);
            }
            catch (Exception)
            {
                // A transient index/database read failure must not interrupt reading or clear the last
                // successfully-rendered marker set. A later viewport change retries the query.
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
                IReadOnlyList<AnnotationAnchor> anchors = await store.GetAnchorsAtOffsetAsync(offset);
                if (anchors.Count == 0 && position.Column > 0)
                {
                    offset = map.GetOffset(position.Line, position.Column - 1);
                    anchors = await store.GetAnchorsAtOffsetAsync(offset);
                }

                anchors = anchors.Where(anchor =>
                    !anchor.IsOrphaned
                    && anchor.DocumentRevision == project.Metadata.DocumentRevision).ToList();
                if (anchors.Count == 0
                    || !ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                HashSet<Guid> annotationIds = anchors.Select(anchor => anchor.AnnotationId).ToHashSet();
                IReadOnlyList<NovelAnnotation> notes =
                    await store.GetAnnotationsIntersectingRangeAsync(offset, checked(offset + 1));
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
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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

                content.Children.Add(new Divider());
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

                Guid annotationId = await store.CreateAnnotationWithAnchorAsync(
                    values.Content,
                    selected.Value.StartOffset,
                    selected.Value.EndOffset,
                    selected.Value.Quote,
                    categoryId: values.CategoryId,
                    title: values.Title,
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

                await store.AddAnchorAsync(
                    target.Annotation.Id,
                    selected.Value.StartOffset,
                    selected.Value.EndOffset,
                    selected.Value.Quote,
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
            string? quotePreview)
        {
            if (activeAnnotationStore is not { } store)
                return null;

            IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
            var panel = new StackPanel { Spacing = 8, MinWidth = 420 };

            panel.Children.Add(new TextBlock { Text = "标题（可选）" });
            var titleBox = new TextBox
            {
                PlaceholderText = "为这条批注起一个简短标题",
                Text = initialTitle ?? "",
            };
            panel.Children.Add(titleBox);

            panel.Children.Add(new TextBlock { Text = "分类" });
            var categoryBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            categoryBox.Items.Add(new ComboBoxItem { Content = "未分类" });
            foreach (AnnotationCategory category in categories)
            {
                categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
            }

            categoryBox.SelectedIndex = 0;
            if (initialCategoryId is Guid selectedCategoryId)
            {
                for (int i = 0; i < categoryBox.Items.Count; i++)
                {
                    if (categoryBox.Items[i] is ComboBoxItem { Tag: Guid matchedCategoryId } && matchedCategoryId == selectedCategoryId)
                    {
                        categoryBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            panel.Children.Add(categoryBox);

            panel.Children.Add(new TextBlock { Text = "批注内容" });
            var contentBox = new TextBox
            {
                Text = initialContent,
                PlaceholderText = "记录你的观察、分析或写作素材……",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 150,
                MaxHeight = 300,
            };
            panel.Children.Add(contentBox);

            if (quotePreview is not null)
            {
                panel.Children.Add(new TextBlock { Text = "关联原文（只读）" });
                var quoteText = new TextBlock
                {
                    Text = quotePreview,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 3,
                    IsTextSelectionEnabled = true,
                };
                var quoteViewer = new ScrollViewer
                {
                    Content = quoteText,
                    MaxHeight = 100,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                panel.Children.Add(quoteViewer);

                if (quotePreview.Length > 60)
                {
                    bool expanded = false;
                    var expandQuoteButton = new Button
                    {
                        Content = "展开全部原文",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Padding = new Thickness(8, 3, 8, 3),
                    };
                    expandQuoteButton.Click += (_, _) =>
                    {
                        expanded = !expanded;
                        quoteText.MaxLines = expanded ? 0 : 3;
                        quoteText.TextTrimming = expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                        quoteViewer.MaxHeight = expanded ? 240 : 100;
                        quoteViewer.VerticalScrollBarVisibility = expanded
                            ? ScrollBarVisibility.Auto
                            : ScrollBarVisibility.Disabled;
                        expandQuoteButton.Content = expanded ? "收起原文" : "展开全部原文";
                    };
                    panel.Children.Add(expandQuoteButton);
                }
            }

            var validationText = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(validationText);

            var dialog = new ContentDialog
            {
                Title = dialogTitle,
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
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
                IReadOnlyList<AnnotationCategory> categories = await store.GetCategoriesAsync();
                if (!ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                {
                    return;
                }

                string categoryName = summary.Annotation.CategoryId is Guid categoryId
                    ? categories.FirstOrDefault(category => category.Id == categoryId)?.Name ?? "未分类"
                    : "未分类";

                var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
                panel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(summary.Annotation.Title)
                        ? "（未命名批注）"
                        : summary.Annotation.Title,
                    FontSize = 17,
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"{categoryName}  ·  创建于 {summary.Annotation.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                        + $"  ·  更新于 {summary.Annotation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = summary.Annotation.Content,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"关联原文（{anchors.Count} 处）",
                    FontSize = 14,
                });

                var anchorList = new ListView
                {
                    Height = 250,
                    SelectionMode = ListViewSelectionMode.Single,
                };
                int navigableCount = 0;
                foreach (AnnotationAnchor anchor in anchors)
                {
                    bool eligible = !anchor.IsOrphaned
                        && anchor.DocumentRevision == project.Metadata.DocumentRevision;
                    if (eligible)
                        navigableCount++;

                    string status = anchor.IsOrphaned
                        ? " · 已失效"
                        : anchor.DocumentRevision != project.Metadata.DocumentRevision
                            ? " · 文档版本不一致"
                            : " · 可定位";
                    var anchorContent = new StackPanel { Spacing = 2 };
                    anchorContent.Children.Add(new TextBlock
                    {
                        Text = $"偏移 {anchor.StartOffset:N0}{status}",
                        FontSize = 12,
                        Opacity = 0.75,
                    });
                    anchorContent.Children.Add(new TextBlock
                    {
                        Text = CreateSmallAnnotationExcerpt(anchor.Quote, 180),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    anchorList.Items.Add(new ListViewItem
                    {
                        Tag = anchor,
                        Content = anchorContent,
                        IsEnabled = eligible,
                        Padding = new Thickness(6, 4, 6, 4),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    });
                }

                if (anchors.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "这条批注目前没有关联原文位置。",
                        Opacity = 0.7,
                    });
                }
                else
                {
                    panel.Children.Add(anchorList);
                }

                var dialog = new ContentDialog
                {
                    Title = "小批注详情",
                    Content = panel,
                    PrimaryButtonText = navigableCount > 0 ? "定位到选中原文" : null,
                    CloseButtonText = "关闭",
                    DefaultButton = navigableCount > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
                    IsPrimaryButtonEnabled = false,
                    XamlRoot = Content.XamlRoot,
                };
                anchorList.SelectionChanged += (_, _) =>
                {
                    dialog.IsPrimaryButtonEnabled =
                        anchorList.SelectedItem is ListViewItem { IsEnabled: true, Tag: AnnotationAnchor };
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary
                    && anchorList.SelectedItem is ListViewItem { Tag: AnnotationAnchor selectedAnchor })
                {
                    await NavigateToSmallAnnotationAnchorAsync(selectedAnchor);
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("无法打开批注详情", ex.Message);
            }
        }

        private async Task NavigateToSmallAnnotationAnchorAsync(AnnotationAnchor anchor)
        {
            NovelProject? project = activeNovelProject;
            if (project is null || activeAnnotationStore is null
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

            if (anchor.DocumentRevision != project.Metadata.DocumentRevision)
            {
                await ShowMessageAsync(
                    "无法定位原文",
                    "这条关联属于其他文档版本。为避免跳转到错误位置，请先修复这条原文关联。");
                return;
            }

            if (filteredSource is not null)
                ExitFilterToFullView();

            if (!TryResolveSmallAnnotationAnchor(anchor, out TextPosition position)
                && !LineIndexer.IsCompleted)
            {
                // A project reopened from disk may not have indexed the saved anchor yet. Resume a stopped
                // indexer if necessary, then wait asynchronously rather than resolving to the current frontier.
                if (StartIndexingItem.IsEnabled)
                {
                    indexRefreshTimer.Start();
                    StartIndexing();
                }

                await indexingTask;
                if (!ReferenceEquals(project, activeNovelProject) || provider is null || annotationOffsetMap is null)
                    return;

                View.UpdateTotalLines(provider.LineCount);
                SyncScrollBars();
            }

            if (!TryResolveSmallAnnotationAnchor(anchor, out position))
            {
                string reason = LineIndexer.IsCompleted
                    ? "当前文档中的原文与保存的摘录不一致，或者文档已经发生变化。为避免错误定位，系统没有执行跳转。"
                    : "全文索引尚未到达该位置。请稍后等待索引完成，再重新尝试定位。";
                await ShowMessageAsync("无法准确定位原文", reason);
                return;
            }

            View.GoToLineColumn(position.Line, position.Column);
            View.FocusCanvas();
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
                if (!string.Equals(currentQuote, anchor.Quote, StringComparison.Ordinal))
                    return false;

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

        private async void EditSelectedSmallAnnotation_Click(object sender, RoutedEventArgs e)
        {
            NovelAnnotationSummary? summary = GetSelectedSmallAnnotationSummary();
            if (summary is null || activeAnnotationStore is not { } store)
                return;

            try
            {
                SmallAnnotationEditValues? values = await ShowSmallAnnotationEditorAsync(
                    "编辑小批注",
                    summary.Annotation.Title,
                    summary.Annotation.Content,
                    summary.Annotation.CategoryId,
                    quotePreview: null);
                if (values is null || !ReferenceEquals(store, activeAnnotationStore))
                    return;

                await store.UpdateAnnotationAsync(
                    summary.Annotation.Id,
                    values.Content,
                    values.CategoryId,
                    values.Title);
                selectedSmallAnnotationCategoryId = values.CategoryId;
                await ReloadSmallAnnotationCategoriesAsync(values.CategoryId);
                await RefreshSmallAnnotationsAsync(summary.Annotation.Id);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("编辑小批注失败", ex.Message);
            }
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
