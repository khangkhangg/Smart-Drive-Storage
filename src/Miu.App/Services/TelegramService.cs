using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miu.Core.Cleanup;
using Miu.Core.Formatting;
using Miu.Core.Rules;
using Miu.Notify;

namespace Miu.App.Services;

/// <summary>
/// Your own Telegram bot as a remote for Miu. Only the paired chat is obeyed. From Telegram Miu can only move items
/// that are still Safe to the Recycle Bin, after a confirm tap, within 24 hours; every clean is logged and undoable.
/// </summary>
public sealed class TelegramService(AppServices services, AutomationService automation, Func<string, ITelegramApi> apiFactory, Func<DateTime> utcNow)
{
    public const string TokenKey = "telegram.token";
    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ButtonLifetime = TimeSpan.FromHours(24);
    private const int MaxPairingTries = 5;
    private const int MaxMessage = 4000; // Telegram allows 4096

    private readonly CleanupService _cleanup = new(services);
    private ITelegramApi? _api;
    private string? _pairingCode;
    private DateTime _pairingUntil;
    private long _offset;
    private int _pairingTries;

    private sealed record Pending(long WatchId, List<PendingItem> Items);
    private sealed record PendingItem(string Path, string Name, long Bytes);

    public string? BotUsername { get; private set; }
    public long? ChatId => services.Telegram.ChatId();
    public bool IsPaired => ChatId is not null && Api is not null;
    public bool IsConnected => Api is not null;
    public bool IsPairingOpen => _pairingCode is not null && utcNow() <= _pairingUntil;

    /// <summary>Why the last poll failed (shown in Settings), or null when polling works.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when pairing starts or ends, including /unpair from Telegram.</summary>
    public event Action? PairingChanged;

    private ITelegramApi? Api => _api ??= services.Secrets.Get(TokenKey) is { Length: > 0 } t ? apiFactory(t) : null;

    /// <summary>Checks the token with Telegram and stores it in the secret store.</summary>
    public async Task ConnectAsync(string token, CancellationToken ct = default)
    {
        var api = apiFactory(token.Trim());
        BotUsername = await api.GetMeAsync(ct);
        services.Secrets.Set(TokenKey, token.Trim());
        _api = api;
        _offset = 0; // update ids belong to a bot; a new bot starts from its own beginning
        LastError = null;
    }

    /// <summary>A 6-digit code valid for 10 minutes; send "/pair CODE" to your bot.</summary>
    public string StartPairing()
    {
        _pairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        _pairingUntil = utcNow() + PairingWindow;
        _pairingTries = 0;
        return _pairingCode;
    }

    public void Unpair()
    {
        services.Telegram.Unpair();
        services.Secrets.Delete(TokenKey);
        services.RemoteActions.ExpireAll();
        _api = null;
        _offset = 0;
        _pairingCode = null;
        BotUsername = null;
        LastError = null;
        PairingChanged?.Invoke();
    }

