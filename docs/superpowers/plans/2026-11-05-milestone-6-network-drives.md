# Milestone 6 — Network drives & sync

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Miu can find, map, scan and sync network locations:
- Find SMB devices on the local network, or type `\\server\share` (also `smb://server/share`).
- Map a share to a drive letter (Windows), with the password kept in the secret store and optional reconnect at sign-in.
- Scan network folders with a throttled scanner (4 threads), so the NAS and the Wi-Fi are not flooded.
- Sync jobs between any two folders, local or network:
  - **Mirror** (target becomes a copy of source),
  - **Copy only** (new and changed files, nothing removed),
  - **Two-way** (changes on either side, conflicts kept as `name (conflict YYYY-MM-DD).ext`).
- Preview before running (dry run), progress, resumable copies, schedules through Automations, results in Activity and on Telegram.

**Spec:** §5.9, features F6.1–F6.5.

**Architecture:**
- `Miu.Core.Sync` (pure, fully unit-tested):
  - `UncPath.TryParse` normalises `\\server\share\sub` and `smb://server/share/sub`.
  - `SyncEntry(RelPath, Size, MtimeUtc)`, `SyncMode`, `SyncJob`.
  - `SyncDiff.Plan(source, target, baseline, mode, sameContent)` → `SyncPlan` (operations + totals + an optional refusal).
  - `ConflictNames.For(rel, date, exists)`.
- `Miu.Platform.Portable`:
  - `SyncLister` lists a folder tree (skips links, `.miu-sync-trash`, `*.miu-partial`).
  - `SyncRunner` executes a plan: resumable copies through `*.miu-partial`, mtime preserved, and every overwritten or removed file is **moved into `<root>/.miu-sync-trash/<timestamp>/`**, never deleted.
  - `ShareDiscovery`: mDNS (`_smb._tcp.local`) and WS-Discovery probes, with parsers tested on captured packets.
- `Miu.Platform.Abstractions`: `INetworkDrives` (IsSupported, Mapped, MapAsync, UnmapAsync, ListSharesAsync, FreeLetters).
  - Windows: `WindowsNetworkDrives` (WNetAddConnection2W / WNetCancelConnection2W / WNetGetConnectionW / NetShareEnum).
  - Elsewhere: `UnsupportedNetworkDrives` (mount with your file manager; Miu still scans and syncs any mounted folder).
- `Miu.Data`: `NetworkSharesRepository`, `SyncJobsRepository`, `SyncBaselineRepository` (migration 005 `sync_baseline`).
- App: `SyncService` (preview, run, baseline update, job run log, Activity entry with Undo that moves archived files back), sync jobs in `AutomationService.TickAsync`, and a real **Cloud & Network** page with tabs **Network drives** · **Sync jobs** · **Cloud backup** (coming in M7).

## Global Constraints
- **Sync never deletes.** Removing or overwriting a file moves the old copy into `.miu-sync-trash/<timestamp>/` on the same side, logged in Activity and restorable with Undo.
- **Mass-removal brake:** a mirror or two-way run that would archive more than 50% of the other side's files (and more than 20 files), or whose source is empty or missing while the target is not, is **refused** with a clear reason. The preview shows it; "Run anyway" is available only from the preview, never from a schedule.
- Neither root can be inside the other, and neither may be a protected location (`ProtectedPathGuard`).
- A file that changed after the plan was made (size or mtime) is skipped, not copied half-way.
- Copies land in `name.miu-partial` and are renamed into place only when complete. A rerun resumes a partial copy only when the source still matches the plan.
- Passwords live only in the secret store (`net.<unc>`), never in the database or logs.
- Scheduled syncs respect the same guards as watches (pause, battery, quiet hours).

## Review Focus
1. **Mass removal:** an unplugged USB disk or a disconnected share as source must never empty the mirror. A share that times out mid-listing must not look like "everything was deleted".
2. **Two-way correctness:** first run (no baseline), deletions vs. new files, edits on both sides, a file deleted on one side and edited on the other (edit wins), case-only renames on Windows.
3. **Partial copies and resume:** a cancelled run leaves no half-written file under its real name; a resumed copy cannot splice two different versions.
4. **Undo:** archived files go back to where they were, not over a newer file.
5. **Credentials and UNC parsing:** passwords never reach logs or the DB; odd input (`\\server`, `\\\\x`, `smb://`, paths with `..`) is rejected.

### Tasks
1. Core: `UncPath`, `SyncEntry/SyncMode/SyncJob`, `SyncDiff`, `ConflictNames`. TDD: every mode, baseline cases, brake, conflicts, mtime tolerance (2 s for FAT/SMB), case-insensitive matching.
2. Portable: `SyncLister`, `SyncRunner` (temp dirs; cancel mid-copy; resume; changed-after-plan; archive). `ShareDiscovery` packet build/parse tests.
3. Abstractions + Windows `WindowsNetworkDrives` (map/unmap/list; Windows CI tests the free-letter list and the error path for an unreachable server) + `UnsupportedNetworkDrives`.
4. Data: migration 005, the three repositories. Roundtrip tests.
5. App: `SyncService` (preview, run, refusal, baseline, Activity + Undo, job runs), `AutomationService` runs due sync jobs and notifies, network scans throttled, reconnect at start-up for saved shares.
6. UI: Cloud & Network page (tabs), map form, discovered devices list, mapped drives with Scan/Unmap, sync job editor, preview sheet, Activity text for sync. Headless tests and screenshots.
7. Docs, independent review, fix pass, push.
