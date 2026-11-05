# Milestone 7 — Cloud backup

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Back up chosen folders to **Google Drive, OneDrive, Dropbox or S3-compatible storage** (AWS, Backblaze B2, Wasabi, Cloudflare R2, MinIO), on a schedule. Three modes:
- **Copy new and changed**
- **Mirror**
- **Back up, then free space**: files untouched for N days are uploaded, verified, and only then moved to the Recycle Bin.

Older versions are kept. You can restore any file to where it was or to another folder.

**Spec:** §5.10, features F7.1–F7.9.

**Architecture:**
- `Miu.Cloud` (new, no third-party packages):
  - The `ICloudStorage` contract works on paths relative to the account's backup area: Get, List (recursive), Upload (with progress), Download, Move, Remove. A provider's own trash is used where it has one.
  - `CloudHashes` covers MD5, the Dropbox content hash and OneDrive's quickXorHash. Each one is computed locally while uploading, then compared with what the provider reports.
  - Streams: `HashingStream` and `ThrottledStream` (bandwidth limit).
  - Providers:
    - `S3Storage`: SigV4 signing, tested against the AWS example; multipart upload above 64 MB; ListObjectsV2; copy + delete for moves.
    - OAuth with PKCE in the system browser, with a loopback redirect on `127.0.0.1` and refresh tokens. `GoogleDriveStorage`, `OneDriveStorage` and `DropboxStorage` use resumable or chunked upload sessions.
  - Least privilege:
    - Google `drive.file`: Miu sees only files it created.
    - OneDrive app folder (`Apps/Miu Smart Storage`).
    - Dropbox app folder.
  - Client IDs come from `cloud-clients.json` next to the app, or from environment variables. A build without them shows "not available in this build" for that provider. S3 always works.
- `Miu.Core.Backup` (pure, TDD):
  - `BackupPlan` / `BackupSource` / `BackupMode`.
  - `BackupDiff.Plan(localByRoot, manifest, mode, now)` returns uploads, remote set-asides (Mirror only) and free-space candidates, or a refusal (the same brake as sync).
- `Miu.Data`, migration 006:
  - Manifest columns: remote path, hash kind, freed.
  - A `backup_versions` table.
  - `CloudAccountsRepository`, `BackupPlansRepository`, `BackupManifestRepository`.
- App:
  - `CloudAccountsService` connects, tests and disconnects accounts. Every secret (refresh token, S3 secret key) is kept in the secret store only.
  - `BackupService` handles preview, run, verify, free and restore. Upload retries back off. The manifest is written after each file, so a cancelled run picks up where it stopped. Versions are pruned.
  - Scheduled plans run through `AutomationService`.
  - Review & Clean gets a "Back up first" option.
  - UI on the Cloud & Network › **Cloud backup** tab: accounts, plan editor, preview, run, restore.

## Global Constraints
- **A local file is freed only when all of these hold:**
  - the upload finished;
  - the provider reports the same size, and the same checksum where it supplies one (S3 multipart: Miu's MD5 metadata, signed with the upload);
  - the local file still has the size and modified time that was uploaded;
  - it hasn't changed for N days.

  Freeing goes through `CleanupService` (guard, Keep check, Recycle Bin, Undo) with origin `backup`.
- **Nothing in the cloud is overwritten or removed without a version.** A changed file's old copy and a file removed in Mirror mode are moved to `.miu-versions/<time>/…`. Pruning keeps the newest N versions per file. Removal uses the provider's trash where there is one (Drive, OneDrive, Dropbox).
- **Brake:** a missing or empty source folder never makes Mirror set aside the backup. More than 50% (and more than 20 files) is refused.
- **Restore never overwrites a different local file.** It writes `name (restored YYYY-MM-DD).ext` instead, and partial downloads use `.miu-partial`.
- Tokens and keys live only in the secret store (`cloud.<id>`), never in the database, the logs or the UI after saving.
- Scheduled backups respect the pause, battery and quiet-hours guards.

## Review Focus
1. **Freeing:**
   - Can a file be recycled when its upload failed, was partial, or doesn't match?
   - Can it be recycled when it changed after the upload?
   - Can it be recycled when the provider reports no checksum and the size happens to match?
2. **S3 signing and multipart:** canonical request encoding (spaces, unicode, `+`, `~`), part sizes, aborting on failure, the 5 GB copy limit.
3. **OAuth:**
   - PKCE verifier and challenge; the `state` check; the loopback listener bound to 127.0.0.1 only, then closed.
   - Refresh on 401 exactly once; tokens never logged.
4. **Mirror brake and versions:** an unplugged source, a renamed root, many deletions, and version pruning that keeps the newest N.
5. **Restore:** collisions, path traversal from remote names (`..`, absolute paths), and partial downloads.

### Tasks
1. Core backup model and diff (TDD).
2. `Miu.Cloud` foundation: the contract, hashes (test vectors), streams, SigV4 (AWS example vector), S3 provider (fake HTTP), and an in-memory provider for tests.
3. OAuth PKCE and loopback (fake browser), plus the Google Drive, OneDrive and Dropbox providers (request shapes, chunking, pagination, hashes).
4. Data: migration 006 and the repositories.
5. App: the accounts service, `BackupService` (preview, run, verify, free, versions, restore, retries) against the in-memory provider, schedules, and Back up first.
6. UI: the Cloud backup tab (accounts, editor, plans, preview, restore). Headless tests and a screenshot.
7. Docs, independent review, fix pass, push.
