# Reader startup / native crash diagnostics

## Current reproduction (2026-11-04)

The latest user-provided `crash.log` records:

- App and MainWindow construction completed.
- `RootGrid Loaded` fired.
- Last-session restore opened `novel.txt`.
- The 76,114,107-byte source was opened and attached to `LineProvider`.
- Background indexing completed at 382,033 lines and reported 100% progress.
- The log ends immediately after the final indexing progress entries.

The log contains no managed exception, `MainWindow Closed`, or `ProcessExit` entry. It also has no `TextCanvas` first-content-draw or `IndexRefreshTimer` milestone entries, but this log was generated before those milestone logs were added to the application. Their absence is therefore not evidence that the corresponding events did not fire. The log confirms the last recorded startup milestones but does not identify the exact failure point.

## Temporary application diagnostics

The current diagnostic build records:

- The first `CanvasControl.Draw` event even if the reader still has zero indexed lines.
- First content draw milestones before/after metrics, viewport synchronization and `GetVisibleLines`.
- The first `IndexRefreshTimer_Tick`, with before/after markers around `View.UpdateTotalLines`, marker refresh and pending navigation.
- The existing managed exception and process-exit diagnostics remain enabled.

Log path:

```text
%LOCALAPPDATA%\FujiyNotepad\crash.log
```

## Rebuild and interpret the next diagnostic run

The first-Draw and first-IndexRefreshTimer milestone logging was added after the user's 21:48 log. It is present on `feature/reader-proportional-font-layout`, but has not yet been confirmed by a user-side build/run.

Before changing rendering code, build and launch the latest branch. First inspect the working tree; if it contains uncommitted changes, preserve them by committing or safely stashing before switching. Do not discard local work. If already on the target branch, the switch command can be skipped:

```powershell
git status --short --branch
git switch feature/reader-proportional-font-layout
git pull --ff-only origin feature/reader-proportional-font-layout
dotnet build src/FujiyNotepad.slnx --configuration Release -p:Platform=x64
```

Reproduce the blank-screen exit once, then inspect the new `crash.log`.

Interpret the last milestone as follows:

- `IndexRefreshTimer: first tick entered` but no `after View.UpdateTotalLines`: inspect the reader engine's line-count update path.
- `after View.UpdateTotalLines` but no `after RefreshMarkerMargin`: inspect marker-margin invalidation/drawing.
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
