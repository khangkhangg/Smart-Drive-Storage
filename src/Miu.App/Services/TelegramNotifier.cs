using Miu.Data;

namespace Miu.App.Services;

/// <summary>Sends notifications to the paired Telegram chat too (if you turned alerts on).</summary>
public sealed class TelegramNotifier(TelegramService telegram, SettingsRepository settings) : INotifier
{
    public const string AlertsKey = "telegram.alerts";

    public void Show(MiuNotification n)
    {
        if (!telegram.IsPaired || !settings.GetBool(AlertsKey, true)) return;
        _ = SendAsync(n);
    }

    private async Task SendAsync(MiuNotification n)
    {
        try { await telegram.NotifyAsync(n); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Telegram notify failed: " + ex.Message); }
    }
}

/// <summary>Shows each notification in every place (the corner popup, Telegram…).</summary>
public sealed class CompositeNotifier : INotifier
{
    private readonly List<INotifier> _targets = [];

    public CompositeNotifier(params INotifier[] targets) => _targets.AddRange(targets);

    public void Add(INotifier target) => _targets.Add(target);

    public void Show(MiuNotification n)
    {
        foreach (var t in _targets)
        {
            try { t.Show(n); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Notifier failed: " + ex.Message); }
        }
    }
}
