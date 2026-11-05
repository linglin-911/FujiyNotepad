# Reader proportional-font text layout architecture

Status: Phase 1 and Phase 2 are accepted. Phase 3 is integrated into TextLayoutEngine and TextCanvas; the user confirmed the proportional-line overlap fix on the dedicated baseline branch, while remaining Phase 3 interaction checks are pending. Phase 4.1 and 4.2 are implemented and user-tested. Phase 4.3 full-width background/centered content hosting, content-width integration, width-change anchor restoration, Night-to-app-Dark override and a dedicated 56-DIP tool-rail column are implemented on `feature/reader-settings-phase4`; user UI verification is in progress. Phase 4.4 is implemented as a non-modal right-side live ReaderSettingsPane overlay: settings apply immediately and are saved with debounce, the panel overlays (rather than shrinks) the reader viewport, and the left reader remains wheel-scrollable while open. The panel and gear button now use an opaque application-theme surface. While the panel is open, only the panel itself is interactive; clicks elsewhere, including the tool rail, close the panel and are consumed, and the rail is temporarily disabled. BUG-018/019 code changes are implemented but await Windows build and user UI verification; Phase 4.3/4.4 are not yet accepted.

## Current implementation status (2026-11-05)

- **Phase 1 — accepted:** Release x64 solution build succeeded and all 501 original Presentation tests passed.
- **Phase 2 — accepted:** the user rebuilt the full Release x64 solution after the DirectWrite provider was added; Presentation.Tests passed 508/508 (0 failed, 0 skipped).
- **Phase 3 — integrated; first-content rendering and the reported overlap issue are user-confirmed fixed; full interaction acceptance pending:** TextLayoutEngine has a source-indexed proportional path for visual-row generation, wrapping, internal caret navigation, measured-row page navigation, horizontal/vertical scrolling, hit testing, drag-selection ranges, find highlights and persistent rule highlights. TextCanvas paints proportional backgrounds before glyphs, draws each complete source-line layout once, and uses shared text-only geometry for selection and annotation underlines. The visible insertion caret and double-click word-selection command have been removed; mouse text selection starts only after the pointer crosses a 4-DIP drag threshold, so a click does not create a selection. The reader does not automatically highlight other occurrences of the selected text. Text rectangles include a small cushion around the glyphs, exclude configured line spacing, remain horizontally continuous across adjacent characters, and are shared by selection and annotation hit testing. When a drag pointer leaves a visual row's text band, its endpoint advances to that row boundary before the next row is entered. Explicit same-count source-content refreshes invalidate native layouts; ordinary indexing polls with an unchanged line count are no-ops, while file-follow growth calls the explicit refresh API; horizontal extents are re-measured and clamped. Engine regression tests cover visual rows, source ranges, hit testing, wrapped-row source coordinates, blank-area hit status, text-only annotation rectangles, misses outside the annotated range, overlapping markers, chapter context hit-testing in blank line space, Unicode caret movement, vertical navigation, character-inclusive forward/reverse drag selection, row-boundary drag behavior, selections across soft wraps/logical lines, horizontal scrolling, measured page navigation, viewport fill, source refresh invalidation, cache pruning and wheel scrolling. The previous fixed-cell path remains in the engine as a fallback. The user reported that line overlap varied with window width while running the original branch, then confirmed the issue was already fixed after switching to `fix/reader-proportional-line-overlap`; no additional overlap patch is planned unless it reappears on that branch.
- The reader currently uses the proportional backend. Fresh settings default to `Microsoft YaHei` at 18 DIP; existing settings files migrate their legacy text-view font and size into the nested `ReaderSettings` to preserve the user's prior choice. The existing font/Zoom quick commands now read and write the nested reader settings. Phase 4.2 adds Paper/Warm/Night reader palettes. Paper and Warm remain reader-only; Night temporarily forces the application chrome to Dark without overwriting the separately saved application-wide `Theme` preference.
- Reader navigation scope: continuous mouse-wheel scrolling and direct jumps from the chapter/annotation directory are the supported primary workflows.
- Character hit-testing, drag selection and small-annotation activation share the same text-only range geometry. A hit requires the pointer to be inside the character rectangle (including a small cushion), not merely inside the larger layout row. The rectangles include enough horizontal and vertical padding to surround the glyphs, exclude line spacing, include whitespace advances, and merge adjacent horizontal character regions. DirectWrite's reported source range is treated as the complete hit character/shaping cluster; the engine only repairs an end that would split the first grapheme and never advances an already-valid end to the next character. A mouse-down establishes a pending anchor, and selection begins only after the pointer moves more than 4 DIP. When the pointer leaves a row vertically it selects to that row's boundary, then resumes at the corresponding horizontal position on the next row. Small-annotation activation tests these same source-range rectangles and resolves the nearest source caret position inside the matched annotation range. Right-click chapter creation remains separate and uses the nearest source position even in blank space to the right of a line. Wrapped visual rows always map back to the original logical-line UTF-16 column. The reader has no visible insertion caret, no double-click word selection and no visible horizontal or vertical scrollbar controls. Primary navigation remains continuous mouse-wheel scrolling and direct jumps from the chapter/annotation directory.
- Phase 3 is not yet fully accepted. The user has confirmed that the application reaches the first content draw, displays body text, no longer crashes, and that the previously reported overlap is fixed on the dedicated fix branch. Remaining targeted checks: ordinary Chinese proportional fonts and Chinese/Latin mixed text; continuous mouse-wheel scrolling; chapter-directory and annotation jumps; drag selection across soft wraps; small-annotation clicks inside source-range rectangles including row padding and overlapping annotations; right-click chapter-annotation creation in blank line space; find/highlight and saved-view restoration. Do not repeat the old startup-crash or already-fixed overlap investigation unless either problem reappears on the confirmed branch.
- Diagnostic logging writes timestamped startup milestones, restore/open/index progress, first CanvasControl Draw entry (including zero-line draws), first content draw stages, index-refresh timer stages and line-count update callbacks to `%LOCALAPPDATA%\\FujiyNotepad\\crash.log`. Managed exceptions in `TextCanvas.OnDraw` are logged and rethrown. The 23:01 `CanvasLineMetrics[]` marshalling exception was fixed by replacing `LineMetrics` array access with scalar `LineCount`/`GetCaretPosition` calls; the user has since confirmed normal body rendering without a crash. WER LocalDumps are not currently needed and should only be enabled if a future unexplained process exit occurs.
- The provider rejects embedded CR/LF because its contract is one logical source line. The existing LineProvider remains responsible for separating line terminators.

