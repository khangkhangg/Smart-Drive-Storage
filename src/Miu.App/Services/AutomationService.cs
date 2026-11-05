using System.Globalization;
using Miu.Core.Automation;
using Miu.Core.Cleanup;
using Miu.Core.Formatting;
using Miu.Core.Insights;
using Miu.Core.Rules;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Services;

/// <summary>
/// Runs watched folders on their schedule. Checked about once a minute. Automatic cleaning only ever removes
/// Safe items, only after you allowed it in Settings, never in a whole drive or your home folder, and always
/// through the Recycle Bin with Undo. Battery, quiet hours and "pause" postpone a run by 30 minutes.
/// </summary>
public sealed class AutomationService(AppServices services, INotifier notifier, IPowerState power, Func<DateTime> nowLocal,
    Action<IReadOnlyList<CartItem>> openReview, Func<string, double>? freePercent = null)
{
    private static readonly TimeSpan Postpone = TimeSpan.FromMinutes(30);
    private readonly CleanupService _cleanup = new(services);
    private readonly Func<string, double> _freePercent = freePercent ?? DriveLookup.FreePercent;
    private readonly Dictionary<long, DateTime> _lowSpaceChecked = new();
    private readonly SemaphoreSlim _gate = new(1, 1); // one run at a time: ticks, Run now and sign-in runs

    public bool IsPaused => PausedUntil is { } p && p > nowLocal();

    public DateTime? PausedUntil =>
        services.Settings.Get(SettingKeys.PausedUntil) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    public void PauseFor(TimeSpan duration) =>
        services.Settings.Set(SettingKeys.PausedUntil, (nowLocal() + duration).ToString("O", CultureInfo.InvariantCulture));

    public void Resume() => services.Settings.Set(SettingKeys.PausedUntil, "");

    public bool InQuietHours()
    {
        if (!int.TryParse(services.Settings.Get(SettingKeys.QuietStartHour), out var start) ||
            !int.TryParse(services.Settings.Get(SettingKeys.QuietEndHour), out var end) || start == end) return false;
        var h = nowLocal().Hour;
        return start < end ? h >= start && h < end : h >= start || h < end; // e.g. 22 → 7 wraps midnight
    }

    private bool Guarded() =>
        IsPaused || InQuietHours() || (services.Settings.GetBool(SettingKeys.PauseOnBattery, true) && power.IsOnBattery);

    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return; // a run is still going
        try
        {
            var now = nowLocal();
            var guarded = Guarded();
            if (!guarded) MaybeDigest(now);
            foreach (var listed in services.Watches.All())
            {
                ct.ThrowIfCancellationRequested();
                // Always act on what is stored now: the list may be stale after a long run.
                if (services.Watches.Get(listed.Id) is not { Enabled: true } w) continue;
                switch (w.Schedule.Kind)
                {
                    case ScheduleKind.AtSignIn:
                        continue;
                    case ScheduleKind.LowSpace:
                        if (!guarded) await CheckLowSpaceAsync(w, now, ct);
                        continue;
                }
                if (w.NextRunLocal is null)
                {
                    services.Watches.Patch(w.Id, x => x with { NextRunLocal = Scheduler.NextRun(x.Schedule, now) });
                    continue;
                }
                if (!Scheduler.IsDue(w.NextRunLocal, now)) continue;
                if (guarded)
                {
                    services.Watches.Patch(w.Id, x => x with { NextRunLocal = now + Postpone });
                    continue;
                }
                await RunUnlockedAsync(w, null, ct);
                services.Watches.Patch(w.Id, x => x with { NextRunLocal = Scheduler.NextRun(x.Schedule, now) });
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>"Every time you sign in" watches. Skipped while paused, in quiet hours or on battery.</summary>
    public async Task RunAtSignInAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Guarded()) return;
            foreach (var w in services.Watches.All().Where(w => w.Enabled && w.Schedule.Kind == ScheduleKind.AtSignIn))
                await RunUnlockedAsync(w, null, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs one watch now ("Run now"). Waits for any run in progress first.</summary>
    public async Task<Watch?> RunAsync(Watch w, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (services.Watches.Get(w.Id) is not { } fresh) return null;
            await RunUnlockedAsync(fresh, null, ct);
            return services.Watches.Get(w.Id);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Low space: a cheap free-space check every 30 minutes; scans and notifies once per low episode.</summary>
    private async Task CheckLowSpaceAsync(Watch w, DateTime now, CancellationToken ct)
    {
        if (_lowSpaceChecked.TryGetValue(w.Id, out var last) && now - last < Postpone) return;
        _lowSpaceChecked[w.Id] = now;
        var key = $"automation.lowSpaceNotified.{w.Id}";
        var free = _freePercent(w.Path);
        if (free >= w.Schedule.LowSpacePercent)
        {
            services.Settings.Set(key, ""); // back to normal: the next low episode notifies again
            return;
        }
        if (services.Settings.Get(key) == "1") return;
        services.Settings.Set(key, "1");
        await RunUnlockedAsync(w, $"Only {free:0}% free.", ct);
    }

    private async Task RunUnlockedAsync(Watch w, string? lowSpaceNote, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        string status = "ok", summary;
        long freed = 0;
        try
        {
            (summary, freed, var shown) = await RunCoreAsync(w, lowSpaceNote, ct);
            if (!shown) status = "quiet";
        }
        catch (DirectoryNotFoundException)
        {
            (status, summary) = ("failed", $"Folder not found: {w.Path}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            (status, summary) = ("failed", "Something went wrong: " + ex.Message);
        }
        services.JobRuns.Add(w.Id, started, DateTime.UtcNow, status, freed, summary);
        services.JobRuns.Prune(w.Id, keep: 50);
        services.Watches.Patch(w.Id, x => x with { LastRunUtc = DateTime.UtcNow, LastResult = summary });
    }

    /// <summary>Automatic cleaning is never allowed for a whole drive, your home folder or system locations.</summary>
    public bool IsTooBroadForAutoClean(string path) =>
        !Path.IsPathRooted(path) || services.Guard.Classify(path) != ProtectionKind.None;

    private async Task<(string Summary, long Freed, bool Shown)> RunCoreAsync(Watch w, string? lowSpaceNote, CancellationToken ct)
    {
        if (!Directory.Exists(w.Path)) throw new DirectoryNotFoundException(w.Path);

        var session = await ScanSession.RunAsync(services, w.Path, null, ct);
        ScanInsights.Record(services, session, DateTime.UtcNow);
        var nowUtc = DateTime.UtcNow;
        var t = session.Tree;
        var items = session.SafeItems
            .Where(i => i.Verdict.Verdict == Verdict.Safe && w.Filters.Matches(t.Size(i.NodeId), t.NewestModifiedUtc(i.NodeId), nowUtc))
            .Select(i => new CartItem(t.GetFullPath(i.NodeId), t.Name(i.NodeId), t.Size(i.NodeId), Verdict.Safe, i.Verdict.Reason))
            .ToList();
        var total = SizeFormatter.Format(items.Sum(i => i.Bytes));
        var count = $"{items.Count} {(items.Count == 1 ? "item" : "items")}";
        NotificationAction Review() => new("Review", () => { openReview(items); return Task.CompletedTask; });

        if (w.Mode == WatchMode.NotifyOnly || lowSpaceNote is not null)
        {
            var body = (lowSpaceNote is null ? "" : lowSpaceNote + " ") +
                       $"{w.Name}: {SizeFormatter.Format(t.Size(t.Root))} in total" + (items.Count > 0 ? $", {count} ({total}) could be cleaned." : ".");
            notifier.Show(new MiuNotification(lowSpaceNote is null ? w.Name : "Running low on space", body, items.Count > 0 ? [Review()] : [], w.Id, items));
            return (body, 0, true);
        }

        if (items.Count == 0) return ("Nothing to clean.", 0, false);

        var allowed = services.Settings.GetBool(SettingKeys.AutoCleanAllowed, false) && !IsTooBroadForAutoClean(w.Path);
        if (w.Mode == WatchMode.Suggest || !allowed)
        {
            var body = $"{count} ({total}) in {w.Name} can be cleaned." +
                       (w.Mode == WatchMode.AutoCleanSafe ? " Automatic cleaning is off for this folder — turn it on in Settings, or pick a narrower folder." : "");
            notifier.Show(new MiuNotification(w.Name, body, [Review()], w.Id, items));
            return (body, 0, true);
        }

        var outcome = await _cleanup.CleanAsync(items, session, "auto", ct: ct);
        var cleaned = $"Miu cleaned {outcome.Count} {(outcome.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(outcome.FreedBytes)}) in {w.Name}.";
        if (outcome.Count == 0)
            return ($"Nothing could be cleaned ({outcome.Failed.Count} couldn't be moved).", 0, false); // no toast for nothing
        var actions = outcome.ActionId > 0
            ? new List<NotificationAction> { new("Undo", async () => await _cleanup.UndoAsync(outcome.ActionId)) }
            : [];
        notifier.Show(new MiuNotification(w.Name, cleaned + (outcome.Failed.Count > 0 ? $" {outcome.Failed.Count} couldn't be moved." : ""), actions));
        return (cleaned, outcome.FreedBytes, true);
    }

    public (string Title, string Body) BuildDigest(DateTime now)
    {
        var weekAgoUtc = now.ToUniversalTime().AddDays(-7);
        var freed = services.Actions.Recent(500).Where(a => a.TsUtc >= weekAgoUtc && !a.Undone && a.Kind == "recycle").Sum(a => a.Bytes);
        return Digest.Build(freed, [], services.Suggestions.Pending().Count, 0);
    }

    private void MaybeDigest(DateTime now)
    {
        try
        {
            var last = services.Settings.Get(SettingKeys.LastDigest);
            if (string.IsNullOrEmpty(last) || !DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastAt))
            {
                services.Settings.Set(SettingKeys.LastDigest, now.ToString("O", CultureInfo.InvariantCulture));
                return;
            }
            if (now - lastAt < TimeSpan.FromDays(7)) return;
            var (title, body) = BuildDigest(now);
            notifier.Show(new MiuNotification(title, body, []));
            services.Settings.Set(SettingKeys.LastDigest, now.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Digest failed: " + ex.Message); }
    }
}
