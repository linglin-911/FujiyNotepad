using System.Text.Json;

namespace FujiyNotepad.Core;

/// <summary>
/// A safe external-edit round trip. The project document remains untouched while the user edits
/// the exported copy. The immutable baseline and its fingerprint are kept inside the project.
/// </summary>
public sealed class ExternalEditSession
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

    public static async Task<ExternalEditSession> CreateAsync(NovelProject project, string exportPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
        string export = Path.GetFullPath(exportPath);
        if (string.Equals(export, Path.GetFullPath(project.DocumentPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The external edit export must not overwrite the project's active document.");
        if (File.Exists(export))
            throw new IOException("The export destination already exists. Choose a new filename to preserve previous edits.");
        string? exportDirectory = Path.GetDirectoryName(export);
        if (string.IsNullOrEmpty(exportDirectory)) throw new IOException("The export path has no parent directory.");
        Directory.CreateDirectory(exportDirectory);

        string sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_ffff") + "_" + Guid.NewGuid().ToString("N")[..8];
        string sessionDirectory = Path.Combine(project.EditSessionsDirectory, sessionId);
        Directory.CreateDirectory(sessionDirectory);
        string baseline = Path.Combine(sessionDirectory, "baseline" + Path.GetExtension(project.DocumentPath));
        string temporaryBaseline = baseline + ".tmp";
        try
        {
            await CopyFileAsync(project.DocumentPath, temporaryBaseline, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryBaseline, baseline);
            string hash = await NovelProject.ComputeSha256Async(baseline, cancellationToken).ConfigureAwait(false);
            long length = new FileInfo(baseline).Length;
            await CopyFileAsync(baseline, export, cancellationToken).ConfigureAwait(false);

            var metadata = new ExternalEditSessionMetadata(
                Guid.NewGuid(), project.Metadata.Id, project.Metadata.DocumentRevision,
                length, hash, DateTimeOffset.UtcNow, Path.GetFileName(export));
            var session = new ExternalEditSession(sessionDirectory, baseline, export, metadata);
            await session.SaveManifestAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            TryDelete(temporaryBaseline);
            TryDelete(baseline);
            TryDelete(export);
            TryDeleteDirectoryIfEmpty(sessionDirectory);
            throw;
        }
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
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, Metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, ManifestPath);
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
    string ExportFileName);
