using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FujiyNotepad.Core;

public static class NovelChapterIndexBuilder
{
    public const int CurrentParserVersion = 2;
    private static readonly byte[] NormalizedNewLine = Encoding.UTF8.GetBytes("\n");

    private static readonly Regex ChineseNumberedHeading = new(
        @"^第\s*[0-9０-９〇零一二三四五六七八九十百千万两兩壹贰貳叁參肆伍陆陸柒捌玖拾佰仟]+\s*(?:章|回|节|節|集|篇)(?=$|[\s:：、.．\-—_【\[（(]).*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EnglishNumberedHeading = new(
        @"^(?:chapter|ch\.?)\s*#?\s*\d+(?:\s*[:：、.．\-—_]\s*.*|\s+.*|\s*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SpecialHeading = new(
        @"^(?:序章|楔子|引子|序幕|终章|尾声|后记|番外(?:\s*\d+)?|prologue|epilogue)(?:\s+.*|\s*[:：、.．\-—_].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds chapter ranges and normalized SHA-256 fingerprints in one streaming pass.
    /// Chapter ranges include the heading line; BodyStartOffset points immediately after the
    /// heading line terminator, and BodySha256 covers only the body with line endings normalized to LF.
    /// </summary>
    public static IReadOnlyList<NovelChapter> Build(
        ILineSource lines,
        long documentRevision = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (documentRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(documentRevision));
        if (lines.LineCount == 0)
            return Array.Empty<NovelChapter>();

        ILineEndingSource? endings = lines as ILineEndingSource;
        var chapters = new List<NovelChapter>();
        IncrementalHash? bodyHash = null;
        string? currentTitle = null;
        long currentStart = 0;
        long currentBodyStart = 0;
        long documentOffset = 0;

        try
        {
            for (int lineIndex = 0; lineIndex < lines.LineCount; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines.GetLineUncached(lineIndex);
                string candidate = line.Trim().TrimStart('\uFEFF').Trim();
                bool isHeading = TryParseHeading(candidate, out string title);
                int terminatorLength = endings is null
                    ? (lineIndex < lines.LineCount - 1 ? 1 : 0)
                    : endings.GetLineEnding(lineIndex) switch
                    {
                        LineEnding.CrLf => 2,
                        LineEnding.Lf => 1,
                        LineEnding.None => 0,
                        _ => 1,
                    };

                if (isHeading)
                {
                    if (bodyHash is not null && currentTitle is not null)
                    {
                        chapters.Add(new NovelChapter(
                            Guid.NewGuid(), currentTitle, currentStart, documentOffset,
                            chapters.Count + 1, documentRevision, true, currentBodyStart,
                            Convert.ToHexString(bodyHash.GetHashAndReset())));
                        bodyHash.Dispose();
                    }

                    currentTitle = title;
                    currentStart = documentOffset;
                    currentBodyStart = checked(documentOffset + line.Length + terminatorLength);
                    bodyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }
                else if (bodyHash is not null)
                {
                    if (line.Length > 0)
                        bodyHash.AppendData(Encoding.UTF8.GetBytes(line));
                    if (terminatorLength > 0)
                        bodyHash.AppendData(NormalizedNewLine);
                }

                documentOffset = checked(documentOffset + line.Length + terminatorLength);
            }

            if (bodyHash is not null && currentTitle is not null)
            {
                chapters.Add(new NovelChapter(
                    Guid.NewGuid(), currentTitle, currentStart, documentOffset,
                    chapters.Count + 1, documentRevision, true, currentBodyStart,
                    Convert.ToHexString(bodyHash.GetHashAndReset())));
            }
        }
        finally
        {
            bodyHash?.Dispose();
        }

        return chapters;
    }

    public static bool TryParseHeading(string line, out string title)
    {
        ArgumentNullException.ThrowIfNull(line);
        string candidate = line.Trim().TrimStart('\uFEFF').Trim();
        if (candidate.Length == 0)
        {
            title = "";
            return false;
        }

        if (ChineseNumberedHeading.IsMatch(candidate)
            || EnglishNumberedHeading.IsMatch(candidate)
            || SpecialHeading.IsMatch(candidate))
        {
            title = candidate;
            return true;
        }

        title = "";
        return false;
    }
}
