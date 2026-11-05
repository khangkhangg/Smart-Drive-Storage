using System.Diagnostics;
using System.Text;
using Miu.Core.Formatting;
using Miu.Core.Imaging;
using Miu.Data;

namespace Miu.App.Services;

/// <param name="Command">What will run (null when <paramref name="Problem"/> says why it can't).</param>
/// <param name="NeededBytes">About how much space the image needs (the source's used space).</param>
public sealed record DiskImagePreview(DiskImageCommand? Command, string? Problem, long NeededBytes, string Source, string Target);

/// <param name="Ok">The tool finished without an error.</param>
public sealed record DiskImageOutcome(bool Ok, string Message, string Output);

/// <summary>Runs one elevated imaging command and reports its output lines. Swapped for a fake in tests.</summary>
public delegate Task<(int ExitCode, string Output)> ElevatedRunner(DiskImageCommand command, IProgress<string>? progress, CancellationToken ct);

/// <summary>
/// Disk images with each system's own tool (wbadmin, hdiutil, dd), started with your admin permission. Miu shows the
/// exact command first, never runs it on a schedule, and logs the result in Activity.
/// </summary>
public sealed class DiskImageService(AppServices services, ElevatedRunner? runner = null, Func<DateTime>? nowLocal = null)
{
    private readonly ElevatedRunner _run = runner ?? RunElevatedAsync;
    private readonly Func<DateTime> _now = nowLocal ?? (() => DateTime.Now);

    public static ImageOs CurrentOs => OperatingSystem.IsWindows() ? ImageOs.Windows : OperatingSystem.IsMacOS() ? ImageOs.MacOS : ImageOs.Linux;

    /// <summary>Drives that can be imaged or hold an image (connected, with a size).</summary>
    public static IReadOnlyList<string> Drives() => DriveInfo.GetDrives()
        .Where(d => { try { return d.IsReady && d.TotalSize > 0 && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network; } catch (IOException) { return false; } })
        .Select(d => d.RootDirectory.FullName)
        .Where(r => OperatingSystem.IsWindows() || r == "/" || r.StartsWith("/media", StringComparison.Ordinal) || r.StartsWith("/mnt", StringComparison.Ordinal)
                    || r.StartsWith("/run/media", StringComparison.Ordinal) || r.StartsWith("/Volumes", StringComparison.Ordinal) || r.StartsWith("/home", StringComparison.Ordinal))
        .ToList();

