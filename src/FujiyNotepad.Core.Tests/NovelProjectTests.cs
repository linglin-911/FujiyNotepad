using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class NovelProjectTests
{
    [Fact]
    public async Task ImportAsync_RefusesToUseANonEmptyProjectFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyProjectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        await File.WriteAllTextAsync(source, "novel");
        string existing = Path.Combine(projectPath, "keep.txt");
        await File.WriteAllTextAsync(existing, "keep");

        try
        {
            await Assert.ThrowsAsync<IOException>(() => NovelProject.ImportAsync(source, projectPath));
            Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_DetectsWhenTheSingleProjectDocumentChangedOutsideTheApp()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyProjectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectPath = Path.Combine(root, "project");
        await File.WriteAllTextAsync(source, "第一章\n原始正文");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectPath);
            Assert.True(project.IsDocumentFingerprintCurrent);

            await File.WriteAllTextAsync(project.DocumentPath, "第一章\n外部修改后的正文");
            NovelProject reopened = await NovelProject.OpenAsync(projectPath);

            Assert.False(reopened.IsDocumentFingerprintCurrent);
            await reopened.RefreshDocumentFingerprintAsync();
            Assert.True(reopened.IsDocumentFingerprintCurrent);
            Assert.Equal(2, reopened.Metadata.DocumentRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_CleansUpProjectFolderWhenCopyIsCancelled()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyProjectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectPath = Path.Combine(root, "project");
        await File.WriteAllTextAsync(source, new string('x', 1024 * 1024));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NovelProject.ImportAsync(source, projectPath, cancellation.Token));
            Assert.False(Directory.Exists(projectPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_CopiesSourceAndKeepsProjectIndependent()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyProjectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.txt");
        string projectPath = Path.Combine(root, "project");
        await File.WriteAllTextAsync(source, "原始小说\r\n第二章");

        try
        {
            NovelProject project = await NovelProject.ImportAsync(source, projectPath);
            Assert.NotEqual(Path.GetFullPath(source), project.DocumentPath);
            Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(project.DocumentPath));
            Assert.Equal(await NovelProject.ComputeSha256Async(source), project.Metadata.DocumentSha256);

            await File.WriteAllTextAsync(project.DocumentPath, "项目内修改");
            Assert.Equal("原始小说\r\n第二章", await File.ReadAllTextAsync(source));
            await project.RefreshDocumentFingerprintAsync();
            Assert.Equal(2, project.Metadata.DocumentRevision);

            NovelProject reopened = await NovelProject.OpenAsync(projectPath);
            Assert.Equal(project.DocumentPath, reopened.DocumentPath);
            Assert.Equal(project.Metadata.DocumentSha256, reopened.Metadata.DocumentSha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
