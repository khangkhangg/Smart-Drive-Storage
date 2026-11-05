# Miu Smart Storage — User guide

Miu shows what fills your drives and cleans only what you choose. Everything it removes goes to the **Recycle Bin** (Windows) or **Trash** (macOS, Linux) first, and every action can be undone from **Activity**.

## Install
Download the file for your system from the [latest release](https://github.com/khangkhangg/Smart-Drive-Storage/releases/latest).

| System | File | Notes |
|---|---|---|
| **Windows 10/11** | `…-windows-x64-setup.exe` | Installs for your user only, without admin rights. You can also use `…-portable.zip`. |
| **macOS 12+ (Apple silicon)** | `…-macos-arm64.dmg` | Drag Miu to Applications. The app isn't notarized yet, so the first time macOS blocks it. Open **System Settings › Privacy & Security** and click **Open Anyway**. Or run `xattr -dr com.apple.quarantine "/Applications/Miu Smart Storage.app"`. |
| **macOS 12+ (Intel)** | `…-macos-x64.dmg` | Same as above. |
| **Linux (x64)** | `…-x86_64.AppImage` | Make it executable, then run it. |
| | `…_amd64.deb` | For Debian and Ubuntu: `sudo apt install ./miu-smart-storage_*.deb` |
| | `…-linux-x64.tar.gz` | Unpack it anywhere and run `./Miu`. |

`SHA256SUMS.txt` lists checksums for every file.

## First start
Miu asks three things:
1. **What to look at first.** For example your home folder, your code folder, or a drive.
2. **Whether to run in the background.** This lets automations and low-space warnings work even when the window is closed.
3. **Optional extras:** Telegram alerts and cloud backup.

The first scan then starts. Nothing is cleaned until you say so.

## Everyday use
- **Home:**
  - how full each drive is, and a forecast of when it will fill up;
  - what is safe to remove;
  - what grew this week;
  - a **Quick Clean** button.
- **Analyze:**
  - browse your folders, sorted by size, with a reason for every verdict;
  - switch between Folders, Map, Types, Largest and **Duplicates**;
  - **Teach Miu** with *Keep* or *OK to delete*; it suggests rules based on your choices.
- **Dev Projects:**
  - rebuildable folders (node_modules, build output, virtual environments, caches) for each project;
  - git status for each project. Miu never removes work you haven't committed.
  - Docker cleanup.
- **Automations:** let Miu look after folders on a schedule. It only cleans automatically if you allow it in Settings, and then only items marked Safe.
- **Cloud & Network:**
  - map and scan network drives;
  - sync folders (Mirror, Copy new, Two-way). Sync never deletes: removed files are set aside in `.miu-sync-trash`.
  - back up to **Google Drive, OneDrive, Dropbox or S3**. *Back up, then free space* removes local copies only after their cloud copy is verified.
- **Activity:** every clean, sync and freeing, with **Undo**.
- **Settings:**
  - background mode and automatic cleaning;
  - quiet hours, and pausing on battery;
  - Telegram, theme and updates;
  - exporting your setup;
  - *Analyze with Miu* in Explorer (Windows).

### Keyboard
**Ctrl+1 … Ctrl+9** switch pages in sidebar order.

## Safety in one paragraph
Some places are always protected: system folders, program folders and your user profile root, and links into them. No rule can change that. Miu explains every verdict. Cleaning goes to the Recycle Bin or Trash, and you can undo it. Sync and backup keep older or removed copies instead of deleting them. If a run looks like a disconnected drive (an empty source, or most files about to be removed), Miu refuses. Automations only ever clean items marked Safe, and only after you turn that on. Telegram can only clean items that are still Safe, after you confirm.

## Your data
Everything Miu knows lives in a folder on your computer:
- Windows: `%LOCALAPPDATA%\Miu`
- macOS and Linux: `~/.local/share/Miu`

Secrets are kept separately:
- **Windows:** passwords, tokens and keys are in Windows Credential Manager.
- **macOS and Linux:** they are in a private file.

The database is copied once a day (the newest 3 copies are kept). If it is ever damaged, Miu restores the newest good copy. If Miu crashes, it keeps a report on your computer (nothing is sent) and offers it at the next start.

## Updates
Miu checks GitHub once a day for a new release (you can turn this off in Settings). When there is one, Miu shows a **Download** button. Nothing is installed without you.

Made with love by Khang · [vietnamcos.com](https://vietnamcos.com) · [duykhang.com](https://duykhang.com) · [threads.com/@dkhang](https://threads.com/@dkhang)
