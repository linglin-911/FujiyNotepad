using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FujiyNotepad.Core;

/// <summary>
/// Append-only byte piece table for large documents. The project's imported document remains immutable;
/// inserted bytes are appended to a sidecar and the logical document is exposed through IByteSource.
/// Edits and reads are bounded by the requested region rather than document size.
/// </summary>
public sealed class PieceTableDocument : IByteSource
{
    private sealed record Piece(bool IsAddition, long SourceOffset, long Length);
    private sealed record Manifest(int Version, string OriginalFileName, long OriginalLength, List<Piece> Pieces);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string originalPath;
    private readonly string additionsPath;
    private readonly string manifestPath;
    private readonly FileByteSource original;
    private readonly SafeFileHandle additions;
    private readonly FileStream additionsWriter;
    private readonly SemaphoreSlim editGate = new(1, 1);
    private readonly object stateLock = new();
    private List<Piece> pieces;
    private long[] pieceStarts = Array.Empty<long>();
    private long length;
    private readonly Stack<List<Piece>> undo = new();
    private readonly Stack<List<Piece>> redo = new();
    private bool disposed;

    private PieceTableDocument(string originalPath, string additionsPath, string manifestPath,
        FileByteSource original, FileStream additionsWriter, List<Piece> pieces)
    {
        this.originalPath = originalPath;
        this.additionsPath = additionsPath;
        this.manifestPath = manifestPath;
        this.original = original;
        this.additionsWriter = additionsWriter;
        additions = additionsWriter.SafeFileHandle;
        this.pieces = Coalesce(pieces);
        RebuildIndex();
    }

    public long Length { get { lock (stateLock) return length; } }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public static async Task<PieceTableDocument> CreateAsync(string originalDocumentPath, string projectFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalDocumentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        string originalPath = Path.GetFullPath(originalDocumentPath);
        string root = Path.GetFullPath(projectFolder);
        if (!File.Exists(originalPath)) throw new FileNotFoundException("Project document not found.", originalPath);
        Directory.CreateDirectory(root);

        string additionsPath = Path.Combine(root, "document.add");
        string manifestPath = Path.Combine(root, "document.pieces.json");
        if (File.Exists(additionsPath) || File.Exists(manifestPath))
            throw new IOException("A piece-table edit store already exists. Open it instead of creating a new one.");

        long originalLength = new FileInfo(originalPath).Length;
        var pieces = originalLength == 0 ? new List<Piece>() : new List<Piece> { new(false, 0, originalLength) };
        var writer = new FileStream(additionsPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read, 64 * 1024, FileOptions.RandomAccess | FileOptions.Asynchronous);
        var document = new PieceTableDocument(originalPath, additionsPath, manifestPath,
            new FileByteSource(originalPath), writer, pieces);
        try
        {
            await document.SaveManifestAsync(originalLength, cancellationToken).ConfigureAwait(false);
            return document;
        }
        catch
        {
            await document.DisposeAsync().ConfigureAwait(false);
            TryDelete(additionsPath);
            throw;
        }
    }

