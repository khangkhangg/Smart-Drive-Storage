# Miu Smart Storage — Design Spec

**Date:** 2026-11-05 · **Status:** Approved (rev 2) · **Platform:** Windows 10 (22H2) / Windows 11 first; macOS and Linux later (Phase 9)
**License / distribution:** Free · direct download (no Store)
**Mockups:** https://claude.ai/artifact/5PtfhdDaUqJTD6ykYroEwN (Home, Analyze, Rules, Cloud & Network)

---

## 1. Purpose and understanding

**Goal.** A Windows app that shows *where your disk space went*, tells you in plain language *what is safe to delete and why*, and then quietly keeps your drives tidy for you: on a schedule, in the background, with Telegram alerts you can act on from your phone.

**What the owner asked for**
- See what each folder contains and how big it is.
- Know whether each item is OK to delete, then delete it (or back it up first).
- A rules engine that **learns** from Keep/Delete decisions and their reasons.
- **Scheduled cleanup** per folder, and **watched folders** that get regular suggestions.
- Sign in to **cloud storage providers** and choose folders to back up.
- **Scan network drives**, **map network drives**, and **sync** with them.
- **Telegram notifications** with action buttons.
- Runs in the **background**, and the user can turn that off.
- Nice-looking, very easy to use, intuitive, fast and light. "Smart, helps the user with their life."

**Assumptions (please correct if wrong)**
- Single user per Windows account. No company or admin console.
- Local-first: no Miu server. All data stays on the PC, apart from what the user sends to their own cloud or Telegram bot.
- Deletes go to the **Recycle Bin** by default. Permanent delete is opt-in and needs extra confirmation.
- English UI first, with strings kept in resource files so it can be localized later.

