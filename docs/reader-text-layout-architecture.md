# Reader proportional-font text layout architecture

Status: Phase 1 and Phase 2 are accepted. Phase 3 is integrated into TextLayoutEngine and TextCanvas. The `CanvasLineMetrics[]` WinRT marshalling crash and a subsequent proportional-line overlap issue have both been fixed on the dedicated fix branch; the user confirmed the overlap was resolved after switching to that branch. Full Phase 3 acceptance still requires the remaining targeted interaction checks and confirmation of the latest boundary tests. Phase 4 architecture is now agreed: global reader-only settings, independent from the application's main UI appearance.

## Current implementation status (2026-11-04)

- **Phase 1 — accepted:** Release x64 solution build succeeded and all 501 original Presentation tests passed.
- **Phase 2 — accepted:** the user rebuilt the full Release x64 solution after the DirectWrite provider was added; Presentation.Tests passed 508/508 (0 failed, 0 skipped).
- **Phase 3 — integrated; first-content rendering and the reported overlap issue are user-confirmed fixed; full interaction acceptance pending:** TextLayoutEngine now has a source-indexed proportional path for visual-row generation, wrapping, caret movement, measured-row page navigation, horizontal/vertical scrolling, hit testing, selection ranges, find highlights and persistent rule highlights. TextCanvas paints proportional backgrounds before glyphs (so later wrapped rows do not cover their highlights), draws each complete source-line layout once, and uses layout-derived geometry for selection, annotation underlines, whitespace markers and the caret. Explicit same-count source-content refreshes invalidate native layouts; ordinary indexing polls with an unchanged line count are no-ops, while file-follow growth calls the explicit refresh API; horizontal extents are re-measured and clamped. Twenty-three engine integration tests cover visual rows, source ranges, hit testing, wrapped-row source coordinates, blank-area hit status, selection-rectangle annotation clicks in row padding, misses outside the annotated range, overlapping markers, chapter context hit-testing in blank line space, Unicode caret movement, vertical navigation, selections across soft wraps/logical lines, word selection across wrapped rows, horizontal scrolling, measured page navigation, viewport fill, source refresh invalidation, cache pruning and wheel scrolling. The previous fixed-cell path remains in the engine as a fallback. The user reported that line overlap varied with window width while running the original branch, then confirmed the issue was already fixed after switching to `fix/reader-proportional-line-overlap`; no additional overlap patch is planned unless it reappears on that branch.
- The reader currently uses the proportional backend with its existing default font family (Consolas). Phase 4 will introduce a dedicated `ReaderSettings` object; it will not reuse the application-wide `Theme` setting for the reader palette. The legacy `FontFamily`/`FontSize` values currently applied to TextCanvas will be migrated into the nested reader settings for compatibility.
- Reader navigation scope: continuous mouse-wheel scrolling and direct jumps from the chapter/annotation directory are the supported primary workflows.
- Character hit-testing exposes whether the pointer actually hit a source character region for text-selection operations. Small-annotation activation does not require a glyph hit: it tests the same source-range rectangles used for selection backgrounds, including whitespace within the rectangle, and resolves the nearest source caret position inside the matched annotation range. This works across wrapped visual rows and overlapping ranges. Right-click chapter creation uses the nearest source caret/insertion position, so clicking blank space to the right of a line still resolves that line and its chapter. Wrapped visual rows always map back to the original logical-line UTF-16 column. Glyph hit results also preserve the full source range of a shaping cluster. The reader has no visible horizontal or vertical scrollbar controls. Primary navigation remains continuous mouse-wheel scrolling and direct jumps from the chapter/annotation directory; internal scroll position is retained for these workflows, saved-view restoration and caret visibility.
- Phase 3 is not yet fully accepted. The user has confirmed that the application reaches the first content draw, displays body text, no longer crashes, and that the previously reported overlap is fixed on the dedicated fix branch. Remaining targeted checks: ordinary Chinese proportional fonts and Chinese/Latin mixed text; continuous mouse-wheel scrolling; chapter-directory and annotation jumps; selection/caret across soft wraps; small-annotation clicks inside source-range rectangles including row padding and overlapping annotations; right-click chapter-annotation creation in blank line space; find/highlight and saved-view restoration. Do not repeat the old startup-crash or already-fixed overlap investigation unless either problem reappears on the confirmed branch.
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
- **Validation status:** code and tests are committed, but this environment has no .NET SDK/Windows App SDK. No build or test pass is claimed. User-side explicit x64 build and Presentation.Tests execution are pending.
- Phase 4.2 is not started: TextCanvas still uses its current theme-following palette and the new reader-only palette/layout application path is not yet connected. No visible settings dialog or content-width host has been added.

## Phase 4 — global reader settings architecture (agreed)

### Product boundary

