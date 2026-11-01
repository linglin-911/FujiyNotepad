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

## Edit coordinate semantics

All document positions are UTF-16 code-unit offsets, matching .NET string indexing. Ranges are half-open: [start, end).

- Insert before or exactly at an anchor start: shift the anchor right, preserving the original quote.
- Insert strictly inside an anchor: extend its end.
- Insert at its end: leave it outside the anchor.
- Delete before an anchor: shift it left.
- Delete part of an anchor: shrink it.
- Replace text inside an anchor: include the replacement in the anchor.
- Delete or replace the entire anchored quote: retain the anchor and mark it orphaned. Never silently retarget it to unrelated text.

The current implementation supplies the streaming project-copy model, SQLite schema/store, category and annotation queries, overlapping-range lookup, chapter persistence, indexed anchor/chapter transforms, and pure edit mapping with unit tests. It does not yet connect a text editor to disk writes. That integration must use a single edit coordinator that stages the document change, applies the anchor/chapter transaction, commits the document atomically, and records inverse snapshots for undo/redo. The current reader remains read-only until that coordinator is implemented.

The schema enforces uniqueness only for an identical range attached twice to the *same* annotation. Identical or overlapping ranges attached to different annotations are valid. A query range returns every intersecting anchor and every distinct note linked to those anchors.

Project import refuses to overwrite an existing project document. The copy is streamed in 1 MiB buffers and fingerprinted with SHA-256 without loading the full file into memory. The project metadata tracks the document revision, byte length and fingerprint. The source path is not required to reopen a project.

## Large-file requirements

- Never materialize the complete novel as a .NET string for ordinary viewing.
- Import, hashing, backups and future text edits must stream or operate on bounded regions.
- SQLite updates only anchors whose ranges intersect or follow the edit point; earlier anchors are not enumerated.
- Large edits may affect many later absolute offsets. The database index narrows the work to the affected suffix; it cannot make every affected offset update constant-time.
