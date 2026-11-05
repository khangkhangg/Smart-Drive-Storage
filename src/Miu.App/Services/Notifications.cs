namespace Miu.App.Services;

public sealed record NotificationAction(string Label, Func<Task> Run);

public sealed record MiuNotification(string Title, string Body, IReadOnlyList<NotificationAction> Actions);

/// <summary>Shows a message with optional action buttons (a small window near the tray, or a test fake).</summary>
public interface INotifier
{
    void Show(MiuNotification notification);
}
