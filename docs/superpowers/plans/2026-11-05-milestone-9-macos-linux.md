# Milestone 9 — macOS & Linux

**Goal:** Miu is as safe and complete on macOS and Linux as on Windows.

**Spec:** F9.1–F9.4.

## What was built
- **`Miu.Platform.MacOS`:**
  - `MacTrash` uses NSFileManager `trashItemAtURL`, so Finder's Put Back works.
  - `MacKeychain` uses the Security framework; secrets never appear on a command line.
  - `MacLaunchAgent` adds start at login via `~/Library/LaunchAgents`.
  - `MacPowerState` reads `pmset`.
  - macOS cleaning, which used to show "coming soon", now works.
- **Linux:**
  - `LinuxKeyring` uses the Secret Service through `secret-tool`, passing secrets on stdin. If there's no keyring it falls back to the owner-only file, and it moves old file secrets into the keyring.
  - Trash (freedesktop), autostart (XDG) and power state (`/sys`) existed already.
- **OS junk rules** (`rules/builtin/macos.json`, `linux.json`):
  - **macOS:** Xcode DerivedData, DeviceSupport and Archives; simulator caches; Homebrew and CocoaPods caches; app caches (Review); `.DS_Store`.
  - **Linux:** thumbnails, browser caches, Flatpak and Snap caches, other `~/.cache` folders (Review), core dumps.
- **Packaging:** `.dmg`/`.zip` (arm64 and x64), `.deb`, `.tar.gz` and AppImage, from Milestone 8.
- **CI:** the full test suite now runs on macOS as well as Windows and Linux.

## Constraints kept
- The protected-path guard covers `/System`, `/Library`, `/Applications`, `/private` (which includes macOS's temp and `/var`) and the Linux system trees.
- Notifications use Miu's own toast window on every system, so no native notification code is needed.

## Not done (needs accounts or hardware)
- **macOS notarization:** needs an Apple Developer ID. Until then, Gatekeeper's "Open Anyway" step is documented.
- **Native notifications** (UNUserNotificationCenter and libnotify) aren't used; the toast window covers this.
- **APFS clones and firmlinks:** a clone's size is counted separately; scanning `/` may also count `/System/Volumes/Data`. Scan your user folder or a volume instead.
