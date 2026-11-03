using System.Text.RegularExpressions;

namespace FujiyNotepad.Core;

public sealed record NovelChapterTitleChange(NovelChapter Previous, NovelChapter Current);
public sealed record NovelChapterContentChange(NovelChapter Previous, NovelChapter Current);

public sealed record NovelChapterIndexReconciliation(
    IReadOnlyList<NovelChapter> Chapters,
    IReadOnlyList<NovelChapter> Added,
    IReadOnlyList<NovelChapter> Removed,
    IReadOnlyList<NovelChapterTitleChange> Renamed,
    IReadOnlyList<NovelChapterContentChange> ContentChanged)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0
        || Renamed.Count > 0 || ContentChanged.Count > 0;
}

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
        var contentChanged = new List<NovelChapterContentChange>();

        void MatchChapter(NovelChapter previous, NovelChapter detectedChapter)
        {
            NovelChapter current = detectedChapter with { Id = previous.Id, IsActive = true };
            matchedOld.Add(previous.Id);
            matchedNew[detectedChapter.Id] = current;

            if (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal))
                renamed.Add(new NovelChapterTitleChange(previous, current));

            // Empty means a legacy index is establishing its first fingerprint baseline.
            if (!string.IsNullOrEmpty(previous.BodySha256)
                && !string.Equals(previous.BodySha256, current.BodySha256, StringComparison.Ordinal))
            {
                contentChanged.Add(new NovelChapterContentChange(previous, current));
            }
        }

        foreach ((string key, List<NovelChapter> oldGroup) in oldGroups)
        {
            if (!newGroups.TryGetValue(key, out List<NovelChapter>? newGroup))
                continue;

            // Some novels contain repeated headings such as multiple "番外" chapters. Matching
            // those groups by order silently transfers a deleted chapter's ID to a different one.
            // Prefer unique body fingerprints, then unique exact titles. If identity remains
            // ambiguous, archive the unmatched old rows and treat detected rows as new.
            foreach (NovelChapter previous in oldGroup)
            {
                if (matchedOld.Contains(previous.Id) || string.IsNullOrEmpty(previous.BodySha256))
                    continue;

                NovelChapter[] oldFingerprintMatches = oldGroup
                    .Where(candidate => !matchedOld.Contains(candidate.Id)
                        && string.Equals(candidate.BodySha256, previous.BodySha256, StringComparison.Ordinal))
                    .ToArray();
                NovelChapter[] newFingerprintMatches = newGroup
                    .Where(candidate => !matchedNew.ContainsKey(candidate.Id)
                        && string.Equals(candidate.BodySha256, previous.BodySha256, StringComparison.Ordinal))
                    .ToArray();
                if (oldFingerprintMatches.Length == 1 && newFingerprintMatches.Length == 1)
                    MatchChapter(oldFingerprintMatches[0], newFingerprintMatches[0]);
            }

            foreach (NovelChapter previous in oldGroup)
            {
                if (matchedOld.Contains(previous.Id))
                    continue;

                NovelChapter[] oldTitleMatches = oldGroup
                    .Where(candidate => !matchedOld.Contains(candidate.Id)
                        && string.Equals(candidate.Title, previous.Title, StringComparison.Ordinal))
                    .ToArray();
                NovelChapter[] newTitleMatches = newGroup
                    .Where(candidate => !matchedNew.ContainsKey(candidate.Id)
                        && string.Equals(candidate.Title, previous.Title, StringComparison.Ordinal))
                    .ToArray();
                if (oldTitleMatches.Length == 1 && newTitleMatches.Length == 1)
                    MatchChapter(oldTitleMatches[0], newTitleMatches[0]);
            }

            NovelChapter[] remainingOld = oldGroup
                .Where(candidate => !matchedOld.Contains(candidate.Id)).ToArray();
            NovelChapter[] remainingNew = newGroup
                .Where(candidate => !matchedNew.ContainsKey(candidate.Id)).ToArray();
            if (remainingOld.Length == 1 && remainingNew.Length == 1)
                MatchChapter(remainingOld[0], remainingNew[0]);
        }

        var result = new List<NovelChapter>(newChapters.Length);
        var added = new List<NovelChapter>();
        foreach (NovelChapter chapter in newChapters)
        {
            if (matchedNew.TryGetValue(chapter.Id, out NovelChapter? matched))
                result.Add(matched);
            else
            {
                NovelChapter newChapter = chapter with { IsActive = true };
                result.Add(newChapter);
                added.Add(newChapter);
            }
        }

        NovelChapter[] removed = oldChapters.Where(chapter => !matchedOld.Contains(chapter.Id)).ToArray();
        return new NovelChapterIndexReconciliation(result, added, removed, renamed, contentChanged);
    }

    private static Dictionary<string, List<NovelChapter>> GroupByIdentity(IEnumerable<NovelChapter> chapters) =>
        chapters.GroupBy(GetIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
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
