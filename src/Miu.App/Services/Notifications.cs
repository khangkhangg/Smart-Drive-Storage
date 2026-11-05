namespace Miu.App.Services;

public sealed record NotificationAction(string Label, Func<Task> Run);

/// <param name="WatchId">The automation it came from, so Telegram can offer Snooze / Never.</param>
/// <param name="Items">Safe items it is about, so Telegram can offer "Clean now".</param>
public sealed record MiuNotification(string Title, string Body, IReadOnlyList<NotificationAction> Actions,
    long? WatchId = null, IReadOnlyList<Miu.Core.Cleanup.CartItem>? Items = null);

/// <summary>Shows a message with optional action buttons (a small window near the tray, or a test fake).</summary>
public interface INotifier
{
    void Show(MiuNotification notification);
}
