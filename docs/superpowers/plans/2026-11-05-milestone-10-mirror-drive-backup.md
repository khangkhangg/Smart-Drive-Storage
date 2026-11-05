# Milestone 10: Mirror folders and back up drives

**Goal:**
- Mirror any folder to one or more places, on a schedule or live (as files change).
- Back up a whole drive to another drive: file by file now, as a full disk image later.

**Answers we assumed:** the A–E question went unanswered, so these are the defaults. All of them can be changed.
- Mirroring keeps an up-to-date copy of a folder somewhere else (A).
- One source can go to several places (C).
- Drive backup is both a file-level backup and, later, a full disk image (the earlier answer "c").

## Methods considered
| Method | Pros | Cons | Chosen |
|---|---|---|---|
| Build on the M6 sync jobs (mirror, copy, set aside, Undo) | Safety, previews, schedules and Activity already exist and are tested | Needs skip lists and a live trigger | ✅ Phases 1–2 |
| A separate backup engine (archives, deduplication) | Smaller backups | Lots of new code, and files aren't directly readable | ✗ |
| OS imaging tools: wbadmin / Windows Backup, `asr`/`hdiutil`, `dd`/partclone | A real bootable system image | Needs admin rights; different on each OS | Phase 3 |

## Phases
1. **Mirror upgrades** (done):
   - skip lists;
   - several targets at once;
   - live mirror (runs about 30 s after changes go quiet);
   - free-space check.
2. **File-level drive backup** (done):
   - pick a drive and a folder on another drive;
   - skips the system, programs, swap files and caches;
   - keeps files you delete; changed files keep their old version in `.miu-sync-trash`.
3. **Disk image** (done):
   - Windows: system image with `wbadmin` (needs admin), with progress and checks.
   - macOS: `asr` / `hdiutil`.
   - Linux: `dd` / partclone through `pkexec`.
   - Restoring an image always goes through the OS's own recovery tools.
4. **Restore UI** (done): browse a backup, restore a file or folder (never overwriting a different local file), and restore older versions from `.miu-sync-trash`.

## Files
- `src/Miu.Core/Sync/SyncFilter.cs`: skip lists and the default drive skip list for each OS.
- `src/Miu.Core/Sync/SyncJob.cs`: `Excludes`, `RunOnChange`, and `Kind` (Folder or DriveBackup).
- `src/Miu.Platform.Portable/SyncLister.cs`: skipped folders aren't even opened.
- `src/Miu.App/Services/SyncService.cs`: skip lists, the drive-backup checks (read-only source, another drive), and free space.
- `src/Miu.App/Services/ChangeWatcher.cs`: live mirroring.
- `src/Miu.App/Services/AutomationService.cs`: runs jobs whose source changed (respects pause, quiet hours and battery).
- `src/Miu.App/ViewModels/CloudNetworkViewModel.cs` and `Views/CloudNetworkView.axaml`: the job kind switch, the drive picker, several targets, the skip list editor and the live-mirror option.

## Safety rules
- A drive backup only **reads** its source. The target must be an ordinary folder on **another** drive.
- Nothing in the target is deleted. Replaced or removed files are set aside in `.miu-sync-trash` and can be undone from Activity.
- The mass-removal brake still applies, so an unplugged drive never empties a mirror.
- A live mirror never runs while Miu is paused, in quiet hours or on battery. It waits and runs later.
- A run that doesn't fit on the target is refused before anything is copied.

## Prompts (for the next phases)
1. "Phase 3: add a Disk image job type. On Windows, run `wbadmin start backup -backupTarget:<drive> -include:<source> -allCritical -quiet` elevated, stream its progress, record the result in Activity, and refuse a target on the same physical disk."
2. "Phase 4: add Restore to drive backups. Browse the backup tree, restore to the original or another folder with `name (restored date)` on clashes, and list older versions from `.miu-sync-trash`."

## Checklist
- [x] Skip lists (names anywhere, paths from the root), applied while listing
- [x] Skip rebuildable folders (option)
- [x] One source to several targets (one job each)
- [x] Live mirror (run on change, 30 s quiet time, guards)
- [x] Free-space check before copying
- [x] Drive backup: drive picker, default skip list, another drive only, keep deleted files
- [x] Tests: filter, service, watcher, multi-target, drive defaults
- [x] Disk image: Windows wbadmin (system image, -allCritical), macOS hdiutil (startup disk → Time Machine), Linux dd | gzip via pkexec; command shown first, admin prompt, progress, Activity
- [x] Network: sweep for file sharing (port 445), auto search on open, \\\\name lists shares
- [x] Restore browser and older versions (never overwrites: “(restored date)” names; .miu-partial copies)
- [ ] Scheduled disk images (needs a stored admin grant; left out on purpose)
- [ ] Verify an image after it's made (wbadmin get versions / hdiutil verify / gzip -t)
