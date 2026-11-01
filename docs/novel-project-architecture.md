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
```

The project document is opened through the existing positional byte-source and line-index pipeline. Import and hashing are streaming operations; they do not load the entire novel into memory.

## Annotation model

- `AnnotationCategory`: user-managed category.
- `NovelAnnotation`: independent note content and optional single category.
- `AnnotationAnchor`: one [start, end) UTF-16 range in the current project document.
- `NovelChapter`: detected chapter range in the current project document.

One annotation can have many anchors. Multiple annotations may have identical, nested, crossing or partially overlapping anchors. No range exclusion constraint is permitted.

SQLite is the durable store. Anchor indexes cover annotation ID, start offset, end offset and chapter ID. An edit queries anchors whose ranges intersect or follow the edit point and updates them in one database transaction.

## External editing workflow

The reader is read-only. Users export a working copy, edit it in a third-party editor, then select the edited file in FujiyNotepad for comparison. The project document remains untouched until the user reviews and applies the diff.

Each export creates an immutable baseline snapshot under `edit_sessions/<session-id>/baseline.txt`, plus a manifest containing project ID, document revision, byte length, SHA-256 and export filename. The edited file is kept outside the project until apply. Before any diff is applied, the app verifies that the active project document still matches the exported baseline. A stale session must be rejected and re-exported or explicitly reconciled; it must never be silently applied.

The external export is streamed in 1 MiB buffers and uses a temporary file followed by a move, so the app does not overwrite an existing export or the active project document.

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


