using Microsoft.Data.Sqlite;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class NovelAnnotationStoreTests
{
    [Fact]
    public async Task Store_AllowsOverlappingAnchorsAndMultipleAnchorsPerAnnotation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid category = await store.CreateCategoryAsync("描写");
            Guid annotationA = await store.CreateAnnotationAsync("完整动作", category, "动作描写");
            Guid annotationB = await store.CreateAnnotationAsync("战斗节奏", category, "节奏");
            await store.AddAnchorAsync(annotationA, 100, 300, "完整动作原文");
            await store.AddAnchorAsync(annotationA, 900, 950, "第二处原文");
            await store.AddAnchorAsync(annotationB, 200, 400, "重叠原文");

            IReadOnlyList<AnnotationAnchor> anchorsA = await store.GetAnchorsForAnnotationAsync(annotationA);
            IReadOnlyList<AnnotationAnchor> at250 = await store.GetAnchorsAtOffsetAsync(250);

            Assert.Equal(2, anchorsA.Count);
            Assert.Equal(2, at250.Count);
            Assert.Contains(at250, anchor => anchor.AnnotationId == annotationA);
            Assert.Contains(at250, anchor => anchor.AnnotationId == annotationB);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task GetAnchorsForAnnotationsAsync_ReturnsRequestedAnchorsGroupedByAnnotation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid first = await store.CreateAnnotationAsync("first");
            Guid second = await store.CreateAnnotationAsync("second");
            Guid ignored = await store.CreateAnnotationAsync("ignored");
            await store.AddAnchorAsync(first, 10, 20, "first occurrence");
            await store.AddAnchorAsync(first, 30, 40, "second occurrence");
            await store.AddAnchorAsync(second, 50, 60, "third occurrence");
            await store.AddAnchorAsync(ignored, 70, 80, "not requested");

            IReadOnlyDictionary<Guid, IReadOnlyList<AnnotationAnchor>> result =
                await store.GetAnchorsForAnnotationsAsync(new[] { first, second });

            Assert.Equal(2, result.Count);
            Assert.Equal(2, result[first].Count);
            Assert.Single(result[second]);
            Assert.All(result[first], anchor => Assert.Equal(first, anchor.AnnotationId));
            Assert.DoesNotContain(ignored, result.Keys);
            Assert.Empty(await store.GetAnchorsForAnnotationsAsync(Array.Empty<Guid>()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task RangeQueries_ReturnAllOverlappingNotesIncludingNestedRanges()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid broad = await store.CreateAnnotationAsync("broad");
            Guid nested = await store.CreateAnnotationAsync("nested");
            Guid outside = await store.CreateAnnotationAsync("outside");
            await store.AddAnchorAsync(broad, 100, 300, "broad quote");
            await store.AddAnchorAsync(nested, 150, 180, "nested quote");
            await store.AddAnchorAsync(outside, 301, 350, "outside quote");

            var anchors = await store.GetAnchorsIntersectingRangeAsync(160, 170);
            var notes = await store.GetAnnotationsIntersectingRangeAsync(160, 170);

            Assert.Equal(2, anchors.Count);
            Assert.Equal(2, notes.Count);
            Assert.Contains(notes, note => note.Id == broad);
            Assert.Contains(notes, note => note.Id == nested);
            Assert.DoesNotContain(notes, note => note.Id == outside);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task ParentRangeQuery_FindsNestedAnchorWhenClickedOffsetIsOutsideChild()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid parentNote = await store.CreateAnnotationAsync("parent");
            Guid childNote = await store.CreateAnnotationAsync("child");
            await store.AddAnchorAsync(parentNote, 100, 300, "parent quote");
            await store.AddAnchorAsync(childNote, 150, 180, "child quote");

            IReadOnlyList<AnnotationAnchor> direct = await store.GetAnchorsAtOffsetAsync(250);
            AnnotationAnchor parent = Assert.Single(direct);
            Assert.Equal(parentNote, parent.AnnotationId);

            IReadOnlyList<AnnotationAnchor> contained =
                await store.GetAnchorsIntersectingRangeAsync(parent.StartOffset, parent.EndOffset);

            Assert.Contains(contained, anchor => anchor.AnnotationId == parentNote);
            Assert.Contains(contained, anchor => anchor.AnnotationId == childNote);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Store_AllowsSameRangeForDifferentAnnotationsButRejectsDuplicateForSameAnnotation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid first = await store.CreateAnnotationAsync("first");
            Guid second = await store.CreateAnnotationAsync("second");
            await store.AddAnchorAsync(first, 10, 20, "same");
            await store.AddAnchorAsync(second, 10, 20, "same");

            Assert.True(await store.HasAnchorAsync(first, 10, 20));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => store.AddAnchorAsync(first, 10, 20, "duplicate"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task ApplyDocumentEdit_UpdatesChapterRangesWithAnchorsAtomically()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid chapterId = Guid.NewGuid();
            Guid annotationId = await store.CreateAnnotationAsync("note");
            await store.UpsertChapterAsync(new NovelChapter(chapterId, "第一章", 100, 500, 1));
            await store.AddAnchorAsync(annotationId, 150, 180, "quote", chapterId);

            await store.ApplyDocumentEditAsync(new DocumentEdit(20, 5, 8), newDocumentRevision: 2);

            NovelChapter chapter = Assert.Single(await store.GetChaptersAsync());
            AnnotationAnchor anchor = Assert.Single(await store.GetAnchorsForAnnotationAsync(annotationId));
            Assert.Equal((103L, 503L), (chapter.StartOffset, chapter.EndOffset));
            Assert.Equal((153L, 183L), (anchor.StartOffset, anchor.EndOffset));
            Assert.Equal(2, chapter.DocumentRevision);
            Assert.Equal(2, anchor.DocumentRevision);
            Assert.Equal(chapterId, anchor.ChapterId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task ApplyDocumentEdit_UpdatesOnlyAnchorsAfterEditAndPreservesOverlaps()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid a = await store.CreateAnnotationAsync("A");
            Guid b = await store.CreateAnnotationAsync("B");
            await store.AddAnchorAsync(a, 100, 200, "A1");
            await store.AddAnchorAsync(b, 150, 250, "B1");
            await store.AddAnchorAsync(a, 400, 500, "A2");

            await store.ApplyDocumentEditAsync(new DocumentEdit(175, 10, 4), newDocumentRevision: 2);

            var aAnchors = await store.GetAnchorsForAnnotationAsync(a);
            var bAnchors = await store.GetAnchorsForAnnotationAsync(b);
            Assert.Equal((100L, 194L), (aAnchors[0].StartOffset, aAnchors[0].EndOffset));
            Assert.Equal((150L, 244L), (bAnchors[0].StartOffset, bAnchors[0].EndOffset));
            Assert.Equal((394L, 494L), (aAnchors[1].StartOffset, aAnchors[1].EndOffset));
            Assert.All(aAnchors.Concat(bAnchors), anchor => Assert.Equal(2, anchor.DocumentRevision));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task ApplyDocumentEdit_UpdatesRevisionOfUnaffectedEarlierAnchorsAndChapters()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid earlierChapterId = Guid.NewGuid();
            Guid laterChapterId = Guid.NewGuid();
            Guid annotationId = await store.CreateAnnotationAsync("revision");
            await store.UpsertChapterAsync(new NovelChapter(earlierChapterId, "第一章", 0, 100, 1));
            await store.UpsertChapterAsync(new NovelChapter(laterChapterId, "第二章", 200, 400, 2));
            await store.AddAnchorAsync(annotationId, 20, 30, "earlier");
            await store.AddAnchorAsync(annotationId, 250, 260, "later");

            await store.ApplyDocumentEditAsync(new DocumentEdit(220, 5, 2), newDocumentRevision: 2);

            var anchors = await store.GetAnchorsForAnnotationAsync(annotationId);
            Assert.Equal((20L, 30L, 2L), (anchors[0].StartOffset, anchors[0].EndOffset, anchors[0].DocumentRevision));
            Assert.Equal((247L, 257L, 2L), (anchors[1].StartOffset, anchors[1].EndOffset, anchors[1].DocumentRevision));

            var chapters = await store.GetChaptersAsync();
            NovelChapter earlier = Assert.Single(chapters, chapter => chapter.Id == earlierChapterId);
            NovelChapter later = Assert.Single(chapters, chapter => chapter.Id == laterChapterId);
            Assert.Equal((0L, 100L, 2L), (earlier.StartOffset, earlier.EndOffset, earlier.DocumentRevision));
            Assert.Equal((200L, 397L, 2L), (later.StartOffset, later.EndOffset, later.DocumentRevision));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task TryRepairAnchor_UpdatesOnlyWhenTheExpectedSavedAnchorStillExists()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid annotationId = await store.CreateAnnotationAsync("repair");
            await store.AddAnchorAsync(annotationId, 100, 105, "quote", documentRevision: 1);
            AnnotationAnchor original = Assert.Single(await store.GetAnchorsForAnnotationAsync(annotationId));

            bool repaired = await store.TryRepairAnchorAsync(
                original, 96, 101, chapterId: null, documentRevision: 2);
            Assert.True(repaired);

            AnnotationAnchor updated = Assert.Single(await store.GetAnchorsForAnnotationAsync(annotationId));
            Assert.Equal((96L, 101L, 2L, "quote"),
                (updated.StartOffset, updated.EndOffset, updated.DocumentRevision, updated.Quote));

            // A stale copy cannot overwrite the repaired range a second time.
            bool staleUpdate = await store.TryRepairAnchorAsync(
                original, 90, 95, chapterId: null, documentRevision: 3);
            Assert.False(staleUpdate);
            updated = Assert.Single(await store.GetAnchorsForAnnotationAsync(annotationId));
            Assert.Equal((96L, 101L, 2L), (updated.StartOffset, updated.EndOffset, updated.DocumentRevision));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    [Fact]
    public async Task TryRepairAnchor_RefreshesRevisionWhenRangeIsUnchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid annotationId = await store.CreateAnnotationAsync("unchanged range");
            await store.AddAnchorAsync(annotationId, 100, 105, "quote", documentRevision: 1);
            AnnotationAnchor original = Assert.Single(await store.GetAnchorsForAnnotationAsync(annotationId));

            bool refreshed = await store.TryRepairAnchorAsync(
                original, 100, 105, chapterId: null, documentRevision: 2);

            Assert.True(refreshed);
            AnnotationAnchor updated = Assert.Single(await store.GetAnchorsForAnnotationAsync(annotationId));
            Assert.Equal((100L, 105L, 2L, "quote"),
                (updated.StartOffset, updated.EndOffset, updated.DocumentRevision, updated.Quote));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    public async Task CreateAnnotationWithAnchor_IsAtomicAndAppearsInSummarySearch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid category = await store.CreateCategoryAsync("人物");

            Guid id = await store.CreateAnnotationWithAnchorAsync(
                "这段体现了人物性格。", 120, 135, "他没有回头，只把剑收了起来。",
                category, "人物性格", documentRevision: 3);

            NovelAnnotationSummary summary = Assert.Single(
                await store.GetAnnotationSummariesAsync(category, "收了起来"));
            Assert.Equal(id, summary.Annotation.Id);
            Assert.Equal("人物性格", summary.Annotation.Title);
            Assert.Equal(1, summary.AnchorCount);
            Assert.Equal(0, summary.OrphanedAnchorCount);
            Assert.Equal(120L, summary.FirstActiveAnchorOffset);
            Assert.Equal("他没有回头，只把剑收了起来。", summary.FirstActiveAnchorQuote);
            Assert.Single(await store.GetAnchorsForAnnotationAsync(id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task GetAnnotationSummariesAsync_WhenCategoryHasNoAnnotations_ReturnsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid emptyCategory = await store.CreateCategoryAsync("空分类");
            Guid usedCategory = await store.CreateCategoryAsync("有内容的分类");
            await store.CreateAnnotationAsync("已有批注", usedCategory, "测试");

            Assert.Empty(await store.GetAnnotationSummariesAsync(emptyCategory));
            Assert.Single(await store.GetAnnotationSummariesAsync(usedCategory));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task CategoryCrudAndMerge_PreserveNotesAndUpdateTheirCategory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid source = await store.CreateCategoryAsync("旧分类");
            Guid target = await store.CreateCategoryAsync("新分类");
            Guid note = await store.CreateAnnotationAsync("note", source, "title");

            await store.UpdateCategoryAsync(target, "目标分类", "#AABBCC", 2);
            await store.MergeCategoriesAsync(source, target);

            NovelAnnotation moved = Assert.Single(await store.GetAnnotationsAsync(target));
            Assert.Equal(note, moved.Id);
            Assert.Equal("目标分类", Assert.Single(await store.GetCategoriesAsync()).Name);

            await store.DeleteCategoryAsync(target);
            NovelAnnotation uncategorized = Assert.Single(await store.GetAnnotationsAsync());
            Assert.Null(uncategorized.CategoryId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task AddAnchorToExistingNote_UpdatesItsTimestampAndDeleteCascades()
    {
        string path = Path.Combine(Path.GetTempPath(), $"novel-annotations-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var store = new NovelAnnotationStore(path);
            await store.InitializeAsync();
            Guid note = await store.CreateAnnotationAsync("note");
            Guid first = await store.AddAnchorAsync(note, 10, 20, "first");
            await store.AddAnchorAsync(note, 30, 40, "second");

            Assert.Equal(2, (await store.GetAnnotationSummariesAsync()).Single().AnchorCount);
            await store.RemoveAnchorAsync(first);
            Assert.Equal(1, (await store.GetAnnotationSummariesAsync()).Single().AnchorCount);

            await store.DeleteAnnotationAsync(note);
            Assert.Empty(await store.GetAnnotationSummariesAsync());
            Assert.Empty(await store.GetAllAnchorsAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
