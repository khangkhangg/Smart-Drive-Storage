namespace Miu.App.Services;

/// <summary>Finds the drive / mount a path lives on (on Linux "/home" can be its own partition).</summary>
public static class DriveLookup
{
    public static string RootFor(string path, IEnumerable<string> roots)
    {
        var p = path.Replace('\\', '/');
        string best = "";
        foreach (var r in roots)
        {
            var root = r.Replace('\\', '/');
            var withSep = root.EndsWith('/') ? root : root + "/";
            var cmp = OperatingSystem.IsWindows() || (root.Length >= 2 && root[1] == ':') ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if ((p.Equals(root.TrimEnd('/'), cmp) || p.StartsWith(withSep, cmp)) && root.Length > best.Length) best = r;
        }
        return best.Length > 0 ? best : Path.GetPathRoot(path) ?? path;
    }

    public static DriveInfo? DriveFor(string path)
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).ToList();
            var root = RootFor(Path.GetFullPath(path), drives.Select(d => d.RootDirectory.FullName));
            return drives.FirstOrDefault(d => d.RootDirectory.FullName == root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static double FreePercent(string path) =>
        DriveFor(path) is { TotalSize: > 0 } d ? 100.0 * d.AvailableFreeSpace / d.TotalSize : 100;
}

/// <summary>The command that starts Miu at sign-in, also when it runs as "dotnet Miu.dll".</summary>
public static class StartupCommand
{
    public static (string Exe, string Args) For(string processPath, string? entryAssemblyPath)
    {
        var name = Path.GetFileNameWithoutExtension(processPath);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssemblyPath)
            ? (processPath, $"\"{entryAssemblyPath}\" --background")
            : (processPath, "--background");
    }
}

/// <summary>Only one Miu runs at a time; a second start asks the first to show its window.</summary>
public static class SingleInstance
{
    public const string ShowSignal = "show.signal";

    public static IDisposable? TryAcquire(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return new FileStream(Path.Combine(dir, "miu.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void SignalShow(string dir)
    {
        try { File.WriteAllText(Path.Combine(dir, ShowSignal), DateTime.UtcNow.ToString("O")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
