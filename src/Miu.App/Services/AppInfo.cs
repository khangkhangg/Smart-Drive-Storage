using System.Reflection;

namespace Miu.App.Services;

public static class AppInfo
{
    public const string Name = "Miu Smart Storage";
    public const string Author = "Khang";

    /// <summary>Semantic version from the build (Directory.Build.props), without the source-revision suffix.</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];

    public static readonly (string Title, string Url)[] Links =
    [
        ("vietnamcos.com", "https://vietnamcos.com"),
        ("duykhang.com", "https://duykhang.com"),
        ("threads.com/@dkhang", "https://www.threads.com/@dkhang"),
    ];
}
