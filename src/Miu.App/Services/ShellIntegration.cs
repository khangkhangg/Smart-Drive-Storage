using System.Diagnostics;

namespace Miu.App.Services;

public static class ShellIntegration
{
    /// <summary>Shows the item in Explorer / Finder / the default file manager.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", ["-R", path]);
            else
                Process.Start("xdg-open", [Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
