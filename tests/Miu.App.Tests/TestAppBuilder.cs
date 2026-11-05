using Avalonia;
using Avalonia.Headless;
using Miu.App;

[assembly: AvaloniaTestApplication(typeof(Miu.App.Tests.TestAppBuilder))]

namespace Miu.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
