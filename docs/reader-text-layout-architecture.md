# Reader proportional-font text layout architecture

Status: approved design; Phase 1 contracts are being introduced on
feature/reader-proportional-font-layout. The existing renderer remains active until the new backend
is integrated and verified.

## Current implementation status (2026-11-04)

- **Phase 1 — accepted:** the user completed a Release x64 solution build and the Presentation test project passed all 501 tests (0 failed, 0 skipped).
- **Phase 2 — backend code implemented, local validation pending:** the WinUI layer now contains a Win2D/DirectWrite provider. It uses real font shaping and line metrics, measured space advances for tab stops, native hit-testing/caret positions, character-region geometry and trailing-whitespace advances. UTF-16 indexes are normalized to Unicode text-element boundaries.
- The Phase 2 provider is not yet wired into TextCanvas or TextLayoutEngine. The current reader therefore keeps its existing visual behaviour; no claim of end-to-end proportional-font support is made yet.
- The new provider rejects embedded CR/LF because its contract is one logical source line. The existing LineProvider remains responsible for separating line terminators.

## Phase 2 backend details

- `Win2DTextLayoutProvider` creates a `CanvasTextLayout` using the selected font, actual viewport width and DirectWrite wrapping.
- Tabs remain a single source character. The backend measures the selected font's space advance and configures DirectWrite's incremental tab stop rather than expanding tabs into a display string.
- `CanvasLineMetrics` supplies wrapped-row source lengths, baseline, height and trailing-whitespace counts.
- Hit testing and character regions are queried from the same native layout used for drawing in the future integration phase.
- `TextElementBoundaryMap` prevents caret movement and returned ranges from splitting surrogate pairs or combining sequences.
- `Win2DTextLayoutResult` owns and disposes its native layout and text format. It is UI-thread-affine and is designed to be cached only for visible logical lines and a small prefetch area.

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
4. **Rendering backend** — the WinUI layer will implement the contract using Win2D/DirectWrite. It owns native layout objects and their disposal.
5. **Canvas layer** — TextCanvas draws and interacts with the layout result. It must not independently estimate character X positions.

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