## Phase 2 backend details

- `Win2DTextLayoutProvider` creates a `CanvasTextLayout` using the selected font, actual viewport width and DirectWrite wrapping.
- Tabs remain a single source character. The backend measures the selected font's space advance and configures DirectWrite's incremental tab stop rather than expanding tabs into a display string.
- Do not read `CanvasTextLayout.LineMetrics`: it returns `CanvasLineMetrics[]`, which threw a confirmed CsWinRT `NotSupportedException` because the struct is non-blittable. The current provider uses scalar `LineCount` and `GetCaretPosition` queries to locate wrapped-row starts by binary search over grapheme boundaries, with a grapheme scan fallback. Adjacent caret Y positions provide row spacing; the final row reuses the preceding spacing, and a single row falls back to layout bounds/font metrics. Trailing whitespace is counted from each reconstructed source range.
- Hit testing, character regions, caret positions and drawing all query the same native layout result.
- `TextElementBoundaryMap` prevents caret movement and returned ranges from splitting surrogate pairs or combining sequences.
- `Win2DTextLayoutResult` owns and disposes its native layout and text format. It is UI-thread-affine; the engine retains layouts for visible logical lines and creates/disposes temporary layouts when traversing off-screen lines for page or wheel navigation.

## Goals

- Support ordinary proportional Chinese fonts as well as Latin and mixed-script text.
- Keep large-file reading incremental; never materialize the complete novel as one .NET string for rendering.
- Keep annotation anchors independent from font, font size, line spacing, viewport width and screen coordinates.
- Use one layout result as the source of truth for drawing, hit-testing, caret geometry, selection and annotation markers.
- Keep the Presentation layer testable without WinUI, Win2D or a graphics device.