**Decisions (rev 2, from the owner)**
- **Free**, distributed as a **direct download** (Velopack installer and auto-update). No Microsoft Store and no paid tier.
- **AutoCleanSafe automations are off until the user turns them on once** in Settings › Automations ("Allow automatic cleaning of 🟢 Safe items").
- **Audience: everyone, with special care for developers.** Miu is a storage cleaner for any Windows user first. It also does more for indie hackers and "vibe coders", whose disks fill up with `node_modules`, framework build output, Docker images and volumes, Python venvs and local AI models. Developer features (§5.11) are **added on top of** the general screens: a "For developers" section on Home, a Dev Projects page, and dev rules. They never replace the general overview.
- **Brand:** the app is named "Miu Smart Storage" and its logo is a round, Pixar-style kitten with a mint sparkle. A splash screen shows the logo, "Made with love by Khang", vietnamcos.com and duykhang.com. The About page adds threads.com/@dkhang. The version number appears small at the bottom of Home.
- **Cross-platform-ready:** the UI is **Avalonia** (C#/XAML, Fluent theme) and all logic sits in OS-independent libraries. Windows ships first; macOS and Linux come in Phase 9 by adding platform adapters, without rewriting the UI.

**Success criteria**
1. A first-time user frees space safely within **60 seconds** of opening the app (Quick Clean).
2. Scanning a 1 TB NTFS SSD with ~1M files takes **≤ 5 s** with the elevated MFT reader, and ≤ 60 s without it.
3. **Idle** background agent: < 30 MB RAM and ~0% CPU. It wakes only on schedule or on change-journal events.
4. Installer < 60 MB. App cold start < 1.5 s.
5. **Zero** deletions inside protected system paths, ever. This is enforced in code and covered by tests.
6. Every safety verdict shows a human-readable **"why"** and the rule that produced it.

---

## 2. Research summary

| Tool | Strength | Weakness Miu addresses |
|---|---|---|
| WizTree | Fastest scanner (reads the MFT) | Dated UI; no guidance on what is safe |
| WinDirStat | Classic treemap | Slow; no safety guidance; no automation |
| TreeSize | Polished, reporting | Paid tiers; aimed at IT; no learning |
| SpaceSniffer | Live treemap | Unmaintained; no cleanup workflow |
| Windows Storage Sense | Built-in, safe, scheduled | Shallow: only fixed categories, can't explore |
| CCleaner-style tools | One-click cleaning | Opaque, aggressive, trust issues |

**Gap Miu fills:** fast analysis **plus** explainable safety verdicts, **plus** a personal rules engine that learns, **plus** automation (schedules, background, Telegram), **plus** backup-before-delete. No current tool combines these.

**Key technical findings**
- **MFT enumeration** (`FSCTL_ENUM_USN_DATA` on the volume handle) lists every file record in seconds. It needs admin rights, so it runs in an elevated helper. For directory sizes, the file sizes come from MFT records (`$DATA` attribute), as WizTree does it.
- The **USN change journal** (`FSCTL_READ_USN_JOURNAL`) reports exactly what changed since the last check. The background agent uses it to keep folder sizes up to date **without rescanning**. This is what makes the agent nearly free when idle.
- **Fallback scanner** (no admin rights, network drives, FAT/exFAT): `FindFirstFileExW` with `FIND_FIRST_EX_LARGE_FETCH` and `FindExInfoBasic`, run in parallel over directories with a bounded channel.
- **Recycle Bin delete**: `IFileOperation` with `FOFX_RECYCLEONDELETE`. It supports long paths, reports progress, and keeps the Windows undo behaviour.
- **Network drives**: `WNetAddConnection2W` / `WNetCancelConnection2W` to map and unmap; `NetShareEnum`, WS-Discovery and mDNS to discover shares.
- **Secrets**: Windows Credential Manager (`CredWriteW`) plus DPAPI. No tokens in plain files.
- **Telegram**: the Bot API with **long polling** (`getUpdates`) needs no open ports and no server, and works behind NAT.
- **.NET version:** .NET 8 support ends on **10 Nov 2026**. **Miu targets .NET 10 LTS** (supported until Nov 2028): same C# stack, longer support.

---

## 3. Architecture: approaches considered

| | A. UI app + tray Agent (**recommended**) | B. UI app + Windows Service | C. UI app only |
|---|---|---|---|
| Background work | Agent process starts at sign-in | Service runs at boot, as SYSTEM | Only while the window is open or minimized |
| Recycle Bin and user files | ✅ runs as the user | ❌ SYSTEM has no per-user Recycle Bin | ✅ |
| Cloud tokens (user's Credential Manager) | ✅ | ❌ awkward | ✅ |
| MFT speed | Via a small elevated helper (on request, or a scheduled task set to "highest privileges") | ✅ always | Via the elevated helper |
| RAM while idle | ~25 MB (agent only) | ~25 MB | 0 when closed, 150 MB+ when open |
| Turn off | Toggle in Settings removes the startup task | Service config (needs admin) | n/a |

**Decision: A.** Every OS-specific call goes through interfaces in `Miu.Platform.Abstractions` (`IScanner`, `IRecycleBin`, `ISecretStore`, `IStartupRegistration`, `INotifier`, `INetworkMapper`, `IChangeWatcher`), implemented in `Miu.Platform.Windows` now and in `Miu.Platform.MacOS` / `Miu.Platform.Linux` later. It has three parts:
- **`Miu.App`**: the Avalonia window. It opens on demand and closes completely.
- **`Miu.Agent`**: a headless tray process that runs schedules, watches the USN journal, polls Telegram, and runs backups and syncs.
- **`Miu.Elevated`**: a tiny helper started with admin rights only for MFT and USN reads. It streams results back and exits.

They talk over **named pipes**, carrying JSON messages defined in `Miu.Contracts`.

```
┌──────────────┐  named pipe   ┌───────────────────────────────┐
│  Miu.App     │◀─────────────▶│  Miu.Agent (tray, per user)   │
│  Avalonia UI │               │  Scheduler · Watchers · Jobs  │
└──────┬───────┘               │  Telegram poller · Toasts     │
       │                       └──────┬─────────────┬──────────┘
       │ (in-proc when agent off)     │             │
┌──────▼──────────────────────────────▼──┐   ┌──────▼─────────┐
│ Miu.Core  (scan model, rules, cleanup, │   │ Miu.Elevated   │
│ suggestions, backup/sync orchestration)│◀──│ MFT/USN reader │
└──────┬──────────────┬──────────────────┘   └────────────────┘
       │              │
┌──────▼─────┐  ┌─────▼────────────────────────────────────────┐
│ Miu.Data   │  │ Miu.Platform (Win32) · Miu.Cloud · Miu.Notify │
│ SQLite     │  │ RecycleBin, WNet, CredMan · GDrive/OneDrive/  │
└────────────┘  │ Dropbox/S3 · Toast, Telegram                  │
                └──────────────────────────────────────────────┘
```

When background mode is **off**, the App hosts the same services in-process, so everything still works while the window is open.

---

## 4. Tech stack

| Concern | Choice |
|---|---|
| Language / runtime | C# 14, **.NET 10 LTS** |
| UI | **Avalonia 11** with the Fluent theme (Windows 11 look, Mica on Windows), light/dark/high-contrast. Runs on Windows, macOS and Linux. |
| MVVM | CommunityToolkit.Mvvm (source generators) |
| DI / hosting | Microsoft.Extensions.Hosting (App and Agent share the same composition) |
| Tray icon | Avalonia `TrayIcon` (cross-platform) |
| Storage | **SQLite** (Microsoft.Data.Sqlite + Dapper), WAL mode, a single file in `%LOCALAPPDATA%\Miu` |
| Scan snapshots | Compact binary file per scan (`.miuscan`, compressed with Brotli). Folder-level aggregates also go into SQLite for trends. |
| Hashing (duplicates) | `System.IO.Hashing.XxHash3`. SHA-256 only on request. |
| Treemap | Custom Avalonia control drawn with Skia, squarified layout in `Miu.Core` (pure, unit-tested) |
| Cloud | Google.Apis.Drive.v3, Microsoft.Graph (OneDrive), Dropbox.Api, AWSSDK.S3 (any S3-compatible service) |
| Telegram | Telegram.Bot |
| Notifications | `INotifier`: Windows toast (WinRT `ToastNotificationManager`) first; macOS `UNUserNotificationCenter` and Linux libnotify in Phase 9 |
| Logging | Serilog to rolling files, plus an in-app Activity log backed by SQLite |
| Packaging / updates | **Velopack** direct-download installer with delta auto-updates (Windows now; `.dmg`/AppImage in Phase 9) |
| Secrets | `ISecretStore`: Windows Credential Manager + DPAPI now; macOS Keychain and Linux libsecret in Phase 9 |
| Tests | xUnit, FluentAssertions, Verify (snapshot tests); WinAppDriver/Appium for UI smoke tests |
| CI | GitHub Actions on `windows-latest`: build, test, package |

> Note on the owner's usual stack (PHP/MySQL/MongoDB): none of it is used here, because a native Windows tool needs low-level disk access and no server. If Miu later gets a website or license server, PHP/MySQL fits there.

---

## 5. Domain design

### 5.1 Scan model
- `ScanNode { Id, ParentId, Name, IsDir, Size, SizeOnDisk, FileCount, Created, Modified, LastAccess, Attributes, Ext, Category }`, held in memory as **struct arrays**. Names live in a shared string pool, which allows about 10M nodes in less than 1 GB.
- Categories: Video, Image, Audio, Archive, Installer, Document, Code/Dev, Game, AppData/Cache, System, Other. They are derived from the extension plus path heuristics.
- **Live results**: the scanner sends a batch of results every 100 ms, so the UI fills in while scanning continues.
- **Snapshots**: folder aggregates are saved after each scan. "What grew" is the difference between two snapshots.

### 5.2 Safety verdicts
| Verdict | Badge | Meaning | Can delete? |
|---|---|---|---|
| **Protected** | 🔴 Do not delete | System or app-managed (Windows, Program Files, ProgramData\Microsoft, active profiles, the pagefile, launcher-managed games) | **Never.** The app shows the right way instead (uninstall, Disk Cleanup, `DISM /StartComponentCleanup`). |
| **Keep** | 🔴 Keep | The user said keep, or personal irreplaceable data | Only after explicit confirmation; Miu never suggests it |
| **Review** | 🟡 Review first | Probably fine, but depends on the user (media, Downloads, ISOs) | Yes, and Miu suggests "Back up first" |
| **Safe** | 🟢 Safe | Can be rebuilt or is junk (temp, caches, `node_modules`, crash dumps) | Yes, and it is eligible for Quick Clean and automation |

### 5.3 Rules engine
A **Rule** is:
```json
{
  "id": "learned-0018",
  "match": { "path": "D:\\Photos\\Lightroom Catalog\\**", "ext": [], "nameRegex": null },
  "conditions": { "olderThanDays": null, "minSizeMB": null, "projectIdleDays": null, "notAccessedDays": null },
  "verdict": "Keep",
  "reason": "Active project",
  "source": "learned",            // builtin | learned | user | protected
  "priority": 800,
  "enabled": true,
  "createdFrom": ["decision-311","decision-322"],
  "stats": { "hits": 1, "accepted": 0, "rejected": 0 }
}
```
- **Evaluation order:** Protected first (hard-coded, cannot be overridden), then user and learned rules, then built-in, then the default (Review). Within a tier the **most specific** match wins (longest path prefix, then the most conditions met). Matching uses a compiled glob trie, so whole trees are evaluated cheaply.
- **Built-in pack:** about 150 rules shipped as JSON under `rules/builtin/`. They cover Windows, browsers, developer tools (npm, pip, NuGet, Gradle, Docker, VS), Office, launchers and media apps, and are versioned and updatable.
- **Explainability:** every verdict carries `{ruleId, reason, confidence}`, shown in the details panel ("Rule: dev.node_modules + stale>90d · confidence 98%").

### 5.4 Learning from the user ("gets smarter")
Deterministic and explainable. **No black-box ML.**
1. **Capture decisions.** Every Keep, Delete, "never suggest" and "always OK", plus an optional reason chip (Active project · Rebuildable · Backed up elsewhere · Never used · Work files · custom text), is saved as a `Decision` together with the item's features: path, parent, extension, name pattern, size bucket, age bucket, category, and whether it is inside a git repo.
2. **Implicit signals.** Removing an item from the cart, ignoring a suggestion 3 times, or restoring something from the Recycle Bin (detected by the agent) count as weak *negative* signals.
3. **Pattern mining** runs nightly in the Agent. It groups decisions by generalized keys: the same parent folder, the same extension in the same parent, the same folder name anywhere (e.g. `bin`, `obj`), and the same category plus age bucket. A **suggested rule** is produced when there are **≥ 3 consistent decisions, no contradictions, and confidence ≥ 90%**.
4. **The user approves.** Suggestions show up on the Rules page and in the weekly digest, each with its evidence ("You deleted 9 of 9 such files, reason 'Never used'"). They only take effect after the user taps **Turn on**. An optional setting auto-accepts Keep rules, since those are always safe.
5. **Feedback loop.** Accepted or rejected counts adjust confidence. A rule with a falling acceptance rate is flagged for review.
6. **Portability.** Rules can be exported and imported as JSON.

### 5.5 Watched folders and schedules
A **Watch** applies to one folder or drive:
```
Watch { path, mode: Suggest | AutoCleanSafe | NotifyOnly, schedule (cron-like: daily/weekly/monthly/at sign-in/on low space),
        filters { olderThanDays, minSizeMB, ext[], categories[] }, action { Recycle | MoveTo(path) | BackupThenRecycle(planId) },
        retention { keepNewest N, keepDays D }, notify { toast, telegram }, quietHours, onlyWhenIdle, onlyOnAC }
```
**Ready-made templates (one tap):**
- Downloads tidy: installers or archives older than 30 days go to the Recycle Bin.
- Screenshots older than 60 days: back up to cloud, then free the space.
- Temp and caches: weekly auto-clean (Safe items only).
- Dev projects: `node_modules` / `bin` / `obj` in projects idle for 90 days.
- Low space guard: when a drive drops below X% free, run a suggestion scan and notify.

**AutoCleanSafe only ever acts on 🟢 Safe items, and only after the user has turned on "Allow automatic cleaning" once in Settings.** Until then, AutoCleanSafe watches behave like Suggest. Anything that isn't Safe always becomes a suggestion.

### 5.6 "Smart and helps your life" features
- **Space forecast:** a linear trend over snapshots, e.g. "C: will be full in about 3 weeks at this rate."
- **Weekly digest** (toast and/or Telegram): space freed, what grew, new suggested rules, backup health.
- **Explain big system files:** hiberfil, pagefile, WinSxS, System Volume Information, Windows.old. Each gets an explanation and a guided safe action.
- **"Before you delete"** check: if a file isn't in any backup, Miu offers "Back up first".
- **Duplicate finder** that keeps the best copy: the oldest original, in the preferred folder.
- **Forgotten big files:** large files not opened in over a year.
- **Plain-language everything:** no jargon, every number has context ("about 9 movies' worth").

### 5.11 Developer junk (indie hackers and vibe coders) — first-class
Vibe-coding produces dozens of throwaway projects, each with hundreds of MB to GBs of rebuildable files. Miu detects them, explains them, and turns cleanup into one-tap rules.

**Project detection.** A folder is a *project* when it has a marker file: `package.json`, `pnpm-lock.yaml`, `bun.lockb`, `Cargo.toml`, `pyproject.toml`, `requirements.txt`, `go.mod`, `pom.xml`, `build.gradle`, `*.csproj`/`*.sln`, `Gemfile`, `composer.json`, `pubspec.yaml`, `Dockerfile`/`compose.yaml`, `.git`.
- **Last activity** is the newest of: the git HEAD commit time, the newest source-file mtime (excluding junk folders), and the last opened time.
- **Idle buckets:** active (under 14 days), cooling (14–90 days), idle (over 90 days), abandoned (over 1 year).

**Dev junk catalog** (built-in rules in `rules/builtin/dev.json`; all *rebuildable*):
| Ecosystem | Folders / files | Verdict |
|---|---|---|
| Node / JS | `node_modules`, `.next`, `.nuxt`, `.svelte-kit`, `.turbo`, `.parcel-cache`, `.vite`, `.angular/cache`, `dist`/`build`/`out` *with a build-script marker*, `.vercel/output`, `.netlify`, `storybook-static`, `coverage` | 🟢 when the project is idle > 14 d; 🟡 when active |
| Package caches | npm `_cacache`, pnpm store, Yarn cache, Bun cache, Deno cache | 🟢 (shows "re-downloads on next install") |
| Python | `.venv`/`venv`/`env` (with `pyvenv.cfg`), `__pycache__`, `.pytest_cache`, `.mypy_cache`, `.ruff_cache`, `.tox`, pip cache, conda `pkgs` | 🟢 |
| Rust / Go / JVM / .NET | `target/` (with `Cargo.toml`), Cargo registry cache, Go build and mod cache, `.gradle`, `build/` (Gradle), `~/.m2` (old artifacts), `bin`/`obj` (with a project file), NuGet `http-cache` | 🟢 / 🟡 (`~/.m2`, NuGet packages) |
| Mobile | Android `build/`, old AVD images, Gradle caches, Xcode `DerivedData` (macOS) | 🟢 / 🟡 |
| Docker | dangling images, stopped containers, unused volumes (🟡), build cache, and on Windows the **WSL2 / Docker Desktop VHDX that never shrinks** (`docker_data.vhdx`, `ext4.vhdx`) | 🟢 cache and dangling items; 🟡 volumes; VHDX: guided "Compact" |
| AI / ML | Hugging Face cache, Ollama models, LM Studio models, PyTorch hub, Playwright and Puppeteer browsers, Cypress cache | 🟡 (big but slow to re-download: shows size and last-used date) |
| Misc | `.terraform`, Unity `Library/`, `.cache` in projects, editor logs, crash dumps | 🟢 / 🟡 |

**Docker integration.**
- Talks to the Docker Engine API (named pipe `//./pipe/docker_engine`, or `/var/run/docker.sock`), falling back to `docker system df -v`.
- Shows images, containers, volumes and build cache with sizes and their last-used time.
- Offers the same actions as `docker image prune`, `docker builder prune` and `docker container prune`. Volumes are always 🟡 and need an explicit choice.
- **VHDX compaction wizard (Windows):**
  1. Prune inside Docker.
  2. `wsl --shutdown`.
  3. `Optimize-VHD`, or `diskpart compact vdisk` when Hyper-V tools are missing.

  This step needs admin rights and shows the before and after sizes. It is often the single biggest win (10–100 GB).

**"Dev Projects" view** (a tab in Analyze, plus a Home card for developers):
- A table of every project found: name, path, stack icons, last activity, total size, **rebuildable size**, and git status (uncommitted changes ⚠).
- Row actions:
  - **Clean rebuildables**: removes only the junk folders, never the source.
  - **Archive project**: zips the source without junk, then optionally backs it up to the cloud and removes it locally.
  - **Open in editor**.
- **Bulk action:** "Clean all projects idle > 90 days: 47.3 GB".
- **Safety:**
  - Never touches a project with uncommitted git changes unless confirmed.
  - Never removes source files.
  - Shows "rebuild with `npm install`" (or the stack's own command) on every item.

**One-tap rules from recommendations.** Every dev recommendation has **"Make this a rule"**. It pre-fills a rule or watch, for example: *"Every **week**, in **D:\Code**, remove **node_modules / .next / dist** in projects idle **> 30 days**"* (a Suggest watch by default, or auto once automation is allowed). Learned decisions on dev folders (§5.4) also produce such suggestions.

**Dev digest line:** "Your 23 side projects hold 31 GB of rebuildable files; 18 are idle > 90 days."

### 5.7 Telegram integration
- **Setup wizard:** (1) the user creates a bot with @BotFather and pastes the token; (2) Miu shows a 6-digit pairing code; (3) the user sends `/pair 123456` to the bot. Miu then stores **only** that `chat_id`. The token is kept in the OS secret store.
- **Transport:** long polling from the Agent. No webhook and no open port.
- **Messages with inline buttons**, for example:
  `🧹 Downloads has 4.2 GB of old installers (9 files).  [Clean now] [Show list] [Snooze 7d] [Never for this folder]`
- **Commands:** `/status` (drive usage and agent state), `/scan C`, `/suggest`, `/pause 2h`, `/resume`, `/digest`, `/help`.
- **Security limits:**
  - Only the paired chat id is accepted; everything else is ignored.
  - Remote actions are limited to **Recycle Bin of 🟢 Safe items** and snoozes or "never" rules.
  - No permanent delete and no Protected or Keep items from Telegram, ever.
  - Each action needs a second confirmation tap and expires after 24 h.
  - Each action is logged and can be undone from the app.
- The user can unpair at any time from the app, or with `/unpair`.

### 5.8 Background mode
- The setting **"Run Miu in the background"** is on by default after onboarding asks. When it is on, Miu creates a per-user Task Scheduler task at sign-in. Turning it off removes the task and stops the Agent.
- **Resource guards:** below-normal CPU and I/O priority; pause on battery saver, fullscreen games or presentations, and metered networks (for cloud work); quiet hours.
- The tray menu has: Open Miu · Quick Clean · Pause 1h/until tomorrow · Exit.

### 5.9 Network drives
- **Discover:** WS-Discovery, mDNS and NetBIOS browsing, plus manual `\\server\share` entry.
- **Map / unmap:** pick a drive letter, enter credentials (saved in the OS secret store), and choose whether to reconnect at sign-in.
- **Scan:** uses the parallel fallback scanner with throttling, because MFT reading is not possible over SMB.
- **Sync jobs:**
  - Modes: **one-way mirror**, **one-way copy (no delete)**, or **two-way**. Two-way keeps a conflict copy named `name (conflict YYYY-MM-DD).ext`.
  - Comparison uses size + mtime, with an optional hash.
  - Copies resume if interrupted, and a dry-run preview is available.

### 5.10 Cloud backup
- **Providers v1:** Google Drive, OneDrive, Dropbox, and S3-compatible storage (AWS, Backblaze B2, Wasabi, Cloudflare R2).
- **Sign-in:** OAuth 2.0 with PKCE in the system browser, using a loopback redirect. Tokens go to the OS secret store.
- **Backup plan:** selected folders (tri-state tree), a destination folder, a schedule, and one of these modes:
  - **Incremental copy**
  - **Mirror**
  - **Back up, then free local space**: a file is uploaded, then verified by size and checksum where the provider supplies one, and only then moved to the Recycle Bin.
- **Versions kept:** last N, or N days. Supported where the provider allows it; otherwise Miu keeps a `.miu-versions/` folder.
- **Reliability:**
  - Chunked or resumable uploads.
  - Bandwidth limits and pause on metered networks.
  - A local manifest in SQLite (path, size, mtime, remote id, hash), so only changed files are uploaded.
- **Restore:** browse the backup inside Miu and restore it to the original or another location.

---

## 6. Data model (SQLite)

```
settings(key PK, value)
volumes(id PK, root, label, fs, kind[local|network|cloud], last_scan_id)
scans(id PK, volume_id, started, finished, method[mft|fallback], total_size, file_count, snapshot_path)
folder_snapshots(scan_id, path_hash, path, size, file_count)         -- folder-level only, for trends
rules(id PK, json, source, priority, enabled, created, updated)
decisions(id PK, ts, path, features_json, action[keep|delete|never|always], reason, origin[app|telegram|auto])
rule_suggestions(id PK, rule_json, evidence_json, confidence, status[pending|accepted|rejected], created)
watches(id PK, path, json, enabled, next_run)
job_runs(id PK, watch_id|plan_id|sync_id, started, finished, status, freed_bytes, summary_json)
actions_log(id PK, ts, kind[recycle|delete|move|backup|sync|map], items_json, bytes, undo_token, origin)
cloud_accounts(id PK, provider, display_name, cred_ref)               -- secret lives in the OS secret store
backup_plans(id PK, account_id, json, enabled, next_run)
backup_manifest(plan_id, local_path, size, mtime, hash, remote_id, uploaded_at)
network_shares(id PK, unc, letter, cred_ref, reconnect)
sync_jobs(id PK, source, target, mode, json, enabled, next_run)
telegram(id PK=1, chat_id, cred_ref, paired_at, prefs_json)
pending_remote_actions(id PK, payload_json, expires_at, status)
```
Migrations are versioned SQL scripts under `src/Miu.Data/Migrations/`.

---

## 7. UI and UX

**Shell:** a sidebar with Home · Analyze (incl. Dev Projects) · Clean · Automations · Rules · Cloud & Network · Activity · Settings. The cleanup cart bar is always visible when it holds items.

| Screen | Key elements |
|---|---|
| **Onboarding (3 steps)** | Pick drives → "Run in background?" → optional Telegram/cloud (skippable). Ends with the first scan running. |
| **Home** | Location cards (local, network, cloud) with usage bars and status chips; **Quick Clean** card with the safe total; What grew; Forecast; Rules summary |
| **Analyze** | Breadcrumb, search (`size:>1GB ext:mp4 older:1y`), view toggle Tree / Map / Types / Duplicates / Largest; three panes: tree, treemap, details with verdict, **Teach Miu** buttons and reason chips |
| **Clean (Review & Clean)** | Grouped list by category and verdict, totals, "Back up first" toggle, final confirmation sheet |
| **Automations** | Watched folders list with next run and last result; templates gallery; editor using plain sentences ("Every **week**, in **Downloads**, move **installers** older than **30 days** to the **Recycle Bin** and **tell me on Telegram**") |
| **Rules** | Stats, Suggested rules (Turn on / Not now), filterable table, toggle, edit, import/export |
| **Cloud & Network** | Tabs: Cloud backup (accounts, plans, restore) · Network drives (shares, map form, sync jobs) |
| **Activity** | Timeline of every action with **Undo**; filter by origin (app, schedule, Telegram) |
| **Settings** | Background mode, resource guards, quiet hours, Telegram, notifications, theme, permanent-delete toggle, data export, about/updates |

**UX principles**
- **One obvious action per screen.** A green primary button for safe actions; a red one only for permanent delete.
- **Progressive disclosure.** Quick Clean for everyone; Analyze for curious users; Rules and Automations for power users.
- **Undo for everything** through the Recycle Bin and the action log. Undo is offered as a toast right after each cleanup.
- **Keyboard:** `Ctrl+F` search, `Space` add to cart, `Del` review and clean, `Alt+←` back, `Ctrl+1..8` switch pages.
- **Accessibility:**
  - Text contrast of 4.5:1 or better.
  - Verdicts use colour **plus** an icon **plus** a label.
  - Every element has a Narrator name.
  - High-contrast theme supported.
  - Targets are at least 40 px.

---

## 8. Error handling and safety

- **Protected path guard** runs inside `Miu.Platform.RecycleBin` itself, the lowest layer, so no caller can bypass it. Unit tests cover every protected root, junctions and symlinks (never followed while scanning or deleting), and `\\?\` paths.
- **Locked or in-use files** are skipped, collected into a list, and reported as "12 files were in use; try again after closing X".
- **Partial failures** never roll back successful items. The summary shows exact counts.
- If **elevation is denied**, Miu falls back to the normal scanner and shows a banner: "Faster scans available: allow admin."
- **Cloud and network errors** retry with exponential backoff. Expired tokens prompt a re-sign-in; there are no silent failures.
- **Agent crash:** the Task Scheduler task restarts it, and the next app launch shows a crash report with a "Copy details" button.
- **Database corruption:** the database is backed up daily (keeping 3 copies) and restored automatically if the integrity check fails.

---

## 9. Testing strategy

- **Core (pure C#):** rules evaluation, glob matching, pattern miner, schedule calculation, sync diff, forecast. Covered by xUnit with test-first development.
- **Platform:** the fallback scanner and Recycle Bin are integration-tested against a temporary VHDX or a temp folder tree. The MFT reader is tested on a CI-created VHDX mounted as NTFS (admin runner).
- **Data:** migrations are tested from an empty database and from each previous version.
- **Cloud:** each provider sits behind `ICloudProvider`, with contract tests against a fake. Real-provider smoke tests are run manually.
- **Telegram:** parsing and authorization (wrong chat id rejected, expired actions rejected) are unit-tested with a fake client.
- **UI:** ViewModel unit tests, plus a small WinAppDriver smoke suite (launch, scan a temp folder, add to cart, clean, undo).
- **Performance budget tests:** scanning a synthetic 1M-file tree must stay under a set time; agent idle RAM is measured in CI.

---

## 10. File structure

```
Smart-Drive-Storage/
├─ Miu.sln
├─ Directory.Build.props            # shared versions, nullable, analyzers
├─ README.md
├─ .github/workflows/ci.yml
├─ docs/
│  ├─ superpowers/specs/2026-11-05-miu-smart-storage-design.md
│  └─ superpowers/plans/            # per-phase implementation plans
├─ rules/
│  └─ builtin/                      # windows.json, browsers.json, dev.json, apps.json, media.json
├─ src/
│  ├─ Miu.Contracts/                # IPC messages, DTOs, enums (no dependencies)
│  ├─ Miu.Core/
│  │  ├─ Scanning/                  # ScanTree, ScanNode, IScanner, Categorizer, SnapshotDiff
│  │  ├─ Rules/                     # Rule, RuleEngine, GlobTrie, BuiltinRuleLoader, ProtectedPaths
│  │  ├─ Learning/                  # Decision, FeatureExtractor, PatternMiner, SuggestionService
│  │  ├─ Cleanup/                   # CleanupCart, CleanupPlanner, QuickClean, DuplicateFinder
│  │  ├─ Automation/                # Watch, Schedule, WatchRunner, Templates
│  │  ├─ Insights/                  # Forecast, Digest, WhatGrew
│  │  ├─ Backup/                    # BackupPlan, BackupRunner, Manifest, ICloudProvider
│  │  └─ Sync/                      # SyncJob, SyncDiff, SyncRunner
│  ├─ Miu.Platform.Abstractions/    # IScanner, IRecycleBin, ISecretStore, IStartupRegistration, INotifier, INetworkMapper, IChangeWatcher
│  ├─ Miu.Platform.Portable/        # cross-platform implementations: ManagedScanner (System.IO, parallel), path guard base
│  ├─ Miu.Platform.Windows/         # Windows-only
│  │  ├─ Scanning/                  # FallbackScanner (FindFirstFileEx), MftClient (talks to Elevated)
│  │  ├─ FileOps/                   # RecycleBin (IFileOperation), Windows protected paths
│  │  ├─ Network/                   # WNetMapper, ShareDiscovery
│  │  ├─ Security/                  # CredentialStore (CredMan + DPAPI)
│  │  └─ System/                    # StartupTask, PowerState, IdleDetector, Elevation, VhdxCompactor
│  ├─ (Miu.Platform.MacOS/, Miu.Platform.Linux/ — Phase 9)
│  ├─ Miu.Dev/                      # ProjectDetector, DevJunkCatalog, DockerClient, GitStatus
│  ├─ Miu.Elevated/                 # tiny admin exe: MFT enumerate + USN journal read → pipe
│  ├─ Miu.Data/                     # SqliteDb, Migrations/*.sql, repositories
│  ├─ Miu.Cloud/                    # GoogleDriveProvider, OneDriveProvider, DropboxProvider, S3Provider, OAuthLoopback
│  ├─ Miu.Notify/                   # ToastNotifier, TelegramBotService, PairingService, RemoteActionGate
│  ├─ Miu.Agent/                    # Program.cs (Generic Host), Tray, Scheduler, UsnWatcher, PipeServer
│  └─ Miu.App/                      # Avalonia (cross-platform UI)
│     ├─ App.axaml, Views/MainWindow.axaml
│     ├─ Views/                     # Home, Analyze, Clean, Automations, Rules, CloudNetwork, Activity, Settings, Onboarding
│     ├─ ViewModels/
│     ├─ Controls/                  # Treemap (Skia), SizeBar, VerdictBadge, CartBar, ReasonChips, SentenceRuleEditor
│     ├─ Services/                  # AgentClient (pipe), NavigationService, ThemeService
│     ├─ Strings/Resources.resx
│     └─ Assets/
└─ tests/
   ├─ Miu.Core.Tests/
   ├─ Miu.Platform.Tests/
   ├─ Miu.Dev.Tests/
   ├─ Miu.Data.Tests/
   ├─ Miu.Notify.Tests/
   └─ Miu.App.UiTests/
```

---

## 11. Phases

Each phase ends with something **usable** that is shipped to the owner as an internal build.

| Phase | Name | Outcome | Depends on |
|---|---|---|---|
| **0** | Foundation | Solution, CI, Avalonia shell with navigation, theme, SQLite, logging, settings | — |
| **1** | Scan & Analyze | Fast scan (fallback scanner, then MFT), tree, treemap, types, largest files, search, snapshots | 0 |
| **2** | Safety & Clean | Protected guard, built-in rules, verdict badges, cart, Review & Clean, Recycle Bin, Activity and undo, Quick Clean, duplicates | 1 |
| **2D** | Developer junk | Project detection, dev junk catalog, Dev Projects view, Docker integration, VHDX compaction, "Make this a rule" | 2 |
| **3** | Learning rules | Teach Miu, decisions, pattern miner, suggested rules, Rules page, import/export | 2 |
| **4** | Background & Automations | Agent and tray, startup toggle, IPC, USN watcher, watched folders, schedules, templates, toasts, forecast, digest | 2 (3 optional) |
| **5** | Telegram | Pairing wizard, notifications, inline actions, commands, remote-action gate | 4 |
| **6** | Network drives | Discover, map, scan, sync jobs | 1, 4 |
| **7** | Cloud backup | Providers, OAuth, plans, back-up-then-free, restore | 2, 4 |
| **8** | Polish & Release | Onboarding, Explorer context menu, Velopack direct download, auto-update, accessibility pass, performance pass, docs | all |
| **9** | macOS & Linux | Platform adapters, packaging (.dmg, AppImage/.deb), OS-specific junk rules | 8 |

**MVP = Phases 0–2 + 2D** (developer junk is core for the target audience). Phases 3–4 are what make Miu "smart"; 5–7 extend its reach; 8 is release.

---

## 12. Detailed feature list

IDs are used by the prompts (§13) and the checklist (§14).

### Phase 0 — Foundation
- **F0.1** Solution and projects layout, `Directory.Build.props`, nullable, analyzers, .NET 10
- **F0.2** GitHub Actions CI: build, test and package on windows-latest
- **F0.3** Avalonia shell: sidebar navigation, Fluent theme, light/dark/high-contrast, custom title bar
- **F0.4** DI and Generic Host shared by App and Agent; Serilog logging
- **F0.5** SQLite database, migration runner, settings repository
- **F0.6** `ISecretStore` + Windows implementation (Credential Manager plus DPAPI)

### Phase 1 — Scan & Analyze
- **F1.1** `ScanTree` in-memory model (struct arrays, string pool, aggregate sizes)
- **F1.2** Fallback scanner (parallel FindFirstFileEx, live batches, cancel, skips reparse points)
- **F1.3** Elevated MFT scanner (`Miu.Elevated` plus pipe streaming; fallback if elevation is denied)
- **F1.4** Categorizer (extension and path heuristics)
- **F1.5** Home: drive cards with usage bars and a "Scan" action
- **F1.6** Analyze: breadcrumb, folder tree with size bars, sorting
- **F1.7** Treemap control (Skia, squarified, click to zoom, hover tooltips)
- **F1.8** Types view, and Largest-files view (top 100)
- **F1.9** Search and filter query language (`size:`, `ext:`, `older:`, `type:`)
- **F1.10** Details pane: path, size, counts, dates, Open in Explorer, Copy path
- **F1.11** Snapshots and "What grew" (diff of folder aggregates)

### Phase 2 — Safety & Clean
- **F2.1** Protected-paths guard in the lowest delete layer, with exhaustive tests
- **F2.2** Rule model, glob trie and RuleEngine (tiers, specificity, explanations)
- **F2.3** Built-in rule pack (≈150 rules in JSON) plus loader
- **F2.4** Verdict badges and the "why" across tree, map and details
- **F2.5** Cleanup cart (add from anywhere, running total, persists across navigation)
- **F2.6** Review & Clean sheet (grouped, totals, final confirmation)
- **F2.7** Recycle Bin delete through IFileOperation, with progress, locked-file handling and summary
- **F2.8** Activity log and Undo (restore from the Recycle Bin)
- **F2.9** Quick Clean (only Safe junk categories, one tap)
- **F2.10** Duplicate finder (size, then partial hash, then full hash; keep-best suggestion)
- **F2.11** System big-file explainer (hiberfil, pagefile, WinSxS, Windows.old) with guided actions
- **F2.12** Optional permanent delete (setting plus double confirmation)

### Phase 2D — Developer junk
- **F2D.1** `ProjectDetector`: marker files, stack detection, last activity (git HEAD, source mtimes), idle buckets
- **F2D.2** Dev junk catalog (`rules/builtin/dev.json`) with project-aware conditions (`projectIdleDays`, `requiresMarker`)
- **F2D.3** Git status check (uncommitted changes guard) via `git status --porcelain` or LibGit2Sharp
- **F2D.4** Dev Projects view (table, rebuildable size, clean rebuildables, bulk idle clean)
- **F2D.5** Global dev caches (npm/pnpm/yarn/bun, pip/conda, cargo, go, gradle, NuGet, Playwright, HF/Ollama/LM Studio)
- **F2D.6** Docker integration (Engine API: images, containers, volumes, build cache; prune actions)
- **F2D.7** WSL2 / Docker VHDX compaction wizard (Windows, elevated)
- **F2D.8** "Make this a rule" from any recommendation (pre-filled rule or watch)
- **F2D.9** Archive project (zip without junk, optional cloud backup, then remove)
- **F2D.10** Dev digest line and Home "Dev projects" card

### Phase 3 — Learning rules
- **F3.1** Teach Miu buttons (Keep / Always OK / Never suggest) and reason chips
- **F3.2** Decision capture with feature extraction
- **F3.3** Implicit signals (cart removals, ignored suggestions, Recycle Bin restores)
- **F3.4** Pattern miner (generalization keys, thresholds, contradiction check)
- **F3.5** Suggested rules with evidence (Turn on / Not now)
- **F3.6** Rules page: stats, filterable table, toggle, edit, delete
- **F3.7** Rule confidence feedback loop and "needs review" flag
- **F3.8** Rules import/export (JSON)

### Phase 4 — Background & Automations
- **F4.1** `Miu.Agent` host plus tray icon and menu (Open, Quick Clean, Pause, Exit)
- **F4.2** Run-in-background toggle (create or remove the startup task)
- **F4.3** Named-pipe IPC (App ⇄ Agent), with in-process fallback when the Agent is off
- **F4.4** USN journal watcher for incremental size updates
- **F4.5** Watch model plus scheduler (daily, weekly, monthly, at sign-in, on low space)
- **F4.6** Watch runner (Suggest / AutoCleanSafe / NotifyOnly; filters; retention; actions)
- **F4.7** Automation templates gallery (Downloads tidy, Screenshots, Temp, Dev, Low-space guard)
- **F4.8** Sentence-style automation editor
- **F4.9** Resource guards (battery, fullscreen, quiet hours, idle-only, low priority)
- **F4.10** Toast notifications with action buttons
- **F4.11** Space forecast
- **F4.12** Weekly digest

### Phase 5 — Telegram
- **F5.1** Bot token setup and pairing wizard (6-digit code, chat-id lock)
- **F5.2** Long-polling bot service in the Agent
- **F5.3** Notifications with inline buttons (Clean now, Show list, Snooze, Never)
- **F5.4** Commands: `/status /scan /suggest /pause /resume /digest /help /unpair`
- **F5.5** Remote-action gate (Safe-only, confirm tap, 24 h expiry, logged, undoable)
- **F5.6** Telegram preferences (which events notify, digest day and time)

### Phase 6 — Network drives
- **F6.1** Share discovery (WS-Discovery, mDNS, NetBIOS) plus manual UNC entry
- **F6.2** Map and unmap drive (letter, credentials, reconnect at sign-in)
- **F6.3** Network scan (throttled fallback scanner)
- **F6.4** Sync job model and diff engine (mirror, copy-only, two-way with conflict copies)
- **F6.5** Sync runner (resume, dry-run preview, progress, schedule through F4.5)

### Phase 7 — Cloud backup
- **F7.1** `ICloudProvider` abstraction plus OAuth loopback with PKCE
- **F7.2** Google Drive provider
- **F7.3** OneDrive provider (Microsoft Graph)
- **F7.4** Dropbox provider
- **F7.5** S3-compatible provider (endpoint, keys, bucket)
- **F7.6** Backup plan editor (tri-state folder tree, destination, schedule, mode, versions)
- **F7.7** Backup runner (manifest, incremental, chunked/resumable, bandwidth limit, metered pause)
- **F7.8** Back-up-then-free (verify, then recycle) plus the "Back up first" action in Clean
- **F7.9** Restore browser

### Phase 8 — Polish & Release
- **F8.1** Onboarding (3 steps)
- **F8.2** Explorer context menu "Analyze with Miu"
- **F8.3** Settings page complete (all toggles, data export, about)
- **F8.4** Packaging: Velopack direct-download installer, signing, auto-update, download page
- **F8.5** Accessibility pass (Narrator, contrast, keyboard)
- **F8.6** Performance pass (1M-file benchmark, agent idle RAM, cold start)
- **F8.7** Crash reporting (local), DB backup and auto-restore
- **F8.8** User docs and README with screenshots

### Phase 9 — macOS & Linux
- **F9.1** `Miu.Platform.MacOS`: Trash, Keychain, LaunchAgent, notifications, APFS-aware scanning
- **F9.2** `Miu.Platform.Linux`: freedesktop Trash, libsecret, systemd user service / autostart, libnotify
- **F9.3** OS-specific junk rules (Xcode DerivedData, Homebrew cache, `~/.cache`, journald, snap/flatpak leftovers)
- **F9.4** Packaging: notarized `.dmg`, AppImage and `.deb`

---

## 13. Prompts per feature (copy into Claude Code, one at a time)

Each prompt assumes this spec is in the repo. Start every session with:
*"Read `docs/superpowers/specs/2026-11-05-miu-smart-storage-design.md`. Implement only the feature below with TDD, commit when tests pass, and tick it in §14."*

**Phase 0**
- **F0.1** "Create `Miu.sln` with the projects in §10 (empty), targeting .NET 10, with nullable enabled, warnings as errors, and a shared `Directory.Build.props`. Add the test projects with xUnit and FluentAssertions."
- **F0.2** "Add `.github/workflows/ci.yml` that restores, builds and tests the solution on windows-latest, and uploads test results."
- **F0.3** "Build the Avalonia shell in `Miu.App`: a sidebar with the pages from §7 as placeholders, the Fluent theme (Mica on Windows), a custom title bar, and a theme switch for light, dark and system."
- **F0.4** "Wire Microsoft.Extensions.Hosting and DI into both `Miu.App` and `Miu.Agent` with a shared `AddMiuCore()` extension. Add Serilog with rolling file logs in `%LOCALAPPDATA%\Miu\logs`."
- **F0.5** "Implement `Miu.Data`: a SQLite connection factory (WAL), a migration runner over embedded `Migrations/*.sql`, and the full schema from §6 as migration 001. Add `SettingsRepository`. Test migrations on an empty database."
- **F0.6** "Define `ISecretStore` and implement `WindowsSecretStore` in `Miu.Platform.Windows` using CredWrite/CredRead/CredDelete through P/Invoke, with DPAPI for blobs. Add integration tests that write, read and delete a test credential."

**Phase 1**
- **F1.1** "Implement `ScanTree` in `Miu.Core.Scanning` as struct arrays with a string pool. Support AddNode, aggregate sizes bottom-up, children enumeration sorted by size, and path lookup. Write tests first, including a 1M-node benchmark."
- **F1.2** "Implement `FallbackScanner : IScanner` using FindFirstFileExW (LARGE_FETCH, Basic info) with parallel directory workers. It emits batches every 100 ms through `IProgress`, supports cancellation, and skips reparse points. Test it against a generated temp tree."
- **F1.3** "Create `Miu.Elevated`: an admin exe that opens `\\.\C:`, enumerates the MFT with FSCTL_ENUM_USN_DATA and file sizes, and streams compact records over a named pipe. Add `MftClient` in Platform to launch it with runas and build a `ScanTree`. Fall back to `FallbackScanner` if elevation is denied."
- **F1.4** "Implement `Categorizer` (extension map plus path heuristics for AppData/Cache, Dev, Games, System) with table-driven tests."
- **F1.5** "Build the Home page: location cards (letter, label, kind, used/free bar, status chip) bound to `VolumeService`, with a Scan all button and a per-card click that goes to Analyze."
- **F1.6** "Build the Analyze page layout: breadcrumb, a virtualized TreeView with name, size, size bar and % columns, sorted by size, with live updates during a scan."
- **F1.7** "Build a `Treemap` Avalonia control drawn with Skia, with the layout algorithm in `Miu.Core`: squarified layout, category colours from §7, labels when they fit, hover tooltip, click to select, double-click to zoom in, and back to zoom out. Unit-test the layout algorithm separately."
- **F1.8** "Add the Types view (size per category as a bar list) and the Largest view (top 100 files with path, size and modified date)."
- **F1.9** "Implement the search query parser (`size:>1GB ext:mp4,mkv older:1y type:video name:*backup*`) and filtering over ScanTree, with tests first. Wire it to the Analyze search box."
- **F1.10** "Build the details pane: name, path, size, size on disk, file count, dates, Open in Explorer and Copy path, bound to the current selection."
- **F1.11** "Save folder aggregates to `folder_snapshots` after each scan. Implement `SnapshotDiff` for 'What grew' and show the top 5 on Home."

**Phase 2**
- **F2.1** "Implement `ProtectedPaths` and the `SafeDelete` guard inside `Miu.Platform.FileOps`. It must reject Windows, Program Files, ProgramData\Microsoft, the user profile root, pagefile and hiberfil, and anything resolving into them through junctions, symlinks, `\\?\` or 8.3 names. Write exhaustive tests first."
- **F2.2** "Implement `Rule`, `GlobTrie` and `RuleEngine` in `Miu.Core.Rules` per §5.3: tiers, specificity and conditions, returning `Verdict{kind, ruleId, reason, confidence}`. Write tests first."
- **F2.3** "Write the built-in rule pack in `rules/builtin/*.json` (Windows temp, update cache, Delivery Optimization, crash dumps, browser caches, thumbnail cache, npm/pip/NuGet/Gradle caches, node_modules idle, bin/obj idle, Docker build cache, old installers) plus `BuiltinRuleLoader`, with a test that every rule parses."
- **F2.4** "Add a `VerdictBadge` control (icon, label and colour) and show verdicts in the tree, the treemap outline and the details pane, with the 'why' and rule id."
- **F2.5** "Implement `CleanupCart` (singleton service: add, remove, total, persists in memory per session) and the `CartBar` control docked at the bottom of the shell."
- **F2.6** "Build the Review & Clean sheet: items grouped by category, verdict chips, totals, a 'Back up first' toggle (disabled until Phase 7), and a final confirmation."
- **F2.7** "Implement `RecycleBin.SendAsync(paths)` with IFileOperation and FOFX_RECYCLEONDELETE, calling the guard first. Report progress, collect locked files, and return a summary. Write integration tests on a temp folder."
- **F2.8** "Write every action to `actions_log`. Build the Activity page timeline with Undo (restore the items from the Recycle Bin through the shell API) and an undo toast after each cleanup."
- **F2.9** "Implement Quick Clean: gather Safe items from the built-in junk categories, show the total on Home, and clean them with one tap and a confirmation."
- **F2.10** "Implement `DuplicateFinder` (group by size, then a 64 KB XxHash3 of the head and tail, then a full XxHash3) with a keep-best suggestion. Add it as the Duplicates view in Analyze."
- **F2.11** "Add the system big-file explainer cards for hiberfil, pagefile, WinSxS and Windows.old, each with plain text and a guided action button (powercfg, DISM, Settings links)."
- **F2.12** "Add the permanent-delete setting (off by default). When it is on, show a separate red action with a typed confirmation. It still goes through the guard."

**Phase 2D**
- **F2D.1** "Implement `ProjectDetector` in `Miu.Dev`: detect projects by the marker files in §5.11, identify the stack(s), compute last activity from git HEAD time and the newest source mtime (excluding junk folders), and assign an idle bucket. Write tests first using a temp folder fixture."
- **F2D.2** "Write `rules/builtin/dev.json` covering the §5.11 catalog. Extend rule conditions with `projectIdleDays` and `requiresMarker`, and test that each rule matches its fixture and does not match source folders."
- **F2D.3** "Add `GitStatus` (porcelain parse) and block cleaning a project with uncommitted changes unless confirmed. Add tests."
- **F2D.4** "Build the Dev Projects tab: project table with stack, last activity, total and rebuildable size, and git warning; row action Clean rebuildables and the bulk 'Clean all idle > N days'."
- **F2D.5** "Add detectors for global dev caches (npm, pnpm, yarn, bun, pip, conda, cargo, go, gradle, NuGet, Playwright, Hugging Face, Ollama, LM Studio) with size and last-used, listed as recommendations."
- **F2D.6** "Implement `DockerClient` over the Engine API (named pipe or unix socket) with a `docker system df` fallback. Show images, containers, volumes and build cache, with prune actions; volumes always need an explicit choice."
- **F2D.7** "Implement the VHDX compaction wizard for Windows: find Docker/WSL VHDX files, prune, `wsl --shutdown`, compact via Optimize-VHD or diskpart (elevated), and show the before and after sizes."
- **F2D.8** "Add 'Make this a rule' to every recommendation, opening the rule/watch editor pre-filled from the item (path scope, folder names, idle days)."
- **F2D.9** "Implement Archive project: zip the source excluding the junk catalog, optionally upload through a backup plan, verify, then recycle the folder."
- **F2D.10** "Add the dev digest line and a Home 'Dev projects' card (project count, rebuildable total, idle count)."

**Phase 3**
- **F3.1** "Add Teach Miu buttons and reason chips to the details pane and the cart items."
- **F3.2** "Implement the `Decision` repository and `FeatureExtractor` (parent, ext, name pattern, size and age buckets, category, inGitRepo). Write tests first."
- **F3.3** "Capture implicit signals: cart removals, suggestions ignored 3 times, and restores from the Recycle Bin detected through the actions_log undo tokens."
- **F3.4** "Implement `PatternMiner` per §5.4 (generalization keys, ≥3 consistent, no contradictions, ≥90% confidence) producing `rule_suggestions`. Write tests first with decision fixtures."
- **F3.5** "Show suggested rules with their evidence on the Rules page and Home, with Turn on / Not now actions."
- **F3.6** "Build the Rules page: stats cards, filter (All, Learned, Built-in, Protected), a table with toggle, edit dialog and delete. Protected rules are read-only."
- **F3.7** "Track accepted and rejected counts per rule. Recompute confidence and flag rules below 70% acceptance as 'needs review'."
- **F3.8** "Add rules export and import (JSON file, schema-validated, preview the changes before importing)."

**Phase 4**
- **F4.1** "Create the `Miu.Agent` Generic Host exe with an Avalonia `TrayIcon` and menu (Open Miu, Quick Clean, Pause 1h / until tomorrow, Exit), single instance."
- **F4.2** "Implement `StartupTask` (a per-user Task Scheduler task at sign-in) and the Settings toggle 'Run Miu in the background'. Turning it off removes the task and stops the Agent."
- **F4.3** "Implement named-pipe JSON IPC between App and Agent (`Miu.Contracts` messages, request/response plus events). `AgentClient` falls back to in-process services when no agent is running."
- **F4.4** "Implement `UsnWatcher` (through Miu.Elevated or a scheduled-task elevation) that reads USN journal changes and updates `ScanTree` aggregates incrementally."
- **F4.5** "Implement the `Watch` model, repository and `Scheduler` (daily, weekly, monthly, at sign-in, low-space trigger) computing `next_run`. Write tests first for the schedule maths."
- **F4.6** "Implement `WatchRunner`: apply filters and the rules engine; for each mode Suggest, AutoCleanSafe (Safe only!) or NotifyOnly, perform the action and retention, and write `job_runs` and `actions_log`."
- **F4.7** "Add the automation templates gallery (5 templates from §5.5), each creating a pre-filled Watch."
- **F4.8** "Build the sentence-style automation editor (inline dropdowns inside a sentence) plus the Automations list page with next run, last result and enable toggle."
- **F4.9** "Implement resource guards: battery saver, fullscreen or presentation detection, quiet hours, idle-only, and below-normal CPU and I/O priority."
- **F4.10** "Add `INotifier` with a Windows toast implementation with action buttons (Clean now, Review, Snooze) routed to the Agent."
- **F4.11** "Implement the `Forecast` (linear regression over snapshots per volume) and show 'Full in ~N weeks' on the Home cards."
- **F4.12** "Implement the weekly digest (freed, grew, suggestions, backup health) as a toast and an in-app panel, sent to Telegram later."

**Phase 5**
- **F5.1** "Build the Telegram setup wizard: paste the bot token (stored in CredentialStore), show a 6-digit code, wait for `/pair code`, and save the chat_id."
- **F5.2** "Implement `TelegramBotService` in the Agent with Telegram.Bot long polling, ignoring any chat id except the paired one."
- **F5.3** "Send notifications with inline keyboards for watch results and low space. Handle the callback buttons."
- **F5.4** "Implement the commands `/status /scan /suggest /pause /resume /digest /help /unpair`."
- **F5.5** "Implement `RemoteActionGate`: only Safe items, a confirm tap, 24 h expiry stored in `pending_remote_actions`, logged with origin=telegram, and undoable. Write tests first, including the attack cases."
- **F5.6** "Add the Telegram preferences UI (which events notify, digest schedule, test message, unpair)."

**Phase 6**
- **F6.1** "Implement `ShareDiscovery` (WS-Discovery, mDNS, NetBIOS) and the 'Find devices on my network' list."
- **F6.2** "Implement `WNetMapper` (map with letter, credentials and reconnect; unmap) and the map form on Cloud & Network › Network drives."
- **F6.3** "Allow scanning network locations with the throttled FallbackScanner and show them on Home."
- **F6.4** "Implement `SyncDiff` (mirror, copy-only, two-way with conflict copies) using size+mtime, with an optional hash. Write tests first."
- **F6.5** "Implement `SyncRunner` (resumable copies, dry-run preview, progress, scheduled through the Scheduler) and the sync jobs UI."

**Phase 7**
- **F7.1** "Define `ICloudProvider` (list, upload chunked, download, delete, quota, checksum). Implement OAuth loopback with PKCE in `Miu.Cloud`, with a fake provider for tests."
- **F7.2** "Implement `GoogleDriveProvider` with resumable uploads and md5 checksum verification."
- **F7.3** "Implement `OneDriveProvider` through Microsoft Graph upload sessions and quickXorHash verification."
- **F7.4** "Implement `DropboxProvider` with upload sessions and content_hash verification."
- **F7.5** "Implement `S3Provider` (custom endpoint, keys and bucket; multipart upload; ETag/MD5 checks) for AWS, B2, Wasabi and R2."
- **F7.6** "Build the backup plan editor: tri-state folder tree with sizes, destination picker, schedule, mode and versions."
- **F7.7** "Implement `BackupRunner` with `backup_manifest` incremental logic, bandwidth limit, pausing on metered networks, and retries with backoff."
- **F7.8** "Implement back-up-then-free (upload, verify, then Recycle Bin) and enable 'Back up first' in Review & Clean and the watch actions."
- **F7.9** "Build the restore browser: list the backed-up tree and restore to the original or a chosen location."

**Phase 8**
- **F8.1** "Build the 3-step onboarding (drives, background mode, optional Telegram/cloud) that ends by starting the first scan."
- **F8.2** "Add the Explorer context menu 'Analyze with Miu' (IExplorerCommand for Windows 11, a classic verb for Windows 10)."
- **F8.3** "Complete the Settings page per §7, including data export and reset."
- **F8.4** "Package with Velopack as a direct-download installer, set up code signing in CI, publish releases on GitHub, and add auto-update checks."
- **F8.5** "Do an accessibility pass: Narrator names, keyboard paths, high-contrast theme, contrast audit. Fix the findings."
- **F8.6** "Do a performance pass: 1M-file benchmark, agent idle RAM < 30 MB, cold start < 1.5 s. Add CI budget tests."
- **F8.7** "Add local crash reports, a daily DB backup (keep 3), and an integrity check with auto-restore."
- **F8.8** "Write the README and user guide with screenshots and an FAQ."
**Phase 9**
- **F9.1** "Implement `Miu.Platform.MacOS` adapters (Trash via NSFileManager, Keychain, LaunchAgent, UNUserNotificationCenter) and run the platform contract tests on macOS CI."
- **F9.2** "Implement `Miu.Platform.Linux` adapters (freedesktop Trash spec, libsecret, systemd user unit / XDG autostart, libnotify) and run the platform contract tests on Linux CI."
- **F9.3** "Add macOS and Linux junk rule packs and protected paths (/System, /usr, /etc, ~/Library essentials)."
- **F9.4** "Package a notarized .dmg, an AppImage and a .deb via Velopack and CI."

---

## 14. Progress checklist

### Phase 0 — Foundation
- [x] F0.1 Solution & projects
- [x] F0.2 CI workflow
- [ ] F0.3 Avalonia shell — partial in M1 (shell; Rules/Settings are placeholders)
- [ ] F0.4 DI, hosting, logging — partial in M1 (manual composition root, no DI container yet)
- [x] F0.5 SQLite + migrations
- [x] F0.6 Credential store

### Phase 1 — Scan & Analyze
- [x] F1.1 ScanTree model
- [ ] F1.2 Fallback scanner — partial in M1 (portable scanner; Windows FindFirstFileEx version pending)
- [ ] F1.3 Elevated MFT scanner
- [x] F1.4 Categorizer
- [ ] F1.5 Home drive cards — partial in M1 (basic)
- [ ] F1.6 Analyze tree — partial in M1 (drill-down list)
- [x] F1.7 Treemap control
- [x] F1.8 Types & Largest views
- [ ] F1.9 Search query language
- [x] F1.10 Details pane
- [x] F1.11 Snapshots & What grew

### Phase 2 — Safety & Clean
- [x] F2.1 Protected-paths guard
- [x] F2.2 Rule engine
- [ ] F2.3 Built-in rule pack — partial in M1 (dev + system packs)
- [ ] F2.4 Verdict badges — partial in M1 (list, treemap, details)
- [x] F2.5 Cleanup cart
- [x] F2.6 Review & Clean sheet
- [x] F2.7 Recycle Bin delete
- [x] F2.8 Activity & Undo
- [x] F2.9 Quick Clean
- [x] F2.10 Duplicate finder
- [ ] F2.11 System file explainer
- [ ] F2.12 Permanent delete option

### Phase 2D — Developer junk
- [x] F2D.1 Project detector
- [ ] F2D.2 Dev junk catalog — partial in M1 (rules; project-aware conditions)
- [ ] F2D.3 Git status guard
- [x] F2D.4 Dev Projects view
- [ ] F2D.5 Global dev caches
- [x] F2D.6 Docker integration
- [ ] F2D.7 VHDX compaction
- [ ] F2D.8 Make this a rule
- [ ] F2D.9 Archive project
- [ ] F2D.10 Dev digest & card

### Phase 3 — Learning rules
- [x] F3.1 Teach Miu UI
- [x] F3.2 Decisions + features
- [ ] F3.3 Implicit signals — partial in M3 (clean + undo; cart removals / ignored suggestions pending)
- [x] F3.4 Pattern miner
- [x] F3.5 Suggested rules
- [x] F3.6 Rules page
- [ ] F3.7 Confidence feedback
- [x] F3.8 Import/export

### Phase 4 — Background & Automations
- [ ] F4.1 Agent + tray — partial in M4 (tray + background inside the app process; no separate agent yet)
- [x] F4.2 Background toggle
- [ ] F4.3 IPC — partial in M4 (not needed while background runs in-process)
- [ ] F4.4 USN watcher — partial in M4 (watched folders are rescanned instead)
- [x] F4.5 Watches + scheduler
- [x] F4.6 Watch runner
- [x] F4.7 Templates
- [x] F4.8 Sentence editor
- [x] F4.9 Resource guards
- [x] F4.10 Toasts
- [x] F4.11 Forecast
- [x] F4.12 Weekly digest

### Phase 5 — Telegram
- [x] F5.1 Pairing wizard
- [x] F5.2 Bot service
- [x] F5.3 Notifications + buttons
- [x] F5.4 Commands
- [x] F5.5 Remote-action gate
- [x] F5.6 Preferences

### Phase 6 — Network drives
- [ ] F6.1 Discovery
- [ ] F6.2 Map/unmap
- [ ] F6.3 Network scan
- [ ] F6.4 Sync diff
- [ ] F6.5 Sync runner + UI

### Phase 7 — Cloud backup
- [ ] F7.1 Provider abstraction + OAuth
- [ ] F7.2 Google Drive
- [ ] F7.3 OneDrive
- [ ] F7.4 Dropbox
- [ ] F7.5 S3-compatible
- [ ] F7.6 Plan editor
- [ ] F7.7 Backup runner
- [ ] F7.8 Back-up-then-free
- [ ] F7.9 Restore

### Phase 8 — Polish & Release
- [ ] F8.1 Onboarding
- [ ] F8.2 Explorer context menu
- [ ] F8.3 Settings complete — partial in M4 (background, automatic cleaning, quiet hours, battery)
- [ ] F8.4 Packaging & updates
- [ ] F8.5 Accessibility pass
- [ ] F8.6 Performance pass
- [ ] F8.7 Crash & DB resilience
- [ ] F8.8 Docs

### Phase 9 — macOS & Linux
- [ ] F9.1 macOS adapters
- [ ] F9.2 Linux adapters
- [ ] F9.3 OS junk rules
- [ ] F9.4 Packaging

---

## 15. Out of scope (v1)
- A web dashboard; multi-user or admin consoles. (macOS/Linux is planned as Phase 9.)
- AI/LLM features (possible later: an optional "explain this folder" button using a user-supplied API key).
- Mobile companion app (Telegram covers remote control).
- Non-NTFS fast scanning (FAT/exFAT and ReFS use the fallback scanner).

## 16. Resolved questions
1. **Pricing:** free.
2. **Distribution:** direct download (Velopack + GitHub Releases). No Store.
3. **Automatic cleaning:** off until turned on once in Settings.
4. **UI framework:** Avalonia (cross-platform), replacing WinUI 3.
