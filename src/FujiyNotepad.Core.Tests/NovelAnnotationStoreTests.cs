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
}
