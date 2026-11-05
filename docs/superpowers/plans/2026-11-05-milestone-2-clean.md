# Milestone 2 — Clean Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Users can collect items into a cleanup cart (or press Quick Clean), review them, move them to the Recycle Bin / Trash, see every action in an Activity log, and undo it. Developers also get Docker cleanup.

**Architecture:**
- `IRecycleBin` (Abstractions) has two implementations:
  - `WindowsRecycleBin`: SHFileOperation to delete; restore by parsing `$I` files.
  - `FreedesktopTrash` for Linux: the XDG trash spec.
- `SafeRecycler` wraps any `IRecycleBin` with the `ProtectedPathGuard`. This is the only path to deletion.
- `CleanupCart` (Core) is pure logic.
- `CleanupService` (App) runs the cart, logs the action to `actions_log`, and updates the in-memory `ScanTree`.
- Docker goes through the `docker` CLI behind `IProcessRunner`.

**Tech Stack:** .NET 10, Avalonia 11.3, SQLite, xUnit/FluentAssertions 7.

**Spec:** `docs/superpowers/specs/2026-11-05-miu-smart-storage-design.md`. Features: F2.5, F2.6, F2.7, F2.8, F2.9, F2D.4 (clean actions), F2D.6.

## Global Constraints
- Deletion is only possible through `SafeRecycler`. It re-checks the guard at deletion time, including link targets. Nothing in the UI calls an `IRecycleBin` directly.
- Items always go to the Recycle Bin / Trash. There is no permanent delete in this milestone.
- Protected items can never be added to the cart. Keep and Review items can be added, but they are flagged in the review sheet. Safe items are pre-checked.
- Quick Clean only takes 🟢 Safe items.
- Every clean writes one `actions_log` row (kind `recycle`) that holds the information needed to undo it.
- On macOS there is no recycle implementation yet, so Clean buttons are disabled with an explanation (Phase 9).
- Docker volumes are always Review and never part of a one-tap action.

## Review Focus
1. **A file is in use or locked during recycle:** the other items still go, and the summary names the failed ones. → Task 3/4 tests.
2. **The item was already deleted or moved after the scan:** reported as "no longer there", not a crash. → Task 4.
3. **A parent and its child are both in the cart:** only the parent is recycled, and the total is not double counted. → Task 5.
4. **Undo when the original path exists again (name clash):** undo fails for that item with a clear reason and never overwrites. → Task 2/3.
5. **Trash on a different drive or filesystem than home (Linux):** the item fails with a clear message rather than a slow copy. → Task 2.

---

### Task 1: ScanTree.Remove
- `public void Remove(int id)`: subtracts Size and FileCount from all ancestors, unlinks the node from its parent's children, and marks it removed (`IsRemoved(id)`). Removing the root throws.
- Tests: `Remove_updates_ancestor_sizes`, `Removed_node_not_in_children`, `Remove_root_throws`.

### Task 2: IRecycleBin + FreedesktopTrash
```csharp
public sealed record RecycledItem(string OriginalPath, string TrashLocation, long Bytes);
public sealed record FailedItem(string Path, string Reason);
public sealed record RecycleResult(IReadOnlyList<RecycledItem> Recycled, IReadOnlyList<FailedItem> Failed);
public sealed record RestoreResult(IReadOnlyList<string> Restored, IReadOnlyList<FailedItem> Failed);
public interface IRecycleBin {
  bool IsSupported { get; }
  Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct);
  Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct);
}
```
- `FreedesktopTrash(string trashRoot)`, defaulting to `$XDG_DATA_HOME/Trash` or `~/.local/share/Trash`:
  - Moves the item into `files/<unique name>`.
  - Writes `info/<name>.trashinfo` with `Path=` (percent-encoded) and `DeletionDate=yyyy-MM-ddTHH:mm:ss`.
  - Unique names: `name`, `name.2`, `name.3`, …
  - A move across devices fails with "Can't move to Trash across drives".
- Tests on a temp trash root:
  - `Recycles_file_and_folder_with_trashinfo`
  - `Unique_names_on_clash`
  - `Missing_path_is_reported`
  - `Restore_moves_back_and_removes_info`
  - `Restore_refuses_to_overwrite`

### Task 3: WindowsRecycleBin
- Recycle: `SHFileOperationW` with `FO_DELETE`, `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR`, one call per item so failures stay per-item.
- Before deleting, record the newest `$I*` entry in `<drive>\$Recycle.Bin\<SID>`. After deleting, find the new `$I` whose original path matches; its `$R` sibling is `TrashLocation`.
- `RecycleBinIndex.Parse(byte[])` (portable, unit-tested): version 1 (fixed 520-byte path) and version 2 (length-prefixed path) → `(OriginalPath, Size, DeletedUtc)`.
- Restore: move `$R…` back to the original path (refuse if it exists), then delete `$I…`.
- Tests: `Parse_v2` and `Parse_v1` use byte fixtures and run everywhere. `Recycle_and_restore_roundtrip` runs on Windows only and is skipped elsewhere.