    /// <param name="sourceDevice">Linux/macOS: the device behind the source (found with findmnt/df when null).</param>
    public async Task<DiskImagePreview> PreviewAsync(string source, string target, string? sourceDevice = null,
        Func<string, (long Used, long Free, string Label, string Root)?>? driveInfo = null, ImageOs? os = null)
    {
        var o = os ?? CurrentOs;
        driveInfo ??= Info;
        var problem = DiskImage.Validate(o, source, target, p => driveInfo(p)?.Root);
        if (problem is not null) return new DiskImagePreview(null, problem, 0, source, target);
        var src = driveInfo(source)!.Value;
        var dst = driveInfo(target)!.Value;
        if (dst.Free < src.Used + (512L << 20))
            return new DiskImagePreview(null, $"Not enough space: the image needs about {SizeFormatter.Format(src.Used)}, and {target} has {SizeFormatter.Format(dst.Free)} free.", src.Used, source, target);
        DiskImageCommand cmd;
        switch (o)
        {
            case ImageOs.Windows:
                var system = string.Equals(src.Root.TrimEnd('\\'), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                cmd = DiskImage.Windows(src.Root, dst.Root, system);
                break;
            case ImageOs.MacOS:
                if (source.TrimEnd('/') is "" or "/System/Volumes/Data")
                    return new DiskImagePreview(null, "Your startup disk can't be imaged while macOS runs on it. Use Time Machine (System Settings › General › Time Machine): it restores the whole Mac from macOS Recovery.", src.Used, source, target);
                var dev = sourceDevice ?? await DeviceOfAsync("df", [source]);
                if (dev is null) return new DiskImagePreview(null, "Miu couldn't find the disk behind that drive.", src.Used, source, target);
                cmd = DiskImage.MacOS(dev, target, src.Label, _now());
                break;
            default:
                var ldev = sourceDevice ?? await DeviceOfAsync("findmnt", ["-no", "SOURCE", "--target", source]);
                if (ldev is null || !ldev.StartsWith("/dev/", StringComparison.Ordinal))
                    return new DiskImagePreview(null, "Miu couldn't find the disk behind that drive.", src.Used, source, target);
                cmd = DiskImage.Linux(ldev, target, src.Label, _now());
                break;
        }
        return new DiskImagePreview(cmd, null, src.Used, source, target);
    }

    public async Task<DiskImageOutcome> RunAsync(DiskImagePreview preview, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (preview.Command is not { } cmd) return new DiskImageOutcome(false, preview.Problem ?? "Nothing to run.", "");
        int exit;
        string output;
        try { (exit, output) = await _run(cmd, progress, ct); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new DiskImageOutcome(false, "Cancelled: Windows asked for admin permission and it wasn't given.", "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new DiskImageOutcome(false, $"{cmd.Program} couldn't start: {ex.Message}", "");
        }
        var ok = exit == 0;
        var outcome = ok
            ? new DiskImageOutcome(true, $"Disk image saved to {cmd.ImagePath}. To restore: {cmd.RestoreHow}", output)
            : exit is 126 or 127 && cmd.Program == "pkexec"
                ? new DiskImageOutcome(false, "Cancelled, or admin permission wasn't given.", output)
                : new DiskImageOutcome(false, $"{cmd.Program} stopped with an error (code {exit}). " + LastLine(output), output);
        services.Actions.Add(ok ? "backup" : "failed",
            [new ActionItem(preview.Source, ok ? cmd.ImagePath : outcome.Message, ok ? preview.NeededBytes : 0)], "disk-image");
        return outcome;
    }

    private static string LastLine(string output) =>
        output.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    private static (long Used, long Free, string Label, string Root)? Info(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } up) full = up;
            var d = new DriveInfo(full);
            if (!d.IsReady) return null;
            var label = OperatingSystem.IsWindows() ? (string.IsNullOrEmpty(d.VolumeLabel) ? d.Name.TrimEnd('\\') : d.VolumeLabel) : Path.GetFileName(d.Name.TrimEnd('/')) is { Length: > 0 } n ? n : "system";
            return (d.TotalSize - d.TotalFreeSpace, d.AvailableFreeSpace, label, d.RootDirectory.FullName);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<string?> DeviceOfAsync(string program, string[] args)
    {
        try
        {
            var r = await new Miu.Dev.Docker.ProcessRunner(TimeSpan.FromSeconds(10)).RunAsync(program, args, default);
            if (r.ExitCode != 0) return null;
            // findmnt prints the device; df prints a header, then "/dev/disk4s1 ..." .
            return r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("/dev/", StringComparison.Ordinal))
                .Select(l => l.Split(' ', 2)[0]).FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Starts the command with admin rights: Windows through the UAC prompt (output goes to a log file Miu follows),
    /// Linux through pkexec, macOS through the system's administrator password dialog.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunElevatedAsync(DiskImageCommand cmd, IProgress<string>? progress, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var log = Path.Combine(Path.GetTempPath(), $"miu-image-{Guid.NewGuid():N}.log");
            var line = string.Join(' ', cmd.Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd.Program} {line} > \"{log}\" 2>&1")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows didn't start it.");
            var wait = p.WaitForExitAsync(ct);
            while (!wait.IsCompleted)
            {
                await Task.WhenAny(wait, Task.Delay(1000, ct));
                if (Tail(log) is { Length: > 0 } last) progress?.Report(last);
            }
            await wait;
            var text = ReadShared(log);
            try { File.Delete(log); } catch (IOException) { }
            return (p.ExitCode, text);
        }
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("osascript", ["-e", $"do shell script \"{AppleEscape(cmd.Program + " " + string.Join(' ', cmd.Arguments.Select(DiskImage.Quote)))}\" with administrator privileges"])
            : new ProcessStartInfo(cmd.Program, cmd.Arguments);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var proc = Process.Start(start) ?? throw new InvalidOperationException("It didn't start.");
        var all = new StringBuilder();
        async Task Pump(StreamReader r)
        {
            var buf = new char[512];
            var current = new StringBuilder();
            int n;
            while ((n = await r.ReadAsync(buf, ct)) > 0)
                for (var i = 0; i < n; i++)
                {
                    var c = buf[i];
                    lock (all) all.Append(c);
                    if (c is '\r' or '\n') // dd rewrites its progress line with \r
                    {
                        if (current.Length > 0) progress?.Report(current.ToString());
                        current.Clear();
                    }
                    else current.Append(c);
                }
        }
        await Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError), proc.WaitForExitAsync(ct));
        return (proc.ExitCode, all.ToString());
    }

    private static string AppleEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ReadShared(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string? Tail(string path) =>
        ReadShared(path).Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
