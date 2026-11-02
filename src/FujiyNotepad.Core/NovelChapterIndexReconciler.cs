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
/// Matches old and newly detected chapter headings by their stable heading identity, not by raw
/// offsets. This allows body edits and heading-title edits to preserve chapter IDs, while a deleted
/// heading is correctly reported as removed instead of being matched to the expanded previous range.
/// </summary>
public static class NovelChapterIndexReconciler
{
    private static readonly Regex ChineseNumberedHeading = new(
        @"^第\\s*(?<number>[0-9０-９〇零一二三四五六七八九十百千万两兩壹贰貳叁參肆伍陆陸柒捌玖拾佰仟]+)\\s*(?<unit>章|回|节|節|集|篇)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishNumberedHeading = new(
        @"^(?:chapter|ch\\.?)\\s*#?\\s*(?<number>\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SpecialHeading = new(
        @"^(?<name>序章|楔子|引子|序幕|终章|尾声|后记|番外(?:\\s*\\d+)?|prologue|epilogue)",
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

        var oldGroups = GroupByIdentity(oldChapters);
        var newGroups = GroupByIdentity(newChapters);
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
                NovelChapter current = newGroup[index] with
                {
                    Id = previous.Id,
                    IsActive = true,
                };
                matchedOld.Add(previous.Id);
                matchedNew[current.Id] = current;
                if (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal))
                    renamed.Add(new NovelChapterTitleChange(previous, current));
            }
        }

        var result = new List<NovelChapter>(newChapters.Length);
        var added = new List<NovelChapter>();
        foreach (NovelChapter chapter in newChapters)
        {
            NovelChapter? match = matchedNew.Values.FirstOrDefault(item =>
                item.StartOffset == chapter.StartOffset
                && item.DocumentRevision == chapter.DocumentRevision
                && string.Equals(GetIdentity(chapter), GetIdentity(item), StringComparison.Ordinal));
            if (match is not null)
            {
                result.Add(match);
                continue;
            }

            // The matched object may have moved since its prior offset. Resolve by the new chapter's
            // identity and occurrence order rather than requiring equal offsets.
            string identity = GetIdentity(chapter);
            List<NovelChapter>? group = newGroups.GetValueOrDefault(identity);
            int occurrence = group?.IndexOf(chapter) ?? -1;
            if (occurrence >= 0 && occurrence < (oldGroups.GetValueOrDefault(identity)?.Count ?? 0))
            {
                NovelChapter old = oldGroups[identity][occurrence];
                if (matchedOld.Contains(old.Id) && matchedNew.TryGetValue(old.Id, out NovelChapter? matched))
                {
                    result.Add(matched);
                    continue;
                }
            }

            result.Add(chapter with { IsActive = true });
            added.Add(chapter with { IsActive = true });
        }

        NovelChapter[] removed = oldChapters.Where(chapter => !matchedOld.Contains(chapter.Id)).ToArray();
        return new NovelChapterIndexReconciliation(
            result.OrderBy(chapter => chapter.StartOffset).ToArray(),
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
