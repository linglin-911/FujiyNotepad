# Reader startup / native crash diagnostics

## Current reproduction (2026-11-04)

Four user-provided runs are available: 21:48, 22:34, 22:45 and 23:01. The 23:01 run is the first one to reach actual proportional text layout and record a managed exception.

### 21:48 run

- App/MainWindow construction, project restoration, file open and indexing completed.
- `novel.txt` was 76,114,107 bytes; indexing reached 382,033 lines and 100%.
- This run predates first-Draw and first-IndexRefreshTimer instrumentation, so their absence is not evidence that those events did not occur.

### 22:34 run

- MainWindow/XAML initialization and `RootGrid Loaded` completed.
- The first empty `CanvasControl.Draw` acquired a DrawingSession and cleared the background.
- The 76,114,107-byte source indexed to 382,033 lines.
- The first `IndexRefreshTimer` tick entered with indexing complete and stopped at `before View.UpdateTotalLines`.
- This build predates the finer-grained `TextCanvas.UpdateTotalLines` and view-change markers.

### 22:45 run — latest evidence

- MainWindow/XAML initialization and the first empty Canvas draw completed.
- The file indexed to 382,033 lines. `IndexRefreshTimer` called `View.UpdateTotalLines`.
- `TextCanvas.UpdateTotalLines` entered; `Ready`, `EnsureMetrics`, `SyncViewport`, and `ClearTextLayoutCache` all reached their after-markers.
- `TextLayoutEngine.UpdateTotalLines` raised `ViewChanged`, which entered `MainWindow.OnReaderViewChanged` and then `SyncScrollBars`.
- The last stage before the nested calls is `SyncScrollBars: before View.FullyVisibleLineCount`. During this getter, `TextCanvas.Ready` ran again (the log records another `EnsureMetrics` and `SyncViewport` sequence).
- The log does not contain `after View.FullyVisibleLineCount` or any vertical/horizontal ScrollBar property assignment. It does record the `SyncScrollBars` finally block and then the `TextCanvas.UpdateTotalLines` diagnostic finally block.
- There is no `engine.UpdateTotalLines completed`, no `after View.UpdateTotalLines`, no first content-draw completion, managed exception, `MainWindow Closed`, or `ProcessExit` record.

This is strong evidence that the failure occurs during the scrollbar synchronization path while reading the viewport-line-count property, with a re-entrant `Ready` call. It does not prove whether the underlying trigger is the property getter, its nested metrics/viewport work, or an exception/termination elsewhere on that call stack. It does show that the failure occurs before either scrollbar receives a value assignment.

The reader's visible vertical and horizontal ScrollBar controls, their synchronization method, event handlers, and associated layout row/column were removed after this run. The later 23:01 run confirms that the line-count update and first content draw now proceed beyond this former path.

### 23:01 run — first content-draw exception

- MainWindow/XAML initialization, project restoration, 76,114,107-byte file open and indexing to 382,033 lines completed.
- The first `IndexRefreshTimer` tick completed `View.UpdateTotalLines`, marker refresh and pending-navigation updates. `EnsureChapterIndex` also completed.
- The first content draw entered `GetVisibleLines`; proportional layout creation reached `Win2DTextLayoutResult.BuildVisualLineMap`.
- `Win2DTextLayoutProvider.cs:400` read `layout.LineMetrics`, which attempted to marshal a `CanvasLineMetrics[]`.
- The runtime threw `System.NotSupportedException: Cannot handle array marshalling for non blittable type 'Microsoft.Graphics.Canvas.Text.CanvasLineMetrics'.`
- `Application.UnhandledException` recorded the same exception and stack. This is a confirmed managed crash, not an unobserved native exit and not a scrollbar failure.

The failing API is the Win2D `CanvasTextLayout.LineMetrics` array. `CanvasLineMetrics` includes an `IsTrimmed` Boolean field, making the struct non-blittable; the current CsWinRT projection rejects marshalling that array. The stack ends in `BuildVisualLineMap`, before any actual novel text is rendered.

A code fix has been pushed: remove all use of `layout.LineMetrics`; use scalar `layout.LineCount` and `GetCaretPosition` (`Vector2`) calls to reconstruct visual-row source boundaries. It locates wrapped-row boundaries by binary search over grapheme boundaries and falls back to a full grapheme-boundary scan if the scalar row count is not recovered. Per-row height comes from adjacent caret Y positions; single/final-row height uses the layout bounds or previous row spacing. This change has not yet been built or runtime-tested by the user.
## Temporary application diagnostics

The current diagnostic build records:

- The first `CanvasControl.Draw` event even if the reader still has zero indexed lines.
- First content draw milestones before/after metrics, viewport synchronization and `GetVisibleLines`.
- The first `IndexRefreshTimer_Tick`, with before/after markers around `View.UpdateTotalLines`, marker refresh and pending navigation.
- The first `TextCanvas.UpdateTotalLines` call: before/after `Ready`, `ClearTextLayoutCache`, and `TextLayoutEngine.UpdateTotalLines`.
- The `TextCanvas` `ViewChanged` and `RedrawRequested` callbacks while the first line-count update is active.
- The existing managed exception and process-exit diagnostics remain enabled. The reader ScrollBar controls and synchronization path have been removed; annotation-marker refresh remains attached to `OnReaderViewChanged`.

