# Novel project and annotation architecture

## Project ownership

Import creates a byte-for-byte copy of the selected TXT inside a new project folder. The original source path is retained only as optional import provenance in future metadata; reading, editing, chapter detection and annotations target the project copy exclusively.

Initial project layout:

```text
<Project>/
  project.json
  novel.txt
  annotations.sqlite
  backups/
  edit_sessions/
    <session-id>/
      baseline.txt
      session.json
```

The project document is opened through the existing positional byte-source and line-index pipeline. Import and hashing are streaming operations; they do not load the entire novel into memory.

## Annotation model

- `AnnotationCategory`: user-managed category.
- `NovelAnnotation`: independent note content and optional single category.
- `AnnotationAnchor`: one [start, end) UTF-16 range in the current project document.
- `NovelChapter`: detected chapter range in the current project document.
- `BigAnnotationCategory`: category namespace dedicated to big annotations.
- `BigAnnotation`: independent long-form note with its own title, body, timestamps and optional category.
- `BigAnnotationChapter`: ordered relation from a big annotation to a stable `ChapterId`.

One annotation can have many anchors. Multiple annotations may have identical, nested, crossing or partially overlapping anchors. No range exclusion constraint is permitted.

Big annotations are independent documents, not parents of small or chapter annotations. Their chapter links are stored in `BigAnnotationChapters`; the store validates that a newly created or changed range contains at least two active, adjacent chapters in chapter order. Existing archived chapter links remain preserved when only the big annotation body is edited. Reference data is queried live from the small-annotation and chapter-annotation tables; it is not copied into the big annotation or represented as a child relationship. A small annotation linked to several selected chapters is returned once with all of its selected source anchors.

SQLite is the durable store. Anchor indexes cover annotation ID, start offset, end offset and chapter ID. An edit queries anchors whose ranges intersect or follow the edit point and updates them in one database transaction.

## WinUI project and marker commands

The main window exposes a `Novel Project` menu:

- `Import TXT as New Project...` asks the user to select the original `.txt` (or `.text`) file. The app creates a project directory under `Documents/FujiyNotepad/NovelProjects/<source-name>.fujiyproject` and copies the source into it; the original file is not changed. If that project name already exists, a timestamped unique name is chosen automatically.
- `Open Project Folder...` opens an existing folder containing `project.json` and loads its project document and SQLite annotation store.
- Project metadata and external-edit manifests use the source-generated `NovelProjectJsonContext`; reflection-based `System.Text.Json` serialization is disabled in the Native AOT application. Failed project imports remove partial metadata, copied documents, and newly created empty project directories.
- `Export Marked Working Copy...` opens a save picker and calls `ExternalEditSession.CreateMarkedAsync`. The marked export is separate from the clean project document.
- `Validate Edited Copy...` asks for the session's `session.json` and the third-party-edited text file, checks that the session belongs to the active project and that the project baseline has not changed, then displays intact and damaged marker counts.
- `Big Annotations Workspace` opens the big-annotation management list. Creating or editing a note opens a two-pane workspace: the left pane queries small and chapter annotations for the linked chapter range; the right pane edits the independent big-annotation title, category and body.
- `Close Novel Project` closes the annotation workspace but leaves the current document open in the ordinary read-only viewer.

The active project folder is persisted alongside the last-session document path in user settings. On startup, the app restores the project workspace (including its SQLite annotation store) before restoring the saved scroll/caret position, rather than reopening only the document as a plain text file. Explicitly closing a project clears this project association while leaving the document open. If the big-annotation editor has unsaved changes, leaving the project first offers save, discard, or continue-editing; cancelling keeps the current project and document active. Project activation failures are surfaced instead of silently leaving the menu in ordinary-file mode.

The small-annotation panel, chapter-annotation panel, chapter directory and big-annotation management/workspace are implemented in the current feature branch. The user has confirmed the big-annotation workspace passes basic workflow testing; full regression and final acceptance remain deferred during the overall reader UI redesign. This first external-edit UI slice deliberately does not apply an edited file. It only validates marker integrity. Diff preview, marker stripping, and the confirmed apply/recovery workflow remain future work. At present, if a project has no saved anchors or chapter ranges, a marked export contains no marker pairs and is effectively a plain text copy.

## External editing workflow

The reader is read-only. Users export a working copy, edit it in a third-party editor, then select the edited file in FujiyNotepad for comparison. The project document remains untouched until the user reviews and applies the diff.

Each export creates an immutable byte-for-byte baseline snapshot under `edit_sessions/<session-id>/baseline.txt`, plus a manifest containing project ID, document revision, byte length, SHA-256, export filename, source/export encoding IDs, and the expected marker identities. The edited file is kept outside the project until apply. Before any diff is applied, the app verifies that the active project document still matches the exported baseline. A stale session must be rejected and re-exported or explicitly reconciled; it must never be silently applied.

The plain-copy API remains available, while `ExternalEditSession.CreateMarkedAsync` queries all non-orphaned annotation anchors and chapter ranges from SQLite and emits an independently paired start/end marker for each. It rejects a stale project fingerprint or any active anchor/chapter from a different document revision. The baseline is copied first and is the sole input to the marked export. Both baseline copying and marked text generation use bounded buffers and temporary files followed by moves, so the novel is never loaded into one large string and an existing export is never overwritten.

