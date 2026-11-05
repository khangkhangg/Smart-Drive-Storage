using System.Globalization;
using System.Runtime.InteropServices;

namespace Miu.App.Services;

/// <summary>
/// Writes a report to Miu's folder when something goes wrong, so the next start can offer it. Reports stay on this
/// computer; nothing is ever uploaded. The newest 10 are kept.
/// </summary>
public static class CrashReporter
{
    private const string Prefix = "crash-";
    public static string Folder(string dataDir) => Path.Combine(dataDir, "crashes");

    public static void Install(string dataDir, Action<Exception>? onUiThreadError = null)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Write(dataDir, ex, "fatal"); };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(dataDir, e.Exception, "background task");
            e.SetObserved(); // a forgotten background task must not take Miu down
        };
        if (onUiThreadError is not null)
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Write(dataDir, e.Exception, "screen");
                e.Handled = true; // keep running; the user sees what happened
                onUiThreadError(e.Exception);
            };
    }

    public static string? Write(string dataDir, Exception ex, string kind)
    {
        try
        {
            var dir = Folder(dataDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Prefix}{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt");
            File.WriteAllText(path,
                $"Miu Smart Storage {AppInfo.Version} ({kind})\n" +
                $"Time: {DateTime.Now:O}\n" +
                $"System: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}\n\n" +
                ex);
            foreach (var old in Directory.GetFiles(dir, Prefix + "*.txt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(10)) File.Delete(old);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The newest report from a previous run that hasn't been shown yet.</summary>
    public static string? Pending(string dataDir)
    {
        var dir = Folder(dataDir);
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, Prefix + "*.txt").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault(f => !File.Exists(f + ".seen"));
    }

    public static void MarkSeen(string report)
    {
        try { File.WriteAllText(report + ".seen", ""); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