## Non-goals for Phase 1

- Do not change the current TextCanvas rendering path.
- Do not change SQLite schemas or annotation anchor formats.
- Do not replace LineProvider, LineIndexer or the large-file byte-source pipeline.
- Do not add the reader settings UI yet.
- Do not remove the current monospace implementation until the proportional path has equivalent behaviour.

## Coordinate systems

Three coordinate systems must remain distinct:

| Coordinate | Meaning | Persistence |
|---|---|---|
| File byte offset | Position in the encoded TXT file; used by byte sources and file indexing | Not an annotation anchor |
| Source UTF-16 offset | Position in decoded original text; used by document mapping and annotation anchors | Durable |
| Layout/screen coordinates | DIP/pixel positions produced by the current font and viewport layout | Ephemeral |

DocumentTextOffsetMap remains the authority for mapping a document line and UTF-16 column to a document-wide UTF-16 offset. Existing annotation anchors continue to store document-wide UTF-16 ranges. Font and viewport changes must never rewrite these offsets.

## Layer responsibilities

1. **File layer** — LineIndexer, byte sources and LineProvider keep the existing positional, bounded-read pipeline.
2. **Source-coordinate layer** — DocumentTextOffsetMap maps line/column positions to document UTF-16 offsets. It is not aware of fonts or pixels.
3. **Layout contract** — ITextLayoutProvider, ITextLayoutResult and TextLayoutOptions describe font-aware layout without referencing UI frameworks.
4. **Rendering backend** — the WinUI layer implements the contract using Win2D/DirectWrite. It owns native layout objects and their disposal.
5. **Canvas layer** — TextCanvas draws and interacts with the same layout result used by the engine. It must not independently estimate character X positions.

## Layout contract

The provider creates a disposable layout for one logical source line (the line terminator is not included). The result exposes:

- ordered visual rows, each with a half-open source UTF-16 range;
- caret geometry for a source UTF-16 boundary;
- hit-test results expressed as a source UTF-16 range;
- range geometry as one or more rectangles, including advance whitespace;
- previous/next valid caret stops for grapheme-safe keyboard navigation.

All indexes returned by the contract are local to the exact original source-line string passed to the provider. The provider may use an internal visual projection, such as tab expansion, but it must translate every hit-test, caret and range result back to original-source UTF-16 indexes. A display-string index must never escape the backend.

TextLayoutHitTestResult.SourceLength is a UTF-16 code-unit count. A supplementary character such as an emoji can occupy two code units; a hit on its trailing side must advance by the full source cluster length. The backend must not expose caret positions inside surrogate pairs or grapheme/shaping clusters.

TextLayoutVisualLine ranges are half-open [SourceStartIndex, SourceEndIndex). A logical empty line still produces one empty visual row. A range can return multiple rectangles when it crosses wrapped rows or bidirectional runs.

## Tabs and proportional metrics

The existing LineColumns class expands tabs and approximates wide characters in fixed display columns. The current LineWrapper and TextLayoutEngine use that fixed-cell model. They remain in place as the legacy path during migration.

The proportional backend must measure the active font's actual space advance when applying tab stops. It must not reuse LineColumns.TotalColumns, ColumnOfCharIndex or CharIndexOfColumn as pixel geometry. If the backend expands tabs into spaces, it owns the reverse mapping so all public indexes still refer to the single original tab character.

## Lifetime and large-file performance

