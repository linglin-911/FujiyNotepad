using System.Text;
using System.Text.Json;

namespace FujiyNotepad.Core;

/// <summary>
/// A safe external-edit round trip. The project document remains untouched while the user edits
/// the exported copy. The immutable baseline and its fingerprint are kept inside the project.
/// </summary>
public sealed class ExternalEditSession
{
    private ExternalEditSession(string sessionDirectory, string baselinePath, string exportPath,
        ExternalEditSessionMetadata metadata)
    {
        SessionDirectory = sessionDirectory;
        BaselinePath = baselinePath;
        ExportPath = exportPath;
        Metadata = metadata;
    }

    public string SessionDirectory { get; }
    public string BaselinePath { get; }
    public string ExportPath { get; }
    public ExternalEditSessionMetadata Metadata { get; }
    public string ManifestPath => Path.Combine(SessionDirectory, "session.json");

    /// <summary>Creates a plain, unmarked working copy. Kept for callers that do not need annotation tracking.</summary>
    public static async Task<ExternalEditSession> CreateAsync(NovelProject project, string exportPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        string export = ValidateExportPath(project, exportPath);
        string sessionId = CreateSessionDirectoryName();
        string sessionDirectory = Path.Combine(project.EditSessionsDirectory, sessionId);
        Directory.CreateDirectory(sessionDirectory);
        string baseline = Path.Combine(sessionDirectory, "baseline" + Path.GetExtension(project.DocumentPath));
        string temporaryBaseline = baseline + ".tmp";
        string temporaryExport = export + "." + sessionId + ".exporting";
        bool exportCreated = false;

        try
        {
            await CopyFileAsync(project.DocumentPath, temporaryBaseline, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryBaseline, baseline);
            string hash = await NovelProject.ComputeSha256Async(baseline, cancellationToken).ConfigureAwait(false);
            long length = new FileInfo(baseline).Length;
            await CopyFileAsync(baseline, temporaryExport, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryExport, export);
            exportCreated = true;

            var metadata = new ExternalEditSessionMetadata(
                Guid.NewGuid(), project.Metadata.Id, project.Metadata.DocumentRevision,
                length, hash, DateTimeOffset.UtcNow, Path.GetFileName(export),
                Array.Empty<ExternalEditMarker>(), null, null, Path.GetFileName(baseline));
            var session = new ExternalEditSession(sessionDirectory, baseline, export, metadata);
            await session.SaveManifestAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            CleanupFailedCreate(temporaryBaseline, temporaryExport, baseline, sessionDirectory, export, exportCreated);
            throw;
        }
    }