### Task 4: SafeRecycler
- `SafeRecycler(IRecycleBin inner, ProtectedPathGuard guard)` implements `IRecycleBin`.
- Paths that are protected, protected through a link target, missing, or empty are filtered into `Failed` with a reason. Only the rest go to `inner`.
- Tests: `Protected_paths_never_reach_inner`, `Link_into_protected_is_refused`, `Missing_paths_reported`, `Inner_failures_are_merged`.

### Task 5: CleanupCart (Core)
- `CleanupCart`:
  - `Add(CartItem)` and `Remove(path)`
  - `Items` (top-level only: adding a child of an existing item is ignored, and adding a parent drops its children)
  - `TotalBytes`
  - `Clear()`
  - a `Changed` event
- `CartItem(string Path, string Name, long Bytes, Verdict Verdict, string Reason)`. Adding a `Protected` item throws `InvalidOperationException`.
- Tests: `Total_and_items`, `Child_of_existing_is_ignored`, `Parent_replaces_children`, `Protected_rejected`, `Case_insensitive_on_windows_style` (via a comparer parameter).

### Task 6: ActionsRepository (Data)
- `long Add(ActionRecord)`, `IReadOnlyList<ActionRecord> Recent(int n)`, `ActionRecord? Get(long id)`, `void MarkUndone(long id)`.
- `ActionRecord(long Id, DateTime TsUtc, string Kind, IReadOnlyList<RecycledItem> Items, long Bytes, string Origin, bool Undone)`.
- Items are stored as JSON in `items_json`; `undo_token` is "undone" once reverted. Migration `002` adds `undone INTEGER NOT NULL DEFAULT 0`.
- Tests: `Add_and_read_back`, `Recent_is_newest_first`, `MarkUndone`.

### Task 7: CleanupService (App)
- `Task<CleanupOutcome> CleanAsync(IReadOnlyList<CartItem>, string origin)` → `SafeRecycler`, then logs the action, removes the recycled nodes from the session tree, and refreshes safe items and projects.
- `Task<RestoreResult> UndoAsync(long actionId)`.
- `CleanupOutcome(long ActionId, long FreedBytes, int Count, IReadOnlyList<FailedItem> Failed)`.
- `ScanSession.Refresh()` recomputes safe items and projects after removals.
- Tests use a fake `IRecycleBin`:
  - `Clean_logs_action_and_updates_tree`
  - `Failed_items_stay_in_tree`
  - `Undo_calls_restore_and_marks_undone`

### Task 8: Docker
- `DockerDf.Parse(string jsonLines)` reads `docker system df --format "{{json .}}"` into `DockerUsage(Type, TotalCount, Active, SizeBytes, ReclaimableBytes)`. Sizes look like "2.1GB" or "512.3MB (45%)".
- `DockerService(IProcessRunner)`:
  - `GetUsageAsync()` → null when docker is missing or not running
  - `PruneBuildCacheAsync()` → `docker builder prune -f`
  - `PruneImagesAsync()` → `docker image prune -f` (dangling only)
  - `PruneContainersAsync()` → `docker container prune -f`
- Tests: `Parse_df_output`, `Missing_docker_returns_null`, `Prune_runs_expected_commands`.

### Task 9: UI
- **Cart bar** at the bottom of the shell (as in the mockup): count, total, short list, Clear, and "Review & Clean".
- **Add to cart:**
  - Analyze: in the details panel and the row context menu.
  - Home: "Review items" rows.
  - Dev Projects: "Clean rebuildables" per row, which adds the project's junk folders.
- **Review & Clean overlay:**
  - Items grouped by verdict, with a checkbox each (Safe pre-checked).
  - Warning chips for Review and Keep items.
  - Total, and a "Move N items (X) to Recycle Bin" button.
- **Quick Clean** on Home opens the same overlay with every Safe item.
- **Result:** a banner showing "Freed X · Undo", plus any failures.
- **Activity page** in the sidebar: a timeline of actions with an Undo button. After an undo, the current folder is rescanned.
- **Docker card** on Dev Projects, with three prune buttons, plus an "Open Docker Desktop" hint for volumes.
- Tests (headless):
  - `Add_to_cart_from_analyze_updates_bar`
  - `Quick_clean_moves_safe_items_and_undo_restores` (on Linux with a temp trash root, via `AppServices.Create(db, recycleBin)`)
  - `Protected_item_cannot_be_added`
- Regenerate the screenshots.

### Task 10: Docs, review, push
- README, spec checklist, final whole-branch review, fix pass, push, PR update.
