using System.Text;
using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests;

public sealed class PieceTableDocumentTests
{
    [Fact]
    public async Task Replace_Read_Undo_RedoAndReopen_PreserveLogicalBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyPieceTableTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "novel.txt");
        await File.WriteAllTextAsync(original, "第一章 ABC\r\n第二章 XYZ", new UTF8Encoding(false));
        try
        {
            await using (var document = await PieceTableDocument.CreateAsync(original, root))
            {
                byte[] before = ReadAll(document);
                byte[] inserted = Encoding.UTF8.GetBytes("新增");
                int start = Encoding.UTF8.GetByteCount("第一章 ");
                int delete = Encoding.UTF8.GetByteCount("ABC");
                await document.ReplaceBytesAsync(start, delete, inserted);
                Assert.Equal("第一章 新增\r\n第二章 XYZ", Encoding.UTF8.GetString(ReadAll(document)));
                Assert.Equal("第一章 ABC\r\n第二章 XYZ", await File.ReadAllTextAsync(original));

                Assert.True(await document.UndoAsync());
                Assert.Equal(before, ReadAll(document));
                Assert.True(await document.RedoAsync());
                Assert.Equal("第一章 新增\r\n第二章 XYZ", Encoding.UTF8.GetString(ReadAll(document)));
            }

            await using var reopened = await PieceTableDocument.OpenAsync(original, root);
            Assert.Equal("第一章 新增\r\n第二章 XYZ", Encoding.UTF8.GetString(ReadAll(reopened)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Replace_CanInsertAtStartAndDeleteAcrossPieceBoundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyPieceTableTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "novel.txt");
        await File.WriteAllTextAsync(original, "abcdef");
        try
        {
            await using var document = await PieceTableDocument.CreateAsync(original, root);
            await document.ReplaceBytesAsync(2, 0, Encoding.UTF8.GetBytes("XX"));
            await document.ReplaceBytesAsync(1, 4, ReadOnlyMemory<byte>.Empty);
            Assert.Equal("aXef", Encoding.UTF8.GetString(ReadAll(document)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadsOnlyRequestedWindowFromLargeLogicalDocument()
    {
        string root = Path.Combine(Path.GetTempPath(), "FujiyPieceTableTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "novel.txt");
        byte[] source = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(original, source);
        try
        {
            await using var document = await PieceTableDocument.CreateAsync(original, root);
            byte[] replacement = { 9, 8, 7, 6 };
            await document.ReplaceBytesAsync(1024 * 1024, 3, replacement);
            byte[] window = new byte[16];
            int read = document.Read(1024 * 1024 - 4, window);
            Assert.Equal(16, read);
            Assert.Equal(new byte[] { source[1024 * 1024 - 4], source[1024 * 1024 - 3], source[1024 * 1024 - 2], source[1024 * 1024 - 1], 9, 8, 7, 6 }, window[..8]);
            Assert.Equal(source.Length + 1L, document.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] ReadAll(IByteSource source)
    {
        byte[] result = new byte[checked((int)source.Length)];
        int read = source.ReadFull(0, result);
        Assert.Equal(result.Length, read);
        return result;
    }
}
