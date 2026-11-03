namespace FujiyNotepad.Core;

/// <summary>Kind of change reported by the document diff engine.</summary>
public enum DocumentDiffKind
{
    Inserted,
    Deleted,
    Replaced
}

/// <summary>
/// One changed region expressed in UTF-16 document coordinates. Ranges are half-open.
/// Insertions have an empty old range; deletions have an empty new range.
/// </summary>
public sealed record DocumentDiffHunk(
    long OldStart,
    long OldEnd,
    long NewStart,
    long NewEnd,
    DocumentDiffKind Kind)
{
    public long OldLength => OldEnd - OldStart;
    public long NewLength => NewEnd - NewStart;
    public long Delta => NewLength - OldLength;

    public void Validate()
    {
        if (OldStart < 0 || OldEnd < OldStart || NewStart < 0 || NewEnd < NewStart)
            throw new ArgumentOutOfRangeException(nameof(DocumentDiffHunk), "Diff ranges must be non-negative half-open ranges.");
        if (Kind == DocumentDiffKind.Inserted && (OldLength != 0 || NewLength == 0)
            || Kind == DocumentDiffKind.Deleted && (OldLength == 0 || NewLength != 0)
            || Kind == DocumentDiffKind.Replaced && (OldLength == 0 || NewLength == 0))
            throw new ArgumentException("The hunk kind does not match its old/new range lengths.", nameof(DocumentDiffHunk));
    }
}

public enum AnchorRemapStatus
{
    Mapped,
    NeedsReview,
    Orphaned
}

public sealed record AnchorRemapResult(
    long StartOffset,
    long EndOffset,
    AnchorRemapStatus Status,
    string? Reason = null);

/// <summary>
/// Maps old UTF-16 offsets through an ordered diff. This is deliberately separate from diff
/// generation: the diff engine supplies hunks, and this class applies them consistently to
/// overlapping anchors and chapter ranges.
/// </summary>
public static class DocumentDiffMapper
{
    public static AnchorRemapResult MapRange(long start, long end, IReadOnlyList<DocumentDiffHunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(hunks);
        if (start < 0 || end <= start)
            throw new ArgumentOutOfRangeException(nameof(start), "The source range must be non-empty.");

        ValidateHunks(hunks);
        long deltaBeforeStart = 0;
        bool intersectsChange = false;
        bool fullyDeleted = false;

        foreach (DocumentDiffHunk hunk in hunks)
        {
            if (hunk.OldEnd <= start)
            {
                // An insertion exactly at the anchor start is before the anchored text.
                deltaBeforeStart = checked(deltaBeforeStart + hunk.Delta);
                continue;
            }

            if (hunk.OldStart >= end)
                break;

            if (hunk.OldLength == 0)
            {
                // An insertion strictly inside an anchor changes the anchored span; insertions
                // at its boundaries are outside it.
                if (hunk.OldStart > start && hunk.OldStart < end)
                    intersectsChange = true;
                continue;
            }

            if (hunk.OldStart < end && hunk.OldEnd > start)
            {
                intersectsChange = true;
                if (hunk.Kind == DocumentDiffKind.Deleted
                    && hunk.OldStart <= start && hunk.OldEnd >= end)
                    fullyDeleted = true;
            }
        }

        long mappedStart = checked(start + deltaBeforeStart);
        if (!intersectsChange)
        {
            long deltaBeforeEnd = 0;
            foreach (DocumentDiffHunk hunk in hunks)
            {
                // Insertions at the end boundary are outside the anchor.
                bool beforeEnd = hunk.OldLength == 0
                    ? hunk.OldStart < end
                    : hunk.OldEnd <= end;
                if (beforeEnd) deltaBeforeEnd = checked(deltaBeforeEnd + hunk.Delta);
                else if (hunk.OldStart >= end) break;
            }
            return new AnchorRemapResult(mappedStart, checked(end + deltaBeforeEnd),
                AnchorRemapStatus.Mapped);
        }

        if (fullyDeleted)
            return new AnchorRemapResult(mappedStart, mappedStart, AnchorRemapStatus.Orphaned,
                "The complete anchored source range was deleted.");

        // Do not silently attach a note to replacement text. The caller must re-find the quote
        // using its saved quote and context, or ask the user to resolve it.
        long approximateEnd = checked(end + deltaBeforeStart);
        return new AnchorRemapResult(mappedStart, Math.Max(mappedStart, approximateEnd),
            AnchorRemapStatus.NeedsReview,
            "The changed region intersects this anchor; quote/context re-anchoring is required.");
    }

    public static AnchorRemapResult MapChapterRange(long start, long end,
        IReadOnlyList<DocumentDiffHunk> hunks)
    {
        AnchorRemapResult result = MapRange(start, end, hunks);
        return result with
        {
            Status = result.Status == AnchorRemapStatus.Orphaned
                ? AnchorRemapStatus.NeedsReview
                : result.Status,
            Reason = result.Status == AnchorRemapStatus.Orphaned
                ? "The chapter range was deleted and must be reviewed."
                : result.Reason
        };
    }

    private static void ValidateHunks(IReadOnlyList<DocumentDiffHunk> hunks)
    {
        long previousOldEnd = 0;
        long previousNewEnd = 0;
        bool first = true;
        foreach (DocumentDiffHunk hunk in hunks)
        {
            hunk.Validate();
            if (!first && (hunk.OldStart < previousOldEnd || hunk.NewStart < previousNewEnd))
                throw new ArgumentException("Diff hunks must be ordered and non-overlapping in both documents.", nameof(hunks));
            previousOldEnd = hunk.OldEnd;
            previousNewEnd = hunk.NewEnd;
            first = false;
        }
    }
}