    /// <summary>
    /// Creates an external-edit copy with invisible range markers for every active annotation anchor
    /// and every chapter. The baseline remains byte-for-byte identical to the project document.
    /// </summary>
    public static async Task<ExternalEditSession> CreateMarkedAsync(
        NovelProject project,
        NovelAnnotationStore annotationStore,
        string exportPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(annotationStore);
        string export = ValidateExportPath(project, exportPath);
        Guid sessionGuid = Guid.NewGuid();
        string sessionFolder = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_ffff") + "_" + sessionGuid.ToString("N")[..8];
        string sessionDirectory = Path.Combine(project.EditSessionsDirectory, sessionFolder);
        Directory.CreateDirectory(sessionDirectory);
        string baseline = Path.Combine(sessionDirectory, "baseline" + Path.GetExtension(project.DocumentPath));
        string temporaryBaseline = baseline + ".tmp";
        string temporaryExport = export + "." + sessionFolder + ".exporting";
        bool exportCreated = false;

        try
        {
            // Snapshot first. The marked export is generated from this immutable snapshot, never
            // from the live project path, so concurrent external writes cannot mix two versions.
            await CopyFileAsync(project.DocumentPath, temporaryBaseline, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryBaseline, baseline);
            string hash = await NovelProject.ComputeSha256Async(baseline, cancellationToken).ConfigureAwait(false);
            long byteLength = new FileInfo(baseline).Length;

            if (byteLength != project.Metadata.DocumentLength
                || !string.Equals(hash, project.Metadata.DocumentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The project document changed outside FujiyNotepad. Reopen or refresh the project before exporting annotation markers.");

            IReadOnlyList<AnnotationAnchor> anchors = await annotationStore.GetAllAnchorsAsync(cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<NovelChapter> chapters = await annotationStore.GetChaptersAsync(cancellationToken)
                .ConfigureAwait(false);

            AnnotationAnchor? staleAnchor = anchors.FirstOrDefault(anchor =>
                !anchor.IsOrphaned && anchor.DocumentRevision != project.Metadata.DocumentRevision);
            if (staleAnchor is not null)
                throw new InvalidOperationException($"Annotation anchor {staleAnchor.Id} belongs to document revision {staleAnchor.DocumentRevision}, not the current revision {project.Metadata.DocumentRevision}.");

            NovelChapter? staleChapter = chapters.FirstOrDefault(chapter =>
                chapter.DocumentRevision != project.Metadata.DocumentRevision);
            if (staleChapter is not null)
                throw new InvalidOperationException($"Chapter {staleChapter.Id} belongs to document revision {staleChapter.DocumentRevision}, not the current revision {project.Metadata.DocumentRevision}.");

            TextEncoding sourceEncoding;
            using (var byteSource = new FileByteSource(baseline))
                sourceEncoding = EncodingDetector.Detect(byteSource);

            IReadOnlyList<ExternalEditMarkerInsertion> insertions =
                ExternalEditMarkerCodec.CreateInsertions(sessionGuid, anchors, chapters);
            ExternalEditMarker[] expectedMarkers = insertions.Select(item => item.Marker).ToArray();

            // Windows-1252 cannot represent Unicode tag characters. Preserve every decoded source
            // character by exporting this one working copy as UTF-8 with BOM in that case.
            TextEncoding exportEncoding = CanRoundTripMarkers(sourceEncoding)
                ? sourceEncoding
                : TextEncoding.Utf8Bom;

            await WriteMarkedFileAsync(baseline, temporaryExport, sourceEncoding, exportEncoding,
                insertions, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryExport, export);
            exportCreated = true;

            var metadata = new ExternalEditSessionMetadata(
                sessionGuid, project.Metadata.Id, project.Metadata.DocumentRevision,
                byteLength, hash, DateTimeOffset.UtcNow, Path.GetFileName(export),
                expectedMarkers, sourceEncoding.Id, exportEncoding.Id, Path.GetFileName(baseline));
            var session = new ExternalEditSession(sessionDirectory, baseline, export, metadata);
            await session.SaveManifestAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            CleanupFailedCreate(temporaryBaseline, temporaryExport, baseline, sessionDirectory, export, exportCreated);
            throw;
        }
    }

    /// <summary>Reopens a persisted external-edit session after the application has restarted.</summary>
    public static async Task<ExternalEditSession> OpenAsync(
        string sessionDirectory,
        string editedFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(editedFilePath);
        string directory = Path.GetFullPath(sessionDirectory);
        string manifestPath = Path.Combine(directory, "session.json");
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ExternalEditSessionMetadata? metadata = await JsonSerializer.DeserializeAsync(
            stream, NovelProjectJsonContext.Default.ExternalEditSessionMetadata, cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.Id == Guid.Empty || metadata.ProjectId == Guid.Empty
            || string.IsNullOrWhiteSpace(metadata.BaseDocumentSha256))
            throw new InvalidDataException("The external-edit session manifest is missing or invalid.");

        string? baselineName = metadata.BaselineFileName;
        if (string.IsNullOrWhiteSpace(baselineName))
            baselineName = Directory.EnumerateFiles(directory, "baseline.*").Select(Path.GetFileName).SingleOrDefault();
        if (string.IsNullOrWhiteSpace(baselineName) || !string.Equals(Path.GetFileName(baselineName), baselineName, StringComparison.Ordinal))
            throw new InvalidDataException("The external-edit session baseline filename is invalid.");
        string baselinePath = Path.GetFullPath(Path.Combine(directory, baselineName));
        if (!baselinePath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(baselinePath))
            throw new InvalidDataException("The external-edit session baseline is missing or points outside the session folder.");
        var baselineInfo = new FileInfo(baselinePath);
        if (baselineInfo.Length != metadata.BaseDocumentLength)
            throw new InvalidDataException("The external-edit session baseline length does not match its manifest.");
        string baselineHash = await NovelProject.ComputeSha256Async(baselinePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(baselineHash, metadata.BaseDocumentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The external-edit session baseline hash does not match its manifest.");

        return new ExternalEditSession(directory, baselinePath, Path.GetFullPath(editedFilePath), metadata);
    }

    /// <summary>
    /// Scans a selected edited copy and validates its markers against the immutable marker list
    /// stored in this session manifest. Returned offsets are in the edited text with markers removed.
    /// </summary>
    public async Task<ExternalEditMarkerValidationResult> ValidateEditedCopyMarkersAsync(
        string editedFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editedFilePath);
        string path = Path.GetFullPath(editedFilePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The edited copy does not exist.", path);

        TextEncoding encoding;
        using (var byteSource = new FileByteSource(path))
            encoding = EncodingDetector.Detect(byteSource);

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(input, encoding.Encoding,
            detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var parsed = await ExternalEditMarkerCodec.ReadMarkersAsync(reader, cancellationToken).ConfigureAwait(false);
        return ExternalEditMarkerCodec.Validate(Metadata.Id,
            Metadata.ExpectedMarkers ?? Array.Empty<ExternalEditMarker>(),
            parsed.Markers, parsed.MalformedCount);
    }

    /// <summary>
    /// Confirms that the project's active document is still exactly the version exported for this
    /// session. If not, the caller must not apply this session's diff.
    /// </summary>
    public async Task ValidateProjectBaselineAsync(NovelProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Metadata.Id != Metadata.ProjectId)
            throw new InvalidOperationException("This edit session belongs to a different novel project.");
        if (project.Metadata.DocumentRevision != Metadata.BaseDocumentRevision)
            throw new InvalidOperationException("The project document revision changed after export.");
        string currentHash = await NovelProject.ComputeSha256Async(project.DocumentPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentHash, Metadata.BaseDocumentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The project document changed after export. This external edit session is stale.");
    }

    private async Task SaveManifestAsync(CancellationToken cancellationToken)
    {
        string temporary = ManifestPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, Metadata,
                    NovelProjectJsonContext.Default.ExternalEditSessionMetadata, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ManifestPath);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static string ValidateExportPath(NovelProject project, string exportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
        string export = Path.GetFullPath(exportPath);
        if (string.Equals(export, Path.GetFullPath(project.DocumentPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The external edit export must not overwrite the project's active document.");
        if (File.Exists(export))
            throw new IOException("The export destination already exists. Choose a new filename to preserve previous edits.");
        string? exportDirectory = Path.GetDirectoryName(export);
        if (string.IsNullOrEmpty(exportDirectory)) throw new IOException("The export path has no parent directory.");
        Directory.CreateDirectory(exportDirectory);
        return export;
    }

    private static string CreateSessionDirectoryName()
        => DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_ffff") + "_" + Guid.NewGuid().ToString("N")[..8];

    private static bool CanRoundTripMarkers(TextEncoding encoding)
    {
        string sample = ExternalEditMarkerCodec.Encode(new ExternalEditMarker(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ExternalEditMarkerTargetKind.AnnotationAnchor,
            ExternalEditMarkerEndpoint.Start));
        return string.Equals(encoding.Encoding.GetString(encoding.Encoding.GetBytes(sample)),
            sample, StringComparison.Ordinal);
    }

    private static async Task WriteMarkedFileAsync(
        string sourcePath,
        string destinationPath,
        TextEncoding sourceEncoding,
        TextEncoding exportEncoding,
        IReadOnlyList<ExternalEditMarkerInsertion> insertions,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(input, sourceEncoding.Encoding,
            detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);

        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (exportEncoding.Bom.Length > 0)
            await output.WriteAsync(exportEncoding.Bom, cancellationToken).ConfigureAwait(false);

        using var writer = new StreamWriter(output, exportEncoding.Encoding,
            bufferSize: 64 * 1024, leaveOpen: true);
        await ExternalEditMarkerCodec.WriteMarkedCopyAsync(reader, writer, insertions, cancellationToken)
            .ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void CleanupFailedCreate(string temporaryBaseline, string temporaryExport,
        string baseline, string sessionDirectory, string export, bool exportCreated)
    {
        TryDelete(temporaryBaseline);
        TryDelete(temporaryExport);
        TryDelete(baseline);
        TryDelete(ManifestPathFor(sessionDirectory));
        TryDelete(ManifestPathFor(sessionDirectory) + ".tmp");
        TryDeleteDirectoryIfEmpty(sessionDirectory);
        if (exportCreated) TryDelete(export);
    }

    private static string ManifestPathFor(string sessionDirectory) => Path.Combine(sessionDirectory, "session.json");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { }
    }
}

public sealed record ExternalEditSessionMetadata(
    Guid Id,
    Guid ProjectId,
    long BaseDocumentRevision,
    long BaseDocumentLength,
    string BaseDocumentSha256,
    DateTimeOffset CreatedAt,
    string ExportFileName,
    IReadOnlyList<ExternalEditMarker>? ExpectedMarkers = null,
    string? SourceEncodingId = null,
    string? ExportEncodingId = null,
    string? BaselineFileName = null);
