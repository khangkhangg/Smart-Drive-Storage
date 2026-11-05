# Milestone 4 — Background & Automations

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Miu keeps drives tidy on its own:
- Watched folders run on a schedule (suggest / auto-clean Safe / just tell me).
- Ready-made templates and a sentence-style editor.
- A tray icon with background mode and start-at-sign-in, which you can turn off.
- Resource guards (battery, quiet hours, pause).
- Notifications with actions.
- "What grew this week", a "full in ~N weeks" forecast, and a weekly digest.
- A real Settings page.

**Architecture:**
- Pure logic goes in `Miu.Core.Automation`: `Watch`, `Schedule`, `Scheduler.NextRun`, `WatchFilters`. Insights go in `Miu.Core.Insights`: `SnapshotDiff`, `Forecast`, `Digest`.
- Persistence goes in `Miu.Data`: `WatchesRepository`, `JobRunsRepository`, `SnapshotsRepository`, `SpaceSamplesRepository`.
- `IStartupRegistration` lives in Abstractions, with two implementations:
  - Windows: the HKCU `Run` key.
  - Linux: an XDG autostart `.desktop` file.
- `IPowerState` uses Windows `GetSystemPowerStatus` and Linux `/sys/class/power_supply`.
- App side:
  - `AutomationService` runs a due-check every minute, then calls `WatchRunner` → `CleanupService`.
  - `INotifier` is implemented by `ToastNotifier`: a small Avalonia window with action buttons, cross-platform.
  - The tray icon comes from Avalonia `TrayIcon`.

**Spec:** features F1.11, F4.1, F4.2, F4.5–F4.12, F8.3 (partial).

## Global Constraints
- **AutoCleanSafe only ever cleans 🟢 Safe items, and only after "Allow automatic cleaning" was turned on in Settings.** Until then it behaves like Suggest (spec §5.5).
- Automatic cleaning goes through `CleanupService` → `SafeRecycler` (Recycle Bin / Trash, logged, undoable). Origin is `auto`, and it teaches the learning system nothing.
- Resource guards skip a run without losing it: when guarded, `NextRun` is pushed by 30 minutes. Guards are battery, quiet hours, and a manual pause.
- Background mode off means: no autostart, and closing the window quits the app.

## Review Focus
1. **The machine was off or asleep at the scheduled time:** the watch runs once when Miu next checks, not several times. Then `NextRun` moves to the next future slot.
2. **The watched folder is missing or unreadable:** the run is logged as failed with a clear reason, and nothing crashes.
3. **Auto-clean is allowed but a Safe item becomes protected or kept since the scan:** `CleanupService` still refuses it through its guard and Keep checks.
4. **DST and month-end dates:** "monthly on the 31st" in a 30-day month runs on the last day; times are local.
5. **The autostart entry with a path containing spaces:** it is quoted correctly in both the Run key and the `.desktop` `Exec`.

### Tasks
1. **Core automation:** `Watch`, `WatchSchedule`, and `Scheduler.NextRun(schedule, afterLocal)`.
   - Supported schedules: daily at a time, weekly on a day and time, monthly on a day (clamped to the month's last day), at sign-in, and low space below N%.
   - `WatchFilters.Matches(size, modified, now)`.
   - TDD: day boundaries, month clamp, missed runs collapse into one, at sign-in, low space.
2. **Core insights:**
   - `SnapshotDiff.Grew(old, new, top)` compares folder sizes by path.
   - `Forecast.DaysUntilFull(samples, total)` is a least-squares slope; it returns null with fewer than 3 samples, under 2 days of history, or a flat or negative trend.
   - `Digest.Build(...)` produces the weekly summary text.
   - TDD.
3. **Data:** the watches, job runs, snapshots and space-samples repositories, plus migration 004. Roundtrip tests.
4. **Platform:**
   - `IStartupRegistration` with `LinuxAutostart(configDir)` (tested) and `WindowsRunKey` (tested on Windows CI).
   - `IPowerState` implementations.
   - Quoting tests.
5. **App services:**
   - `WatchRunner.RunAsync(watch)`: scan → candidates (Safe + filters) → act by mode → job run + notification.
   - `AutomationService`: tick, guards and pause; snapshots, space samples and the weekly digest on each scan.
   - `INotifier`, plus a fake for tests.
   - Tests: suggest notifies; auto-clean refused without opt-in; with opt-in, only Safe items are cleaned and the clean is logged; a missing folder is logged; guards postpone; missed runs collapse.
6. **UI:**
   - **Automations page:** watch list with next run, last result, toggle, "Run now" and delete; a templates gallery; a sentence-style editor.
   - **Settings page:** background mode, allow automatic cleaning, quiet hours, pause on battery.
   - **Tray icon:** Open, Quick Clean, Pause 1 h / until tomorrow, Exit. Close goes to the tray when background mode is on. `--background` starts hidden with no splash.
   - **Toast window** with action buttons.
   - **Home:** "What grew this week" is real, and drive cards show "Full in ~N weeks".
   - Headless tests and screenshots.
7. Docs, independent review, fix pass, push.
