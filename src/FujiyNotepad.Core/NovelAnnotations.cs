namespace FujiyNotepad.Core;

/// <summary>A user-defined annotation category.</summary>
public sealed record AnnotationCategory(
    Guid Id,
    string Name,
    string? Color = null,
    int SortOrder = 0,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// An independent note. It has no ownership relationship with chapter notes or multi-chapter notes.
/// A note may have any number of source anchors.
/// </summary>
public sealed record NovelAnnotation(
    Guid Id,
    Guid? CategoryId,
    string? Title,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One source-text occurrence associated with an annotation. Ranges are UTF-16 offsets in the
/// project's current document and use [StartOffset, EndOffset). Overlap is intentionally allowed.
/// </summary>
public sealed record AnnotationAnchor(
    Guid Id,
    Guid AnnotationId,
    long StartOffset,
    long EndOffset,
    string Quote,
    Guid? ChapterId = null,
    bool IsOrphaned = false,
    long DocumentRevision = 1)
{
    public long Length => Math.Max(0, EndOffset - StartOffset);
}

/// <summary>A chapter detected in the current project document.</summary>
public sealed record NovelChapter(
    Guid Id,
    string Title,
    long StartOffset,
    long EndOffset,
    int ChapterNumber,
    long DocumentRevision = 1);

/// <summary>
/// A replacement in the document's UTF-16 coordinate space. The removed range is
/// [StartOffset, StartOffset + DeletedLength), and InsertedLength is the replacement's UTF-16 length.
/// </summary>
public readonly record struct DocumentEdit(
    long StartOffset,
    long DeletedLength,
    long InsertedLength)
{
    public long DeletedEnd => checked(StartOffset + DeletedLength);

    public void Validate()
    {
        if (StartOffset < 0 || DeletedLength < 0 || InsertedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DocumentEdit), "Edit offsets and lengths cannot be negative.");
        }
        _ = DeletedEnd;
    }
}

/// <summary>The transformed location of an anchor after a document edit.</summary>
public readonly record struct AnchorTransformResult(
    long StartOffset,
    long EndOffset,
    bool IsOrphaned);

/// <summary>
/// Transforms annotation anchors after one text replacement. Insertions at an anchor's start are
/// considered before the anchored text; insertions strictly inside an anchor expand it. Deletions
/// shrink partial overlaps. If an edit removes the entire anchored quote, the anchor is retained
/// but marked orphaned instead of silently pointing at unrelated replacement text.
/// </summary>
public static class AnnotationAnchorTransformer
{
    public static AnchorTransformResult Apply(AnnotationAnchor anchor, DocumentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        edit.Validate();

        long start = anchor.StartOffset;
        long end = anchor.EndOffset;
        if (start < 0 || end < start)
        {
            throw new ArgumentException("The anchor range is invalid.", nameof(anchor));
        }

        long p = edit.StartOffset;
        long inserted = edit.InsertedLength;

        if (edit.DeletedLength == 0)
        {
            if (p <= start)
            {
                start = checked(start + inserted);
                end = checked(end + inserted);
            }
            else if (p < end)
            {
                end = checked(end + inserted);
            }

            return new AnchorTransformResult(start, end, anchor.IsOrphaned);
        }

        long deletedEnd = edit.DeletedEnd;
        bool fullyRemoved = start < end && p <= start && deletedEnd >= end;

        long newStart = MapStart(start, p, deletedEnd, inserted);
        long newEnd = MapEnd(end, p, deletedEnd, inserted);
        bool orphaned = anchor.IsOrphaned || fullyRemoved || newEnd <= newStart;

        if (newEnd < newStart)
        {
            newEnd = newStart;
        }

        return new AnchorTransformResult(newStart, newEnd, orphaned);
    }

    private static long MapStart(long value, long editStart, long editEnd, long insertedLength)
    {
        if (value < editStart) return value;
        if (value >= editEnd) return checked(value - (editEnd - editStart) + insertedLength);
        return editStart;
    }

    private static long MapEnd(long value, long editStart, long editEnd, long insertedLength)
    {
        if (value <= editStart) return value;
        if (value >= editEnd) return checked(value - (editEnd - editStart) + insertedLength);
        return checked(editStart + insertedLength);
    }
}