The export retains the detected source encoding when it can represent Unicode tag characters. Windows-1252 cannot represent these markers, so only that marked working copy is converted to UTF-8 with BOM; the project document and immutable baseline remain byte-for-byte unchanged. The manifest records both encoding IDs. `ValidateEditedCopyMarkersAsync` scans a selected edited copy incrementally and validates marker identities and positions against the manifest, returning offsets in marker-free UTF-16 text coordinates.

## Invisible external-edit markers

Marked exports use Unicode tag characters (U+E0001 start tag, U+E0000..U+E007E payload tags, U+E007F cancel tag). The tag sequence is default-ignorable in normal text rendering, unlike visible HTML comments or private-use glyphs. It is still not guaranteed to survive every editor: sanitizers, format conversion, or text normalization may remove it. Marker integrity is therefore validated after the edited file is selected; markers are never treated as trusted input.

Each tracked range receives an independent start/end pair carrying a format version, external-edit session ID, target ID (annotation-anchor or chapter), endpoint kind, and short checksum. The design does not depend on marker nesting, so overlapping, nested, crossing, and same-offset ranges are supported. Orphaned anchors are not exported as active ranges.

Marker writing and scanning operate on TextReader/TextWriter buffers and UTF-16 document offsets, not on a whole-file string. The writer inserts all markers at a sorted list of offsets. The scanner reports each marker's offset in the marker-free text, malformed sequences, and then validation detects missing, duplicate, reversed, or unexpected pairs. A complete pair supplies the edited range directly; a damaged pair must be reviewed or reconciled using diff/context matching.

The marker codec, planner, marked export path, expected-marker manifest persistence, and edited-copy marker validation are implemented in the Core layer with tests. The WinUI export/import UI is not yet wired to these APIs. Marker stripping, large-file diff generation, visual diff UI, re-anchoring changed quotes/context, and safe apply/recovery are still pending. Do not apply an edited file yet: marker validation alone does not compare or update the project document.

## Diff and anchor remapping

Diff generation and coordinate mapping are separate responsibilities:

- The diff engine compares baseline and edited text and emits ordered hunks in UTF-16 coordinates: old start/end and new start/end.
- `DocumentDiffMapper` maps unchanged anchor and chapter ranges through those hunks.
- Anchors that intersect changed text are not silently attached to replacement text. They are marked `NeedsReview` for quote/context re-anchoring.
- Anchors whose entire source quote was deleted are retained as orphaned.
- Overlapping, nested and crossing anchors are mapped independently, so one note's range does not suppress another.
- A note with several anchors is retained even if one anchor becomes orphaned.

For a range untouched by any hunk, cumulative length deltas before that range determine its new offset. Insertions exactly at an anchor's start are treated as preceding the quote; insertions at its end are outside it. Insertions strictly inside an anchor require review because the selected source text changed.

The first implementation adds the baseline snapshot/export workflow and pure diff-hunk mapping. It does not yet generate the hunks from two large files, show a visual diff, re-anchor changed quotes, apply the new document, or rebuild the reader indexes. Those are subsequent stages.

## Apply and recovery requirements

Applying a reviewed diff must be a recoverable operation:

1. Revalidate the project ID, revision, length and SHA-256 against the session baseline.
2. Generate and validate all diff hunks and anchor/chapter remap outcomes.
3. Back up the current project document and SQLite database.
4. Write the edited content to a temporary file inside the project folder and flush it.
5. Apply all anchor/chapter updates and revision changes in one SQLite transaction.
6. Atomically replace the project document and record a durable apply journal so startup can finish or roll back an interrupted apply.
7. Rebuild or incrementally update line/viewport indexes, then update project metadata and session status.

The document and SQLite cannot participate in one native filesystem/database transaction. The apply journal and backups are therefore mandatory; simply replacing the text file and then updating SQLite is not sufficiently safe.

## Large-file requirements

- Never materialize the complete novel as a .NET string for ordinary viewing.
- Import, hashing, backups and future text edits must stream or operate on bounded regions.
- SQLite updates only anchors whose ranges intersect or follow the edit point; earlier anchors are not enumerated.
- Large edits may affect many later absolute offsets. The database index narrows the work to the affected suffix; it cannot make every affected offset update constant-time.




## Proportional-font reader layout

The reader is moving from the inherited fixed-cell layout to a font-aware layout contract while preserving the existing streaming document pipeline.

- File-byte offsets remain an implementation detail of the positional byte source and line index.
- Document-wide UTF-16 offsets remain the durable coordinate for chapters and annotation anchors; DocumentTextOffsetMap remains their line/column adapter.
- Layout-local UTF-16 indexes are relative to one original decoded source line. They must map back to the exact source line even if the rendering backend expands tabs or shapes multi-code-unit characters.
- Screen/DIP geometry is temporary and must never be stored in SQLite or used as an annotation anchor.
- The Presentation layer defines ITextLayoutProvider and ITextLayoutResult without WinUI/Win2D dependencies. The WinUI layer will implement the provider using the existing Win2D/DirectWrite stack.
- The current LineColumns, LineWrapper and TextLayoutEngine remain the legacy monospace path until the proportional backend has equivalent scrolling, hit-testing, selection, search and annotation-marker behaviour.

Phase 1 adds the layout contract and headless contract tests only. It does not change the visible renderer, annotation stores, DocumentTextOffsetMap or large-file read/cache limits. See reader-text-layout-architecture.md for the complete migration plan.
