using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class ExternalEditMarkerCodecTests
{
    [Fact]
    public async Task MarkedCopy_RoundTripsInvisibleMarkersAndReportsTextOffsets()
    {
        Guid sessionId = Guid.NewGuid();
        Guid anchorId = Guid.NewGuid();
        var begin = new ExternalEditMarker(sessionId, anchorId,
            ExternalEditMarkerTargetKind.AnnotationAnchor, ExternalEditMarkerEndpoint.Start);
        var end = begin with { Endpoint = ExternalEditMarkerEndpoint.End };
        string original = "第一章\r\n张三来到青云宗。\r\n第二章";
        var insertions = new[]
        {
            new ExternalEditMarkerInsertion(5, begin),
            new ExternalEditMarkerInsertion(18, end)
        };

        using var source = new StringReader(original);
        using var destination = new StringWriter();
        await ExternalEditMarkerCodec.WriteMarkedCopyAsync(source, destination, insertions);

        string markedText = destination.ToString();
        Assert.NotEqual(original, markedText);
        var parsed = await ExternalEditMarkerCodec.ReadMarkersAsync(new StringReader(markedText));
        Assert.Equal(0, parsed.MalformedCount);
        Assert.Equal(2, parsed.Markers.Count);
        Assert.Equal(5, parsed.Markers[0].Offset);
        Assert.Equal(18, parsed.Markers[1].Offset);
        Assert.Equal(begin, parsed.Markers[0].Marker);
        Assert.Equal(end, parsed.Markers[1].Marker);

        ExternalEditMarkerValidationResult validation = ExternalEditMarkerCodec.Validate(
            sessionId, insertions.Select(item => item.Marker).ToArray(), parsed.Markers, parsed.MalformedCount);
        Assert.False(validation.HasIssues);
        Assert.Equal(5L, validation.Targets[0].StartOffset);
        Assert.Equal(18L, validation.Targets[0].EndOffset);
    }

    [Fact]
    public async Task MarkersCanBeInsertedAndParsedAcrossSmallReaderChunks()
    {
        Guid sessionId = Guid.NewGuid();
        var marker = new ExternalEditMarker(sessionId, Guid.NewGuid(),
            ExternalEditMarkerTargetKind.Chapter, ExternalEditMarkerEndpoint.Start);
        string original = new string('甲', 80);
        var insertions = new[] { new ExternalEditMarkerInsertion(37, marker) };

        using var source = new ChunkedTextReader(original, 3);
        using var destination = new StringWriter();
        await ExternalEditMarkerCodec.WriteMarkedCopyAsync(source, destination, insertions);

        var parsed = await ExternalEditMarkerCodec.ReadMarkersAsync(new ChunkedTextReader(destination.ToString(), 2));
        Assert.Equal(0, parsed.MalformedCount);
        Assert.Single(parsed.Markers);
        Assert.Equal(37, parsed.Markers[0].Offset);
        Assert.Equal(marker, parsed.Markers[0].Marker);
    }

    [Fact]
    public void PlannerCreatesIndependentPairsForOverlappingAndCrossingAnchors()
    {
        Guid sessionId = Guid.NewGuid();
        var anchors = new[]
        {
            new AnnotationAnchor(Guid.NewGuid(), Guid.NewGuid(), 10, 50, "broad"),
            new AnnotationAnchor(Guid.NewGuid(), Guid.NewGuid(), 20, 60, "crossing"),
            new AnnotationAnchor(Guid.NewGuid(), Guid.NewGuid(), 25, 35, "nested")
        };
        var chapters = new[] { new NovelChapter(Guid.NewGuid(), "第一章", 0, 100, 1) };

        IReadOnlyList<ExternalEditMarkerInsertion> plan =
            ExternalEditMarkerCodec.CreateInsertions(sessionId, anchors, chapters);

        Assert.Equal(8, plan.Count);
        Assert.Equal(plan.OrderBy(item => item.Offset).Select(item => item.Offset), plan.Select(item => item.Offset));
        Assert.Equal(8, plan.Select(item => (item.Marker.TargetId, item.Marker.Endpoint)).Distinct().Count());
        Assert.All(plan, item => Assert.Equal(sessionId, item.Marker.SessionId));
    }

    [Fact]
    public void ValidationReportsMissingDuplicateAndReversedMarkerPairs()
    {
        Guid sessionId = Guid.NewGuid();
        Guid missingId = Guid.NewGuid();
        Guid duplicateId = Guid.NewGuid();
        Guid reversedId = Guid.NewGuid();
        var expected = new[]
        {
            Pair(sessionId, missingId),
            Pair(sessionId, duplicateId),
            Pair(sessionId, reversedId)
        };
        var observed = new[]
        {
            new ExternalEditMarkerOccurrence(expected[0][0], 10),
            new ExternalEditMarkerOccurrence(expected[1][0], 20),
            new ExternalEditMarkerOccurrence(expected[1][0], 21),
            new ExternalEditMarkerOccurrence(expected[1][1], 30),
            new ExternalEditMarkerOccurrence(expected[2][0], 50),
            new ExternalEditMarkerOccurrence(expected[2][1], 40)
        };

        ExternalEditMarkerValidationResult result =
            ExternalEditMarkerCodec.Validate(sessionId, expected.SelectMany(pair => pair), observed);

        Assert.True(result.HasIssues);
        Assert.Contains(result.Targets, item => item.TargetId == missingId && item.Status == ExternalEditMarkerTargetStatus.MissingEnd);
        Assert.Contains(result.Targets, item => item.TargetId == duplicateId && item.Status == ExternalEditMarkerTargetStatus.DuplicateStart);
        Assert.Contains(result.Targets, item => item.TargetId == reversedId && item.Status == ExternalEditMarkerTargetStatus.Reversed);
    }

    [Fact]
    public async Task ParserCountsTamperedAndTruncatedMarkersAsMalformed()
    {
        Guid sessionId = Guid.NewGuid();
        string encoded = ExternalEditMarkerCodec.Encode(new ExternalEditMarker(
            sessionId, Guid.NewGuid(), ExternalEditMarkerTargetKind.Chapter, ExternalEditMarkerEndpoint.Start));
        string tampered = encoded.Replace(char.ConvertFromUtf32(0xE0046), char.ConvertFromUtf32(0xE0047), StringComparison.Ordinal);

        var parsedTampered = await ExternalEditMarkerCodec.ReadMarkersAsync(new StringReader(tampered));
        var parsedTruncated = await ExternalEditMarkerCodec.ReadMarkersAsync(new StringReader(encoded[..^2]));

        Assert.Equal(1, parsedTampered.MalformedCount);
        Assert.Empty(parsedTampered.Markers);
        Assert.Equal(1, parsedTruncated.MalformedCount);
        ExternalEditMarkerOccurrence survivingStart = Assert.Single(parsedTruncated.Markers);
        Assert.Equal(ExternalEditMarkerEndpoint.Start, survivingStart.Marker.Endpoint);

        var expectedPair = new[]
        {
            survivingStart.Marker,
            survivingStart.Marker with { Endpoint = ExternalEditMarkerEndpoint.End }
        };
        ExternalEditMarkerValidationResult missingEnd = ExternalEditMarkerCodec.Validate(
            sessionId, expectedPair, parsedTruncated.Markers, parsedTruncated.MalformedCount);
        Assert.Contains(missingEnd.Targets, item => item.Status == ExternalEditMarkerTargetStatus.MissingEnd);
    }

    [Fact]
    public async Task StrayEndTokenIsRemovedAndDoesNotShiftLaterMarkerOffsets()
    {
        Guid sessionId = Guid.NewGuid();
        var marker = new ExternalEditMarker(sessionId, Guid.NewGuid(),
            ExternalEditMarkerTargetKind.Chapter, ExternalEditMarkerEndpoint.Start);
        string strayEnd = char.ConvertFromUtf32(0xE007F);
        string text = "abc" + strayEnd + ExternalEditMarkerCodec.Encode(marker) + "xyz";

        var parsed = await ExternalEditMarkerCodec.ReadMarkersAsync(new StringReader(text));

        Assert.Equal(1, parsed.MalformedCount);
        Assert.Single(parsed.Markers);
        Assert.Equal(3, parsed.Markers[0].Offset);
        Assert.Equal(marker, parsed.Markers[0].Marker);
    }

    private static ExternalEditMarker[] Pair(Guid sessionId, Guid targetId)
    {
        var start = new ExternalEditMarker(sessionId, targetId,
            ExternalEditMarkerTargetKind.AnnotationAnchor, ExternalEditMarkerEndpoint.Start);
        return new[] { start, start with { Endpoint = ExternalEditMarkerEndpoint.End } };
    }

    private sealed class ChunkedTextReader(string text, int chunkSize) : TextReader
    {
        private int position;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, chunkSize), text.Length - position);
            if (count <= 0) return ValueTask.FromResult(0);
            text.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(char[] buffer, int index, int count)
        {
            int length = Math.Min(Math.Min(count, chunkSize), text.Length - position);
            if (length <= 0) return 0;
            text.CopyTo(position, buffer, index, length);
            position += length;
            return length;
        }
    }
}
