<p align="center"><img src="docs/brand/miu-logo-256.png" width="128" alt="Miu logo"></p>

# Miu Smart Storage

**See where your disk space went, find out what's safe to delete (and why), and keep your drives clean on their own.**
Free and for everyone, with extra help for developers and vibe coders, whose disks fill up with `node_modules`, build folders, Python venvs, Docker images and AI models.

Made with ♥ by Khang · [vietnamcos.com](https://vietnamcos.com) · [duykhang.com](https://duykhang.com) · [Threads @dkhang](https://www.threads.com/@dkhang)

> Status: **Milestone 2 (Clean)**. Miu scans, explains every item, and cleans what you choose: everything goes to the **Recycle Bin (Windows) / Trash (Linux)** and can be undone from the Activity page. macOS cleaning comes later.

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
3. **Learning rules:** Miu learns from your Keep and Delete choices.
4. **Background and schedules:** watched folders, scheduled cleanup, notifications.
5. **Telegram:** alerts with action buttons.
6. **Network drives and cloud backup.**
7. **macOS and Linux releases.**

## License
Free. A license file will be added before the first public release.