- Create layout objects only for visible logical lines and a small adjacent prefetch region.
- Keep the layout cache bounded to the active viewport; scrolling must not retain a native layout for every line visited.
- Include font family, font size, available width, wrapping mode, line-spacing multiplier, tab size and source-line identity/version in cache identity.
- Invalidate layout objects when any layout-affecting option changes, but never invalidate or rewrite annotation anchors.
- Keep DirectWrite/Win2D object creation and disposal on the UI dispatcher unless an implementation explicitly documents thread safety.
- Do not run a whole-document reflow when a setting changes. Re-layout the visible region first and create other layouts on demand.
- Preserve the current bounded line cache and positional reads. A very long logical line remains subject to the existing LineProvider line-length cap until a separate chunked-line design is approved.

## Migration sequence

1. **Phase 1 — contracts and coordinate semantics:** add UI-independent layout contracts and tests; document the legacy fixed-cell path. No visible rendering change.
2. **Phase 2 — proportional DirectWrite backend:** implement the contract in WinUI and prove measured visual rows, source-index hit testing, caret positions and range geometry.
3. **Phase 3 — engine integration:** replace fixed-width wrapping, navigation, hit testing and render-model calculations incrementally; keep the old path available until parity is demonstrated.
4. **Phase 4 — independent reader settings:** add globally persisted reader font family, font size, line spacing, content width and reader-only theme; keep all application chrome styling/settings independent.
5. **Phase 5 — performance and cleanup:** bound caches, validate long-line behaviour and remove obsolete fixed-cell code only after all dependent paths have migrated.


## Phase 4.1 implementation status (2026-11-05)

- Added the Presentation-layer `ReaderSettings` model with reader-only font family, DIP font size, line-spacing multiplier, content width and Paper/Warm/Night theme keys.
- Added `AppSettings.Reader` as a nested global settings object. It is persisted through the existing source-generated JSON settings store and remains outside novel-project folders and SQLite.
- `SettingsStore.Load` detects old settings files without a Reader object, migrates the legacy text-view font/size into ReaderSettings, normalizes invalid values, and persists the migration. An explicit nested Reader object always takes precedence.
- The inherited font-family and Zoom quick commands now initialize from and write to ReaderSettings; legacy root FontFamily/FontSize values are still synchronized for backward compatibility.
- Added SettingsStore tests for nested round-trip, defaults, legacy migration, explicit Reader precedence and invalid-value normalization.
- **Validation status:** the user ran `dotnet build src/FujiyNotepad.slnx --configuration Release -p:Platform=x64` successfully and `dotnet test src/FujiyNotepad.slnx --configuration Release -p:Platform=x64` passed 983/983 tests (0 failed, 0 skipped). The build reported one non-blocking xUnit2031 analyzer warning at `ProportionalTextLayoutEngineTests.cs(310,42)`; it recommended the predicate overload of `Assert.Single`. The latest branch has changed that assertion to the predicate overload; disappearance of the warning awaits the next user build. The 983-test result covers Phase 4.1 and predates the Phase 4.2 tests below.
- Phase 4.2 is implemented and user-tested. Phase 4.3 content-width hosting and Night-to-app-Dark linkage are implemented; user UI verification is in progress. Phase 4.4 is implemented as a right-side live settings panel; BUG-018 and BUG-019 remain pending.

## Phase 4.2 implementation status (2026-11-05; user-tested)

