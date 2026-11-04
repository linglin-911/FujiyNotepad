using System.Text.Json;
using System.Text.Json.Serialization;

namespace FujiyNotepad.Presentation
{
    // Source-generated JSON metadata so (de)serialization works under Native AOT without reflection.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(ReaderSettings))]
    internal sealed partial class SettingsJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> as a JSON file. All I/O is best-effort: a missing,
    /// corrupt, or locked file yields defaults, and save failures are swallowed, so settings never crash
    /// the app or block startup/shutdown.
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly string filePath;

        public SettingsStore(string filePath) => this.filePath = filePath;

        /// <summary>The default store at <c>%LOCALAPPDATA%\FujiyNotepad\settings.json</c>.</summary>
        public static SettingsStore Default()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FujiyNotepad");
            return new SettingsStore(Path.Combine(dir, "settings.json"));
        }

        public AppSettings Load()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    bool hasReaderSettings = HasReaderSettingsObject(json);
                    AppSettings? loaded = JsonSerializer.Deserialize(
                        json, SettingsJsonContext.Default.AppSettings);
                    if (loaded is not null)
                    {
                        loaded.RecentFiles ??= new List<string>();
                        loaded.RecentSearches ??= new List<string>();
                        loaded.RecentFilters ??= new List<string>();

                        // Older versions stored the text-surface font and zoom at the AppSettings root.
                        // Migrate them only when the new nested reader object is absent; an explicit Reader
                        // object is authoritative and must never be overwritten by legacy values.
                        if (!hasReaderSettings)
                        {
                            loaded.Reader = ReaderSettings.FromLegacy(loaded.FontFamily, loaded.FontSize);
                        }

                        loaded.Reader.Normalize();

                        // Persist the migration so subsequent launches use the nested reader settings only.
                        if (!hasReaderSettings)
                        {
                            Save(loaded);
                        }

                        return loaded;
                    }
                }
            }
            catch
            {
                // Ignore: a missing/corrupt/locked file just means we start from defaults.
            }

            return new AppSettings();
        }

        private static bool HasReaderSettingsObject(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(AppSettings.Reader), StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.Object;
                }
            }

            return false;
        }

        public void Save(AppSettings settings)
        {
            try
            {
                settings.Reader.Normalize();

                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(filePath,
                    JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            }
            catch
            {
                // Best-effort: never let a settings write break the app.
            }
        }
    }
}
