using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class NovelChapterIndexReconcilerTests
{
    [Fact]
    public void Reconcile_PreservesIdsAndReportsAddedRemovedAndRenamedChapters()
    {
        var first = new NovelChapter(Guid.NewGuid(), "第1章 开始", 0, 100, 1, 1);
        var second = new NovelChapter(Guid.NewGuid(), "第2章 旧标题", 100, 200, 2, 1);
        var third = new NovelChapter(Guid.NewGuid(), "第3章 结束", 200, 300, 3, 1);
        var detected =
        [
            new NovelChapter(Guid.NewGuid(), "第1章 开始", 0, 110, 1, 2),
            new NovelChapter(Guid.NewGuid(), "第2章 新标题", 110, 220, 2, 2),
            new NovelChapter(Guid.NewGuid(), "第4章 新增", 220, 350, 3, 2),
        ];

        NovelChapterIndexReconciliation result =
            NovelChapterIndexReconciler.Reconcile(new[] { first, second, third }, detected);

        Assert.Equal(first.Id, result.Chapters[0].Id);
        Assert.Equal(second.Id, result.Chapters[1].Id);
        Assert.NotEqual(third.Id, result.Chapters[2].Id);
        Assert.Single(result.Added);
        Assert.Equal("第4章 新增", result.Added[0].Title);
        Assert.Single(result.Removed);
        Assert.Equal(third.Id, result.Removed[0].Id);
        NovelChapterTitleChange rename = Assert.Single(result.Renamed);
        Assert.Equal(second.Id, rename.Previous.Id);
        Assert.Equal("第2章 新标题", rename.Current.Title);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public void Reconcile_DeletedHeadingIsNotMatchedToPreviousChapterWhoseRangeExpanded()
    {
        var sixth = new NovelChapter(Guid.NewGuid(), "第六章", 0, 100, 1, 1);
        var seventh = new NovelChapter(Guid.NewGuid(), "第七章", 100, 200, 2, 1);
        var eighth = new NovelChapter(Guid.NewGuid(), "第八章", 200, 300, 3, 1);
        var detected =
        [
            new NovelChapter(Guid.NewGuid(), "第六章", 0, 200, 1, 2),
            new NovelChapter(Guid.NewGuid(), "第八章", 200, 300, 2, 2),
        ];

        NovelChapterIndexReconciliation result =
            NovelChapterIndexReconciler.Reconcile(new[] { sixth, seventh, eighth }, detected);

        Assert.Equal(sixth.Id, result.Chapters[0].Id);
        Assert.Equal(eighth.Id, result.Chapters[1].Id);
        Assert.Equal(seventh.Id, Assert.Single(result.Removed).Id);
        Assert.Empty(result.Added);
        Assert.Empty(result.Renamed);
    }
}
