# Milestone 8 — Polish & Release (Windows, macOS, Linux builds)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Miu can be downloaded and installed on **Windows, macOS and Linux** from a GitHub Release. It tells you when there's a new version. It survives crashes and a damaged database. It greets new users with a 3-step setup and has a complete Settings page.

**Spec:** F8.1–F8.8 (and F2.12 stays off: permanent delete is not offered in v1).

**Decisions:**
- **Packaging with each OS's own tools, no third-party runtime library:**
  - **Windows:** a per-user Inno Setup installer (no admin rights) plus a portable `.zip`. Both use a self-contained `win-x64` publish.
  - **macOS:** a `Miu Smart Storage.app` bundle (arm64 and x64) in a `.dmg` and a `.zip`. It is ad-hoc signed. Notarization comes once an Apple Developer ID is available; until then, right-click › Open the first time.
  - **Linux:** a `.tar.gz`, a `.deb`, and an AppImage (`linux-x64`).
- **Updates:** Miu checks GitHub Releases once a day (you can turn this off) and offers a Download button for your OS. Updates are not installed silently. Velopack-style delta updates may come later; the spec listed Velopack, and this keeps the app free of a dependency until signing is in place.
- **Release workflow:**
  - A tag `v*` builds all three systems and publishes a GitHub Release.
  - CI also builds the packages on every push, so the PR proves all three build.

## Global Constraints
- An installer never needs admin rights: per-user locations only.
- Crash reports stay on your computer and are never uploaded; "Copy details" is up to you.
- The database is backed up daily (3 copies kept). A failed integrity check restores the newest good copy, and the damaged file is kept aside, not deleted.
- The data export never contains secrets (tokens, keys, passwords).

### Tasks
1. Packaging scripts (`build/`) and workflows: Linux packages are tested locally; macOS and Windows run in CI.
2. `UpdateService`: GitHub latest release, version comparison, the asset for this OS, a daily check, a Home banner and a Settings button. Tested with fake HTTP.
3. Crash and database resilience: unhandled-exception handlers write a local report, and the next start offers "Copy details". The daily `VACUUM INTO` backup keeps 3 copies, and an integrity check restores from them. Tests.
4. Onboarding, 3 steps: what to scan, background mode, optional Telegram or cloud. The first scan starts when you finish. Test and screenshot.
5. The rest of Settings: theme (System/Light/Dark), updates, data export, and on Windows the Explorer menu.
6. "Analyze with Miu": the Windows Explorer context menu (HKCU), the `--analyze <path>` argument, a Linux `.desktop` action, and handing the folder to an already-running Miu.
7. Keyboard and accessibility: Ctrl+1…9 for pages, names on icon-only buttons, and a contrast test for the palette.
8. Performance: a synthetic large-tree benchmark test (opt-in) with time limits.
9. Docs (user guide and README download section), independent review, fix pass, push.