    public static async Task<PieceTableDocument> OpenAsync(string originalDocumentPath, string projectFolder,
        CancellationToken cancellationToken = default)
    {
        string originalPath = Path.GetFullPath(originalDocumentPath);
        string root = Path.GetFullPath(projectFolder);
        string additionsPath = Path.Combine(root, "document.add");
        string manifestPath = Path.Combine(root, "document.pieces.json");
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Manifest manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Piece-table manifest is invalid.");
        if (manifest.Version != 1 || !string.Equals(manifest.OriginalFileName, Path.GetFileName(originalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Piece-table manifest does not match this project document.");
        var original = new FileByteSource(originalPath);
        if (original.Length != manifest.OriginalLength)
        {
            original.Dispose();
            throw new InvalidDataException("The immutable project document changed after the edit store was created.");
        }
        var writer = new FileStream(additionsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.RandomAccess | FileOptions.Asynchronous);
        var document = new PieceTableDocument(originalPath, additionsPath, manifestPath, original, writer, manifest.Pieces);
        document.ValidatePieces(manifest.OriginalLength, writer.Length);
        return document;
    }

    /// <summary>Replaces a byte range. Callers encode replacement text using the document's detected encoding.</summary>
    public async Task ReplaceBytesAsync(long offset, long deleteLength, ReadOnlyMemory<byte> insertedBytes,
        CancellationToken cancellationToken = default)
    {
        await editGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            List<Piece> before;
            List<Piece> after;
            long oldLength;
            long additionOffset;
            lock (stateLock)
            {
                oldLength = length;
                if (offset < 0 || deleteLength < 0 || offset > oldLength || deleteLength > oldLength - offset)
                    throw new ArgumentOutOfRangeException(nameof(offset), "The edit range is outside the document.");
                before = new List<Piece>(pieces);
                additionOffset = additionsWriter.Length;
            }

            if (!insertedBytes.IsEmpty)
            {
                additionsWriter.Position = additionOffset;
                await additionsWriter.WriteAsync(insertedBytes, cancellationToken).ConfigureAwait(false);
                await additionsWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                additionsWriter.Flush(flushToDisk: true);
            }

            after = ReplaceRange(before, oldLength, offset, deleteLength,
                insertedBytes.IsEmpty ? null : new Piece(true, additionOffset, insertedBytes.Length));

            lock (stateLock)
            {
                undo.Push(before);
                redo.Clear();
                pieces = after;
                RebuildIndex();
            }

            try
            {
                await SaveManifestAsync(original.Length, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (stateLock)
                {
                    pieces = before;
                    undo.Pop();
                    RebuildIndex();
                }
                throw;
            }
        }
        finally { editGate.Release(); }
    }

    public async Task<bool> UndoAsync(CancellationToken cancellationToken = default)
        => await RestoreHistoryAsync(undo, redo, cancellationToken).ConfigureAwait(false);

    public async Task<bool> RedoAsync(CancellationToken cancellationToken = default)
        => await RestoreHistoryAsync(redo, undo, cancellationToken).ConfigureAwait(false);

    private async Task<bool> RestoreHistoryAsync(Stack<List<Piece>> source, Stack<List<Piece>> destination,
        CancellationToken cancellationToken)
    {
        await editGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            List<Piece> current;
            lock (stateLock)
            {
                if (source.Count == 0) return false;
                current = new List<Piece>(pieces);
                List<Piece> restored = source.Pop();
                destination.Push(current);
                pieces = new List<Piece>(restored);
                RebuildIndex();
            }
            try { await SaveManifestAsync(original.Length, cancellationToken).ConfigureAwait(false); }
            catch
            {
                lock (stateLock)
                {
                    List<Piece> failed = destination.Pop();
                    source.Push(new List<Piece>(pieces));
                    pieces = failed;
                    RebuildIndex();
                }
                throw;
            }
            return true;
        }
        finally { editGate.Release(); }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        lock (stateLock)
        {
            if (offset >= length || buffer.IsEmpty) return 0;
            int requested = (int)Math.Min(buffer.Length, length - offset);
            int copied = 0;
            int pieceIndex = FindPiece(offset);
            long within = offset - pieceStarts[pieceIndex];
            while (copied < requested && pieceIndex < pieces.Count)
            {
                Piece piece = pieces[pieceIndex];
                int count = (int)Math.Min(requested - copied, piece.Length - within);
                long sourceOffset = piece.SourceOffset + within;
                int read = piece.IsAddition
                    ? RandomAccess.Read(additions, buffer.Slice(copied, count), sourceOffset)
                    : original.Read(sourceOffset, buffer.Slice(copied, count));
                if (read != count) throw new EndOfStreamException("A piece-table source was truncated.");
                copied += read;
                pieceIndex++;
                within = 0;
            }
            return copied;
        }
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(offset, buffer.Span));
    }

    public long RefreshLength() => Length;

    private async Task SaveManifestAsync(long originalLength, CancellationToken cancellationToken)
    {
        List<Piece> snapshot;
        lock (stateLock) snapshot = new List<Piece>(pieces);
        string temporary = manifestPath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream,
                new Manifest(1, Path.GetFileName(originalPath), originalLength, snapshot), JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, manifestPath, overwrite: true);
    }

    private void ValidatePieces(long originalLength, long additionsLength)
    {
        foreach (Piece piece in pieces)
        {
            long sourceLength = piece.IsAddition ? additionsLength : originalLength;
            if (piece.SourceOffset < 0 || piece.Length <= 0 || piece.SourceOffset > sourceLength
                || piece.Length > sourceLength - piece.SourceOffset)
                throw new InvalidDataException("Piece-table manifest contains an invalid piece.");
        }
    }

    private void RebuildIndex()
    {
        pieceStarts = new long[pieces.Count];
        long total = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            pieceStarts[i] = total;
            total = checked(total + pieces[i].Length);
        }
        length = total;
    }

    private int FindPiece(long offset)
    {
        int index = Array.BinarySearch(pieceStarts, offset);
        if (index >= 0) return index;
        return ~index - 1;
    }

    private static List<Piece> ReplaceRange(List<Piece> source, long totalLength, long offset,
        long deleteLength, Piece? insertion)
    {
        long end = checked(offset + deleteLength);
        var result = new List<Piece>(source.Count + 2);
        long cursor = 0;
        bool inserted = false;
        foreach (Piece piece in source)
        {
            long pieceEnd = cursor + piece.Length;
            if (pieceEnd <= offset || cursor >= end)
            {
                if (!inserted && cursor >= end)
                {
                    if (insertion is not null) result.Add(insertion);
                    inserted = true;
                }
                result.Add(piece);
            }
            else
            {
                if (offset > cursor) result.Add(piece with { Length = offset - cursor });
                if (!inserted)
                {
                    if (insertion is not null) result.Add(insertion);
                    inserted = true;
                }
                if (end < pieceEnd)
                    result.Add(piece with { SourceOffset = piece.SourceOffset + (end - cursor), Length = pieceEnd - end });
            }
            cursor = pieceEnd;
        }
        if (!inserted && insertion is not null) result.Add(insertion);
        return Coalesce(result);
    }

    private static List<Piece> Coalesce(List<Piece> source)
    {
        var result = new List<Piece>(source.Count);
        foreach (Piece piece in source)
        {
            if (piece.Length <= 0) continue;
            if (result.Count > 0)
            {
                Piece last = result[^1];
                if (last.IsAddition == piece.IsAddition && last.SourceOffset + last.Length == piece.SourceOffset)
                {
                    result[^1] = last with { Length = last.Length + piece.Length };
                    continue;
                }
            }
            result.Add(piece);
        }
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PieceTableDocument));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            additionsWriter.Dispose();
            original.Dispose();
            editGate.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            additionsWriter.Dispose();
            original.Dispose();
            editGate.Dispose();
        }
    }
}
