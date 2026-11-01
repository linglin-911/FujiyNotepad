using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FujiyNotepad.Core;

public enum ExternalEditMarkerTargetKind
{
    AnnotationAnchor = 1,
    Chapter = 2
}

public enum ExternalEditMarkerEndpoint
{
    Start = 1,
    End = 2
}

/// <summary>A hidden, session-scoped marker surrounding one source range in an exported working copy.</summary>
public sealed record ExternalEditMarker(
    Guid SessionId,
    Guid TargetId,
    ExternalEditMarkerTargetKind TargetKind,
    ExternalEditMarkerEndpoint Endpoint);

public sealed record ExternalEditMarkerInsertion(long Offset, ExternalEditMarker Marker);

public enum ExternalEditMarkerTargetStatus
{
    Intact,
    MissingStart,
    MissingEnd,
    MissingBoth,
    DuplicateStart,
    DuplicateEnd,
    DuplicateBoth
}

public sealed record ExternalEditMarkerTargetResult(
    Guid TargetId,
    ExternalEditMarkerTargetKind TargetKind,
    ExternalEditMarkerTargetStatus Status,
    long? StartOffset,
    long? EndOffset);

public sealed record ExternalEditMarkerValidationResult(
    IReadOnlyList<ExternalEditMarkerTargetResult> Targets,
    IReadOnlyList<ExternalEditMarker> UnexpectedMarkers,
    int MalformedMarkerCount)
{
    public bool HasIssues => MalformedMarkerCount > 0 || UnexpectedMarkers.Count > 0
        || Targets.Any(target => target.Status != ExternalEditMarkerTargetStatus.Intact);
}

/// <summary>
/// Creates, streams and validates invisible Unicode tag markers used only in third-party editing
/// copies. The tag characters are default-ignorable, but editors or text-cleaning tools may remove
/// them; the validation result must therefore always be checked before applying an edit.
/// </summary>
public static class ExternalEditMarkerCodec
{
    private const int TagBase = 0xE0000;
    private const char TagSequenceStart = '\uDB40'; // U+E0001 high surrogate
    private const char TagSequenceEnd = '\uDB40';   // U+E007F low-surrogate pair differs below
    private const string PayloadPrefix = "FJ1";

    public static IReadOnlyList<ExternalEditMarkerInsertion> CreateInsertions(
        Guid sessionId,
        IEnumerable<AnnotationAnchor> anchors,
        IEnumerable<NovelChapter> chapters)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(chapters);

        var insertions = new List<ExternalEditMarkerInsertion>();
        foreach (AnnotationAnchor anchor in anchors)
        {
            if (anchor.IsOrphaned) continue;
            if (anchor.StartOffset < 0 || anchor.EndOffset <= anchor.StartOffset)
                throw new ArgumentException("An annotation anchor has an invalid range.", nameof(anchors));
            insertions.Add(new(anchor.StartOffset, new(sessionId, anchor.Id,
                ExternalEditMarkerTargetKind.AnnotationAnchor, ExternalEditMarkerEndpoint.Start)));
            insertions.Add(new(anchor.EndOffset, new(sessionId, anchor.Id,
                ExternalEditMarkerTargetKind.AnnotationAnchor, ExternalEditMarkerEndpoint.End)));
        }

        foreach (NovelChapter chapter in chapters)
        {
            if (chapter.StartOffset < 0 || chapter.EndOffset <= chapter.StartOffset)
                throw new ArgumentException("A chapter has an invalid range.", nameof(chapters));
            insertions.Add(new(chapter.StartOffset, new(sessionId, chapter.Id,
                ExternalEditMarkerTargetKind.Chapter, ExternalEditMarkerEndpoint.Start)));
            insertions.Add(new(chapter.EndOffset, new(sessionId, chapter.Id,
                ExternalEditMarkerTargetKind.Chapter, ExternalEditMarkerEndpoint.End)));
        }

