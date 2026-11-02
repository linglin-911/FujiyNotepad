using System.Text.RegularExpressions;

namespace FujiyNotepad.Core;

/// <summary>A detected chapter whose title changed while retaining its identity.</summary>
public sealed record NovelChapterTitleChange(NovelChapter Previous, NovelChapter Current);

/// <summary>
/// A non-destructive comparison between the stored chapter index and a freshly detected index.
/// Matched chapters retain their IDs so existing chapter annotations remain attached.
/// </summary>
public sealed record NovelChapterIndexReconciliation(
    IReadOnlyList<NovelChapter> Chapters,
    IReadOnlyList<NovelChapter> Added,
    IReadOnlyList<NovelChapter> Removed,
    IReadOnlyList<NovelChapterTitleChange> Renamed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Renamed.Count > 0;
}

/// <summary>
/// Matches old and newly detected chapter headings by stable heading identity, not raw offsets.
/// This lets body edits and title edits preserve chapter IDs, while deleted headings remain removed
/// instead of being matched to a previous chapter whose range expanded.
/// </summary>
public static class NovelChapterIndexReconciler
{
    private static readonly Regex ChineseNumberedHeading = new(
        @"^第\s*(?<number>[0-9０-９〇零一二三四五六七八九十百千万两兩壹贰貳叁參肆伍陆陸柒捌玖拾佰仟]+)\s*(?<unit>章|回|节|節|集|篇)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishNumberedHeading = new(
        @"^(?:chapter|ch\.?)\s*#?\s*(?<number>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SpecialHeading = new(
        @"^(?<name>序章|楔子|引子|序幕|终章|尾声|后记|番外(?:\s*\d+)?|prologue|epilogue)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static NovelChapterIndexReconciliation Reconcile(
        IReadOnlyList<NovelChapter> existing,
        IReadOnlyList<NovelChapter> detected)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(detected);

        NovelChapter[] oldChapters = existing.Where(chapter => chapter.IsActive)
            .OrderBy(chapter => chapter.StartOffset).ToArray();
        NovelChapter[] newChapters = detected.OrderBy(chapter => chapter.StartOffset).ToArray();

        Dictionary<string, List<NovelChapter>> oldGroups = GroupByIdentity(oldChapters);
        Dictionary<string, List<NovelChapter>> newGroups = GroupByIdentity(newChapters);
        var matchedOld = new HashSet<Guid>();
        var matchedNew = new Dictionary<Guid, NovelChapter>();
        var renamed = new List<NovelChapterTitleChange>();

        foreach ((string key, List<NovelChapter> oldGroup) in oldGroups)
        {
            if (!newGroups.TryGetValue(key, out List<NovelChapter>? newGroup))
                continue;

            int count = Math.Min(oldGroup.Count, newGroup.Count);
            for (int index = 0; index < count; index++)
            {
                NovelChapter previous = oldGroup[index];
                NovelChapter detectedChapter = newGroup[index];
                NovelChapter current = detectedChapter with
                {
                    Id = previous.Id,
                    IsActive = true,
                };

                matchedOld.Add(previous.Id);
                matchedNew[detectedChapter.Id] = current;
                if (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal))
                    renamed.Add(new NovelChapterTitleChange(previous, current));
            }
        }

        var result = new List<NovelChapter>(newChapters.Length);
        var added = new List<NovelChapter>();
        foreach (NovelChapter chapter in newChapters)
        {
            if (matchedNew.TryGetValue(chapter.Id, out NovelChapter? matched))
            {
                result.Add(matched);
            }
            else
            {
                NovelChapter newChapter = chapter with { IsActive = true };
                result.Add(newChapter);
                added.Add(newChapter);
            }
        }

        NovelChapter[] removed = oldChapters.Where(chapter => !matchedOld.Contains(chapter.Id)).ToArray();
        return new NovelChapterIndexReconciliation(
            result,
            added,
            removed,
            renamed);
    }

    private static Dictionary<string, List<NovelChapter>> GroupByIdentity(IEnumerable<NovelChapter> chapters) =>
        chapters.GroupBy(GetIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(chapter => chapter.StartOffset).ToList(),
                StringComparer.Ordinal);

    private static string GetIdentity(NovelChapter chapter)
    {
        string title = chapter.Title.Trim();
        Match chinese = ChineseNumberedHeading.Match(title);
        if (chinese.Success)
            return $"cn:{chinese.Groups["number"].Value}:{chinese.Groups["unit"].Value}";

        Match english = EnglishNumberedHeading.Match(title);
        if (english.Success)
            return $"en:{english.Groups["number"].Value}";

        Match special = SpecialHeading.Match(title);
        if (special.Success)
            return "special:" + special.Groups["name"].Value.Trim().ToUpperInvariant();

        return "title:" + title;
    }
}
