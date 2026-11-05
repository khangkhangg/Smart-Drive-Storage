using Avalonia.Threading;
using Miu.App.Views;

namespace Miu.App.Services;

/// <summary>Shows notifications as a small window in the corner of the screen, with action buttons.</summary>
public sealed class ToastNotifier : INotifier
{
    public void Show(MiuNotification notification) =>
        Dispatcher.UIThread.Post(() => new ToastWindow(notification).Show());
}