        return insertions
            .OrderBy(item => item.Offset)
            .ThenBy(item => item.Marker.TargetKind)
            .ThenBy(item => item.Marker.TargetId)
            .ThenBy(item => item.Marker.Endpoint)
            .ToArray();
    }

    /// <summary>
    /// Writes a marked copy without materializing the complete document. Offsets are UTF-16
    /// character offsets, matching AnnotationAnchor and NovelChapter. The caller chooses the
    /// reader/writer encoding and is responsible for preserving the source BOM policy.
    /// </summary>
    public static async Task WriteMarkedCopyAsync(
        TextReader source,
        TextWriter destination,
        IReadOnlyList<ExternalEditMarkerInsertion> insertions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(insertions);

        long previousOffset = -1;
        foreach (ExternalEditMarkerInsertion insertion in insertions)
        {
            if (insertion.Offset < 0 || insertion.Offset < previousOffset)
                throw new ArgumentException("Marker insertions must be ordered by non-negative offset.", nameof(insertions));
            previousOffset = insertion.Offset;
            _ = Encode(insertion.Marker);
        }

        char[] buffer = new char[16 * 1024];
        long position = 0;
        int next = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cursor = 0;
            long chunkEnd = checked(position + read);
            while (next < insertions.Count && insertions[next].Offset <= chunkEnd)
            {
                long relative = insertions[next].Offset - position;
                if (relative < cursor)
                    throw new ArgumentException("Multiple marker offsets are not in nondecreasing source order.", nameof(insertions));
                int count = checked((int)relative - cursor);
                if (count > 0)
                    await destination.WriteAsync(buffer.AsMemory(cursor, count), cancellationToken).ConfigureAwait(false);

                long markerOffset = insertions[next].Offset;
                while (next < insertions.Count && insertions[next].Offset == markerOffset)
                {
                    await destination.WriteAsync(Encode(insertions[next].Marker).AsMemory(), cancellationToken).ConfigureAwait(false);
                    next++;
                }
                cursor = checked((int)relative);
            }

            if (cursor < read)
                await destination.WriteAsync(buffer.AsMemory(cursor, read - cursor), cancellationToken).ConfigureAwait(false);
            position = chunkEnd;
        }

        while (next < insertions.Count && insertions[next].Offset == position)
        {
            await destination.WriteAsync(Encode(insertions[next].Marker).AsMemory(), cancellationToken).ConfigureAwait(false);
            next++;
        }
        if (next < insertions.Count)
            throw new InvalidDataException($"A marker offset ({insertions[next].Offset}) exceeds the source text length ({position}).");
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans markers incrementally; ordinary document text is discarded by the scanner.</summary>
    public static async Task<(IReadOnlyList<ExternalEditMarker> Markers, int MalformedCount)> ReadMarkersAsync(
        TextReader source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        char[] buffer = new char[16 * 1024];
        var markers = new List<ExternalEditMarker>();
        var payload = new StringBuilder(128);
        bool inMarker = false;
        int malformed = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                if (!inMarker)
                {
                    if (ch == '\uDB40' && i + 1 < read && buffer[i + 1] == '\uDC01')
                    {
                        inMarker = true;
                        payload.Clear();
                        i++;
                    }
                    continue;
                }

                if (ch == '\uDB40' && i + 1 < read && buffer[i + 1] == '\uDC7F')
                {
                    if (TryDecodePayload(payload.ToString(), out ExternalEditMarker? marker))
                        markers.Add(marker!);
                    else
                        malformed++;
                    inMarker = false;
                    payload.Clear();
                    i++;
                    continue;
                }

                if (ch == '\uDB40' && i + 1 < read && buffer[i + 1] >= '\uDC20' && buffer[i + 1] <= '\uDC7E')
                {
                    payload.Append((char)(buffer[++i] - '\uDC00'));
                    continue;
                }

                // Any non-tag character inside a marker makes that marker malformed.
                payload.Append('\0');
            }
        }

        if (inMarker) malformed++;
        return (markers, malformed);
    }

    public static ExternalEditMarkerValidationResult Validate(
        Guid expectedSessionId,
        IEnumerable<ExternalEditMarker> expectedMarkers,
        IReadOnlyList<ExternalEditMarker> observedMarkers,
        int malformedMarkerCount = 0)
    {
        if (expectedSessionId == Guid.Empty) throw new ArgumentException("Session ID cannot be empty.", nameof(expectedSessionId));
        ArgumentNullException.ThrowIfNull(expectedMarkers);
        ArgumentNullException.ThrowIfNull(observedMarkers);

        var expected = expectedMarkers
            .Where(marker => marker.SessionId == expectedSessionId)
            .GroupBy(marker => (marker.TargetId, marker.TargetKind))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var observed = observedMarkers
            .Where(marker => marker.SessionId == expectedSessionId)
            .GroupBy(marker => (marker.TargetId, marker.TargetKind))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var results = new List<ExternalEditMarkerTargetResult>();
        foreach (var pair in expected)
        {
            ExternalEditMarker[] found = observed.GetValueOrDefault(pair.Key) ?? Array.Empty<ExternalEditMarker>();
            int starts = found.Count(marker => marker.Endpoint == ExternalEditMarkerEndpoint.Start);
            int ends = found.Count(marker => marker.Endpoint == ExternalEditMarkerEndpoint.End);
            ExternalEditMarkerTargetStatus status = (starts > 1, ends > 1, starts == 0, ends == 0) switch
            {
                (true, true, _, _) => ExternalEditMarkerTargetStatus.DuplicateBoth,
                (true, false, _, _) => ExternalEditMarkerTargetStatus.DuplicateStart,
                (false, true, _, _) => ExternalEditMarkerTargetStatus.DuplicateEnd,
                (_, _, true, true) => ExternalEditMarkerTargetStatus.MissingBoth,
                (_, _, true, false) => ExternalEditMarkerTargetStatus.MissingStart,
                (_, _, false, true) => ExternalEditMarkerTargetStatus.MissingEnd,
                _ => ExternalEditMarkerTargetStatus.Intact
            };
            results.Add(new(pair.Key.TargetId, pair.Key.TargetKind, status,
                found.FirstOrDefault(marker => marker.Endpoint == ExternalEditMarkerEndpoint.Start) is null ? null : null,
                found.FirstOrDefault(marker => marker.Endpoint == ExternalEditMarkerEndpoint.End) is null ? null : null));
        }

        var expectedKeys = expected.Keys.ToHashSet();
        var unexpected = observedMarkers
            .Where(marker => marker.SessionId != expectedSessionId
                || !expectedKeys.Contains((marker.TargetId, marker.TargetKind)))
            .ToArray();
        return new ExternalEditMarkerValidationResult(results, unexpected, malformedMarkerCount);
    }

    public static string Encode(ExternalEditMarker marker)
    {
        if (marker.SessionId == Guid.Empty || marker.TargetId == Guid.Empty)
            throw new ArgumentException("Marker IDs cannot be empty.", nameof(marker));
        if (!Enum.IsDefined(marker.TargetKind) || !Enum.IsDefined(marker.Endpoint))
            throw new ArgumentOutOfRangeException(nameof(marker));

        string body = string.Join('|', PayloadPrefix, marker.SessionId.ToString("N"),
            marker.TargetId.ToString("N"), ((int)marker.TargetKind).ToString(CultureInfo.InvariantCulture),
            ((int)marker.Endpoint).ToString(CultureInfo.InvariantCulture));
        string payload = body + "|" + Checksum(body);
        var result = new StringBuilder(payload.Length + 4);
        result.Append(char.ConvertFromUtf32(0xE0001));
        foreach (char ch in payload)
        {
            if (ch > 0x7F) throw new InvalidOperationException("Marker payload must be ASCII.");
            result.Append(char.ConvertFromUtf32(TagBase + ch));
        }
        result.Append(char.ConvertFromUtf32(0xE007F));
        return result.ToString();
    }

    private static bool TryDecodePayload(string payload, out ExternalEditMarker? marker)
    {
        marker = null;
        if (payload.IndexOf('\0') >= 0) return false;
        string[] parts = payload.Split('|');
        if (parts.Length != 6 || parts[0] != PayloadPrefix) return false;
        string body = string.Join('|', parts.Take(5));
        if (!string.Equals(parts[5], Checksum(body), StringComparison.Ordinal)) return false;
        if (!Guid.TryParseExact(parts[1], "N", out Guid sessionId)
            || !Guid.TryParseExact(parts[2], "N", out Guid targetId)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int kind)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int endpoint)
            || !Enum.IsDefined((ExternalEditMarkerTargetKind)kind)
            || !Enum.IsDefined((ExternalEditMarkerEndpoint)endpoint))
            return false;

        marker = new ExternalEditMarker(sessionId, targetId,
            (ExternalEditMarkerTargetKind)kind, (ExternalEditMarkerEndpoint)endpoint);
        return true;
    }

    private static string Checksum(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)).AsSpan(0, 4));
}
