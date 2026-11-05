using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Miu.Platform.Windows;

/// <summary>"Analyze with Miu" when you right-click a folder, the inside of a folder, or a drive in Explorer (current user only).</summary>
[SupportedOSPlatform("windows")]
public static class WindowsExplorerMenu
{
    public const string Verb = "MiuAnalyze";
    private static readonly (string Key, string Arg)[] Places =
    [
        (@"Software\Classes\Directory\shell\" + Verb, "%1"),
        (@"Software\Classes\Directory\Background\shell\" + Verb, "%V"),
        (@"Software\Classes\Drive\shell\" + Verb, "%1"),
    ];

    public static string Command(string exePath, string arg) => $"\"{exePath}\" --analyze \"{arg}\"";

    public static void Register(string exePath)
    {
        foreach (var (key, arg) in Places)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue("", "Analyze with Miu");
            k.SetValue("Icon", $"\"{exePath}\",0");
            using var c = k.CreateSubKey("command");
            c.SetValue("", Command(exePath, arg));
        }
    }

    public static void Unregister()
    {
        foreach (var (key, _) in Places) Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }

    public static bool IsRegistered
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Places[0].Key + @"\command");
            return k?.GetValue("") is string;
        }
    }
}