- Added the device-free `ReaderPalette` resolver with independent `Paper`, `Warm` and `Night` presets. Every palette defines the Win2D text surface background/text/caret, selection, search and occurrence highlights, gutter, bookmark, whitespace/control markers and annotation-marker colors.
- Windows High Contrast overrides the selected reader preset. Normal Windows/application Light/Dark changes no longer change the Win2D reading palette; `TextCanvas` does not set or modify `RootGrid.RequestedTheme`.
- `TextCanvas.ApplyReaderSettings` now applies the reader font family, DIP font size, line-spacing multiplier and reader theme snapshot. Both the constructor and font-refresh path pass the reader line-spacing multiplier into the existing `TextLayoutEngine.ConfigureProportionalLayout` contract; the existing Win2D provider already maps it to DirectWrite proportional line spacing. No second layout engine was added.
- Typography refresh captures the first visible row's original-source UTF-16 start position and restores the nearest visual row containing that source position after reflow. Added `TextLayoutEngine.SetViewportAnchor` and regression coverage for wrapping changes; the engine continues to create layouts on demand rather than reflowing the whole novel.
- Added 11 ReaderPalette tests (three presets, fallback, High Contrast role mapping and preset separation) and two proportional-engine tests (line-spacing option propagation and viewport-anchor restoration). The default `Microsoft YaHei` font is also present in the existing quick Font menu so its selected state is visible.
- **Validation status:** the user tested the current `feature/reader-settings-phase4` code and reported that the application runs normally with no issues found. The user did not provide an exact build/test summary, test count or analyzer-warning count, so no precise totals are claimed. This user feedback supersedes the earlier pending-validation status; the 13 Phase 4.2 tests are part of the submitted code.
- Phase 4.3 content-width host and Night-to-app-Dark linkage are implemented, pending complete user smoke verification. Phase 4.4 right-side live settings panel is implemented and visible in the user's latest screenshots. BUG-018 (panel/button opacity) and BUG-019 (dedicated right-rail layout reservation) remain unresolved. No new application-chrome typography/theme controls have been added.

## Phase 4 — global reader settings architecture (agreed)

### Product boundary

- Reader appearance and application chrome remain two separately persisted visual systems, with one explicit Night-mode linkage.
- Paper and Warm affect only the novel-reading surface. The application chrome continues to follow the saved `AppSettings.Theme` setting.
- Night affects the reading surface and temporarily forces the application chrome to Dark. It does not overwrite `AppSettings.Theme`; changes made to the global theme while Night is active are saved but remain visually overridden until the reader leaves Night, at which point the latest saved application theme is restored.
- Menus, toolbars, status bar, annotation lists and dialogs use the application's own theme resources. They never receive `ReaderPalette` colors, and Phase 4 adds no application-chrome font, font-size or background-color controls.
- Windows High Contrast is the sole system-level override allowed to supersede a selected reader palette for accessibility.

### Persistence model

- Reading settings are global across all novel projects and are stored in the existing `%LOCALAPPDATA%\\FujiyNotepad\\settings.json` file.
- Add a nested `ReaderSettings Reader` object to `AppSettings`; do not add these values to `project.json`, any novel project's SQLite database, or per-project metadata.
- Add `ReaderSettings` in the Presentation layer with these fields:
  - `FontFamily`: installed font-family name; initial default `Microsoft YaHei`.
  - `FontSizeDip`: font size in DIPs; initial default 18.
  - `LineSpacingMultiplier`: multiplier applied to natural baseline spacing; initial default 1.4.
  - `ContentWidthDip`: maximum reading-content width in DIPs; initial default 900. A value of 0 means adaptive/full available width.
  - `Theme`: reader-only preset key; initial default `Paper`; supported presets are `Paper`, `Warm` and `Night`. There is deliberately no `FollowApp` value because the reader theme must remain independent from the main UI.
- Keep the existing top-level `AppSettings.FontFamily` and `FontSize` properties as legacy compatibility fields for old settings files. When a settings file has no nested `Reader` object, migrate those legacy text-view values into `ReaderSettings` once; after migration, the reader UI and its quick zoom/font commands read and write only `AppSettings.Reader`. Do not use these legacy fields to style application chrome.
- Keep `AppSettings.Theme` separate. It continues to represent the user's saved application theme preference; neither setting is copied into the other. The Night reader preset temporarily overrides only the effective application theme to Dark and never mutates the saved preference.
- `SettingsStore.Load` must normalize null, blank, out-of-range and non-finite reader values to safe defaults. Missing nested reader settings in old JSON must remain backward compatible. The existing source-generated JSON serialization path remains in use.

### Rendering and layout ownership

