using System.Text.Json;

namespace FujiyNotepad.Presentation.Tests
{
    /// <summary>
    /// Tests the JSON settings store round-trip (which also exercises the Native-AOT source-generated
    /// serializer) and its resilience to missing / corrupt files.
    /// </summary>
    public class SettingsStoreTests
    {
        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "fujiy-settings-" + Guid.NewGuid().ToString("N") + ".json");

        [Fact]
        public void SaveThenLoad_RoundTripsAllFields()
        {
            string path = TempPath();
            try
            {
                var store = new SettingsStore(path);
                var saved = new AppSettings
                {
                    TabWidth = 8,
                    WindowWidth = 1234,
                    WindowHeight = 777,
                    WindowMaximized = true,
                    RecentFiles = { @"C:\a.txt", @"C:\b.log" },
                    RecentSearches = { "ERROR", "Thread-33" },
                    RecentFilters = { "WARN" },
                    RestoreLastSession = false,
                    LastSessionFilePath = @"C:\logs\app.log",
                    LastSessionProjectFolderPath = @"C:\Users\user\Documents\FujiyNotepad\NovelProjects\Novel.fujiyproject",
                    LastSessionFirstVisibleLine = 4200,
                    LastSessionCaretLine = 4321,
                    LastSessionCaretColumn = 7,
                    WordWrap = true,
                    ReadingMode = true,
                    HighlightSelectionOccurrences = false,
                    Reader = new ReaderSettings
                    {
                        FontFamily = "SimSun",
                        FontSizeDip = 20,
                        LineSpacingMultiplier = 1.6,
                        ContentWidthDip = 1024,
                        Theme = "Warm",
                    },
                };

                store.Save(saved);
                AppSettings loaded = store.Load();

                Assert.Equal(8, loaded.TabWidth);
                Assert.Equal(1234, loaded.WindowWidth);
                Assert.Equal(777, loaded.WindowHeight);
                Assert.True(loaded.WindowMaximized);
                Assert.Equal(new[] { @"C:\a.txt", @"C:\b.log" }, loaded.RecentFiles);
                Assert.Equal(new[] { "ERROR", "Thread-33" }, loaded.RecentSearches);
                Assert.Equal(new[] { "WARN" }, loaded.RecentFilters);
                Assert.False(loaded.RestoreLastSession);
                Assert.Equal(@"C:\logs\app.log", loaded.LastSessionFilePath);
                Assert.Equal(@"C:\Users\user\Documents\FujiyNotepad\NovelProjects\Novel.fujiyproject",
                    loaded.LastSessionProjectFolderPath);
                Assert.Equal(4200, loaded.LastSessionFirstVisibleLine);
                Assert.Equal(4321, loaded.LastSessionCaretLine);
                Assert.Equal(7, loaded.LastSessionCaretColumn);
                Assert.True(loaded.WordWrap);
                Assert.True(loaded.ReadingMode);
                Assert.False(loaded.HighlightSelectionOccurrences);
                Assert.Equal("SimSun", loaded.Reader.FontFamily);
                Assert.Equal(20d, loaded.Reader.FontSizeDip);
                Assert.Equal(1.6d, loaded.Reader.LineSpacingMultiplier);
                Assert.Equal(1024d, loaded.Reader.ContentWidthDip);
                Assert.Equal("Warm", loaded.Reader.Theme);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_MissingFile_ReturnsDefaults()
        {
            AppSettings s = new SettingsStore(TempPath()).Load();

            Assert.Equal(4, s.TabWidth);
            Assert.Equal(0, s.WindowWidth);
            Assert.False(s.WindowMaximized);
            Assert.Empty(s.RecentFiles);
            Assert.True(s.RestoreLastSession); // session restore is on by default
            Assert.False(s.ReadingMode); // reading mode is off by default
            Assert.True(s.HighlightSelectionOccurrences); // selection-occurrence highlighting is on by default
            Assert.Equal("", s.LastSessionFilePath);
            Assert.Equal("", s.LastSessionProjectFolderPath);
            Assert.Equal(ReaderSettings.DefaultFontFamily, s.Reader.FontFamily);
            Assert.Equal(ReaderSettings.DefaultFontSizeDip, s.Reader.FontSizeDip);
            Assert.Equal(ReaderSettings.DefaultLineSpacingMultiplier, s.Reader.LineSpacingMultiplier);
            Assert.Equal(ReaderSettings.DefaultContentWidthDip, s.Reader.ContentWidthDip);
            Assert.Equal(ReaderSettings.DefaultTheme, s.Reader.Theme);
        }

        [Fact]
        public void Load_LegacyTextViewSettings_MigratesToReaderSettingsAndPersists()
        {
            string path = TempPath();
            File.WriteAllText(path, """
                {
                  "FontFamily": "Cascadia Code",
                  "FontSize": 22,
                  "Theme": "Dark"
                }
                """);
            try
            {
                var store = new SettingsStore(path);

                AppSettings loaded = store.Load();

                Assert.Equal("Cascadia Code", loaded.Reader.FontFamily);
                Assert.Equal(22d, loaded.Reader.FontSizeDip);
                Assert.Equal(ReaderSettings.DefaultLineSpacingMultiplier, loaded.Reader.LineSpacingMultiplier);
                Assert.Equal(ReaderSettings.DefaultContentWidthDip, loaded.Reader.ContentWidthDip);
                Assert.Equal(ReaderSettings.DefaultTheme, loaded.Reader.Theme);
                Assert.Equal("Dark", loaded.Theme);

                using JsonDocument migrated = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal(JsonValueKind.Object, migrated.RootElement.GetProperty("Reader").ValueKind);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_ExplicitReaderSettings_TakePrecedenceOverLegacyFontValues()
        {
            string path = TempPath();
            File.WriteAllText(path, """
                {
                  "FontFamily": "Courier New",
                  "FontSize": 12,
                  "Reader": {
                    "FontFamily": "Microsoft YaHei",
                    "FontSizeDip": 19,
                    "LineSpacingMultiplier": 1.5,
                    "ContentWidthDip": 1100,
                    "Theme": "Night"
                  }
                }
                """);
            try
            {
                AppSettings loaded = new SettingsStore(path).Load();

                Assert.Equal("Microsoft YaHei", loaded.Reader.FontFamily);
                Assert.Equal(19d, loaded.Reader.FontSizeDip);
                Assert.Equal(1.5d, loaded.Reader.LineSpacingMultiplier);
                Assert.Equal(1100d, loaded.Reader.ContentWidthDip);
                Assert.Equal("Night", loaded.Reader.Theme);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_InvalidReaderSettings_NormalizesEachFieldToSafeDefaults()
        {
            string path = TempPath();
            File.WriteAllText(path, """
                {
                  "Reader": {
                    "FontFamily": " ",
                    "FontSizeDip": 1000,
                    "LineSpacingMultiplier": 0,
                    "ContentWidthDip": -50,
                    "Theme": "FollowApp"
                  }
                }
                """);
            try
            {
                AppSettings loaded = new SettingsStore(path).Load();

                Assert.Equal(ReaderSettings.DefaultFontFamily, loaded.Reader.FontFamily);
                Assert.Equal(ReaderSettings.DefaultFontSizeDip, loaded.Reader.FontSizeDip);
                Assert.Equal(ReaderSettings.DefaultLineSpacingMultiplier, loaded.Reader.LineSpacingMultiplier);
                Assert.Equal(ReaderSettings.DefaultContentWidthDip, loaded.Reader.ContentWidthDip);
                Assert.Equal(ReaderSettings.DefaultTheme, loaded.Reader.Theme);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Load_CorruptFile_ReturnsDefaults()
        {
            string path = TempPath();
            File.WriteAllText(path, "{ this is not valid json ]");
            try
            {
                Assert.Equal(4, new SettingsStore(path).Load().TabWidth);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
