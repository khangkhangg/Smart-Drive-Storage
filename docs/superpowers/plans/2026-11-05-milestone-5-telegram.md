# Milestone 5 — Telegram

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Connect your own Telegram bot, then use it to:
- get automation results and low-space alerts with buttons: **Clean now**, **Show list**, **Snooze 7 days**, **Never for this folder**;
- send the commands `/status /scan /suggest /pause /resume /digest /help /unpair`.

Remote actions are limited to moving **Safe** items to the Recycle Bin. Each one needs a confirm tap, expires after 24 h, is logged with origin `telegram`, and can be undone (spec §5.7).

**Architecture:**
- `Miu.Notify` (new project):
  - `ITelegramApi` + `TelegramBotApi`: a minimal HTTP client for getMe, getUpdates (long polling), sendMessage with an inline keyboard, answerCallbackQuery and editMessageText. No third-party package.
  - `TelegramUpdate` models.
- Secrets: `ISecretStore` in Abstractions, with two implementations:
  - `WindowsCredentialStore` (CredWrite/CredRead/CredDelete) in `Miu.Platform.Windows`;
  - `FileSecretStore` (a 0600-permission file in the app data folder) in Portable, for Linux and macOS until Phase 9 adds libsecret and Keychain.
- App:
  - `TelegramService`: pairing, the polling loop, command handling and `RemoteActionGate` (backed by the `pending_remote_actions` table).
  - `TelegramNotifier`, plus a `CompositeNotifier` that sends to both the toast and Telegram.
  - `MiuNotification` gains optional `WatchId` and `Items` so Telegram can offer actions.

## Global Constraints
- Only the paired `chat_id` is obeyed. Any other chat gets no reply, except `/pair <code>` while pairing is open (10 minutes).
- The bot token lives only in the secret store, never in the database or logs.
- "Clean now" → a confirmation message → "Yes, clean" → re-scan the watch folder and clean only items that are **still Safe** and were in the original list → `CleanupService` (guard, Keep check, Recycle Bin) with origin `telegram` → reply with an Undo button.
- A pending action expires after 24 h and runs at most once.
- No permanent delete, and no Protected or Keep items, from Telegram, ever.

## Review Focus
1. **A forged or old callback** (another chat, an expired id, a reused id, malformed data): ignored or answered "expired", with no action taken.
2. **Items that changed between the alert and the confirm** (now Keep, protected, gone, or newly added): only the intersection of the original list and items still Safe is cleaned.
3. **Network errors or a 409 conflict** (another poller) in the polling loop: back off and never crash.
4. **A wrong pairing code, or pairing attempts from several chats:** only an exact code inside its time window pairs.
5. **Unpairing in the app or with `/unpair`:** polling stops obeying immediately, and the secret is removed.

### Tasks
1. `ISecretStore` + `FileSecretStore` (permissions test) + `WindowsCredentialStore` (roundtrip on Windows CI).
2. `Miu.Notify`: the models, `ITelegramApi` and `TelegramBotApi` (JSON shapes tested with a fake `HttpMessageHandler`).
3. `RemoteActionsRepository` (create, get, mark used, expiry).
4. `TelegramService`: pairing, the command handlers and the callback flow. Tested with `FakeTelegramApi`, with time injected.
5. `TelegramNotifier` + `CompositeNotifier`, wired into `MainWindowViewModel` / `AutomationService`, plus the polling loop in the App.
6. UI: a Telegram card in Settings (token → Connect → pairing code → paired; test message; notify toggles; unpair). Screenshot.
7. Docs, review, fix pass, push.