Log path:

```text
%LOCALAPPDATA%\FujiyNotepad\crash.log
```

## Rebuild and interpret the next diagnostic run

The 23:01 log confirms the scrollbar-removal change passed line-count update, marker refresh and chapter indexing. The actual crash is the `CanvasTextLayout.LineMetrics` array marshalling in `BuildVisualLineMap`. The latest code replaces that array with scalar line-count/caret-position queries. Build and test this new change before doing any more native-dump work; WER dumps are not needed for the already captured managed exception.

Before making further rendering changes, build and launch the latest branch. First inspect the working tree; if it contains uncommitted changes, preserve them by committing or safely stashing before switching. Do not discard local work. If already on the target branch, the switch command can be skipped:

```powershell
git status --short --branch
git switch feature/reader-proportional-font-layout
git pull --ff-only origin feature/reader-proportional-font-layout
dotnet build src/FujiyNotepad.slnx --configuration Release -p:Platform=x64
```

Reproduce the blank-screen exit once, then inspect the new `crash.log`.

Interpret the last milestone as follows:

- No `TextCanvas.UpdateTotalLines: entered` after the MainWindow's `before View.UpdateTotalLines`: the failure is at/before entry to the reader control method, or the logger did not flush.
- The TextCanvas method enters but stops at `before Ready`, `Ready before EnsureMetrics`, or `Ready after EnsureMetrics`: inspect metric probing and viewport synchronization.
- It stops before/after `ClearTextLayoutCache`: inspect disposal of cached Win2D layouts.
- It reaches `engine.UpdateTotalLines` but stops during `engine ViewChanged`: inspect the remaining annotation-marker refresh logic in `OnReaderViewChanged`.
- `engine ViewChanged` completes but `engine RedrawRequested` does not: inspect Canvas invalidation.
- `after View.UpdateTotalLines` appears but no `after RefreshMarkerMargin`: inspect marker-margin invalidation/drawing.
- The first timer tick completes but no `TextCanvas: first Draw event entered`: inspect CanvasControl lifecycle, visibility and the reader visual tree.
- A first Draw entry exists but the content-draw milestones stop: inspect the exact last stage (metrics, viewport sync, `GetVisibleLines`, or drawing).
- A `TextCanvas.OnDraw: FAILED` entry with a managed stack trace identifies a managed drawing failure. In the 23:01 run, the stack identifies unsupported WinRT array marshalling for `CanvasLineMetrics[]` in `BuildVisualLineMap`; the latest fix removes this array access.
- If the process still exits without an exception or ProcessExit entry, use Windows Error Reporting LocalDumps below; do not assume Win2D is the cause solely from a truncated log.

## Capture a Windows Error Reporting full dump

If the app still exits without an exception in `crash.log`, configure a per-executable LocalDumps rule. This writes a full user-mode dump when Windows Error Reporting handles an application crash.

Run this in PowerShell (no administrator elevation is normally required for the per-user HKCU key):

```powershell
$dumpDir = Join-Path $env:LOCALAPPDATA "FujiyNotepad\Dumps"
New-Item -ItemType Directory -Force -Path $dumpDir | Out-Null

$key = "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\FujiyNotepad.WinUI.exe"
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DumpFolder -Value $dumpDir -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $key -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name DumpCount -Value 3 -PropertyType DWord -Force | Out-Null

Write-Host "WER full dumps will be written to: $dumpDir"
```

If the executable in the build output has a different filename, replace `FujiyNotepad.WinUI.exe` in the registry key with that exact filename.

After enabling the rule:

1. Launch the application and reproduce the blank-screen exit once.
2. Check `%LOCALAPPDATA%\FujiyNotepad\Dumps` for a new `.dmp` file.
3. Also inspect Application events around the reproduction time:

```powershell
$since = (Get-Date).AddMinutes(-15)
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ProviderName -in @('Application Error', 'Windows Error Reporting') -and
        $_.Message -match 'FujiyNotepad'
    } |
    Select-Object TimeCreated, ProviderName, Id, Message |
    Format-List
```

Send the new `crash.log`, any matching Application Error / Windows Error Reporting event, and (if generated) the dump file. A full dump may be large; do not upload it unless requested, as the exception code, faulting module and event details may first be enough to narrow the cause.

## Disable dump capture afterwards

Once diagnosis is complete, remove only this application's LocalDumps key:

```powershell
Remove-Item "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\FujiyNotepad.WinUI.exe" -Recurse -Force
```

The dump folder can then be deleted manually if no longer needed.

## Important interpretation

A log that stops without a managed exception or `ProcessExit` marker is not proof of a Win2D bug. It is evidence that the current managed logger did not observe the termination. Use the next diagnostic milestones and Windows crash event/dump to identify the actual failing instruction/module before changing rendering behavior.
