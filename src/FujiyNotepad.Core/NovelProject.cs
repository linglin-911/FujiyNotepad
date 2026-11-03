using System.Security.Cryptography;
using System.Text.Json;

namespace FujiyNotepad.Core;

/// <summary>
/// A self-contained novel workspace. The imported source is copied into the project and is never
/// referenced again; all subsequent reading and editing targets <see cref="DocumentPath"/>.
/// </summary>
public sealed class NovelProject
{
    private NovelProject(string rootPath, NovelProjectMetadata metadata, bool documentFingerprintMatchesMetadata = true)
    {
        RootPath = Path.GetFullPath(rootPath);
        Metadata = metadata;
        IsDocumentFingerprintCurrent = documentFingerprintMatchesMetadata;
    }

    public string RootPath { get; }
    public NovelProjectMetadata Metadata { get; }

    /// <summary>True when the complete document currently on disk matches the saved project fingerprint.</summary>
    public bool IsDocumentFingerprintCurrent { get; private set; } = true;

    public string DocumentPath => Path.Combine(RootPath, Metadata.DocumentFileName);
    public string DatabasePath => Path.Combine(RootPath, "annotations.sqlite");
    public string BackupDirectory => Path.Combine(RootPath, "backups");
    public string EditSessionsDirectory => Path.Combine(RootPath, "edit_sessions");
    public string MetadataPath => Path.Combine(RootPath, "project.json");

    /// <summary>
    /// Imports a novel by copying its bytes into a new project folder. The source file is not modified
    /// and is not needed after this method completes.
    /// </summary>
    public static async Task<NovelProject> ImportAsync(
        string sourceFilePath,
        string projectFolderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolderPath);

        string source = Path.GetFullPath(sourceFilePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The novel source file does not exist.", source);
        }

        string root = Path.GetFullPath(projectFolderPath);
        if (File.Exists(root))
        {
            throw new IOException("The project path is an existing file.");
        }

        bool createdRoot = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("Choose an empty folder for the new novel project. Existing files will not be overwritten.");
        }

        string documentName = "novel" + (Path.GetExtension(source).Length == 0 ? ".txt" : Path.GetExtension(source));
        string destination = Path.Combine(root, documentName);
        string temporary = destination + ".importing";

        bool documentCreated = false;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination);
            documentCreated = true;
            string hash = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
            var metadata = new NovelProjectMetadata(
                Guid.NewGuid(),
                Path.GetFileNameWithoutExtension(source),
                documentName,
                DateTimeOffset.UtcNow,
                new FileInfo(destination).Length,
                hash,
                1);

            var project = new NovelProject(root, metadata);
            await project.SaveMetadataAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(project.BackupDirectory);
            return project;
        }
        catch
        {
            TryDelete(temporary);
            if (documentCreated) TryDelete(destination);
            TryDelete(Path.Combine(root, "project.json.tmp"));
            TryDelete(Path.Combine(root, "project.json"));
            TryDeleteDirectoryIfEmpty(Path.Combine(root, "backups"));
            if (createdRoot) TryDeleteDirectoryIfEmpty(root);
            throw;
        }
    }

    public static async Task<NovelProject> OpenAsync(string projectFolderPath, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectFolderPath);
        string metadataPath = Path.Combine(root, "project.json");
        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        NovelProjectMetadata? metadata = await JsonSerializer.DeserializeAsync(
            stream, NovelProjectJsonContext.Default.NovelProjectMetadata, cancellationToken).ConfigureAwait(false);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.DocumentFileName))
        {
            throw new InvalidDataException("The project metadata is missing or invalid.");
        }

        string documentPath = Path.GetFullPath(Path.Combine(root, metadata.DocumentFileName));
        if (!documentPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(documentPath))
        {
            throw new InvalidDataException("The project document is missing or points outside the project folder.");
        }

        string actualHash = await ComputeSha256Async(documentPath, cancellationToken).ConfigureAwait(false);
        bool fingerprintMatches = new FileInfo(documentPath).Length == metadata.DocumentLength
            && string.Equals(actualHash, metadata.DocumentSha256, StringComparison.OrdinalIgnoreCase);

        return new NovelProject(root, metadata, fingerprintMatches);
    }

    /// <summary>Recomputes the project document hash after an edit has been durably saved.</summary>
    public async Task RefreshDocumentFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(DocumentPath);
        Metadata.DocumentLength = info.Length;
        Metadata.DocumentSha256 = await ComputeSha256Async(DocumentPath, cancellationToken).ConfigureAwait(false);
        Metadata.DocumentRevision++;
        await SaveMetadataAsync(cancellationToken).ConfigureAwait(false);
        IsDocumentFingerprintCurrent = true;
    }

    public async Task SaveMetadataAsync(CancellationToken cancellationToken = default)
    {
        string temporary = MetadataPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, Metadata,
                    NovelProjectJsonContext.Default.NovelProjectMetadata, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, MetadataPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>Persisted project metadata. Mutable document fields are updated after successful edits.</summary>
public sealed class NovelProjectMetadata
{
    public NovelProjectMetadata() { }

    public NovelProjectMetadata(Guid id, string title, string documentFileName, DateTimeOffset importedAt,
        long documentLength, string documentSha256, long documentRevision)
    {
        Id = id;
        Title = title;
        DocumentFileName = documentFileName;
        ImportedAt = importedAt;
        DocumentLength = documentLength;
        DocumentSha256 = documentSha256;
        DocumentRevision = documentRevision;
    }

    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string DocumentFileName { get; set; } = "novel.txt";
    public DateTimeOffset ImportedAt { get; set; }
    public long DocumentLength { get; set; }
    public string DocumentSha256 { get; set; } = "";
    public long DocumentRevision { get; set; }
}
