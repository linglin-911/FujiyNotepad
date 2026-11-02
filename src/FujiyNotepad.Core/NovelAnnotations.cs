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

/// <summary>A compact list projection that avoids one database query per note in the annotation pane.</summary>
public sealed record NovelAnnotationSummary(
    NovelAnnotation Annotation,
    int AnchorCount,
    int OrphanedAnchorCount,
    long? FirstActiveAnchorOffset,
    string? FirstActiveAnchorQuote = null);

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

/// <summary>A category used only by chapter annotations; stored separately from small-annotation categories.</summary>
public sealed record ChapterAnnotationCategory(
    Guid Id,
    string Name,
    string? Color = null,
    int SortOrder = 0,
    DateTimeOffset? CreatedAt = null);

/// <summary>A note bound to one chapter, without a source-text range.</summary>
public sealed record ChapterAnnotation(
    Guid Id,
    Guid ChapterId,
    Guid? CategoryId,
    string? Title,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A list projection that includes the chapter and optional category for one chapter note.</summary>
public sealed record ChapterAnnotationSummary(
    ChapterAnnotation Annotation,
    NovelChapter Chapter,
    ChapterAnnotationCategory? Category);

/// <summary>Records which parser revision indexed a project's chapter headings.</summary>
public sealed record NovelChapterIndexState(
    long DocumentRevision,
    int ParserVersion,
    int ChapterCount,
    DateTimeOffset IndexedAt);

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
        AnchorTransformResult range = TransformRange(anchor.StartOffset, anchor.EndOffset, edit);
        bool fullyRemoved = anchor.StartOffset < anchor.EndOffset
                            && edit.DeletedLength > 0
                            && edit.StartOffset <= anchor.StartOffset
                            && edit.DeletedEnd >= anchor.EndOffset;
        return range with { IsOrphaned = anchor.IsOrphaned || fullyRemoved || range.EndOffset <= range.StartOffset };
    }

    /// <summary>Transforms any half-open document range, for example a chapter boundary.</summary>
    public static AnchorTransformResult TransformRange(long start, long end, DocumentEdit edit)
    {
        edit.Validate();
        if (start < 0 || end < start)
            throw new ArgumentOutOfRangeException(nameof(start), "The range is invalid.");

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
            return new AnchorTransformResult(start, end, false);
        }

        long deletedEnd = edit.DeletedEnd;
        long newStart = MapStart(start, p, deletedEnd, inserted);
        long newEnd = MapEnd(end, p, deletedEnd, inserted);
        if (newEnd < newStart) newEnd = newStart;
        return new AnchorTransformResult(newStart, newEnd, false);
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
