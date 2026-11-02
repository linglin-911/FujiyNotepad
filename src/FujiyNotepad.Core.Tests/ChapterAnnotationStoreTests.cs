using Microsoft.Data.Sqlite;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class ChapterAnnotationStoreTests
{
    [Fact]
    public async Task SaveChapterIndexIfEmptyAsync_PersistsOrderedRangesAndIndexStateOnlyOnce()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            NovelChapter first = new(Guid.NewGuid(), "第1章 开始", 0, 100, 1, 3);
            NovelChapter second = new(Guid.NewGuid(), "第2章 继续", 100, 250, 2, 3);

            Assert.True(await store.SaveChapterIndexIfEmptyAsync(new[] { first, second }, 3));
            Assert.False(await store.SaveChapterIndexIfEmptyAsync(new[] { first, second }, 3));

            IReadOnlyList<NovelChapter> chapters = await store.GetChaptersAsync();
            Assert.Equal(new[] { first.Id, second.Id }, chapters.Select(chapter => chapter.Id));
            NovelChapter? atStart = await store.GetChapterAtOffsetAsync(0);
            NovelChapter? atBoundary = await store.GetChapterAtOffsetAsync(100);
            NovelChapter? atEnd = await store.GetChapterAtOffsetAsync(250);
            Assert.Equal(first.Id, atStart?.Id);
            Assert.Equal(second.Id, atBoundary?.Id);
            Assert.Null(atEnd);

            NovelChapterIndexState state = Assert.IsType<NovelChapterIndexState>(
                await store.GetChapterIndexStateAsync());
            Assert.Equal(3L, state.DocumentRevision);
            Assert.Equal(NovelChapterIndexBuilder.CurrentParserVersion, state.ParserVersion);
            Assert.Equal(2, state.ChapterCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task SaveChapterIndexIfEmptyAsync_RecordsStateForLegacyRowsWithoutReplacingChapterIds()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            var existing = new NovelChapter(Guid.NewGuid(), "第1章 原章节", 0, 100, 1, 1);
            await store.UpsertChapterAsync(existing);
            var proposedReplacement = new NovelChapter(Guid.NewGuid(), "第1章 新标题", 0, 100, 1, 1);

            Assert.False(await store.SaveChapterIndexIfEmptyAsync(new[] { proposedReplacement }, 1));

            NovelChapter stored = Assert.Single(await store.GetChaptersAsync());
            Assert.Equal(existing.Id, stored.Id);
            Assert.Equal("第1章 原章节", stored.Title);
            NovelChapterIndexState state = Assert.IsType<NovelChapterIndexState>(
                await store.GetChapterIndexStateAsync());
            Assert.Equal(1, state.ChapterCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task SaveChapterIndexIfEmptyAsync_RecordsAnEmptyResultToAvoidRepeatedScans()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();

            Assert.True(await store.SaveChapterIndexIfEmptyAsync(Array.Empty<NovelChapter>(), 1));
            Assert.False(await store.SaveChapterIndexIfEmptyAsync(Array.Empty<NovelChapter>(), 1));

            NovelChapterIndexState state = Assert.IsType<NovelChapterIndexState>(
                await store.GetChapterIndexStateAsync());
            Assert.Equal(0, state.ChapterCount);
            Assert.Empty(await store.GetChaptersAsync());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task SaveChapterIndexIfEmptyAsync_RebuildsAnEmptyIndexWhenParserVersionChanges()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(Array.Empty<NovelChapter>(), 1, parserVersion: 99));

            var chapter = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1, 1);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(
                new[] { chapter }, 1, parserVersion: NovelChapterIndexBuilder.CurrentParserVersion));

            Assert.Equal(chapter.Id, Assert.Single(await store.GetChaptersAsync()).Id);
            NovelChapterIndexState state = Assert.IsType<NovelChapterIndexState>(
                await store.GetChapterIndexStateAsync());
            Assert.Equal(NovelChapterIndexBuilder.CurrentParserVersion, state.ParserVersion);
            Assert.Equal(1, state.ChapterCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ChapterAnnotations_AreIndependentFromSmallAnnotationCategoriesAndAllowMultipleNotesPerChapter()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid smallCategory = await store.CreateCategoryAsync("剧情");
            Guid chapterCategory = await store.CreateChapterAnnotationCategoryAsync("剧情");
            Guid chapterId = Guid.NewGuid();
            var chapter = new NovelChapter(chapterId, "第15章 变化", 100, 500, 15);
            await store.UpsertChapterAsync(chapter);

            Guid summaryId = await store.CreateChapterAnnotationAsync(
                chapterId, "本章发生了重要转折。", chapterCategory, "本章概括");
            Guid characterId = await store.CreateChapterAnnotationAsync(
                chapterId, "主角开始主动布局。", chapterCategory, "人物变化");

            IReadOnlyList<ChapterAnnotationSummary> all =
                await store.GetChapterAnnotationSummariesAsync(chapterId: chapterId);
            IReadOnlyList<ChapterAnnotationSummary> search =
                await store.GetChapterAnnotationSummariesAsync(searchText: "主动布局");
            IReadOnlyList<ChapterAnnotationSummary> chapterSearch =
                await store.GetChapterAnnotationSummariesAsync(searchText: "第15章");
            IReadOnlyList<AnnotationCategory> smallCategories = await store.GetCategoriesAsync();
            IReadOnlyList<ChapterAnnotationCategory> chapterCategories =
                await store.GetChapterAnnotationCategoriesAsync();

            Assert.Equal(2, all.Count);
            Assert.Contains(all, item => item.Annotation.Id == summaryId);
            Assert.Contains(all, item => item.Annotation.Id == characterId);
            Assert.Equal("第15章 变化", all[0].Chapter.Title);
            Assert.Equal("剧情", Assert.Single(chapterCategories).Name);
            Assert.Equal("剧情", Assert.Single(smallCategories).Name);
            Assert.Single(search);
            Assert.Equal(characterId, search[0].Annotation.Id);
            Assert.Equal(2, chapterSearch.Count);
            Assert.NotEqual(smallCategory, chapterCategory);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ChapterAnnotationCategoryDeletion_UncategorizesNotesWithoutDeletingThem()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            var chapter = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1);
            await store.UpsertChapterAsync(chapter);
            Guid category = await store.CreateChapterAnnotationCategoryAsync("伏笔");
            Guid note = await store.CreateChapterAnnotationAsync(chapter.Id, "埋下一个伏笔。", category, "伏笔记录");

            await store.DeleteChapterAnnotationCategoryAsync(category);

            ChapterAnnotationSummary summary = Assert.Single(
                await store.GetChapterAnnotationSummariesAsync(chapterId: chapter.Id));
            Assert.Equal(note, summary.Annotation.Id);
            Assert.Null(summary.Annotation.CategoryId);
            Assert.Null(summary.Category);
            Assert.Empty(await store.GetChapterAnnotationCategoriesAsync());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ChapterAnnotationCategories_CanBeRenamedAndMerged()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            var chapter = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1);
            await store.UpsertChapterAsync(chapter);
            Guid source = await store.CreateChapterAnnotationCategoryAsync("人物");
            Guid target = await store.CreateChapterAnnotationCategoryAsync("剧情");
            Guid first = await store.CreateChapterAnnotationAsync(chapter.Id, "角色出场", source);
            Guid second = await store.CreateChapterAnnotationAsync(chapter.Id, "发生冲突", source);

            await store.UpdateChapterAnnotationCategoryAsync(target, "主线剧情");
            await store.MergeChapterAnnotationCategoriesAsync(source, target);

            IReadOnlyList<ChapterAnnotationSummary> notes =
                await store.GetChapterAnnotationSummariesAsync(chapterId: chapter.Id, categoryId: target);
            Assert.Equal(2, notes.Count);
            Assert.Contains(notes, item => item.Annotation.Id == first);
            Assert.Contains(notes, item => item.Annotation.Id == second);
            Assert.All(notes, item => Assert.Equal("主线剧情", item.Category?.Name));
            Assert.Single(await store.GetChapterAnnotationCategoriesAsync());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ChapterAnnotationCrud_UpdatesAndDeletesOnlyTheSelectedNote()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            var firstChapter = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1);
            var secondChapter = new NovelChapter(Guid.NewGuid(), "第2章", 100, 200, 2);
            await store.UpsertChapterAsync(firstChapter);
            await store.UpsertChapterAsync(secondChapter);
            Guid category = await store.CreateChapterAnnotationCategoryAsync("剧情");
            Guid first = await store.CreateChapterAnnotationAsync(firstChapter.Id, "旧内容", category, "旧标题");
            Guid second = await store.CreateChapterAnnotationAsync(secondChapter.Id, "保留内容");

            Assert.True(await store.UpdateChapterAnnotationAsync(
                first, secondChapter.Id, "更新后的内容", category, "更新后的标题"));
            Assert.False(await store.UpdateChapterAnnotationAsync(Guid.NewGuid(), firstChapter.Id, "不存在"));

            await store.DeleteChapterAnnotationAsync(first);

            IReadOnlyList<ChapterAnnotationSummary> remaining =
                await store.GetChapterAnnotationSummariesAsync();
            ChapterAnnotationSummary kept = Assert.Single(remaining);
            Assert.Equal(second, kept.Annotation.Id);
            Assert.Equal(secondChapter.Id, kept.Chapter.Id);
            Assert.Equal("保留内容", kept.Annotation.Content);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ChapterAnnotations_PersistAfterReopeningTheProjectDatabase()
    {
        string path = CreateDatabasePath();
        try
        {
            Guid chapterId = Guid.NewGuid();
            Guid categoryId;
            Guid annotationId;
            var chapter = new NovelChapter(chapterId, "第9章 旧日", 0, 900, 9, 1);

            await using (var firstStore = new NovelAnnotationStore(path))
            {
                await firstStore.InitializeAsync();
                Assert.True(await firstStore.SaveChapterIndexIfEmptyAsync(new[] { chapter }, 1));
                categoryId = await firstStore.CreateChapterAnnotationCategoryAsync("伏笔");
                annotationId = await firstStore.CreateChapterAnnotationAsync(
                    chapterId, "这一章埋下了关键伏笔。", categoryId, "伏笔记录");
            }

            await using (var reopenedStore = new NovelAnnotationStore(path))
            {
                await reopenedStore.InitializeAsync();
                ChapterAnnotationSummary summary = Assert.Single(
                    await reopenedStore.GetChapterAnnotationSummariesAsync(chapterId: chapterId));

                Assert.Equal(annotationId, summary.Annotation.Id);
                Assert.Equal(categoryId, summary.Annotation.CategoryId);
                Assert.Equal("第9章 旧日", summary.Chapter.Title);
                Assert.Equal("伏笔", summary.Category?.Name);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task DeletingChapter_CascadesItsChapterAnnotations()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            var chapter = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1);
            await store.UpsertChapterAsync(chapter);
            await store.CreateChapterAnnotationAsync(chapter.Id, "本章批注");

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM Chapters WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", chapter.Id.ToString("D"));
            await delete.ExecuteNonQueryAsync();

            Assert.Empty(await store.GetChapterAnnotationSummariesAsync());
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task SavingChapterIndex_BackfillsExistingSmallAnnotationAnchorChapterIds()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid note = await store.CreateAnnotationAsync("已有小批注");
            await store.AddAnchorAsync(note, 25, 40, "原文摘录");

            var first = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1);
            var second = new NovelChapter(Guid.NewGuid(), "第2章", 100, 200, 2);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(new[] { first, second }, 1));

            AnnotationAnchor anchor = Assert.Single(await store.GetAnchorsForAnnotationAsync(note));
            Assert.Equal(first.Id, anchor.ChapterId);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ApplyingDocumentEdit_UpdatesRangesAndMarksChapterIndexStale()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            var chapter = new NovelChapter(Guid.NewGuid(), "第1章", 0, 100, 1, 1);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(new[] { chapter }, 1));
            await store.CreateChapterAnnotationAsync(chapter.Id, "本章批注");

            await store.ApplyDocumentEditAsync(new DocumentEdit(25, 0, 5), newDocumentRevision: 2);

            NovelChapter updated = Assert.Single(await store.GetChaptersAsync());
            Assert.Equal(105L, updated.EndOffset);
            Assert.Equal(2L, updated.DocumentRevision);
            NovelChapterIndexState state = Assert.IsType<NovelChapterIndexState>(
                await store.GetChapterIndexStateAsync());
            // Text edits transform stored ranges but do not prove that chapter headings still exist.
            // Keep the previous index revision so the user can explicitly re-identify chapters.
            Assert.Equal(1L, state.DocumentRevision);
            Assert.Single(await store.GetChapterAnnotationSummariesAsync(chapterId: chapter.Id));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ReplaceChapterIndex_ArchivesRemovedChapterAndPreservesItsAnnotation()
    {
        string path = CreateDatabasePath();
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();

            var first = new NovelChapter(Guid.NewGuid(), "第六章 旧标题", 0, 100, 1, 1);
            var removed = new NovelChapter(Guid.NewGuid(), "第七章", 100, 200, 2, 1);
            var third = new NovelChapter(Guid.NewGuid(), "第八章 后续", 200, 300, 3, 1);
            Assert.True(await store.SaveChapterIndexIfEmptyAsync(new[] { first, removed, third }, 1));
            Guid noteId = await store.CreateChapterAnnotationAsync(removed.Id, "第七章的重要剧情记录", title: "保留这条批注");

            Guid smallNoteId = await store.CreateAnnotationAsync("小批注锚点");
            await store.AddAnchorAsync(smallNoteId, 150, 160, "十个字符", removed.Id, documentRevision: 2);

            var detected = new[]
            {
                new NovelChapter(first.Id, "第六章 旧标题", 0, 200, 1, 2),
                new NovelChapter(third.Id, "第八章 后续", 200, 300, 2, 2),
            };
            NovelChapterIndexReconciliation reconciliation =
                NovelChapterIndexReconciler.Reconcile(new[] { first, removed, third }, detected);

            Assert.Equal(new[] { first.Id, third.Id }, reconciliation.Chapters.Select(chapter => chapter.Id));
            Assert.Single(reconciliation.Removed);
            Assert.Equal(removed.Id, reconciliation.Removed[0].Id);
            Assert.Empty(reconciliation.Added);

            await store.ReplaceChapterIndexAsync(reconciliation.Chapters, 2);

            IReadOnlyList<NovelChapter> active = await store.GetChaptersAsync();
            Assert.Equal(new[] { first.Id, third.Id }, active.Select(chapter => chapter.Id));
            IReadOnlyList<NovelChapter> all = await store.GetChaptersAsync(includeInactive: true);
            Assert.Equal(3, all.Count);
            Assert.False(all.Single(chapter => chapter.Id == removed.Id).IsActive);

            ChapterAnnotationSummary preserved = Assert.Single(
                await store.GetChapterAnnotationSummariesAsync(chapterId: removed.Id));
            Assert.Equal(noteId, preserved.Annotation.Id);
            Assert.False(preserved.Chapter.IsActive);
            Assert.Equal("第七章", preserved.Chapter.Title);
            Assert.Contains(
                await store.GetChapterAnnotationSummariesAsync(),
                summary => summary.Annotation.Id == noteId && !summary.Chapter.IsActive);

            AnnotationAnchor reassigned = Assert.Single(await store.GetAnchorsForAnnotationAsync(smallNoteId));
            Assert.Equal(first.Id, reassigned.ChapterId);

            NovelChapterIndexState state = Assert.IsType<NovelChapterIndexState>(
                await store.GetChapterIndexStateAsync());
            Assert.Equal(2L, state.DocumentRevision);
            Assert.Equal(2, state.ChapterCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesExistingChapterTableWithActiveFlag()
    {
        string path = CreateDatabasePath();
        try
        {
            await using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString()))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE Chapters (
                        Id TEXT PRIMARY KEY NOT NULL,
                        Title TEXT NOT NULL,
                        StartOffset INTEGER NOT NULL,
                        EndOffset INTEGER NOT NULL,
                        ChapterNumber INTEGER NOT NULL,
                        DocumentRevision INTEGER NOT NULL DEFAULT 1
                    );
                    INSERT INTO Chapters (Id, Title, StartOffset, EndOffset, ChapterNumber, DocumentRevision)
                    VALUES ('11111111-1111-1111-1111-111111111111', '第1章 旧项目', 0, 100, 1, 1);
                    """;
                await create.ExecuteNonQueryAsync();
            }

            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();

            NovelChapter chapter = Assert.Single(await store.GetChaptersAsync());
            Assert.Equal("第1章 旧项目", chapter.Title);
            Assert.True(chapter.IsActive);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"chapter-annotations-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
    }
}
