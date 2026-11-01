using Microsoft.Data.Sqlite;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class ExternalEditSessionTests
{
    [Fact]
    public async Task CreateAsync_ExportsCopyAndKeepsProjectBaselineUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyExternalEditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-edit.txt");
        await File.WriteAllTextAsync(source, "第一章\r\n正文内容");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            ExternalEditSession session = await ExternalEditSession.CreateAsync(project, export);

            Assert.Equal(await File.ReadAllTextAsync(project.DocumentPath), await File.ReadAllTextAsync(session.BaselinePath));
            Assert.Equal(await File.ReadAllTextAsync(project.DocumentPath), await File.ReadAllTextAsync(export));
            Assert.True(File.Exists(session.ManifestPath));
            await session.ValidateProjectBaselineAsync(project);

            await File.WriteAllTextAsync(export, "编辑后的正文");
            await session.ValidateProjectBaselineAsync(project);
            Assert.Equal("第一章\r\n正文内容", await File.ReadAllTextAsync(project.DocumentPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_RestoresSessionManifestAfterApplicationRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyExternalEditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-edit.txt");
        await File.WriteAllTextAsync(source, "第一章\r\n正文内容");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            ExternalEditSession created = await ExternalEditSession.CreateAsync(project, export);
            ExternalEditSession reopened = await ExternalEditSession.OpenAsync(created.SessionDirectory, export);

            Assert.Equal(created.Metadata.Id, reopened.Metadata.Id);
            Assert.Equal(created.Metadata.ProjectId, reopened.Metadata.ProjectId);
            Assert.Equal(created.Metadata.BaseDocumentSha256, reopened.Metadata.BaseDocumentSha256);
            Assert.Equal(created.BaselinePath, reopened.BaselinePath);
            await reopened.ValidateProjectBaselineAsync(project);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateMarkedAsync_ManifestRoundTripsMarkerTargetsThroughSourceGeneratedJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyExternalEditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-marked.txt");
        await File.WriteAllTextAsync(source, "abcdefg");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            await using var store = new NovelAnnotationStore(project.DatabasePath);
            await store.InitializeAsync();
            Guid annotationId = await store.CreateAnnotationAsync("Test note", title: "Note");
            await store.AddAnchorAsync(annotationId, 0, 3, "abc", documentRevision: project.Metadata.DocumentRevision);
            await store.UpsertChapterAsync(new NovelChapter(
                Guid.NewGuid(), "Chapter 1", 0, 7, 1, project.Metadata.DocumentRevision));

            ExternalEditSession created = await ExternalEditSession.CreateMarkedAsync(project, store, export);
            ExternalEditSession reopened = await ExternalEditSession.OpenAsync(created.SessionDirectory, export);

            Assert.Equal(4, reopened.Metadata.ExpectedMarkers?.Count);
            ExternalEditMarkerValidationResult validation = await reopened.ValidateEditedCopyMarkersAsync(export);
            Assert.False(validation.HasIssues);
            Assert.Equal(2, validation.Targets.Count);
            Assert.All(validation.Targets, target => Assert.Equal(ExternalEditMarkerTargetStatus.Intact, target.Status));
        }
        finally
        {
            // Microsoft.Data.Sqlite pools disposed connections by default. Clear the idle pool
            // before deleting the temporary project directory, otherwise Windows can keep
            // annotations.sqlite (and its WAL sidecars) locked after the store is disposed.
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateProjectBaselineAsync_RejectsProjectChangedAfterExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyExternalEditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        string export = Path.Combine(root, "external", "novel-edit.txt");
        await File.WriteAllTextAsync(source, "original");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            ExternalEditSession session = await ExternalEditSession.CreateAsync(project, export);
            await File.WriteAllTextAsync(project.DocumentPath, "changed by another process");
            await project.RefreshDocumentFingerprintAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ValidateProjectBaselineAsync(project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_RefusesToExportOverProjectDocumentOrExistingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyExternalEditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectFolder = Path.Combine(root, "project");
        await File.WriteAllTextAsync(source, "original");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectFolder);
            await Assert.ThrowsAsync<IOException>(() =>
                ExternalEditSession.CreateAsync(project, project.DocumentPath));
            string existing = Path.Combine(root, "existing.txt");
            await File.WriteAllTextAsync(existing, "do not overwrite");
            await Assert.ThrowsAsync<IOException>(() => ExternalEditSession.CreateAsync(project, existing));
            Assert.Equal("do not overwrite", await File.ReadAllTextAsync(existing));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