- Use the independent `ReaderPalette` resolver rather than extending application theme logic into the reader. It supplies background, text, caret, selection, search highlight, selection-occurrence, gutter, bookmark, whitespace, control-character and annotation-marker colors.
- `TextCanvas` consumes a reader-settings snapshot and renders only the reader palette. Paper/Warm changes never restyle menus, annotation panels or dialogs. When `Reader.Theme` becomes Night, the window host temporarily sets `RootGrid.RequestedTheme` to Dark; application chrome still uses its own theme resources, not reader palette colors. Leaving Night restores the latest saved `AppSettings.Theme` preference.
- `TextLayoutOptions` already carries `FontFamily`, `FontSize`, `MaxWidth`, `WordWrap` and `LineSpacingMultiplier`. Phase 4 reuses the existing contract/provider path and cache identity/invalidation; do not create a second layout engine.
- Font, size, line-spacing and width changes invalidate only affected visible/near-visible layout objects. Keep the bounded native-layout cache and the positional LineProvider pipeline; never load or synchronously reflow the entire novel.
- Width is implemented by a full-width `ReaderViewport` background host and a centered `ReaderContentHost`. Centering applies to the reading area as a whole; `TextCanvas` and DirectWrite text formats remain left-aligned inside that area. No paragraph-centering or synthetic first-line indent is applied, and source whitespace is not silently removed. `ContentWidthDip` caps the `TextCanvas` width itself; the adjacent 6-DIP find/bookmark marker ruler is additional and remains aligned with the text surface. A value of zero uses all available text width after reserving the ruler. The full viewport keeps one continuous reader background. On width changes, capture the first visible source UTF-16 position before syncing the new viewport width, then restore it with the existing `SetViewportAnchor` path and regenerate visible layouts on demand.
- The parent reader-workspace Grid has four columns: return gutter, flexible `ReaderViewport`, mutually exclusive annotation pane, and a 56-DIP tool-rail column. When the rail is visible, its column reserves the width before `ReaderViewport.ActualWidth` is measured; when another right-side annotation/workspace pane is open, the rail is collapsed and its column becomes zero. `ReaderOverlayHost` is a child of the reader-viewport column and covers only that viewport. `ReaderSettingsPanel` is right-aligned inside this overlay, immediately to the left of the rail; it does not participate in Grid measurement and never reduces the reader width. Do not subtract the rail a second time in `UpdateReaderContentHostWidth`. The 6-DIP marker ruler and all annotation geometry/hit testing remain in the TextCanvas coordinate system.
- Reader colors are rendering-only. The Night preset's temporary application-theme override is a window-level presentation effect; it must not alter document offsets, ChapterId values, annotation anchors, SQLite schemas or project files.
- Keep the current Win2D/DirectWrite layout provider as the single source of truth for drawing, hit testing, caret, selection and annotation geometry.

### Settings interaction

- `View → Reader Settings...` and the right-edge gear button toggle the same non-modal `ReaderSettingsPane` hosted in the reader overlay.
- The panel edits the global ReaderSettings directly. Each control change is applied to the existing TextCanvas immediately; MainWindow updates the existing settings object and schedules a debounced SettingsStore save. Closing the panel flushes any pending save. There is no preview transaction, Apply button, Cancel button or rollback-on-close.
- While the panel is open, the left reader remains visible and wheel-scrollable. TextCanvas enters a passive interaction state: drag selection, annotation clicks, context menus and keyboard navigation are suppressed; double-click word selection is not available in the reader at any time; Ctrl+wheel does not change font size behind the panel.
- While the panel is open, the reader remains wheel-scrollable but passive for drag selection, annotation clicks, context menus and keyboard navigation; double-click selection is not part of the reader interaction model. A click inside the settings panel is handled normally. Any click outside it—including on the tool-rail column—closes the panel and is consumed. The rail container is not hit-testable and its current gear button is disabled while the panel is open; a transparent host in the reserved rail column catches the outside click without invoking a toolbar command.
- The existing font-family and Zoom quick actions operate on the same ReaderSettings model. They must not become separate application-chrome settings.
- The panel provides system-installed font selection, font size, line spacing, maximum content width/adaptive width, and the three preset reader themes. Selecting Night temporarily forces application chrome to Dark while preserving the saved global theme preference; Paper/Warm do not change it. Custom RGB colors, imported font files and per-project overrides are non-goals.
- Switching projects does not change reader settings. Restarting the application restores the same global reader settings.

