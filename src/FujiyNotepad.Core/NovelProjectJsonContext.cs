using System.Text.Json.Serialization;

namespace FujiyNotepad.Core;

/// <summary>
/// Source-generated JSON metadata for project files and external-edit manifests.
/// Required because the WinUI application is published with Native AOT and reflection-based
/// System.Text.Json serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NovelProjectMetadata))]
[JsonSerializable(typeof(ExternalEditSessionMetadata))]
internal partial class NovelProjectJsonContext : JsonSerializerContext
{
}
