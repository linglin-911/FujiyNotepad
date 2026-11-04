# Reader proportional-font text layout architecture

Status: Phase 1 and Phase 2 accepted by local build/test; Phase 3 engine and TextCanvas integration code is in place. The user confirmed a local Release x64 build and 975/975 tests passed before the latest diagnostic instrumentation, but the reader still opens blank and exits after roughly ten seconds. Startup/index/render lifecycle diagnostics have been added and require another local build and reproduction.

## Current implementation status (2026-11-04)

- **Phase 1 — accepted:** Release x64 solution build succeeded and all 501 original Presentation tests passed.
- **Phase 2 — accepted:** the user rebuilt the full Release x64 solution after the DirectWrite provider was added; Presentation.Tests passed 508/508 (0 failed, 0 skipped).
- **Phase 3 — integration code implemented, runtime validation blocked by startup/render exit:** TextLayoutEngine now has a source-indexed proportional path for visual-row generation, wrapping, caret movement, measured-row page navigation, horizontal/vertical scrolling, hit testing, selection ranges, find highlights and persistent rule highlights. TextCanvas paints proportional backgrounds before glyphs (so later wrapped rows do not cover their highlights), draws each complete source-line layout once, and uses layout-derived geometry for selection, annotation underlines, whitespace markers and the caret. Explicit same-count source-content refreshes invalidate native layouts; ordinary indexing polls with an unchanged line count are no-ops, while file-follow growth calls the explicit refresh API; horizontal extents are re-measured and clamped. Twenty-three engine integration tests cover visual rows, source ranges, hit testing, wrapped-row source coordinates, blank-area hit status, selection-rectangle annotation clicks in row padding, misses outside the annotated range, overlapping markers, chapter context hit-testing in blank line space, Unicode caret movement, vertical navigation, selections across soft wraps/logical lines, word selection across wrapped rows, horizontal scrolling, measured page navigation, viewport fill, source refresh invalidation, cache pruning and wheel scrolling. The previous fixed-cell path remains in the engine as a fallback.
- The reader currently uses the proportional backend with its existing default font family (Consolas). User-facing font family selection, persisted font/size/line-spacing/content-width settings and reading themes remain Phase 4 work.
- Reader navigation scope: continuous mouse-wheel scrolling and direct jumps from the chapter/annotation directory are the supported primary workflows.
- Character hit-testing exposes whether the pointer actually hit a source character region for text-selection operations. Small-annotation activation does not require a glyph hit: it tests the same source-range rectangles used for selection backgrounds, including whitespace within the rectangle, and resolves the nearest source caret position inside the matched annotation range. This works across wrapped visual rows and overlapping ranges. Right-click chapter creation uses the nearest source caret/insertion position, so clicking blank space to the right of a line still resolves that line and its chapter. Wrapped visual rows always map back to the original logical-line UTF-16 column. Glyph hit results also preserve the full source range of a shaping cluster. Vertical scrollbar thumb sizing is not a reader requirement and should not receive further proportional-layout work.
- Phase 3 has not yet been accepted: the new engine/rendering integration must first pass the user's local Release x64 build/tests and a real-reader visual/interaction check.
- Diagnostic logging now writes timestamped startup milestones, restore/open/index progress, first content draw entry/completion, unobserved task exceptions and process exit to `%LOCALAPPDATA%\\FujiyNotepad\\crash.log`. Managed exceptions in `TextCanvas.OnDraw` are logged and rethrown. These diagnostics are not a substitute for a native crash dump if the process terminates outside managed exception handling.
- The provider rejects embedded CR/LF because its contract is one logical source line. The existing LineProvider remains responsible for separating line terminators.

## Phase 2 backend details

- `Win2DTextLayoutProvider` creates a `CanvasTextLayout` using the selected font, actual viewport width and DirectWrite wrapping.
- Tabs remain a single source character. The backend measures the selected font's space advance and configures DirectWrite's incremental tab stop rather than expanding tabs into a display string.
- `CanvasLineMetrics` supplies wrapped-row source lengths, baseline, height and trailing-whitespace counts.
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
4. **Phase 4 — reader typography settings:** add font family, font size, line spacing, content width and reading theme settings, with persisted values.
5. **Phase 5 — performance and cleanup:** bound caches, validate long-line behaviour and remove obsolete fixed-cell code only after all dependent paths have migrated.

## Phase 1 acceptance

Phase 1 was locally accepted on 2026-11-04: Release x64 build succeeded and all 501 Presentation tests passed.

- The Presentation project contains no WinUI/Win2D references in the new contract.
- All public text positions in the contract are explicitly source UTF-16 positions.
- Hit-test results can represent supplementary characters without splitting surrogate pairs.
- Empty lines and multi-rectangle selection/annotation geometry are part of the contract.
- The existing renderer, annotation storage and document-offset mapping remain unchanged.
