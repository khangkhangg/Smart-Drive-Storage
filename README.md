<p align="center"><img src="docs/brand/miu-logo-256.png" width="128" alt="Miu logo"></p>

# Miu Smart Storage

**See where your disk space went, find out what's safe to delete (and why), and keep your drives clean on their own.**
Free and for everyone, with extra help for developers and vibe coders, whose disks fill up with `node_modules`, build folders, Python venvs, Docker images and AI models.

Made with ♥ by Khang · [vietnamcos.com](https://vietnamcos.com) · [duykhang.com](https://duykhang.com) · [Threads @dkhang](https://www.threads.com/@dkhang)

> **[⬇ Download for Windows, macOS or Linux](#download)** · Status: **Milestone 9 (macOS & Linux)**. Builds and runs on **Windows, macOS and Linux**. Miu scans, explains every item, and cleans what you choose. Everything goes to the **Recycle Bin (Windows) or Trash (macOS, Linux)** and can be undone from the Activity page.

| Home | Analyze |
|---|---|
| ![Home](docs/screenshots/home.png) | ![Analyze](docs/screenshots/analyze.png) |

| Inside a project | Dev Projects |
|---|---|
| ![Project](docs/screenshots/analyze-project.png) | ![Dev projects](docs/screenshots/dev-projects.png) |

| Review & Clean | After cleaning (with Undo) |
|---|---|
| ![Review](docs/screenshots/review.png) | ![After clean](docs/screenshots/after-clean.png) |

| Cleanup cart | Activity |
|---|---|
| ![Cart](docs/screenshots/cart.png) | ![Activity](docs/screenshots/activity.png) |

| Duplicates | Rules (learns from you) |
|---|---|
| ![Duplicates](docs/screenshots/duplicates.png) | ![Rules](docs/screenshots/rules.png) |

| Types | Teach Miu |
|---|---|
| ![Types](docs/screenshots/types.png) | ![Teach](docs/screenshots/teach.png) |

| Automations | Settings |
|---|---|
| ![Automations](docs/screenshots/automations.png) | ![Settings](docs/screenshots/settings.png) |

| Network drives | Sync jobs |
|---|---|
| ![Network drives](docs/screenshots/network.png) | ![Sync jobs](docs/screenshots/sync.png) |

| Welcome | Cloud backup |
|---|---|
| ![Welcome](docs/screenshots/onboarding.png) | ![Cloud backup](docs/screenshots/backup.png) |

| Notification | |
|---|---|
| ![Notification](docs/screenshots/notification.png) | |

| Splash | About |
|---|---|
| ![Splash](docs/screenshots/splash.png) | ![About](docs/screenshots/about.png) |

## What works today
- **Home:** a storage overview with the "safe to remove" total grouped into categories, location cards, a rules summary, and a **For developers** section.
- **Fast parallel scan** of any drive or folder. Symlinks and junctions are never followed, and folders you can't read are skipped.
- **Analyze:** a drill-down list with size bars, a **treemap**, breadcrumbs and a details pane.
- **A safety verdict on every item:** 🟢 Safe to delete · 🟡 Review first · 🔴 Keep / Do not delete, always with a plain-language *why* and the rule that decided it.
- **Protected-path guard:** Windows, Program Files, system folders, your profile root and drive roots can never be marked deletable. No rule can override it.
- **Developer junk detection:**
  - Projects are recognized by their marker files (Node, Python, Rust, .NET, JVM, Go, PHP, Flutter, Docker, Git).
  - Miu finds each project's last activity and puts it in a bucket: active, cooling, idle or abandoned.
  - It measures how much of each project is *rebuildable* and tells you the rebuild command (`npm install`, `cargo build`, `dotnet build`…).
- **Built-in rules** for `node_modules`, `.next`, `dist`, `target`, `bin/obj`, venvs, `__pycache__`, the npm/pnpm/yarn/pip/NuGet caches, Playwright browsers, Hugging Face and Ollama models, temp files, crash dumps, browser caches and more. See `rules/builtin/`.
- **Clean safely:**
  - Add anything to the **cleanup cart**, or press **Quick Clean** to gather every 🟢 Safe item.
  - **Review & Clean** shows exactly what will move. Items that aren't Safe start unchecked.
  - Everything goes to the Recycle Bin / Trash, and each clean can be undone from **Activity**.
  - Protected locations can't be added, and they are checked again at the moment of deleting.
- **Types, Largest and Duplicates:** see which kinds of files take the space and the 100 biggest files. Find exact copies: Miu keeps the oldest and offers the rest (as Review).
- **Miu learns from you:**
  - **Keep — never suggest** and **Always OK to delete** take effect right away, with an optional reason.
  - When you repeatedly clean or restore similar items, Miu suggests a rule and shows its evidence ("You deleted 3 of 3 such items"). It is only used after you tap **Turn on**.
  - The Rules page lets you see, switch off, delete, import and export rules.
- **Automations:**
  - Watch folders on a schedule: every day, week or month, when you sign in, or when space is low.
  - For each one, Miu can **suggest** what to clean, **clean Safe items automatically** (only after you allow it in Settings), or **just tell you**.
  - Start from templates (Downloads tidy, Temp & caches, Dev projects, Low-space guard) or write your own in a sentence-style editor.
- **Background mode:** a tray icon, start at sign-in, and closing keeps Miu in the tray. You can switch it off.
  - Automations pause on battery, in quiet hours, or when you pause them. A paused run is postponed, not lost.
- **Notifications** with actions (Review, Undo), a weekly digest, **What grew this week**, and **Full in ~N weeks** on drive cards.
- **Telegram:**
  - Connect your own bot (from @BotFather) and pair it with a 6-digit code.
  - Alerts arrive with **Clean now**, **Show list**, **Snooze 7 days** and **Never for this folder**.
  - Commands: `/status /scan /suggest /pause 2h /resume /digest /help /unpair`.
  - From Telegram, Miu only moves items that are *still* Safe to the Recycle Bin. You confirm first, buttons expire after 24 h, and every clean can be undone.
- **Network drives:**
  - Find computers and NAS boxes on your network (Bonjour and Windows network discovery), or type `\\server\share`.
  - Map a share to a drive letter on Windows, optionally reconnecting at sign-in. The password is kept in Windows Credential Manager.
  - Scan network folders like any other. Miu scans them gently (4 threads), so the NAS and the Wi-Fi stay usable.
- **Sync jobs** between any two folders, local or network: **Mirror**, **Copy new and changed**, or **Two-way**.
  - Preview every step before running. Run on a schedule, or only when you press Run.
  - **Sync never deletes.** Files it replaces or removes are set aside in `.miu-sync-trash`, and Undo in Activity puts them back.
  - If a run would empty a folder (an unplugged drive?) or set aside most of it, Miu refuses and explains why.
  - A file changed on both sides keeps both versions: `name (conflict 2026-11-05).ext`.
  - Interrupted copies resume, and you never get a half-written file.
- **Cloud backup** to **Google Drive, OneDrive, Dropbox** or **S3-compatible storage** (AWS, Backblaze B2, Wasabi, Cloudflare R2, MinIO):
  - Modes: **Copy new and changed**, **Mirror**, or **Back up, then free space**. The last one moves files you haven't touched for N days to the Recycle Bin, but only after their cloud copy is checked.
  - Every upload is verified with the provider's checksum, compared with one Miu computes while reading the file. Replaced or removed cloud files are kept as older versions (you choose how many). Interrupted backups continue where they stopped. You can set a bandwidth limit.
  - **Restore** any file to where it was or to another folder. A different file in the way is never replaced; the restored copy is saved as "name (restored date)".
  - **Back up first** in Review & Clean uploads and checks everything before cleaning.
  - Miu only gets access to its own folder: Google `drive.file`, and the OneDrive and Dropbox app folders. Tokens and keys are kept in the system's secure store.
- **For developers:** clean one project's rebuildable folders, or every idle project at once. **Docker** shows its disk usage and offers safe prunes: build cache, unused image layers and stopped containers. Volumes are never touched.
- **Folders that hold cleanable files** are labelled with how much inside is safe, e.g. "4.8 GB to clean".

## Build and run
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Miu.App
```

```bash
dotnet test Miu.sln
```

The app runs on Windows, macOS and Linux (Avalonia). Windows is the first release target. Windows-only speed-ups (MFT scanning, Recycle Bin, Credential Manager) come in later milestones.

Regenerate the screenshots:

```bash
MIU_SCREENSHOT_DIR=$PWD/docs/screenshots dotnet test tests/Miu.App.Tests --filter Screenshots
```

## Project layout
```
src/Miu.Core                   scan tree, categorizer, rules engine, protected-path guard, treemap layout, formatting
src/Miu.Dev                    project detector and dev junk catalog
src/Miu.Data                   SQLite database, migrations, settings
src/Miu.Platform.Abstractions  IScanner and other OS interfaces
src/Miu.Platform.Portable      cross-platform scanner
src/Miu.App                    Avalonia UI (Home, Analyze, Dev Projects)
rules/builtin                  built-in rule packs (JSON)
tests/                         xUnit tests for every project, plus headless UI tests
docs/superpowers/specs         full product spec (features, phases, prompts, checklist)
docs/superpowers/plans         implementation plans
```

## Roadmap
Full detail is in the [spec](docs/superpowers/specs/2026-11-05-miu-smart-storage-design.md).
1. ~~**Clean:** cleanup cart, Recycle Bin with undo, Quick Clean~~ ✅ · next: duplicates, Types view.
2. **Developer tools:** ~~Docker cleanup~~ ✅ · WSL/Docker disk compaction, "Make this a rule".
3. ~~**Learning rules**~~ ✅ · ~~duplicates, Types view~~ ✅
4. ~~**Background and schedules**~~ ✅
5. ~~**Telegram**~~ ✅
6. ~~**Network drives and sync**~~ ✅
7. ~~**Cloud backup**~~ ✅
8. ~~**Polish and release**~~ ✅ · onboarding, installers for all three systems, update check, crash and database safety.
9. ~~**macOS and Linux**~~ ✅ · Trash, Keychain / Secret Service, start at login, OS-specific junk rules.

## Download

**Latest test build: [v0.9.0-beta.1](https://github.com/khangkhangg/Smart-Drive-Storage/releases/tag/v0.9.0-beta.1)** (pre-release). The newest version is always on the [Releases page](https://github.com/khangkhangg/Smart-Drive-Storage/releases).

**What's in 0.9.0-beta.1:**
- Scanning, Safe/Review verdicts, Review & Clean with progress, and Undo from Activity.
- "What is this?" cards for dev folders, databases and system files, plus multi-select, right-click and Quick preview.
- Dev Projects, and a **Docker** page to start, stop, pause, read logs and delete containers, images and volumes.
- Network drives (automatic discovery), sync and live mirror, drive backup, disk images, and browse & restore.
- Cloud backup to Google Drive, OneDrive, Dropbox (with **your own app keys**, see below) and S3-compatible storage.
- Telegram alerts and actions, automations, and in-app updates that download for you.

Already have Miu? To get test builds like this one, turn on **Settings › Updates › Include test builds**. Miu then offers it, downloads it (checked against `SHA256SUMS.txt`) and installs when you press **Install now**.

| System | Download | Notes |
|---|---|---|
| **Windows 10/11** (64-bit) | [⬇ Installer (.exe)](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-windows-x64-setup.exe) · [⬇ Portable (.zip)](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-windows-x64-portable.zip) | Installs for your user only, without admin rights. If SmartScreen appears, click **More info › Run anyway**. |
| **macOS 12+** (Apple silicon) | [⬇ .dmg](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-macos-arm64.dmg) · [⬇ .zip](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-macos-arm64.zip) | Drag Miu to Applications. The first time, use **System Settings › Privacy & Security › Open Anyway**. |
| **macOS 12+** (Intel) | [⬇ .dmg](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-macos-x64.dmg) · [⬇ .zip](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-macos-x64.zip) | Same as above. |
| **Linux** (x64) | [⬇ AppImage](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/Miu-Smart-Storage-0.9.0-beta.1-x86_64.AppImage) · [⬇ .deb](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/miu-smart-storage_0.9.0-beta.1_amd64.deb) · [⬇ .tar.gz](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/miu-smart-storage-0.9.0-beta.1-linux-x64.tar.gz) | **AppImage:** `chmod +x` it, then run it. **Debian/Ubuntu:** `sudo apt install ./miu-smart-storage_*.deb`. |

Checksums: [SHA256SUMS.txt](https://github.com/khangkhangg/Smart-Drive-Storage/releases/download/v0.9.0-beta.1/SHA256SUMS.txt). See the [user guide](docs/USER-GUIDE.md) for details.

**Making a release:**
```
git tag v1.0.0 && git push origin v1.0.0
```
The **Release** workflow tests and packages on all three systems and publishes the files with `SHA256SUMS.txt`. To build locally, run one of these:
- `build/package-linux.sh <version>`
- `build/package-macos.sh <version>` (on a Mac)
- `pwsh build/package-windows.ps1 -Version <version>` (on Windows, with Inno Setup 6)

## Cloud sign-in: use your own app keys
S3-compatible storage (AWS, Backblaze B2, Wasabi, Cloudflare R2, MinIO) works out of the box with its access keys.

Google Drive, OneDrive and Dropbox need an "app" registered with that company. **Each user registers their own, for free, in about 5 minutes.** Nobody depends on someone else's app or its limits, so Miu keeps working for as long as you like.

- In Miu, open **Settings › Cloud apps**. Each provider has step-by-step instructions, a button that opens the right console, and the exact redirect address to enter.
- Paste your Client ID (Google also needs its client secret, which is kept in your system's secure store), then connect in **Cloud & Network › Cloud backup**.
- Your sign-in happens in your browser with PKCE. Miu only ever sees its own folder (Google `drive.file`, the OneDrive app folder, a Dropbox app folder).

**Optional, for people who make builds:** a build can include default keys in `cloud-clients.json` next to the app (see `docs/cloud-clients.example.json`), or through the environment variables `MIU_GOOGLE_CLIENT_ID`, `MIU_GOOGLE_CLIENT_SECRET`, `MIU_MICROSOFT_CLIENT_ID` and `MIU_DROPBOX_CLIENT_ID`. Keys a user enters in Settings always take priority.

## Support Miu

Miu is free. If it saved you some space, a small donation says thanks and helps keep it updated with new rules, fixes and features:

- ☕ [Buy me a coffee](https://buymeacoffee.com/dkhang)
- 💙 [Donate with PayPal](https://www.paypal.com/donate/?hosted_button_id=FWYNCN2KN3C3L)

## License
Free. A license file will be added before the first public release.
