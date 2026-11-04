# Reader startup / native crash diagnostics

## Current reproduction (2026-11-04)

Three user-provided runs are available: the original 21:48 run and the later 22:34 and 22:45 runs.

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

The reader's visible vertical and horizontal ScrollBar controls, their synchronization method, event handlers, and associated layout row/column have now been removed. Mouse-wheel scrolling, programmatic navigation, saved view restoration, caret visibility, and the independent bookmark/find overview ruler remain. The next local run will test whether removing this synchronization path resolves startup.

## Temporary application diagnostics

The current diagnostic build records:

- The first `CanvasControl.Draw` event even if the reader still has zero indexed lines.
- First content draw milestones before/after metrics, viewport synchronization and `GetVisibleLines`.
- The first `IndexRefreshTimer_Tick`, with before/after markers around `View.UpdateTotalLines`, marker refresh and pending navigation.
- The first `TextCanvas.UpdateTotalLines` call: before/after `Ready`, `ClearTextLayoutCache`, and `TextLayoutEngine.UpdateTotalLines`.
- The `TextLayoutEngine.ViewChanged` and `RedrawRequested` callbacks while the first line-count update is active.
- The corresponding `OnReaderViewChanged` and individual `SyncScrollBars` getter/assignment stages, including vertical and horizontal scrollbar updates.
- The existing managed exception and process-exit diagnostics remain enabled.

Log path:

```text
%LOCALAPPDATA%\FujiyNotepad\crash.log
```

## Rebuild and interpret the next diagnostic run

The 22:34 log confirms the first empty Canvas draw and first index-refresh tick, then stops at the call to `View.UpdateTotalLines`. The new fine-grained markers listed above were added after that run and have not yet been confirmed by a user-side build/run.

Before changing rendering code, build and launch the latest branch. First inspect the working tree; if it contains uncommitted changes, preserve them by committing or safely stashing before switching. Do not discard local work. If already on the target branch, the switch command can be skipped:

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
- It reaches `engine.UpdateTotalLines` but stops during `engine ViewChanged`: inspect `OnReaderViewChanged` and the logged scrollbar stages.
- It stops at a specific `VScroll` or `HScroll` assignment: inspect that WinUI ScrollBar property update and the values logged immediately before it.
- `engine ViewChanged` completes but `engine RedrawRequested` does not: inspect Canvas invalidation.
- `after View.UpdateTotalLines` appears but no `after RefreshMarkerMargin`: inspect marker-margin invalidation/drawing.
- The first timer tick completes but no `TextCanvas: first Draw event entered`: inspect CanvasControl lifecycle, visibility and the reader visual tree.
- A first Draw entry exists but the content-draw milestones stop: inspect the exact last stage (metrics, viewport sync, `GetVisibleLines`, or drawing).
- A `TextCanvas.OnDraw: FAILED` entry with a managed stack trace identifies a managed drawing failure.
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