    /// <summary>One long-poll round. Returns false on a network or API error (the caller waits and retries).</summary>
    public async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        if (Api is not { } api) return false;
        IReadOnlyList<TelegramUpdate> updates;
        try { updates = await api.GetUpdatesAsync(_offset, 50, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything (offline, 409 from another poller or a webhook, odd JSON from a proxy): report, back off, retry.
            LastError = ex is TelegramApiException { Code: 409 }
                ? "Another program is reading this bot's messages (or a webhook is set). Stop it, or create a new bot for Miu."
                : ex.Message;
            return false;
        }
        LastError = null;
        foreach (var u in updates)
        {
            if (!ReferenceEquals(api, _api)) break; // unpaired or switched bots while this poll was waiting
            _offset = Math.Max(_offset, u.UpdateId + 1);
            try { await HandleAsync(u, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine("Telegram update failed: " + ex.Message); }
        }
        return true;
    }

    /// <summary>Polls until cancelled, backing off after errors (5 s → 1 min).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            bool ok;
            try
            {
                if (Api is null) { await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                ok = await PollOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message; // e.g. the secret store could not be read
                ok = false;
            }
            if (ok) delay = TimeSpan.FromSeconds(5);
            else
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    public async Task HandleAsync(TelegramUpdate u, CancellationToken ct = default)
    {
        if (Api is not { } api) return;
        var paired = ChatId;

        // Pairing is the only thing an unknown chat can do, and only with the right code in time.
        // Only a private chat (not a group, where every member could press buttons) can pair, and wrong guesses close the window.
        if (u.Text is { } t && t.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            if (_pairingCode is null || utcNow() > _pairingUntil || !u.IsPrivate) return;
            var code = t.Length > 5 ? t[5..].Trim() : "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_pairingCode)))
            {
                if (++_pairingTries >= MaxPairingTries) { _pairingCode = null; PairingChanged?.Invoke(); }
                return;
            }
            services.Telegram.Pair(u.ChatId, utcNow());
            _pairingCode = null;
            PairingChanged?.Invoke();
            await TrySendAsync(api, u.ChatId, "Paired ✅ Miu will send you alerts here. Send /help to see what I can do.", ct);
            return;
        }
        if (paired is null || u.ChatId != paired) return; // not yours: no reply at all

        if (u.CallbackData is { } data) { await HandleTapAsync(api, u, data, ct); return; }
        if (u.Text is { } text) await HandleCommandAsync(api, u.ChatId, text.Trim(), ct);
    }

    private async Task HandleCommandAsync(ITelegramApi api, long chat, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.Length == 0 ? "" : parts[0].ToLowerInvariant().Split('@')[0];
        string reply;
        switch (cmd)
        {
            case "/status":
                reply = await StatusTextAsync();
                break;
            case "/scan":
                var lines = new List<string>();
                foreach (var w in services.Watches.All().Where(w => w.Enabled))
                    if (await automation.RunAsync(w, ct) is { } done) lines.Add($"• {done.Name}: {done.LastResult}");
                reply = lines.Count == 0 ? "No automations yet. Add one in Miu › Automations." : "Done:\n" + string.Join("\n", lines);
                break;
            case "/suggest":
                var pending = services.Suggestions.Pending();
                reply = pending.Count == 0 ? "No rule suggestions right now."
                    : "Waiting for your OK in Miu › Rules:\n" + string.Join("\n", pending.Take(10).Select(p => "• " + RuleDescriber.Describe(p.Suggestion.Rule)));
                break;
            case "/pause":
                var span = ParseDuration(parts.Length > 1 ? parts[1] : "1h");
                automation.PauseFor(span);
                reply = $"Paused automations for {Describe(span)}. Send /resume to start again.";
                break;
            case "/resume":
                automation.Resume();
                reply = "Automations are running again.";
                break;
            case "/digest":
                var (title, body) = automation.BuildDigest(utcNow().ToLocalTime());
                reply = $"{title}\n{body}";
                break;
            case "/unpair":
                Unpair(); // first, so a failed goodbye can't leave the chat paired
                await TrySendAsync(api, chat, "Unpaired. Miu won't listen to this chat any more.", ct);
                return;
            default:
                reply = "I can do:\n/status – how full your drives are\n/scan – run your automations now\n/suggest – rule suggestions\n" +
                        "/pause 2h – pause automations\n/resume – start them again\n/digest – this week's summary\n/unpair – stop using this chat";
                break;
        }
        await api.SendMessageAsync(chat, reply, null, ct);
    }

    private async Task HandleTapAsync(ITelegramApi api, TelegramUpdate u, string data, CancellationToken ct)
    {
        var colon = data.IndexOf(':');
        if (colon <= 0 || !long.TryParse(data[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            await Answer(api, u, "That button isn't valid.", ct);
            return;
        }
        switch (data[..colon])
        {
            case "c": // Clean now → ask first
                if (Load(services.RemoteActions.Peek(id, utcNow())) is not { } p) { await Answer(api, u, "This button has expired.", ct); return; }
                await Answer(api, u, null, ct);
                await api.SendMessageAsync(u.ChatId,
                    $"Move {p.Items.Count} {(p.Items.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(p.Items.Sum(i => i.Bytes))}) to the {services.RecycleBinName}? You can restore {(p.Items.Count == 1 ? "it" : "them")} later.",
                    [[new TelegramButton("Yes, clean", $"y:{id}"), new TelegramButton("Cancel", $"x:{id}")]], ct);
                return;
            case "y": // confirmed
                if (Load(services.RemoteActions.TryTake(id, utcNow())) is not { } take) { await Answer(api, u, "This button has expired.", ct); return; }
                await Answer(api, u, "Cleaning…", ct);
                try { await CleanAsync(api, u.ChatId, take, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await TrySendAsync(api, u.ChatId, "Couldn't clean: " + ex.Message + " Nothing else was changed.", ct);
                }
                return;
            case "x":
                services.RemoteActions.TryTake(id, utcNow());
                await Answer(api, u, "Cancelled.", ct);
                return;
            case "l":
                if (Load(services.RemoteActions.Peek(id, utcNow())) is not { } list) { await Answer(api, u, "This button has expired.", ct); return; }
                await Answer(api, u, null, ct);
                await api.SendMessageAsync(u.ChatId, ListText(list.Items), null, ct);
                return;
            case "s":
                var snoozed = services.Watches.Patch(id, w => w with { NextRunLocal = utcNow().ToLocalTime().AddDays(7) });
                await Answer(api, u, snoozed is null ? "That automation is gone." : $"Snoozed “{snoozed.Name}” for 7 days.", ct);
                return;
            case "n":
                var off = services.Watches.Patch(id, w => w with { Enabled = false });
                await Answer(api, u, off is null ? "That automation is gone." : $"Turned off “{off.Name}”. Turn it back on in Miu › Automations.", ct);
                return;
            case "u":
                var action = services.Actions.Get(id);
                if (action is null || action.Undone || action.Origin is not ("telegram" or "auto")) { await Answer(api, u, "Nothing to undo.", ct); return; }
                var restored = await _cleanup.UndoAsync(id, ct);
                await Answer(api, u, $"Restored {restored.Restored.Count} {(restored.Restored.Count == 1 ? "item" : "items")}.", ct);
                return;
            default:
                await Answer(api, u, "That button isn't valid.", ct);
                return;
        }
    }

    /// <summary>
    /// Re-scans the folder and cleans only what was offered AND is still Safe now (not kept, protected or gone since).
    /// </summary>
    private async Task CleanAsync(ITelegramApi api, long chat, Pending p, CancellationToken ct)
    {
        var watch = services.Watches.Get(p.WatchId);
        var folder = watch?.Path ?? CommonFolder(p.Items.Select(i => i.Path));
        if (folder is null || !Directory.Exists(folder))
        {
            await api.SendMessageAsync(chat, "That folder is no longer there, so nothing was cleaned.", null, ct);
            return;
        }
        var session = await ScanSession.RunAsync(services, folder, null, ct);
        var cmp = services.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var offered = p.Items.Select(i => i.Path).ToHashSet(cmp);
        var t = session.Tree;
        var items = session.SafeItems
            .Where(i => i.Verdict.Verdict == Verdict.Safe && offered.Contains(t.GetFullPath(i.NodeId)))
            .Select(i => new CartItem(t.GetFullPath(i.NodeId), t.Name(i.NodeId), t.Size(i.NodeId), Verdict.Safe, i.Verdict.Reason))
            .ToList();
        if (items.Count == 0)
        {
            await api.SendMessageAsync(chat, "Nothing is safe to clean there any more, so nothing was moved.", null, ct);
            return;
        }
        var outcome = await _cleanup.CleanAsync(items, session, "telegram", ct: ct);
        var changed = p.Items.Count - items.Count;
        var failed = items.Count - outcome.Count;
        var text = $"Moved {outcome.Count} {(outcome.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(outcome.FreedBytes)}) to the {services.RecycleBinName}." +
                   (changed > 0 ? $" Left {changed} alone because {(changed == 1 ? "it" : "they")} changed since the alert." : "") +
                   (failed > 0 ? $" {failed} couldn't be moved (in use or locked)." : "");
        await api.SendMessageAsync(chat, text, outcome.ActionId > 0 ? [[new TelegramButton("Undo", $"u:{outcome.ActionId}")]] : null, ct);
    }

    /// <summary>Sends a notification to the paired chat, with buttons when it is about Safe items of an automation.</summary>
    public async Task NotifyAsync(MiuNotification n, CancellationToken ct = default)
    {
        if (!IsPaired || Api is not { } api || ChatId is not { } chat) return;
        List<IReadOnlyList<TelegramButton>>? buttons = null;
        if (n.WatchId is { } watchId && n.Items is { Count: > 0 } items)
        {
            var pending = new Pending(watchId, items.Where(i => i.Verdict == Verdict.Safe).Select(i => new PendingItem(i.Path, i.Name, i.Bytes)).ToList());
            if (pending.Items.Count > 0)
            {
                var id = services.RemoteActions.Create(JsonSerializer.Serialize(pending), utcNow() + ButtonLifetime);
                buttons = [[new TelegramButton("Clean now", $"c:{id}"), new TelegramButton("Show list", $"l:{id}")]];
            }
            buttons ??= [];
            buttons.Add([new TelegramButton("Snooze 7 days", $"s:{watchId}"), new TelegramButton("Never for this folder", $"n:{watchId}")]);
        }
        await api.SendMessageAsync(chat, $"{n.Title}\n{n.Body}", buttons, ct);
    }

    /// <summary>Sends a hello to the paired chat. False when nothing is paired.</summary>
    public async Task<bool> SendTestAsync(CancellationToken ct = default)
    {
        if (Api is not { } api || ChatId is not { } chat) return false;
        await api.SendMessageAsync(chat, "Hello from Miu 👋 Alerts will appear here.", null, ct);
        return true;
    }

    private static string ListText(IReadOnlyList<PendingItem> items)
    {
        var sb = new StringBuilder();
        var shown = 0;
        foreach (var i in items.Take(20))
        {
            var name = i.Name.Length > 80 ? i.Name[..79] + "…" : i.Name;
            var line = $"• {name} — {SizeFormatter.Format(i.Bytes)}\n";
            if (sb.Length + line.Length > MaxMessage - 40) break;
            sb.Append(line);
            shown++;
        }
        if (items.Count > shown) sb.Append($"…and {items.Count - shown} more");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> StatusTextAsync()
    {
        // Drive checks can block on a sleeping network drive, so keep them off the UI thread.
        var drives = await Task.Run(DrivesText);
        var sb = new StringBuilder(drives);
        var watches = services.Watches.All();
        sb.AppendLine($"Automations: {watches.Count(w => w.Enabled)} on" + (automation.IsPaused ? " (paused)" : ""));
        var pendingRules = services.Suggestions.Pending().Count;
        if (pendingRules > 0) sb.AppendLine($"{pendingRules} rule suggestions waiting");
        return sb.ToString().TrimEnd();
    }

    private static string DrivesText()
    {
        var sb = new StringBuilder();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.TotalSize < 1L << 30 || d.DriveType is not (DriveType.Fixed or DriveType.Network or DriveType.Removable)) continue;
                var pct = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                sb.AppendLine($"💾 {d.Name}: {SizeFormatter.Format(d.AvailableFreeSpace)} free ({pct:0}% used)");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return sb.ToString();
    }

    private static Pending? Load(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<Pending>(json); } catch (JsonException) { return null; }
    }

    private static string? CommonFolder(IEnumerable<string> paths) => paths.Select(Path.GetDirectoryName).FirstOrDefault();

    /// <summary>
    /// Best effort: Telegram refuses answers to taps older than a few seconds (Miu was asleep or busy), and that must never
    /// stop the action itself.
    /// </summary>
    private static async Task Answer(ITelegramApi api, TelegramUpdate u, string? text, CancellationToken ct)
    {
        if (u.CallbackId is not { } id) return;
        try { await api.AnswerCallbackAsync(id, text, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TelegramApiException || ex is TaskCanceledException && !ct.IsCancellationRequested) { }
    }

    private static async Task TrySendAsync(ITelegramApi api, long chat, string text, CancellationToken ct)
    {
        try { await api.SendMessageAsync(chat, text, null, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TelegramApiException || ex is TaskCanceledException && !ct.IsCancellationRequested) { }
    }

    public static TimeSpan ParseDuration(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.Length >= 2 && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
            return s[^1] switch { 'm' => TimeSpan.FromMinutes(Math.Min(n, 10080)), 'd' => TimeSpan.FromDays(Math.Min(n, 30)), _ => TimeSpan.FromHours(Math.Min(n, 720)) };
        return TimeSpan.FromHours(1);
    }

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#} days" : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hours" : $"{t.TotalMinutes:0} minutes";
}
