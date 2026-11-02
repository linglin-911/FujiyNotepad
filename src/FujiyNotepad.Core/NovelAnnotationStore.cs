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
            CREATE TABLE IF NOT EXISTS ChapterIndexState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                DocumentRevision INTEGER NOT NULL,
                ParserVersion INTEGER NOT NULL,
                ChapterCount INTEGER NOT NULL,
                IndexedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ChapterAnnotationCategories (
                Id TEXT PRIMARY KEY NOT NULL,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Color TEXT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ChapterAnnotations (
                Id TEXT PRIMARY KEY NOT NULL,
                ChapterId TEXT NOT NULL REFERENCES Chapters(Id) ON DELETE CASCADE,
                CategoryId TEXT NULL REFERENCES ChapterAnnotationCategories(Id) ON DELETE SET NULL,
                Title TEXT NULL,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ChapterAnnotations_ChapterId ON ChapterAnnotations(ChapterId);
            CREATE INDEX IF NOT EXISTS IX_ChapterAnnotations_CategoryId ON ChapterAnnotations(CategoryId);
            CREATE INDEX IF NOT EXISTS IX_ChapterAnnotations_UpdatedAt ON ChapterAnnotations(UpdatedAt);
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
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AnnotationAnchors_NoDuplicateWithinAnnotation
                ON AnnotationAnchors(AnnotationId, StartOffset, EndOffset);
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

    public async Task UpdateCategoryAsync(Guid categoryId, string name, string? color = null, int sortOrder = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AnnotationCategories
            SET Name = $name, Color = $color, SortOrder = $sort
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        command.Parameters.AddWithValue("$sort", sortOrder);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"Annotation category {categoryId} was not found.");
    }

    /// <summary>Deletes a category; its annotations are retained and become uncategorized.</summary>
    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AnnotationCategories WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves every note from one category into another, then removes the source category atomically.</summary>
    public async Task MergeCategoriesAsync(Guid sourceCategoryId, Guid targetCategoryId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (sourceCategoryId == targetCategoryId)
            return;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var move = connection.CreateCommand())
        {
            move.Transaction = (SqliteTransaction)transaction;
            move.CommandText = "UPDATE Annotations SET CategoryId = $target, UpdatedAt = $updated WHERE CategoryId = $source;";
            move.Parameters.AddWithValue("$target", targetCategoryId.ToString("D"));
            move.Parameters.AddWithValue("$source", sourceCategoryId.ToString("D"));
            move.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM AnnotationCategories WHERE Id = $source;";
            delete.Parameters.AddWithValue("$source", sourceCategoryId.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AnnotationCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Color, SortOrder, CreatedAt FROM AnnotationCategories ORDER BY SortOrder, Name;";
        var result = new List<AnnotationCategory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AnnotationCategory(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return result;
    }

    public async Task<IReadOnlyList<NovelAnnotation>> GetAnnotationsAsync(Guid? categoryId = null,
        string? searchText = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CategoryId, Title, Content, CreatedAt, UpdatedAt
            FROM Annotations
            WHERE ($category IS NULL OR CategoryId = $category)
              AND ($search IS NULL OR Title LIKE $search ESCAPE '\' OR Content LIKE $search ESCAPE '\')
            ORDER BY UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        string? pattern = string.IsNullOrWhiteSpace(searchText) ? null : "%" + EscapeLike(searchText.Trim()) + "%";
        command.Parameters.AddWithValue("$search", (object?)pattern ?? DBNull.Value);

        var result = new List<NovelAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new NovelAnnotation(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5))));
        }
        return result;
    }

    /// <summary>
    /// Returns note summaries with anchor counts in one query, including optional text search across note
    /// content, titles and linked source quotations.
    /// </summary>
    public async Task<IReadOnlyList<NovelAnnotationSummary>> GetAnnotationSummariesAsync(
        Guid? categoryId = null, string? searchText = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.Id, n.CategoryId, n.Title, n.Content, n.CreatedAt, n.UpdatedAt,
                   COUNT(a.Id),
                   COALESCE(SUM(CASE WHEN a.IsOrphaned = 1 THEN 1 ELSE 0 END), 0),
                   MIN(CASE WHEN a.IsOrphaned = 0 THEN a.StartOffset END),
                   (SELECT q.Quote FROM AnnotationAnchors q
                    WHERE q.AnnotationId = n.Id AND q.IsOrphaned = 0
                    ORDER BY q.StartOffset, q.EndOffset LIMIT 1)
            FROM Annotations n
            LEFT JOIN AnnotationAnchors a ON a.AnnotationId = n.Id
            WHERE ($category IS NULL OR n.CategoryId = $category)
              AND ($search IS NULL
                   OR n.Title LIKE $search ESCAPE '\'
                   OR n.Content LIKE $search ESCAPE '\'
                   OR EXISTS (
                       SELECT 1 FROM AnnotationAnchors q
                       WHERE q.AnnotationId = n.Id AND q.Quote LIKE $search ESCAPE '\'
                   ))
            GROUP BY n.Id
            ORDER BY n.UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        string? pattern = string.IsNullOrWhiteSpace(searchText) ? null : "%" + EscapeLike(searchText.Trim()) + "%";
        command.Parameters.AddWithValue("$search", (object?)pattern ?? DBNull.Value);

        var result = new List<NovelAnnotationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var annotation = new NovelAnnotation(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5)));
            result.Add(new NovelAnnotationSummary(
                annotation,
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return result;
    }

    public async Task<bool> HasAnchorAsync(Guid annotationId, long startOffset, long endOffset,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM AnnotationAnchors
                WHERE AnnotationId = $annotation AND StartOffset = $start AND EndOffset = $end
            );
            """;
        command.Parameters.AddWithValue("$annotation", annotationId.ToString("D"));
        command.Parameters.AddWithValue("$start", startOffset);
        command.Parameters.AddWithValue("$end", endOffset);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task<bool> UpdateAnnotationAsync(Guid annotationId, string content,
        Guid? categoryId = null, string? title = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(content);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Annotations
            SET CategoryId = $category, Title = $title, Content = $content, UpdatedAt = $updated
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task DeleteAnnotationAsync(Guid annotationId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Annotations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a note and its first source anchor in one transaction.</summary>
    public async Task<Guid> CreateAnnotationWithAnchorAsync(
        string content, long startOffset, long endOffset, string quote,
        Guid? categoryId = null, string? title = null, Guid? chapterId = null,
        long documentRevision = 1, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(quote);
        if (startOffset < 0 || endOffset <= startOffset)
            throw new ArgumentOutOfRangeException(nameof(startOffset), "An anchor must be a non-empty [start, end) range.");

        Guid annotationId = Guid.NewGuid();
        Guid anchorId = Guid.NewGuid();
        string now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var note = connection.CreateCommand())
        {
            note.Transaction = (SqliteTransaction)transaction;
            note.CommandText = """
                INSERT INTO Annotations (Id, CategoryId, Title, Content, CreatedAt, UpdatedAt)
                VALUES ($id, $category, $title, $content, $created, $updated);
                """;
            note.Parameters.AddWithValue("$id", annotationId.ToString("D"));
            note.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
            note.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            note.Parameters.AddWithValue("$content", content);
            note.Parameters.AddWithValue("$created", now);
            note.Parameters.AddWithValue("$updated", now);
            await note.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var anchor = connection.CreateCommand())
        {
            anchor.Transaction = (SqliteTransaction)transaction;
            anchor.CommandText = """
                INSERT INTO AnnotationAnchors
                    (Id, AnnotationId, ChapterId, StartOffset, EndOffset, Quote, IsOrphaned, DocumentRevision)
                VALUES ($id, $annotation, $chapter, $start, $end, $quote, 0, $revision);
                """;
            anchor.Parameters.AddWithValue("$id", anchorId.ToString("D"));
            anchor.Parameters.AddWithValue("$annotation", annotationId.ToString("D"));
            anchor.Parameters.AddWithValue("$chapter", (object?)chapterId?.ToString("D") ?? DBNull.Value);
            anchor.Parameters.AddWithValue("$start", startOffset);
            anchor.Parameters.AddWithValue("$end", endOffset);
            anchor.Parameters.AddWithValue("$quote", quote);
            anchor.Parameters.AddWithValue("$revision", documentRevision);
            await anchor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return annotationId;
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

    public async Task UpsertChapterAsync(NovelChapter chapter, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(chapter);
        if (chapter.StartOffset < 0 || chapter.EndOffset < chapter.StartOffset)
            throw new ArgumentOutOfRangeException(nameof(chapter), "Chapter offsets are invalid.");

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Chapters (Id, Title, StartOffset, EndOffset, ChapterNumber, DocumentRevision)
            VALUES ($id, $title, $start, $end, $number, $revision)
            ON CONFLICT(Id) DO UPDATE SET
                Title = excluded.Title,
                StartOffset = excluded.StartOffset,
                EndOffset = excluded.EndOffset,
                ChapterNumber = excluded.ChapterNumber,
                DocumentRevision = excluded.DocumentRevision;
            """;
        command.Parameters.AddWithValue("$id", chapter.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", chapter.Title);
        command.Parameters.AddWithValue("$start", chapter.StartOffset);
        command.Parameters.AddWithValue("$end", chapter.EndOffset);
        command.Parameters.AddWithValue("$number", chapter.ChapterNumber);
        command.Parameters.AddWithValue("$revision", chapter.DocumentRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NovelChapter>> GetChaptersAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, StartOffset, EndOffset, ChapterNumber, DocumentRevision
            FROM Chapters ORDER BY StartOffset, ChapterNumber;
            """;
        var result = new List<NovelChapter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new NovelChapter(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt64(5)));
        }
        return result;
    }

    public async Task<NovelChapterIndexState?> GetChapterIndexStateAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DocumentRevision, ParserVersion, ChapterCount, IndexedAt
            FROM ChapterIndexState WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new NovelChapterIndexState(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            DateTimeOffset.Parse(reader.GetString(3)));
    }

    /// <summary>
    /// Saves the first chapter index for a project. Existing chapter rows are never replaced implicitly,
    /// because chapter annotations refer to their IDs and must not be detached by an automatic rescan.
    /// An empty result is recorded too, so projects without recognizable headings are not rescanned on
    /// every launch.
    /// </summary>
    public async Task<bool> SaveChapterIndexIfEmptyAsync(
        IReadOnlyList<NovelChapter> chapters,
        long documentRevision,
        int parserVersion = NovelChapterIndexBuilder.CurrentParserVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(chapters);
        if (documentRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(documentRevision));
        if (parserVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(parserVersion));

        long previousEnd = -1;
        for (int index = 0; index < chapters.Count; index++)
        {
            NovelChapter chapter = chapters[index] ?? throw new ArgumentException("Chapter entries cannot be null.", nameof(chapters));
            if (chapter.StartOffset < 0 || chapter.EndOffset <= chapter.StartOffset
                || chapter.StartOffset < previousEnd
                || chapter.DocumentRevision != documentRevision)
            {
                throw new ArgumentException("Chapter ranges must be ordered, non-overlapping, non-empty, and use the current document revision.", nameof(chapters));
            }
            previousEnd = chapter.EndOffset;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = (SqliteTransaction)transaction;
            count.CommandText = """
                SELECT EXISTS(SELECT 1 FROM Chapters)
                    OR EXISTS(SELECT 1 FROM ChapterIndexState WHERE Id = 1);
                """;
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
                return false;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO Chapters (Id, Title, StartOffset, EndOffset, ChapterNumber, DocumentRevision)
                VALUES ($id, $title, $start, $end, $number, $revision);
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            var start = insert.Parameters.Add("$start", SqliteType.Integer);
            var end = insert.Parameters.Add("$end", SqliteType.Integer);
            var number = insert.Parameters.Add("$number", SqliteType.Integer);
            var revision = insert.Parameters.Add("$revision", SqliteType.Integer);

            foreach (NovelChapter chapter in chapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                id.Value = chapter.Id.ToString("D");
                title.Value = chapter.Title;
                start.Value = chapter.StartOffset;
                end.Value = chapter.EndOffset;
                number.Value = chapter.ChapterNumber;
                revision.Value = chapter.DocumentRevision;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = (SqliteTransaction)transaction;
            state.CommandText = """
                INSERT INTO ChapterIndexState (Id, DocumentRevision, ParserVersion, ChapterCount, IndexedAt)
                VALUES (1, $revision, $parser, $count, $indexed)
                ON CONFLICT(Id) DO UPDATE SET
                    DocumentRevision = excluded.DocumentRevision,
                    ParserVersion = excluded.ParserVersion,
                    ChapterCount = excluded.ChapterCount,
                    IndexedAt = excluded.IndexedAt;
                """;
            state.Parameters.AddWithValue("$revision", documentRevision);
            state.Parameters.AddWithValue("$parser", parserVersion);
            state.Parameters.AddWithValue("$count", chapters.Count);
            state.Parameters.AddWithValue("$indexed", DateTimeOffset.UtcNow.ToString("O"));
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<NovelChapter?> GetChapterAtOffsetAsync(long offset,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, StartOffset, EndOffset, ChapterNumber, DocumentRevision
            FROM Chapters
            WHERE StartOffset <= $offset AND EndOffset > $offset
            ORDER BY StartOffset DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new NovelChapter(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt64(5));
    }

    public async Task<Guid> CreateChapterAnnotationCategoryAsync(string name, string? color = null,
        int sortOrder = 0, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Guid id = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ChapterAnnotationCategories (Id, Name, Color, SortOrder, CreatedAt)
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

    public async Task UpdateChapterAnnotationCategoryAsync(Guid categoryId, string name, string? color = null,
        int sortOrder = 0, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ChapterAnnotationCategories
            SET Name = $name, Color = $color, SortOrder = $sort
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        command.Parameters.AddWithValue("$sort", sortOrder);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"Chapter annotation category {categoryId} was not found.");
    }

    /// <summary>Deletes only the category; its chapter annotations become uncategorized.</summary>
    public async Task DeleteChapterAnnotationCategoryAsync(Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ChapterAnnotationCategories WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MergeChapterAnnotationCategoriesAsync(Guid sourceCategoryId, Guid targetCategoryId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (sourceCategoryId == targetCategoryId)
            return;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var move = connection.CreateCommand())
        {
            move.Transaction = (SqliteTransaction)transaction;
            move.CommandText = """
                UPDATE ChapterAnnotations
                SET CategoryId = $target, UpdatedAt = $updated
                WHERE CategoryId = $source;
                """;
            move.Parameters.AddWithValue("$target", targetCategoryId.ToString("D"));
            move.Parameters.AddWithValue("$source", sourceCategoryId.ToString("D"));
            move.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM ChapterAnnotationCategories WHERE Id = $source;";
            delete.Parameters.AddWithValue("$source", sourceCategoryId.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChapterAnnotationCategory>> GetChapterAnnotationCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Color, SortOrder, CreatedAt
            FROM ChapterAnnotationCategories
            ORDER BY SortOrder, Name;
            """;
        var result = new List<ChapterAnnotationCategory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ChapterAnnotationCategory(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return result;
    }

    public async Task<Guid> CreateChapterAnnotationAsync(Guid chapterId, string content,
        Guid? categoryId = null, string? title = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(content);
        Guid id = Guid.NewGuid();
        string now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ChapterAnnotations (Id, ChapterId, CategoryId, Title, Content, CreatedAt, UpdatedAt)
            VALUES ($id, $chapter, $category, $title, $content, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$chapter", chapterId.ToString("D"));
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<bool> UpdateChapterAnnotationAsync(Guid annotationId, Guid chapterId, string content,
        Guid? categoryId = null, string? title = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(content);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ChapterAnnotations
            SET ChapterId = $chapter, CategoryId = $category, Title = $title,
                Content = $content, UpdatedAt = $updated
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
        command.Parameters.AddWithValue("$chapter", chapterId.ToString("D"));
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task DeleteChapterAnnotationAsync(Guid annotationId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ChapterAnnotations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChapterAnnotationSummary>> GetChapterAnnotationSummariesAsync(
        Guid? chapterId = null, Guid? categoryId = null, string? searchText = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.ChapterId, a.CategoryId, a.Title, a.Content, a.CreatedAt, a.UpdatedAt,
                   c.Id, c.Title, c.StartOffset, c.EndOffset, c.ChapterNumber, c.DocumentRevision,
                   cat.Id, cat.Name, cat.Color, cat.SortOrder, cat.CreatedAt
            FROM ChapterAnnotations a
            INNER JOIN Chapters c ON c.Id = a.ChapterId
            LEFT JOIN ChapterAnnotationCategories cat ON cat.Id = a.CategoryId
            WHERE ($chapter IS NULL OR a.ChapterId = $chapter)
              AND ($category IS NULL OR a.CategoryId = $category)
              AND ($search IS NULL
                   OR a.Title LIKE $search ESCAPE '\'
                   OR a.Content LIKE $search ESCAPE '\')
            ORDER BY c.ChapterNumber, c.StartOffset, a.UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$chapter", (object?)chapterId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)categoryId?.ToString("D") ?? DBNull.Value);
        string? pattern = string.IsNullOrWhiteSpace(searchText)
            ? null
            : "%" + EscapeLike(searchText.Trim()) + "%";
        command.Parameters.AddWithValue("$search", (object?)pattern ?? DBNull.Value);

        var result = new List<ChapterAnnotationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var annotation = new ChapterAnnotation(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6)));
            var chapter = new NovelChapter(
                Guid.Parse(reader.GetString(7)),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt32(11),
                reader.GetInt64(12));
            ChapterAnnotationCategory? category = reader.IsDBNull(13)
                ? null
                : new ChapterAnnotationCategory(
                    Guid.Parse(reader.GetString(13)),
                    reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.GetInt32(16),
                    DateTimeOffset.Parse(reader.GetString(17)));
            result.Add(new ChapterAnnotationSummary(annotation, chapter, category));
        }
        return result;
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
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
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = (SqliteTransaction)transaction;
            touch.CommandText = "UPDATE Annotations SET UpdatedAt = $updated WHERE Id = $id;";
            touch.Parameters.AddWithValue("$id", annotationId.ToString("D"));
            touch.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task RemoveAnchorAsync(Guid anchorId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string? annotationId = null;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = "SELECT AnnotationId FROM AnnotationAnchors WHERE Id = $id;";
            find.Parameters.AddWithValue("$id", anchorId.ToString("D"));
            annotationId = (string?)await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM AnnotationAnchors WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", anchorId.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (annotationId is not null)
        {
            await using var touch = connection.CreateCommand();
            touch.Transaction = (SqliteTransaction)transaction;
            touch.CommandText = "UPDATE Annotations SET UpdatedAt = $updated WHERE Id = $id;";
            touch.Parameters.AddWithValue("$id", annotationId);
            touch.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AnnotationAnchor>> GetAnchorsIntersectingRangeAsync(long startOffset, long endOffset,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (startOffset < 0 || endOffset <= startOffset)
            throw new ArgumentOutOfRangeException(nameof(startOffset), "A query range must be non-empty.");

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, AnnotationId, StartOffset, EndOffset, Quote, ChapterId, IsOrphaned, DocumentRevision
            FROM AnnotationAnchors
            WHERE IsOrphaned = 0 AND StartOffset < $end AND EndOffset > $start
            ORDER BY StartOffset, EndOffset;
            """;
        command.Parameters.AddWithValue("$start", startOffset);
        command.Parameters.AddWithValue("$end", endOffset);
        var result = new List<AnnotationAnchor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadAnchor(reader));
        return result;
    }

    /// <summary>Returns every independent note linked to at least one overlapping source anchor.</summary>
    public async Task<IReadOnlyList<NovelAnnotation>> GetAnnotationsIntersectingRangeAsync(long startOffset, long endOffset,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (startOffset < 0 || endOffset <= startOffset)
            throw new ArgumentOutOfRangeException(nameof(startOffset), "A query range must be non-empty.");

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT n.Id, n.CategoryId, n.Title, n.Content, n.CreatedAt, n.UpdatedAt
            FROM Annotations n
            INNER JOIN AnnotationAnchors a ON a.AnnotationId = n.Id
            WHERE a.IsOrphaned = 0 AND a.StartOffset < $end AND a.EndOffset > $start
            ORDER BY n.UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$start", startOffset);
        command.Parameters.AddWithValue("$end", endOffset);
        var result = new List<NovelAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new NovelAnnotation(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5))));
        }
        return result;
    }

    /// <summary>Returns every anchor, including orphaned anchors, for export/session validation.</summary>
    public async Task<IReadOnlyList<AnnotationAnchor>> GetAllAnchorsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, AnnotationId, StartOffset, EndOffset, Quote, ChapterId, IsOrphaned, DocumentRevision
            FROM AnnotationAnchors
            ORDER BY StartOffset, EndOffset, Id;
            """;
        var result = new List<AnnotationAnchor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadAnchor(reader));
        return result;
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

    /// <summary>
    /// Returns the anchors for a set of annotations in a small number of queries, so the annotation pane
    /// can render every source occurrence without issuing one database round-trip per list item.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AnnotationAnchor>>> GetAnchorsForAnnotationsAsync(
        IEnumerable<Guid> annotationIds, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(annotationIds);

        Guid[] ids = annotationIds.Distinct().ToArray();
        var grouped = ids.ToDictionary(id => id, _ => new List<AnnotationAnchor>());
        if (ids.Length == 0)
            return grouped.ToDictionary(
                pair => pair.Key, pair => (IReadOnlyList<AnnotationAnchor>)pair.Value);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const int batchSize = 500;
        for (int offset = 0; offset < ids.Length; offset += batchSize)
        {
            Guid[] batch = ids.Skip(offset).Take(batchSize).ToArray();
            await using var command = connection.CreateCommand();
            string[] parameterNames = batch.Select((_, index) => "$id" + index).ToArray();
            command.CommandText = $"""
                SELECT Id, AnnotationId, StartOffset, EndOffset, Quote, ChapterId, IsOrphaned, DocumentRevision
                FROM AnnotationAnchors
                WHERE AnnotationId IN ({string.Join(", ", parameterNames)})
                ORDER BY AnnotationId, StartOffset, EndOffset;
                """;

            for (int index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameterNames[index], batch[index].ToString("D"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AnnotationAnchor anchor = ReadAnchor(reader);
                if (grouped.TryGetValue(anchor.AnnotationId, out List<AnnotationAnchor>? anchors))
                    anchors.Add(anchor);
            }
        }

        return grouped.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<AnnotationAnchor>)pair.Value);
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

        var affectedChapters = new List<NovelChapter>();
        await using (var selectChapters = connection.CreateCommand())
        {
            selectChapters.Transaction = (SqliteTransaction)transaction;
            selectChapters.CommandText = """
                SELECT Id, Title, StartOffset, EndOffset, ChapterNumber, DocumentRevision
                FROM Chapters
                WHERE EndOffset > $start OR StartOffset >= $start;
                """;
            selectChapters.Parameters.AddWithValue("$start", edit.StartOffset);
            await using var reader = await selectChapters.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                affectedChapters.Add(new NovelChapter(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt32(4),
                    reader.GetInt64(5)));
            }
        }

        await using (var updateChapters = connection.CreateCommand())
        {
            updateChapters.Transaction = (SqliteTransaction)transaction;
            updateChapters.CommandText = """
                UPDATE Chapters
                SET StartOffset = $start, EndOffset = $end, DocumentRevision = $revision
                WHERE Id = $id;
                """;
            var startParameter = updateChapters.Parameters.Add("$start", SqliteType.Integer);
            var endParameter = updateChapters.Parameters.Add("$end", SqliteType.Integer);
            var revisionParameter = updateChapters.Parameters.Add("$revision", SqliteType.Integer);
            var idParameter = updateChapters.Parameters.Add("$id", SqliteType.Text);

            foreach (NovelChapter chapter in affectedChapters)
            {
                AnchorTransformResult transformed = AnnotationAnchorTransformer.TransformRange(
                    chapter.StartOffset, chapter.EndOffset, edit);
                startParameter.Value = transformed.StartOffset;
                endParameter.Value = transformed.EndOffset;
                revisionParameter.Value = newDocumentRevision;
                idParameter.Value = chapter.Id.ToString("D");
                await updateChapters.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
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

        await using (var updateIndexState = connection.CreateCommand())
        {
            updateIndexState.Transaction = (SqliteTransaction)transaction;
            updateIndexState.CommandText = """
                UPDATE ChapterIndexState
                SET DocumentRevision = $revision,
                    ChapterCount = (SELECT COUNT(*) FROM Chapters)
                WHERE Id = 1;
                """;
            updateIndexState.Parameters.AddWithValue("$revision", newDocumentRevision);
            await updateIndexState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

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