- Reader appearance and application chrome are two independent visual systems.
- Phase 4 settings apply only to the novel-reading surface: text font, text size, line spacing, content width, reading background and reading text/overlay colors.
- Menus, toolbars, status bar, annotation lists, dialogs and other application chrome keep their existing fixed styling. Phase 4 must not add controls for their font family, font size, background or text color.
- The existing application-wide `AppSettings.Theme` remains an application setting and must not be copied into, or used as the default for, the reader theme. The reader must not change `RootGrid.RequestedTheme` when its theme is changed.
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
- Keep `AppSettings.Theme` separate. It continues to represent the inherited application theme setting; the reader palette must not derive from it.
- `SettingsStore.Load` must normalize null, blank, out-of-range and non-finite reader values to safe defaults. Missing nested reader settings in old JSON must remain backward compatible. The existing source-generated JSON serialization path remains in use.

### Rendering and layout ownership

- Add a reader-specific palette resolver (for example `ReaderPalette`) rather than extending the application theme logic into the reader. It must supply background, text, caret, selection, search highlight, selection-occurrence, gutter, bookmark, whitespace, control-character and annotation-marker colors.
- `TextCanvas` consumes a reader-settings snapshot and renders only the reader palette. Changing `Reader.Theme` must not restyle menus, annotation panels or dialogs. The existing application theme may continue to style application chrome independently.
- `TextLayoutOptions` already carries `FontFamily`, `FontSize`, `MaxWidth`, `WordWrap` and `LineSpacingMultiplier`. Phase 4 must pass the reader values through the existing contract/provider path and include every layout-affecting value in cache identity/invalidation. Do not create a second layout engine.
- Font, size, line-spacing and width changes invalidate only affected visible/near-visible layout objects. Keep the bounded native-layout cache and the positional LineProvider pipeline; never load or synchronously reflow the entire novel.
- Width is implemented by a full-width reader viewport/background host and a centered content host with a maximum width. The annotation/marker gutter remains aligned with the content host; the full viewport keeps one continuous reader background. On width changes, preserve the current source-text viewport anchor where possible, then regenerate visible layouts on demand.
- Reader colors are rendering-only. They must not alter document offsets, ChapterId values, annotation anchors, SQLite schemas or project files.
- Keep the current Win2D/DirectWrite layout provider as the single source of truth for drawing, hit testing, caret, selection and annotation geometry.

### Settings interaction

- Add one unified Reader Settings dialog reachable from the View menu.
- The dialog edits a temporary settings snapshot. Apply updates the active reader immediately and persists the nested global reader settings; Cancel discards pending changes and leaves the current reader unchanged.
- The existing font-family and Zoom quick actions, if retained, operate on the same reader settings model. They must not become separate application-chrome settings.
- The first version provides system-installed font selection, font size, line spacing, maximum content width/adaptive width, and the three preset reader themes. Custom RGB colors, imported font files and per-project overrides are non-goals.
- Switching projects does not change reader settings. Restarting the application restores the same global reader settings.

### Implementation slices

1. **4.1 Settings model and compatibility:** add nested `ReaderSettings`, defaults, normalization and legacy JSON migration; add SettingsStore tests. No visible UI changes.
2. **4.2 Layout/render integration:** pass reader font, size and line spacing into the existing proportional layout contract; add reader-only palette resolution and preserve the viewport source anchor during re-layout.
3. **4.3 Content width:** introduce the full-width reader viewport and centered maximum-width content host while keeping the annotation marker gutter aligned.
4. **4.4 Settings UI:** add the unified dialog, apply/cancel behaviour, installed-font list, controls and persistence.
5. **4.5 Acceptance:** explicit x64 build, Presentation/Core tests, then targeted Windows smoke tests for font/size/line spacing/width/theme, project switching, restart persistence, annotations and a 70MB+ novel.

### Non-goals and protected behaviour

- No per-project reader settings.
- No new settings for application menus, toolbar, status bar, annotation panels or dialogs.
- No SQLite or novel-project schema migration.
- No changes to durable UTF-16 annotation anchors or chapter offsets.
- No whole-document in-memory loading/reflow.
- No full visual redesign of the main window in Phase 4; overall chrome and annotation-workspace visual design remains a later UI-optimization stage.

## Phase 1 acceptance

Phase 1 was locally accepted on 2026-11-04: Release x64 build succeeded and all 501 Presentation tests passed.

- The Presentation project contains no WinUI/Win2D references in the new contract.
- All public text positions in the contract are explicitly source UTF-16 positions.
- Hit-test results can represent supplementary characters without splitting surrogate pairs.
- Empty lines and multi-rectangle selection/annotation geometry are part of the contract.
- The existing renderer, annotation storage and document-offset mapping remain unchanged.