### Implementation slices

1. **4.1 Settings model and compatibility:** add nested `ReaderSettings`, defaults, normalization and legacy JSON migration; add SettingsStore tests. No visible UI changes.
2. **4.2 Layout/render integration — user-tested:** pass reader font, size and line spacing into the existing proportional layout contract; add reader-only palette resolution and preserve the viewport source anchor during re-layout. The user reports the current code runs normally with no issues found.
3. **4.3 Content width — implementation updated, user verification in progress:** full-width reader viewport/background, centered content host, `ContentWidthDip` integration, 6-DIP marker ruler alignment, source UTF-16 viewport-anchor restoration and Night-to-app-Dark linkage. The parent Grid now reserves a dedicated 56-DIP column for the visible tool rail, so `ReaderViewport.ActualWidth` and the existing TextCanvas/TextLayoutEngine width chain automatically exclude it.
4. **4.4 Settings UI — implementation updated, user verification in progress:** `View → Reader Settings...` and the right-edge gear button open the same `ReaderSettingsPane` UserControl. Win2D enumerates installed font families; controls edit a ReaderSettings snapshot and send changes to MainWindow; MainWindow applies through `TextCanvas.ApplyReaderSettings`, updates legacy compatibility fields and saves with debounce. No preview sample, Apply or Cancel transaction exists. The panel overlays the reader viewport without changing its measured width; the reader remains wheel-scrollable in passive mode. Only the panel itself remains interactive while open. Any outside click, including the rail, closes and consumes the click; the rail is temporarily disabled. BUG-018/019 code fixes are implemented and await Windows verification.
5. **4.5 Acceptance:** explicit x64 build, Presentation/Core tests, then targeted Windows smoke tests for font/size/line spacing/width/theme, opaque panel/button surfaces, dedicated rail reservation, project switching, restart persistence, annotations and a 70MB+ novel.

### Phase 4.4 actual implementation architecture and pending UI issues

**Control and host ownership**

- `ReaderSettingsPane.xaml/.cs` is a WinUI `UserControl`, not a `ContentDialog`. It owns only its controls and a detached in-memory ReaderSettings snapshot.
- `MainWindow.xaml` hosts `ReaderOverlayHost` inside the `ReaderViewport` Grid column; it contains `ReaderSettingsPanel` and overlays only the reader. `ReaderToolsRailHost` occupies a separate 56-DIP Grid column and contains the right-edge `ReaderToolsRail`. The View menu item and gear button both call `ToggleReaderSettingsPanel()`.
- `MainWindow.xaml.cs` owns the settings lifecycle: closes mutually exclusive annotation/directory panes, loads the current settings into the control, handles outside clicks, applies changes to TextCanvas, synchronizes legacy fields, debounces persistence and flushes on close.
- `TextCanvas.IsReaderInteractionPassive` keeps wheel scrolling available while suppressing drag selection, annotation marker clicks, context menus and keyboard navigation. The reader does not gain a scrollbar; double-click selection is disabled globally.

**Control and data flow**

1. `ReaderSettingsPane.LoadSettings(settings.Reader)` clones the global settings and populates installed fonts, sliders, width controls and theme radio buttons without emitting a change event.
2. Each control change updates the pane's draft and raises `SettingsChanged` with a detached ReaderSettings clone.
3. MainWindow applies the snapshot through the existing `View.ApplyReaderSettings()` path, updates `settings.Reader` and legacy `settings.FontFamily/FontSize`, then schedules the existing SettingsStore save timer.
4. Saving uses the existing `SettingsStore.Save(settings)`; a failed save leaves the pending flag set for retry. Closing the panel stops the debounce timer and flushes pending changes.
5. The panel close button raises `CloseRequested`. A root pointer press outside `ReaderSettingsPanel`—including anywhere in the rail column—closes the panel and marks the event handled. While open, `ReaderToolsRail` is not hit-testable and `ReaderSettingsToggleButton` is disabled; its transparent parent host still receives the pointer press so the click dismisses the panel without activating a rail command.
6. There is no Apply/Cancel or preview rollback: changes are live and close only hides the panel.

