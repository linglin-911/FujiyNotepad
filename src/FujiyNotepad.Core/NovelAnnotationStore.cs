using Microsoft.Data.Sqlite;

namespace FujiyNotepad.Core;

/// <summary>
/// SQLite persistence for a novel project's categories, independent annotations, chapters and
/// many-to-many source anchors. It deliberately has no uniqueness constraint on source ranges:
/// overlapping and identical ranges are valid.
/// </summary>
public sealed class NovelAnnotationStore : IAsyncDisposable
{
    private readonly string connectionString;
    private bool initialized;

    public NovelAnnotationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS AnnotationCategories (
                Id TEXT PRIMARY KEY NOT NULL,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Color TEXT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Annotations (
                Id TEXT PRIMARY KEY NOT NULL,
                CategoryId TEXT NULL REFERENCES AnnotationCategories(Id) ON DELETE SET NULL,
                Title TEXT NULL,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Annotations_CategoryId ON Annotations(CategoryId);
            CREATE TABLE IF NOT EXISTS Chapters (
                Id TEXT PRIMARY KEY NOT NULL,
                Title TEXT NOT NULL,
                StartOffset INTEGER NOT NULL,
                EndOffset INTEGER NOT NULL,
                ChapterNumber INTEGER NOT NULL,
                DocumentRevision INTEGER NOT NULL DEFAULT 1,
                CHECK (StartOffset >= 0 AND EndOffset >= StartOffset)
            );
            CREATE INDEX IF NOT EXISTS IX_Chapters_StartOffset ON Chapters(StartOffset);
            CREATE TABLE IF NOT EXISTS AnnotationAnchors (
                Id TEXT PRIMARY KEY NOT NULL,
                AnnotationId TEXT NOT NULL REFERENCES Annotations(Id) ON DELETE CASCADE,
                ChapterId TEXT NULL REFERENCES Chapters(Id) ON DELETE SET NULL,
                StartOffset INTEGER NOT NULL,
                EndOffset INTEGER NOT NULL,
                Quote TEXT NOT NULL,
                IsOrphaned INTEGER NOT NULL DEFAULT 0,
                DocumentRevision INTEGER NOT NULL DEFAULT 1,
                CHECK (StartOffset >= 0 AND EndOffset >= StartOffset)
            );
            CREATE INDEX IF NOT EXISTS IX_AnnotationAnchors_AnnotationId ON AnnotationAnchors(AnnotationId);
            CREATE INDEX IF NOT EXISTS IX_AnnotationAnchors_StartOffset ON AnnotationAnchors(StartOffset);
            CREATE INDEX IF NOT EXISTS IX_AnnotationAnchors_EndOffset ON AnnotationAnchors(EndOffset);
            CREATE INDEX IF NOT EXISTS IX_AnnotationAnchors_ChapterId ON AnnotationAnchors(ChapterId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        initialized = true;
    }

    public async Task<Guid> CreateCategoryAsync(string name, string? color = null, int sortOrder = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Guid id = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AnnotationCategories (Id, Name, Color, SortOrder, CreatedAt)
            VALUES ($id, $name, $color, $sort, $created);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        command.Parameters.AddWithValue("$sort", sortOrder);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<Guid> CreateAnnotationAsync(string content, Guid? categoryId = null, string? title = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(content);
        Guid id = Guid.NewGuid();
        string now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Annotations (Id, CategoryId, Title, Content, CreatedAt, UpdatedAt)
            VALUES ($id, $category, $title, $content, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<Guid> AddAnchorAsync(Guid annotationId, long startOffset, long endOffset, string quote,
        Guid? chapterId = null, long documentRevision = 1, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (startOffset < 0 || endOffset <= startOffset)
            throw new ArgumentOutOfRangeException(nameof(startOffset), "An anchor must be a non-empty [start, end) range.");
        ArgumentNullException.ThrowIfNull(quote);

        Guid id = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AnnotationAnchors
                (Id, AnnotationId, ChapterId, StartOffset, EndOffset, Quote, IsOrphaned, DocumentRevision)
            VALUES ($id, $annotation, $chapter, $start, $end, $quote, 0, $revision);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$annotation", annotationId.ToString("D"));
        command.Parameters.AddWithValue("$chapter", (object?)chapterId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$start", startOffset);
        command.Parameters.AddWithValue("$end", endOffset);
        command.Parameters.AddWithValue("$quote", quote);
        command.Parameters.AddWithValue("$revision", documentRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task RemoveAnchorAsync(Guid anchorId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AnnotationAnchors WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", anchorId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AnnotationAnchor>> GetAnchorsForAnnotationAsync(Guid annotationId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, AnnotationId, StartOffset, EndOffset, Quote, ChapterId, IsOrphaned, DocumentRevision
            FROM AnnotationAnchors WHERE AnnotationId = $id ORDER BY StartOffset, EndOffset;
            """;
        command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
        var result = new List<AnnotationAnchor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadAnchor(reader));
        return result;
    }

    public async Task<IReadOnlyList<AnnotationAnchor>> GetAnchorsAtOffsetAsync(long offset,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, AnnotationId, StartOffset, EndOffset, Quote, ChapterId, IsOrphaned, DocumentRevision
            FROM AnnotationAnchors
            WHERE IsOrphaned = 0 AND StartOffset <= $offset AND EndOffset > $offset
            ORDER BY StartOffset, EndOffset;
            """;
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<AnnotationAnchor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadAnchor(reader));
        return result;
    }

    /// <summary>
    /// Applies one document edit to every anchor whose range can be affected. SQLite uses the
    /// EndOffset/StartOffset indexes to avoid loading unrelated earlier anchors into application memory.
    /// The updates are committed atomically in one transaction.
    /// </summary>
    public async Task ApplyDocumentEditAsync(DocumentEdit edit, long newDocumentRevision,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        edit.Validate();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var affected = new List<AnnotationAnchor>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = """
                SELECT Id, AnnotationId, StartOffset, EndOffset, Quote, ChapterId, IsOrphaned, DocumentRevision
                FROM AnnotationAnchors
                WHERE EndOffset > $start OR StartOffset >= $start;
                """;
            select.Parameters.AddWithValue("$start", edit.StartOffset);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                affected.Add(ReadAnchor(reader));
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE AnnotationAnchors
                SET StartOffset = $start, EndOffset = $end, IsOrphaned = $orphaned, DocumentRevision = $revision
                WHERE Id = $id;
                """;
            var startParameter = update.Parameters.Add("$start", SqliteType.Integer);
            var endParameter = update.Parameters.Add("$end", SqliteType.Integer);
            var orphanParameter = update.Parameters.Add("$orphaned", SqliteType.Integer);
            var revisionParameter = update.Parameters.Add("$revision", SqliteType.Integer);
            var idParameter = update.Parameters.Add("$id", SqliteType.Text);

            foreach (AnnotationAnchor anchor in affected)
            {
                AnchorTransformResult transformed = AnnotationAnchorTransformer.Apply(anchor, edit);
                startParameter.Value = transformed.StartOffset;
                endParameter.Value = transformed.EndOffset;
                orphanParameter.Value = transformed.IsOrphaned ? 1 : 0;
                revisionParameter.Value = newDocumentRevision;
                idParameter.Value = anchor.Id.ToString("D");
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AnnotationAnchor ReadAnchor(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetInt64(2),
        reader.GetInt64(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
        reader.GetInt32(6) != 0,
        reader.GetInt64(7));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureInitialized()
    {
        if (!initialized)
            throw new InvalidOperationException("Call InitializeAsync before using the annotation store.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
