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
/// 🟢 Safe items, only after you allowed it in Settings, and always through the Recycle Bin with Undo.
/// Battery, quiet hours and "pause" postpone a run by 30 minutes instead of dropping it.
/// </summary>
public sealed class AutomationService(AppServices services, INotifier notifier, IPowerState power, Func<DateTime> nowLocal,
    Action<IReadOnlyList<CartItem>> openReview)
{
    private static readonly TimeSpan Postpone = TimeSpan.FromMinutes(30);
    private readonly CleanupService _cleanup = new(services);
    private readonly Dictionary<long, DateTime> _lowSpaceChecked = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

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
            MaybeDigest(now);
            var guarded = Guarded();
            foreach (var w in services.Watches.All().Where(w => w.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                switch (w.Schedule.Kind)
                {
                    case ScheduleKind.AtSignIn:
                        continue;
                    case ScheduleKind.LowSpace:
                        if (guarded || (_lowSpaceChecked.TryGetValue(w.Id, out var last) && now - last < Postpone)) continue;
                        _lowSpaceChecked[w.Id] = now;
                        await RunAsync(w, ct);
                        continue;
                }
                if (w.NextRunLocal is null)
                {
                    services.Watches.Update(w with { NextRunLocal = Scheduler.NextRun(w.Schedule, now) });
                    continue;
                }
                if (!Scheduler.IsDue(w.NextRunLocal, now)) continue;
                if (guarded)
                {
                    services.Watches.Update(w with { NextRunLocal = now + Postpone });
                    continue;
                }
                var done = await RunAsync(w, ct);
                services.Watches.Update(done with { NextRunLocal = Scheduler.NextRun(w.Schedule, now) });
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs the "every time you sign in" watches (called once when Miu starts in the background).</summary>
    public async Task RunAtSignInAsync(CancellationToken ct = default)
    {
        foreach (var w in services.Watches.All().Where(w => w.Enabled && w.Schedule.Kind == ScheduleKind.AtSignIn))
            await RunAsync(w, ct);
    }

    /// <summary>Runs one watch now (also used by "Run now"). Returns the watch with its last result filled in.</summary>
    public async Task<Watch> RunAsync(Watch w, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        string status = "ok", summary;
        long freed = 0;
        try
        {
            (summary, freed, var shown) = await RunCoreAsync(w, ct);
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
        var updated = w with { LastRunUtc = DateTime.UtcNow, LastResult = summary };
        services.Watches.Update(updated);
        return updated;
    }

    private async Task<(string Summary, long Freed, bool Shown)> RunCoreAsync(Watch w, CancellationToken ct)
    {
        if (!Directory.Exists(w.Path)) throw new DirectoryNotFoundException(w.Path);

        string? lowSpaceNote = null;
        if (w.Schedule.Kind == ScheduleKind.LowSpace)
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(w.Path))!);
            var freePct = drive.TotalSize == 0 ? 100 : 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
            if (freePct >= w.Schedule.LowSpacePercent) return ($"Space is fine ({freePct:0}% free).", 0, false);
            lowSpaceNote = $"{drive.Name.TrimEnd('\\', '/')} has only {freePct:0}% free.";
        }

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

        if (w.Mode == WatchMode.NotifyOnly || lowSpaceNote is not null)
        {
            var body = (lowSpaceNote is null ? "" : lowSpaceNote + " ") +
                       $"{w.Name}: {SizeFormatter.Format(t.Size(t.Root))} in total" + (items.Count > 0 ? $", {count} ({total}) could be cleaned." : ".");
            notifier.Show(new MiuNotification(lowSpaceNote is null ? w.Name : "Running low on space", body,
                items.Count > 0 ? [new NotificationAction("Review", () => { openReview(items); return Task.CompletedTask; })] : []));
            return (body, 0, true);
        }

        if (items.Count == 0) return ("Nothing to clean.", 0, false);

        var allowed = services.Settings.GetBool(SettingKeys.AutoCleanAllowed, false);
        if (w.Mode == WatchMode.Suggest || !allowed)
        {
            var body = $"{count} ({total}) in {w.Name} can be cleaned." +
                       (w.Mode == WatchMode.AutoCleanSafe ? " Automatic cleaning is off — turn it on in Settings to let Miu do this for you." : "");
            notifier.Show(new MiuNotification(w.Name, body, [new NotificationAction("Review", () => { openReview(items); return Task.CompletedTask; })]));
            return (body, 0, true);
        }

        var outcome = await _cleanup.CleanAsync(items, session, "auto", ct: ct);
        var cleaned = $"Miu cleaned {outcome.Count} {(outcome.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(outcome.FreedBytes)}) in {w.Name}.";
        var actions = outcome.ActionId > 0
            ? new List<NotificationAction> { new("Undo", async () => await _cleanup.UndoAsync(outcome.ActionId)) }
            : [];
        notifier.Show(new MiuNotification(w.Name, cleaned + (outcome.Failed.Count > 0 ? $" {outcome.Failed.Count} couldn't be moved." : ""), actions));
        return (cleaned, outcome.FreedBytes, true);
    }

    private void MaybeDigest(DateTime now)
    {
        var last = services.Settings.Get(SettingKeys.LastDigest);
        if (string.IsNullOrEmpty(last) || !DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastAt))
        {
            services.Settings.Set(SettingKeys.LastDigest, now.ToString("O", CultureInfo.InvariantCulture));
            return;
        }
        if (now - lastAt < TimeSpan.FromDays(7)) return;
        var weekAgoUtc = now.ToUniversalTime().AddDays(-7);
        var freed = services.Actions.Recent(500).Where(a => a.TsUtc >= weekAgoUtc && !a.Undone && a.Kind == "recycle").Sum(a => a.Bytes);
        var (title, body) = Digest.Build(freed, [], services.Suggestions.Pending().Count, 0);
        notifier.Show(new MiuNotification(title, body, []));
        services.Settings.Set(SettingKeys.LastDigest, now.ToString("O", CultureInfo.InvariantCulture));
    }
}