**Controls**

- System font ComboBox via `CanvasTextFormat.GetSystemFontFamilies(ApplicationLanguages.Languages)`, with fallback enumeration and preservation of the currently saved font if it is not found.
- Font size Slider: 6–72 DIP.
- Line spacing Slider: 0.8–2.5.
- Content width NumberBox: 480–2400 DIP; adaptive-width checkbox maps to `ContentWidthDip=0`.
- Mutually exclusive Paper/Warm/Night radio choices; swatches use the existing `ReaderPalette`.
- Windows High Contrast continues to override the effective reader palette. No custom RGB picker, imported fonts or per-project settings.

**BUG-018/019 implementation changes — pending Windows verification**

- **BUG-018 — opaque panel and tool button backgrounds:** `ReaderSettingsPanel` and the gear button now use `SolidBackgroundFillColorBaseBrush`, a theme-aware application surface already used by the main workspace. They do not consume ReaderPalette colors. Verify actual opacity, foreground and border contrast in application Light/Dark themes with Paper/Warm/Night reader presets.
- **BUG-019 — dedicated tool-rail layout region:** the parent reader-workspace Grid now reserves a 56-DIP column for the visible rail. `ReaderViewport` is measured only in the remaining width; the existing `UpdateReaderContentHostWidth` → `TextCanvas.ActualWidth` → `TextLayoutEngine.ViewportWidth` chain remains unchanged and must not subtract the rail again. The settings panel remains an overlay inside the ReaderViewport column, right-aligned immediately before the rail; opening/closing it does not resize the viewport or trigger width reflow. When another annotation/workspace pane is open, the rail is hidden and its column collapses to zero.
- **Confirmed outside-click behaviour:** while the settings panel is open, the panel itself is interactive; the reader remains wheel-scrollable but otherwise passive. Every click outside the panel—including a click on the tool rail—closes the panel and is consumed. The rail container is not hit-testable and its gear button is disabled while open; its transparent host catches the click so no toolbar command runs.
- These code changes are implemented on the current feature branch. They are not user-accepted until Windows x64 build/test and the focused UI smoke checks below are completed.

**Validation state**

- The user's latest screenshots show the app running and the live side panel opening/closing. They do not include a full build/test transcript or test counts; do not claim automated tests passed.
- Next local validation for the implemented BUG-018/019 fixes:
  `dotnet build src/FujiyNotepad.slnx --configuration Release -p:Platform=x64`
  `dotnet test src/FujiyNotepad.slnx --configuration Release -p:Platform=x64`
- UI smoke tests: menu and gear toggle; panel-internal controls remain usable; click on reader, toolbar, and other outside areas closes the panel and does not trigger the underlying reader or toolbar command; wheel scroll remains available while open; toolbar is re-enabled after close; opaque panel and button in both app themes; adaptive and explicit widths exclude the 56-DIP rail; opening/closing the overlay does not resize the viewport; all reader settings apply immediately and persist after restart.

### Non-goals and protected behaviour

### 2026-11-05 BUG-018/019 implementation note

- The settings panel remains a visual overlay over the right side of the reader viewport; it does not take a Grid column and does not squeeze or reflow the reader. The toolbar alone owns a separate 56-DIP layout column.
- The toolbar column is reserved while the rail is visible and collapses when a mutually exclusive annotation/workspace pane hides the rail. The reader width calculation consumes `ReaderViewport.ActualWidth` and does not subtract 56 DIP a second time.
- While settings are open, only the panel is interactive. The toolbar is disabled and non-hit-testable; its transparent host catches clicks, and the root pointer handler closes the panel and consumes the click.
- Both the panel and gear button use `SolidBackgroundFillColorBaseBrush`. Windows build/test and visual verification remain pending.
