using System.Globalization;

namespace FujiyNotepad.Presentation;

/// <summary>
/// Maps UTF-16 indexes to valid Unicode text-element (grapheme) boundaries. This keeps caret navigation
/// and hit-test ranges from splitting surrogate pairs or combining sequences. Native text-layout backends
/// may impose additional shaping-cluster rules, but must at least preserve these boundaries.
/// </summary>
internal sealed class TextElementBoundaryMap
{
    private readonly int[] boundaries;
    private readonly int sourceLength;

    public TextElementBoundaryMap(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        sourceLength = sourceText.Length;

        int[] starts = StringInfo.ParseCombiningCharacters(sourceText);
        if (starts.Length == 0)
        {
            boundaries = new[] { 0 };
            return;
        }

        boundaries = new int[starts.Length + 1];
        Array.Copy(starts, boundaries, starts.Length);
        boundaries[^1] = sourceLength;
    }

    public int SnapToNearestBoundary(int utf16Index)
    {
        int index = Math.Clamp(utf16Index, 0, sourceLength);
        int insertion = Array.BinarySearch(boundaries, index);
        if (insertion >= 0)
            return boundaries[insertion];

        insertion = ~insertion;
        int previous = boundaries[Math.Max(0, insertion - 1)];
        int next = boundaries[Math.Min(boundaries.Length - 1, insertion)];

        // Prefer the leading edge on an exact tie. This is stable when a pointer or stale caret
        // position lands in the middle of a surrogate pair or combining sequence.
        return index - previous <= next - index ? previous : next;
    }

    public int SnapToPreviousBoundary(int utf16Index)
    {
        int index = Math.Clamp(utf16Index, 0, sourceLength);
        int insertion = Array.BinarySearch(boundaries, index);
        if (insertion >= 0)
            return boundaries[Math.Max(0, insertion - 1)];

        insertion = ~insertion;
        return boundaries[Math.Max(0, insertion - 1)];
    }

    public int SnapToNextBoundary(int utf16Index)
    {
        int index = Math.Clamp(utf16Index, 0, sourceLength);
        int insertion = Array.BinarySearch(boundaries, index);
        if (insertion >= 0)
            return boundaries[Math.Min(boundaries.Length - 1, insertion + 1)];

        insertion = ~insertion;
        return boundaries[Math.Min(boundaries.Length - 1, insertion)];
    }

    /// <summary>Returns the first valid text-element boundary at or after the supplied UTF-16 index.</summary>
    public int SnapToBoundaryAtOrAfter(int utf16Index)
    {
        int index = Math.Clamp(utf16Index, 0, sourceLength);
        int insertion = Array.BinarySearch(boundaries, index);
        if (insertion >= 0)
            return boundaries[insertion];

        insertion = ~insertion;
        return boundaries[Math.Min(boundaries.Length - 1, insertion)];
    }

    public (int Start, int Length) ExpandRange(int utf16Start, int utf16Length)
    {
        int start = Math.Clamp(utf16Start, 0, sourceLength);
        if (utf16Length <= 0)
            return (SnapToNearestBoundary(start), 0);

        int end = (int)Math.Clamp((long)utf16Start + utf16Length, 0L, sourceLength);
        if (end <= start)
            return (SnapToNearestBoundary(start), 0);

        int expandedStart = SnapDown(start);
        int expandedEnd = SnapUp(end);
        return (expandedStart, expandedEnd - expandedStart);
    }

    private int SnapDown(int utf16Index)
    {
        int insertion = Array.BinarySearch(boundaries, utf16Index);
        if (insertion >= 0)
            return boundaries[insertion];

        insertion = ~insertion;
        return boundaries[Math.Max(0, insertion - 1)];
    }

    private int SnapUp(int utf16Index)
    {
        int insertion = Array.BinarySearch(boundaries, utf16Index);
        if (insertion >= 0)
            return boundaries[insertion];

        insertion = ~insertion;
        return boundaries[Math.Min(boundaries.Length - 1, insertion)];
    }
}
