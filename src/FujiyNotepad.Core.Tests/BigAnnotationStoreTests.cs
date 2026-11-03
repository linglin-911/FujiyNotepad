using Microsoft.Data.Sqlite;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class BigAnnotationStoreTests
{
    [Fact]
    public async Task BigAnnotations_CreateUpdateAndDeleteWithIndependentCategoriesAndChapterRanges()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter[] chapters = CreateChapters(3);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(chapters, 1));

            Guid category = await store.CreateBigAnnotationCategoryAsync("阶段总结");
            Guid id = await store.CreateBigAnnotationAsync(
                "第一阶段", "阶段正文", category, new[] { chapters[0].Id, chapters[1].Id });

            BigAnnotation saved = Assert.IsType<BigAnnotation>(await store.GetBigAnnotationAsync(id));
            Assert.Equal("第一阶段", saved.Title);
            Assert.Equal("阶段正文", saved.Content);
            Assert.Equal(category, saved.CategoryId);
            Assert.Equal(new[] { chapters[0].Id, chapters[1].Id },
                (await store.GetBigAnnotationChaptersAsync(id)).Select(chapter => chapter.Id));

            IReadOnlyList<BigAnnotationSummary> summaries =
                await store.GetBigAnnotationSummariesAsync(categoryId: category, searchText: "阶段");
            BigAnnotationSummary summary = Assert.Single(summaries);
            Assert.Equal(2, summary.ChapterCount);
            Assert.Equal(chapters[0].Title, summary.FirstChapterTitle);
            Assert.Equal(chapters[1].Title, summary.LastChapterTitle);

            await store.UpdateBigAnnotationCategoryAsync(category, "人物分析");
            await store.UpdateBigAnnotationAsync(
                id, "主角成长", "更新后的正文", category, new[] { chapters[1].Id, chapters[2].Id });

            BigAnnotation updated = Assert.IsType<BigAnnotation>(await store.GetBigAnnotationAsync(id));
            Assert.Equal("主角成长", updated.Title);
            Assert.Equal("更新后的正文", updated.Content);
            Assert.Equal("人物分析", Assert.Single(await store.GetBigAnnotationCategoriesAsync()).Name);
            Assert.Equal(new[] { chapters[1].Id, chapters[2].Id },
                (await store.GetBigAnnotationChaptersAsync(id)).Select(chapter => chapter.Id));

            await store.DeleteBigAnnotationAsync(id);
            Assert.Null(await store.GetBigAnnotationAsync(id));
            Assert.Empty(await store.GetBigAnnotationSummariesAsync());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task BigAnnotations_RejectSingleChapterAndNonContiguousRanges()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter[] chapters = CreateChapters(3);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(chapters, 1));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.CreateBigAnnotationAsync("单章", "", null, new[] { chapters[0].Id }));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.CreateBigAnnotationAsync("跳章", "", null, new[] { chapters[0].Id, chapters[2].Id }));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.CreateBigAnnotationAsync("乱序", "", null, new[] { chapters[1].Id, chapters[0].Id }));

            Assert.Empty(await store.GetBigAnnotationSummariesAsync());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task BigAnnotationReferences_ShowEachSmallNoteOnceWithAllSelectedChapterLocations()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter[] chapters = CreateChapters(3);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(chapters, 1));

            Guid smallCategory = await store.CreateCategoryAsync("人物");
            Guid chapterCategory = await store.CreateChapterAnnotationCategoryAsync("剧情");
            Guid multiChapterNote = await store.CreateAnnotationAsync("主角性格变化", smallCategory, "性格变化");
            Guid otherNote = await store.CreateAnnotationAsync("新势力登场", smallCategory, "势力线索");
            await store.AddAnchorAsync(multiChapterNote, 10, 20, "第一处原文", chapters[0].Id);
            await store.AddAnchorAsync(multiChapterNote, 120, 130, "第二处原文", chapters[1].Id);
            await store.AddAnchorAsync(otherNote, 220, 230, "第三处原文", chapters[2].Id);
            await store.CreateChapterAnnotationAsync(chapters[0].Id, "本章转折", chapterCategory, "章节概括");
            await store.CreateChapterAnnotationAsync(chapters[2].Id, "势力正式出现", chapterCategory, "势力变化");

            IReadOnlyList<BigAnnotationSmallReference> smallReferences =
                await store.GetBigAnnotationSmallReferencesAsync(chapters.Select(chapter => chapter.Id).ToArray());
            Assert.Equal(2, smallReferences.Count);
            BigAnnotationSmallReference multi = Assert.Single(
                smallReferences, item => item.Annotation.Id == multiChapterNote);
            Assert.Equal(2, multi.Anchors.Count);
            Assert.Equal(new[] { chapters[0].Id, chapters[1].Id },
                multi.Anchors.Select(item => item.Chapter.Id));

            IReadOnlyList<ChapterAnnotationSummary> chapterReferences =
                await store.GetBigAnnotationChapterReferencesAsync(chapters.Select(chapter => chapter.Id).ToArray());
            Assert.Equal(2, chapterReferences.Count);
            Assert.Contains(chapterReferences, item => item.Chapter.Id == chapters[0].Id);
            Assert.Contains(chapterReferences, item => item.Chapter.Id == chapters[2].Id);

            Assert.Single(await store.GetBigAnnotationSmallReferencesAsync(
                chapters.Select(chapter => chapter.Id).ToArray(), categoryId: smallCategory, searchText: "性格"));
            Assert.Single(await store.GetBigAnnotationChapterReferencesAsync(
                chapters.Select(chapter => chapter.Id).ToArray(), categoryId: chapterCategory, searchText: "势力"));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task BigAnnotationCategoryDeletionAndMerge_DoNotDeleteAnnotations()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter[] chapters = CreateChapters(2);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(chapters, 1));
            Guid source = await store.CreateBigAnnotationCategoryAsync("旧分类");
            Guid target = await store.CreateBigAnnotationCategoryAsync("目标分类");
            Guid first = await store.CreateBigAnnotationAsync("一", "正文一", source, chapters.Select(c => c.Id).ToArray());
            Guid second = await store.CreateBigAnnotationAsync("二", "正文二", source, chapters.Select(c => c.Id).ToArray());

            await store.MergeBigAnnotationCategoriesAsync(source, target);
            Assert.All(await store.GetBigAnnotationSummariesAsync(), item => Assert.Equal(target, item.Annotation.CategoryId));

            await store.DeleteBigAnnotationCategoryAsync(target);
            Assert.All(await store.GetBigAnnotationSummariesAsync(), item => Assert.Null(item.Annotation.CategoryId));
            Assert.NotNull(await store.GetBigAnnotationAsync(first));
            Assert.NotNull(await store.GetBigAnnotationAsync(second));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task EditingOrDeletingSmallAndChapterAnnotations_DoesNotChangeBigAnnotation()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter[] chapters = CreateChapters(2);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(chapters, 1));

            Guid bigId = await store.CreateBigAnnotationAsync(
                "阶段总结", "独立的大批注正文", null, chapters.Select(chapter => chapter.Id).ToArray());
            Guid smallId = await store.CreateAnnotationAsync("小批注原文分析");
            await store.AddAnchorAsync(smallId, 10, 20, "第一处原文", chapters[0].Id);
            Guid chapterNoteId = await store.CreateChapterAnnotationAsync(
                chapters[0].Id, "章节批注原文", title: "本章分析");

            Assert.True(await store.UpdateAnnotationAsync(smallId, "修改后的小批注正文", title: "修改后的小批注"));
            Assert.True(await store.UpdateChapterAnnotationAsync(
                chapterNoteId, chapters[0].Id, "修改后的章节批注正文", title: "修改后的章节批注"));

            BigAnnotation before = Assert.IsType<BigAnnotation>(await store.GetBigAnnotationAsync(bigId));
            IReadOnlyList<Guid> chaptersBefore =
                (await store.GetBigAnnotationChaptersAsync(bigId)).Select(chapter => chapter.Id).ToArray();

            await store.DeleteAnnotationAsync(smallId);
            await store.DeleteChapterAnnotationAsync(chapterNoteId);

            BigAnnotation after = Assert.IsType<BigAnnotation>(await store.GetBigAnnotationAsync(bigId));
            Assert.Equal(before, after);
            Assert.Equal(chaptersBefore,
                (await store.GetBigAnnotationChaptersAsync(bigId)).Select(chapter => chapter.Id));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task BigAnnotation_UpdateWithoutChangingRange_CanPreserveAnArchivedChapterRange()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter[] chapters = CreateChapters(2);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(chapters, 1));
            Guid id = await store.CreateBigAnnotationAsync(
                "旧阶段", "原正文", null, chapters.Select(chapter => chapter.Id).ToArray());

            await store.ReplaceChapterIndexAsync(
                new[] { new NovelChapter(chapters[0].Id, chapters[0].Title, 0, 200, 1, 2) }, 2);

            Assert.True(await store.UpdateBigAnnotationAsync(id, "仍保留", "可继续编辑", null));
            Assert.Equal(2, (await store.GetBigAnnotationChaptersAsync(id)).Count);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static NovelChapter[] CreateChapters(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new NovelChapter(
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                $"第{index + 1}章",
                index * 100,
                (index + 1) * 100,
                index + 1,
                1,
                true,
                index * 100,
                "body"))
            .ToArray();

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"big-annotations-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
    }
}
