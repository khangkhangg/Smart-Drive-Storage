using System.Diagnostics;
using System.Text;
using Miu.Core.Formatting;
using Miu.Core.Imaging;
using Miu.Data;

namespace Miu.App.Services;

/// <param name="Command">What will run (null when <paramref name="Problem"/> says why it can't).</param>
/// <param name="NeededBytes">About how much space the image needs (the source's used space).</param>
/// <param name="Device">Linux/macOS: the disk behind the source. <param name="Label">The source drive's name.</param></param>
public sealed record DiskImagePreview(DiskImageCommand? Command, string? Problem, long NeededBytes, string Source, string Target,
    string? Device = null, string? Label = null, bool IsSystem = false);

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
        Func<string, (long Used, long Free, string Label, string Root, string Format)?>? driveInfo = null, ImageOs? os = null)
    {
        var o = os ?? CurrentOs;
        driveInfo ??= Info;
        // Windows can also save to a network folder (\\nas\backups); Miu can't measure its free space first.
        var network = o == ImageOs.Windows && DiskImage.IsNetwork(target);
        var problem = DiskImage.Validate(o, source, target, p => network && p == target ? target.TrimEnd('\\') : driveInfo(p)?.Root);
        if (problem is not null) return new DiskImagePreview(null, problem, 0, source, target);
        var src = driveInfo(source)!.Value;
        var dst = network ? (Used: 0L, Free: long.MaxValue, Label: "", Root: target.TrimEnd('\\'), Format: "network") : driveInfo(target)!.Value;
        if (o == ImageOs.Windows && DiskImage.WindowsTargetProblem(target, network ? null : dst.Format) is { } fs)
            return new DiskImagePreview(null, fs, src.Used, source, target);
        if (dst.Free < src.Used + (512L << 20))
            return new DiskImagePreview(null, $"Not enough space: the image needs about {SizeFormatter.Format(src.Used)}, and {target} has {SizeFormatter.Format(dst.Free)} free.", src.Used, source, target);
        DiskImageCommand cmd;
        string? device = null;
        var isSystem = false;
        switch (o)
        {
            case ImageOs.Windows:
                var system = string.Equals(src.Root.TrimEnd('\\'), Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                cmd = DiskImage.Windows(src.Root, dst.Root, system);
                isSystem = system;
                break;
            case ImageOs.MacOS:
                if (source.TrimEnd('/') is "" or "/System/Volumes/Data")
                    return new DiskImagePreview(null, "Your startup disk can't be imaged while macOS runs on it. Use Time Machine (System Settings › General › Time Machine): it restores the whole Mac from macOS Recovery.", src.Used, source, target);
                var dev = sourceDevice ?? await DeviceOfAsync("df", [source]);
                if (dev is null) return new DiskImagePreview(null, "Miu couldn't find the disk behind that drive.", src.Used, source, target);
                cmd = DiskImage.MacOS(dev, target, src.Label, _now());
                device = dev;
                break;
            default:
                var ldev = sourceDevice ?? await DeviceOfAsync("findmnt", ["-no", "SOURCE", "--target", source]);
                if (ldev is null || !ldev.StartsWith("/dev/", StringComparison.Ordinal))
                    return new DiskImagePreview(null, "Miu couldn't find the disk behind that drive.", src.Used, source, target);
                cmd = DiskImage.Linux(ldev, target, src.Label, _now());
                device = ldev;
                break;
        }
        return new DiskImagePreview(cmd, null, src.Used, source, target, device, src.Label, isSystem);
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
                : new DiskImageOutcome(false, $"{cmd.Program} stopped with an error (code {exit}): " + Explain(output), output);
        services.Actions.Add(ok ? "backup" : "failed",
            [new ActionItem(preview.Source, ok ? cmd.ImagePath : outcome.Message, ok ? preview.NeededBytes : 0)], "disk-image");
        return outcome;
    }

    // ---------- Schedules ----------

    public const string SchedulesKey = "diskimage.schedules";

    /// <summary>Runs a normal (not elevated) command to read a schedule's status. Swapped in tests.</summary>
    public Func<string, string[], Task<(int ExitCode, string Output)>> Query { get; set; } = async (program, args) =>
    {
        var r = await new Miu.Dev.Docker.ProcessRunner(TimeSpan.FromSeconds(20)).RunAsync(program, args, default);
        return (r.ExitCode, r.Output);
    };

    public IReadOnlyList<DiskImageSchedule> Schedules()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<DiskImageSchedule>>(services.Settings.Get(SchedulesKey) ?? "[]") ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private void Save(IEnumerable<DiskImageSchedule> list) => services.Settings.Set(SchedulesKey, System.Text.Json.JsonSerializer.Serialize(list.ToList()));

    /// <summary>
    /// Hands a checked image to the system's scheduler (one admin prompt now, none at run time). Returns what happened.
    /// macOS has Time Machine for this; Miu doesn't add its own schedule there.
    /// </summary>
    public async Task<(bool Ok, string Message)> AddScheduleAsync(DiskImagePreview preview, ImageFrequency frequency, DayOfWeek day, int dayOfMonth, int hour, ImageOs? os = null)
    {
        var o = os ?? CurrentOs;
        if (preview.Command is null) return (false, preview.Problem ?? "Check the image first.");
        if (o == ImageOs.MacOS)
            return (false, "On a Mac, use Time Machine for regular backups (System Settings › General › Time Machine): it runs every hour and keeps older versions. Make an image now here when you need one.");
        var schedule = new DiskImageSchedule(Guid.NewGuid().ToString("N")[..8], preview.Source, preview.Target, frequency, day, dayOfMonth, hour,
            preview.IsSystem, preview.Device, preview.Label);
        if (DiskImage.ScheduleProblem(schedule) is { } problem) return (false, problem);
        var cmd = o == ImageOs.Windows ? DiskImage.WindowsScheduleCreate(schedule) : DiskImage.LinuxScheduleCreate(schedule);
        var (ok, message) = await RunAdminAsync(cmd);
        if (!ok) return (false, "The schedule wasn't saved: " + message);
        Save([.. Schedules(), schedule]);
        services.Actions.Add("rule-changed", [new ActionItem($"Disk image of {schedule.Source} scheduled: {schedule.Describe()}", schedule.Id, 0)], "disk-image");
        return (true, $"Scheduled: {schedule.Describe()}. Your system runs it even when Miu is closed; its results show up here and in Activity.");
    }

    public async Task<(bool Ok, string Message)> RemoveScheduleAsync(string id, ImageOs? os = null)
    {
        var o = os ?? CurrentOs;
        if (Schedules().FirstOrDefault(s => s.Id == id) is not { } schedule) return (false, "That schedule is already gone.");
        var cmd = o == ImageOs.Windows ? DiskImage.WindowsScheduleDelete(id) : DiskImage.LinuxScheduleDelete(id);
        var (ok, message) = await RunAdminAsync(cmd);
        // A task someone already deleted in Task Scheduler is gone too: forget it either way when Windows can't find it.
        if (!ok && !message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)) return (false, "The schedule wasn't removed: " + message);
        Save(Schedules().Where(s => s.Id != id));
        services.Actions.Add("rule-changed", [new ActionItem($"Disk image schedule removed ({schedule.Source}, {schedule.Describe()})", id, 0)], "disk-image");
        return (true, "Schedule removed. Images already made are kept.");
    }

    private async Task<(bool Ok, string Message)> RunAdminAsync(DiskImageCommand cmd)
    {
        try
        {
            var (exit, output) = await _run(cmd, null, default);
            return exit == 0 ? (true, "") : exit is 126 or 127 && cmd.Program == "pkexec" ? (false, "admin permission wasn't given.") : (false, Explain(output));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, "admin permission wasn't given."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return (false, ex.Message); }
    }

    /// <param name="LastRunUtc">Null when it hasn't run yet.</param>
    public sealed record ScheduleStatus(DateTime? NextRunLocal, DateTime? LastRunUtc, bool? LastOk, string Text);

    /// <summary>
    /// Asks the system scheduler how each schedule is doing, and records runs Miu hasn't seen yet in Activity.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ScheduleStatus>> StatusAsync(ImageOs? os = null)
    {
        var o = os ?? CurrentOs;
        var result = new Dictionary<string, ScheduleStatus>();
        var list = Schedules().ToList();
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            ScheduleStatus st;
            try { st = o == ImageOs.Windows ? await WindowsStatusAsync(s.Id) : await LinuxStatusAsync(s.Id); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or FormatException or System.ComponentModel.Win32Exception)
            { st = new ScheduleStatus(null, null, null, "Couldn't read its status: " + ex.Message); }
            result[s.Id] = st;
            if (st.LastRunUtc is { } last && (s.LastSeenRunUtc is null || last > s.LastSeenRunUtc.Value))
            {
                services.Actions.Add(st.LastOk == true ? "backup" : "failed",
                    [new ActionItem(s.Source, st.LastOk == true ? $"Scheduled image saved on {s.Target}" : "Scheduled image failed: " + st.Text, 0)], "disk-image");
                list[i] = s with { LastSeenRunUtc = last };
                changed = true;
            }
        }
        if (changed) Save(list);
        return result;
    }

    private async Task<ScheduleStatus> WindowsStatusAsync(string id)
    {
        var name = DiskImage.WindowsTaskName(id);
        var folder = "\\" + name[..(name.LastIndexOf('\\') + 1)];
        var task = name[(name.LastIndexOf('\\') + 1)..];
        var (exit, json) = await Query("powershell", ["-NoProfile", "-NonInteractive", "-Command",
            $"Get-ScheduledTaskInfo -TaskPath '{folder}' -TaskName '{task}' | Select-Object LastRunTime,LastTaskResult,NextRunTime | ConvertTo-Json -Compress"]);
        if (exit != 0 || json.Trim().Length == 0) return new ScheduleStatus(null, null, null, "Windows has no such task any more (it may have been deleted in Task Scheduler).");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        DateTime? next = PsDate(r, "NextRunTime");
        DateTime? last = PsDate(r, "LastRunTime");
        var code = r.TryGetProperty("LastTaskResult", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt64() : 0;
        // Task Scheduler reports a "last run" in 1999 or 0x41303 ("has not run yet") before the first run.
        if (last is { Year: < 2000 } || code == 0x41303) last = null;
        bool? ok = last is null ? null : code == 0;
        var text = (next is { } n ? $"Next: {n:ddd d MMM, HH:mm}" : "Not scheduled")
            + (last is { } l ? $" · Last: {l.ToLocalTime():ddd d MMM, HH:mm} ({(ok == true ? "done" : $"error 0x{code:X}")})" : " · Hasn't run yet");
        return new ScheduleStatus(next, last, ok, text);
    }

    /// <summary>PowerShell 5 writes dates as "/Date(1700000000000)/"; PowerShell 7 as ISO text.</summary>
    private static DateTime? PsDate(System.Text.Json.JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        var s = v.GetString() ?? "";
        if (s.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var digits = new string(s[6..].TakeWhile(ch => char.IsDigit(ch) || ch == '-').ToArray());
            return long.TryParse(digits, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
        }
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    private async Task<ScheduleStatus> LinuxStatusAsync(string id)
    {
        var unit = DiskImage.LinuxUnit(id);
        var (e1, timer) = await Query("systemctl", ["show", unit + ".timer", "-p", "NextElapseUSecRealtime", "-p", "LastTriggerUSec", "-p", "LoadState"]);
        var (_, service) = await Query("systemctl", ["show", unit + ".service", "-p", "Result", "-p", "ExecMainExitTimestamp"]);
        string Prop(string text, string key) =>
            text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";
        if (e1 != 0 || Prop(timer, "LoadState") == "not-found") return new ScheduleStatus(null, null, null, "The system has no such timer any more.");
        var next = Prop(timer, "NextElapseUSecRealtime");
        var last = Prop(timer, "LastTriggerUSec");
        var res = Prop(service, "Result");
        DateTime? lastUtc = SystemdDate(Prop(service, "ExecMainExitTimestamp"));
        bool? ok = lastUtc is null ? null : res == "success";
        var text = (next.Length > 0 ? "Next: " + next : "Not scheduled")
            + (lastUtc is null ? " · Hasn't run yet" : $" · Last: {last} ({(ok == true ? "done" : res)})");
        return new ScheduleStatus(null, lastUtc, ok, text);
    }

    /// <summary>systemd's "Sun 2026-11-08 03:00:12 +07" (or "n/a").</summary>
    private static DateTime? SystemdDate(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        return DateTime.TryParse(parts[1] + " " + parts[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t)
            ? t.ToUniversalTime() : null;
    }

    /// <summary>
    /// The useful part of a tool's output: from its error line to the end (wbadmin wraps one message over several lines,
    /// so the last line alone reads like "another NTFS/ReFS volume."), at most 4 lines.
    /// </summary>
    public static string Explain(string output)
    {
        var lines = output.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("wbadmin ", StringComparison.OrdinalIgnoreCase)
            && !l.StartsWith("(C) Copyright", StringComparison.OrdinalIgnoreCase) && !l.StartsWith('-')).ToList();
        if (lines.Count == 0) return "no details.";
        var start = lines.FindLastIndex(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase));
        var pick = start >= 0 ? lines.Skip(start).Take(4) : lines.TakeLast(3);
        return string.Join(" ", pick);
    }

    private static (long Used, long Free, string Label, string Root, string Format)? Info(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            while (!Directory.Exists(full) && Path.GetDirectoryName(full) is { } up) full = up;
            var d = new DriveInfo(full);
            if (!d.IsReady) return null;
            var label = OperatingSystem.IsWindows() ? (string.IsNullOrEmpty(d.VolumeLabel) ? d.Name.TrimEnd('\\') : d.VolumeLabel) : Path.GetFileName(d.Name.TrimEnd('/')) is { Length: > 0 } n ? n : "system";
            return (d.TotalSize - d.TotalFreeSpace, d.AvailableFreeSpace, label, d.RootDirectory.FullName, d.DriveFormat);
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
