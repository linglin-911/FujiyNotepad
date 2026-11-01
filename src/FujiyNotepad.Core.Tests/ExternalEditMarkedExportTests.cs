using System.Text;
using System.Text.Json;
using FujiyNotepad.Core;
using Microsoft.Data.Sqlite;

namespace FujiyNotepad.Core.Tests;

public sealed class ExternalEditMarkedExportTests
{
    [Fact]
    public async Task CreateMarkedAsync_ExportsAnchorsAndChaptersWithoutChangingProjectDocument()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyMarkedExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-marked.txt");
        const string novel = "第一章\r\n张三来到青云宗。\r\n第二章\r\n李四登场。";
        await File.WriteAllTextAsync(source, novel, new UTF8Encoding(false));

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            await using var store = new NovelAnnotationStore(project.DatabasePath);
            await store.InitializeAsync();

            Guid annotationId = await store.CreateAnnotationAsync("人物初次登场", title: "出场描写");
            long quoteStart = novel.IndexOf("张三", StringComparison.Ordinal);
            const string quote = "张三来到青云宗。";
            long quoteEnd = quoteStart + quote.Length;
            Guid anchorId = await store.AddAnchorAsync(annotationId, quoteStart, quoteEnd, quote);

            long secondChapterStart = novel.IndexOf("第二章", StringComparison.Ordinal);
            Guid chapterOneId = Guid.NewGuid();
            Guid chapterTwoId = Guid.NewGuid();
            await store.UpsertChapterAsync(new NovelChapter(chapterOneId, "第一章", 0, secondChapterStart, 1));
            await store.UpsertChapterAsync(new NovelChapter(chapterTwoId, "第二章", secondChapterStart, novel.Length, 2));

            ExternalEditSession session = await ExternalEditSession.CreateMarkedAsync(project, store, export);

            Assert.Equal(novel, await File.ReadAllTextAsync(project.DocumentPath));
            Assert.Equal(await NovelProject.ComputeSha256Async(project.DocumentPath),
                await NovelProject.ComputeSha256Async(session.BaselinePath));
            Assert.Equal(6, session.Metadata.ExpectedMarkers!.Count);
            Assert.Equal("utf-8", session.Metadata.SourceEncodingId);
            Assert.Equal("utf-8", session.Metadata.ExportEncodingId);

            string markedText = await File.ReadAllTextAsync(export, Encoding.UTF8);
            Assert.NotEqual(novel, markedText);
            Assert.Contains(ExternalEditMarkerCodec.Encode(new ExternalEditMarker(
                session.Metadata.Id, anchorId, ExternalEditMarkerTargetKind.AnnotationAnchor,
                ExternalEditMarkerEndpoint.Start)), markedText);

            ExternalEditMarkerValidationResult initial =
                await session.ValidateEditedCopyMarkersAsync(export);
            Assert.False(initial.HasIssues);
            Assert.Equal(6, initial.Targets.Count);

            ExternalEditSession reopened = await ExternalEditSession.OpenAsync(session.SessionDirectory, export);
            Assert.Equal(session.Metadata.Id, reopened.Metadata.Id);
            Assert.Equal(session.BaselinePath, reopened.BaselinePath);
            Assert.False((await reopened.ValidateEditedCopyMarkersAsync(export)).HasIssues);

            // Simulate a third-party edit inside the anchored quote while preserving the markers.
            string editedText = markedText.Replace(quote, "张三缓缓来到青云宗。", StringComparison.Ordinal);
            await File.WriteAllTextAsync(export, editedText, new UTF8Encoding(false));

            ExternalEditMarkerValidationResult afterEdit =
                await session.ValidateEditedCopyMarkersAsync(export);
            Assert.False(afterEdit.HasIssues);
            ExternalEditMarkerTargetResult anchor = Assert.Single(afterEdit.Targets,
                item => item.TargetId == anchorId && item.TargetKind == ExternalEditMarkerTargetKind.AnnotationAnchor);
            Assert.Equal(quoteStart, anchor.StartOffset);
            Assert.Equal(quoteEnd + 2, anchor.EndOffset);

            using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(session.ManifestPath));
            Assert.Equal(6, manifest.RootElement.GetProperty("ExpectedMarkers").GetArrayLength());
            Assert.Equal(Path.GetFileName(session.BaselinePath),
                manifest.RootElement.GetProperty("BaselineFileName").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateMarkedAsync_FallsBackToUtf8ForWindows1252SoMarkersRemainRepresentable()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyMarkedExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-marked.txt");
        const string novel = "Chapter 1\r\nCafe\u00E9\r\n";
        byte[] originalBytes = TextEncoding.Windows1252.Encoding.GetBytes(novel);
        await File.WriteAllBytesAsync(source, originalBytes);

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            await using var store = new NovelAnnotationStore(project.DatabasePath);
            await store.InitializeAsync();
            Guid annotationId = await store.CreateAnnotationAsync("Cafe note");
            long start = novel.IndexOf("Cafe", StringComparison.Ordinal);
            await store.AddAnchorAsync(annotationId, start, start + 5, "Cafe\u00E9");

            ExternalEditSession session = await ExternalEditSession.CreateMarkedAsync(project, store, export);

            Assert.Equal("windows-1252", session.Metadata.SourceEncodingId);
            Assert.Equal("utf-8-bom", session.Metadata.ExportEncodingId);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(session.BaselinePath));

            string exported = await File.ReadAllTextAsync(export, Encoding.UTF8);
            Assert.Contains("Cafe\u00E9", exported);
            ExternalEditMarkerValidationResult validation =
                await session.ValidateEditedCopyMarkersAsync(export);
            Assert.False(validation.HasIssues);
            Assert.Equal(2, validation.Targets.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateMarkedAsync_RejectsStaleAnchorRevisionAndDoesNotLeaveAnExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyMarkedExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-marked.txt");
        await File.WriteAllTextAsync(source, "第一章\r\n正文");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            await using var store = new NovelAnnotationStore(project.DatabasePath);
            await store.InitializeAsync();
            Guid annotationId = await store.CreateAnnotationAsync("old revision");
            await store.AddAnchorAsync(annotationId, 0, 2, "第一", documentRevision: 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ExternalEditSession.CreateMarkedAsync(project, store, export));
            Assert.False(File.Exists(export));
            Assert.Empty(Directory.Exists(project.EditSessionsDirectory)
                ? Directory.EnumerateFileSystemEntries(project.EditSessionsDirectory)
                : Array.Empty<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
