using FujiyNotepad.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FujiyNotepad.WinUI;

public sealed partial class MainWindow
{
    private IReadOnlyList<NovelChapter> chapterDirectoryVisibleChapters = Array.Empty<NovelChapter>();

    private async void ChapterDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (BigAnnotationWorkspace.Visibility == Visibility.Visible)
        {
            if (!await TryLeaveBigAnnotationEditorAsync())
            {
                ChapterDirectoryItem.IsChecked = false;
                return;
            }
            HideBigAnnotationWorkspace();
        }

        if (activeAnnotationStore is null || activeNovelProject is null)
        {
            ChapterDirectoryItem.IsChecked = false;
            ChapterDirectoryPane.Visibility = Visibility.Collapsed;
            return;
        }

        bool show = ChapterDirectoryItem.IsChecked;
        ChapterDirectoryPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        CancelSmallAnnotationCardSingleTap();
        SmallAnnotationsItem.IsChecked = false;
        SmallAnnotationPane.Visibility = Visibility.Collapsed;
        ChapterAnnotationsItem.IsChecked = false;
        ChapterAnnotationPane.Visibility = Visibility.Collapsed;
        chapterAnnotationFilterDebounce?.Cancel();

        await LoadChapterDirectoryAsync();
    }

    private void CloseChapterDirectory_Click(object sender, RoutedEventArgs e)
    {
        ChapterDirectoryItem.IsChecked = false;
        ChapterDirectoryPane.Visibility = Visibility.Collapsed;
    }

    private async Task LoadChapterDirectoryAsync()
    {
        NovelAnnotationStore? store = activeAnnotationStore;
        NovelProject? project = activeNovelProject;
        if (store is null || project is null)
            return;

        ChapterDirectoryEmptyState.Text = "正在加载章节目录...";
        ChapterDirectoryEmptyState.Visibility = Visibility.Visible;
        try
        {
            IReadOnlyList<NovelChapter> chapters = await store.GetChaptersAsync();
            if (!ReferenceEquals(store, activeAnnotationStore)
                || !ReferenceEquals(project, activeNovelProject))
                return;

            chapterAnnotationChapters = chapters;
            if (chapters.Count == 0 && LineIndexer.IsCompleted)
            {
                await EnsureChapterIndexAsync();
                if (!ReferenceEquals(store, activeAnnotationStore)
                    || !ReferenceEquals(project, activeNovelProject))
                    return;
                chapterAnnotationChapters = await store.GetChaptersAsync();
            }

            RefreshChapterDirectoryList();
        }
        catch (Exception ex)
        {
            diagnostics.LogSwallowed("Load chapter directory", ex);
            ChapterDirectoryEmptyState.Text = $"加载章节目录失败：{ex.Message}";
            ChapterDirectoryEmptyState.Visibility = Visibility.Visible;
        }
    }

    private void ChapterDirectorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshChapterDirectoryList();
    }

    private async void ChapterDirectorySearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        e.Handled = true;
        if (chapterDirectoryVisibleChapters.Count > 0)
            await NavigateToChapterAsync(chapterDirectoryVisibleChapters[0]);
    }

    private async void ChapterDirectoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NovelChapter chapter && chapter.IsActive)
            await NavigateToChapterAsync(chapter);
    }

    private void RefreshChapterDirectoryList()
    {
        if (ChapterDirectoryPane.Visibility != Visibility.Visible)
            return;

        string search = ChapterDirectorySearchBox.Text.Trim();
        IEnumerable<NovelChapter> chapters = chapterAnnotationChapters
            .Where(chapter => chapter.IsActive);
        if (search.Length > 0)
        {
            chapters = chapters.Where(chapter =>
                chapter.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        chapterDirectoryVisibleChapters = chapters
            .OrderBy(chapter => chapter.StartOffset)
            .ThenBy(chapter => chapter.ChapterNumber)
            .ToArray();
        // Keep the directory list on the same explicit Items pattern as the chapter filters.
        // The WinUI ItemsSource setter throws E_INVALIDARG for this project's runtime item collection.
        ChapterDirectoryList.Items.Clear();
        foreach (NovelChapter chapter in chapterDirectoryVisibleChapters)
            ChapterDirectoryList.Items.Add(chapter);

        if (chapterDirectoryVisibleChapters.Count == 0)
        {
            bool indexRunning = chapterIndexBuildRunningVersion == chapterIndexBuildVersion;
            ChapterDirectoryEmptyState.Text = chapterAnnotationChapters.Count == 0
                ? !LineIndexer.IsCompleted || indexRunning
                    ? "正在识别章节，请稍候..."
                    : "没有识别到章节标题。"
                : "没有找到匹配的章节标题。";
            ChapterDirectoryEmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            ChapterDirectoryEmptyState.Visibility = Visibility.Collapsed;
        }
    }
}
