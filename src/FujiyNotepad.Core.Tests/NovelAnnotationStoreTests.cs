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
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
