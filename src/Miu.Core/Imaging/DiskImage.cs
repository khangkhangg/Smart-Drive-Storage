namespace Miu.Core.Imaging;

public enum ImageOs { Windows, MacOS, Linux }

/// <summary>
/// One run of the operating system's own imaging tool, shown to you before it starts. Miu never writes to a disk
/// itself: it asks the OS (with your admin permission) to make the image, and restoring always uses the OS's own
/// recovery tools.
/// </summary>
/// <param name="Program">The program to start elevated.</param>
/// <param name="Arguments">Its arguments, one per item (no shell quoting needed).</param>
/// <param name="Display">The same command as you'd type it, to show and copy.</param>
/// <param name="ImagePath">Where the image ends up.</param>
/// <param name="RestoreHow">How to restore from it, in plain words.</param>
public sealed record DiskImageCommand(string Program, IReadOnlyList<string> Arguments, string Display, string ImagePath, string RestoreHow);

public enum ImageFrequency { Weekly, Monthly }

/// <summary>A disk image made on a schedule by the system's own scheduler (Task Scheduler on Windows, a systemd timer on Linux).</summary>
/// <param name="Id">Short and safe ("a1b2c3d4"): it names the scheduled task.</param>
/// <param name="Device">Linux: the device behind the source (/dev/sdb1).</param>
/// <param name="LastSeenRunUtc">The last run Miu already recorded in Activity.</param>
public sealed record DiskImageSchedule(string Id, string Source, string Target, ImageFrequency Frequency, DayOfWeek Day, int DayOfMonth, int Hour,
    bool System = false, string? Device = null, string? Label = null, DateTime? LastSeenRunUtc = null)
{
    public string Describe() => (Frequency == ImageFrequency.Weekly ? $"Every {Day}" : $"Every month on day {DayOfMonth}") + $" at {Hour:00}:00";
}

public static class DiskImage
{
    /// <summary>Why this image can't be made, or null. Checks only what doesn't need the disks (see the service for space).</summary>
    public static string? Validate(ImageOs os, string source, string target, Func<string, string?> rootOf)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return "Choose the drive to image and where to save it.";
        var sr = rootOf(source);
        var tr = rootOf(target);
        if (sr is null || tr is null) return "Miu can't find one of those drives. Is it connected?";
        if (string.Equals(sr, tr, os == ImageOs.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            return "Save the image on a different drive: an image on the same drive is lost with it.";
        return null;
    }

    /// <summary>
    /// Why Windows can't save a system image there, or null. wbadmin needs an NTFS or ReFS drive, or a network folder:
    /// exFAT and FAT32 (how most external drives and USB sticks come) are refused by Windows itself.
    /// </summary>
    public static string? WindowsTargetProblem(string target, string? format)
    {
        if (IsNetwork(target) || format is null) return null;
        if (format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || format.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) return null;
        var drive = target.TrimEnd('\\', '/');
        return $"{drive} is formatted as {format}, and Windows can only save a system image on an NTFS or ReFS drive, or a network folder. "
            + $@"Choose another drive, type a network folder (for example \\nas\backups), or reformat {drive} as NTFS in File Explorer "
            + "(right-click › Format; this erases everything on it, so copy its files somewhere else first).";
    }

    public static bool IsNetwork(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    // ---------- Schedules ----------

    public static string WindowsTaskName(string id) => $@"Miu\Disk image {id}";
    public static string LinuxUnit(string id) => $"miu-disk-image-{id}";

    /// <summary>Ids, drives and paths that are safe to put in a scheduled command (no quotes, % or $), and sane times.</summary>
    public static string? ScheduleProblem(DiskImageSchedule s)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.Id, "^[a-z0-9]{4,16}$")) return "Bad schedule id.";
        foreach (var v in new[] { s.Source, s.Target, s.Device ?? "", s.Label ?? "" })
            if (v.IndexOfAny(['"', '%', '$', '`', '\n', '\r', '\'']) >= 0) return "Paths with quotes, %, $ or ` can't be scheduled. Pick another folder.";
        if (s.Hour is < 0 or > 23 || s.DayOfMonth is < 1 or > 28) return "Pick an hour from 0 to 23 and a day of the month from 1 to 28.";
        return null;
    }

    /// <summary>
    /// Windows: a Task Scheduler task that runs wbadmin as SYSTEM, so no password is stored and nothing is asked at run
    /// time. Creating it needs one admin prompt.
    /// </summary>
    public static DiskImageCommand WindowsScheduleCreate(DiskImageSchedule s)
    {
        var image = Windows(s.Source, s.Target, s.System);
        // /TR is one string; an argument with spaces is quoted with \" (schtasks reads C-style escapes).
        var tr = "wbadmin " + string.Join(' ', image.Arguments.Select(a => a.Contains(' ') ? "\\\"" + a + "\\\"" : a));
        string[] when = s.Frequency == ImageFrequency.Weekly
            ? ["/SC", "WEEKLY", "/D", s.Day.ToString()[..3].ToUpperInvariant()]
            : ["/SC", "MONTHLY", "/D", s.DayOfMonth.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        string[] args = ["/Create", "/F", "/TN", WindowsTaskName(s.Id), .. when, "/ST", $"{s.Hour:00}:00", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", tr];
        return new DiskImageCommand("schtasks", args, "schtasks " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)), image.ImagePath, image.RestoreHow);
    }

    public static DiskImageCommand WindowsScheduleDelete(string id)
    {
        string[] args = ["/Delete", "/F", "/TN", WindowsTaskName(id)];
        return new DiskImageCommand("schtasks", args, $"schtasks /Delete /F /TN \"{WindowsTaskName(id)}\"", "", "");
    }

    /// <summary>
    /// Linux: a systemd service and timer run as root (Persistent=true: a missed run happens at the next start). Each run
    /// writes a new dated .img.gz next to the older ones.
    /// </summary>
    public static DiskImageCommand LinuxScheduleCreate(DiskImageSchedule s)
    {
        var unit = LinuxUnit(s.Id);
        var folder = s.Target.TrimEnd('/');
        var name = Safe(s.Label ?? "disk");
        // systemd turns %% into % and $$ into $. sh -c gets one double-quoted argument; paths are single-quoted inside it.
        var exec = $"/bin/sh -c \"dd if='{s.Device}' bs=4M conv=noerror,sync status=none | gzip -1 > '{folder}/{name}-'$$(date +%%Y-%%m-%%d-%%H%%M)'.img.gz'\"";
        var calendar = s.Frequency == ImageFrequency.Weekly
            ? $"{s.Day.ToString()[..3]} *-*-* {s.Hour:00}:00:00"
            : $"*-*-{s.DayOfMonth:00} {s.Hour:00}:00:00";
        var script = string.Join('\n',
            "set -e",
            $"cat > /etc/systemd/system/{unit}.service <<'MIU_EOF'",
            "[Unit]", $"Description=Miu Smart Storage: disk image of {s.Source}",
            "[Service]", "Type=oneshot", $"ExecStart={exec}",
            "MIU_EOF",
            $"cat > /etc/systemd/system/{unit}.timer <<'MIU_EOF'",
            "[Unit]", "Description=Miu Smart Storage: disk image schedule",
            "[Timer]", $"OnCalendar={calendar}", "Persistent=true",
            "[Install]", "WantedBy=timers.target",
            "MIU_EOF",
            "systemctl daemon-reload",
            $"systemctl enable --now {unit}.timer");
        return new DiskImageCommand("pkexec", ["sh", "-c", script], $"sudo systemctl enable --now {unit}.timer  (OnCalendar={calendar})",
            $"{folder}/{name}-<date>.img.gz", "Each run adds a new dated image next to the older ones.");
    }

    /// <summary>Stops and removes the timer and its service (only Miu's own unit files, by their fixed names).</summary>
    public static DiskImageCommand LinuxScheduleDelete(string id)
    {
        var unit = LinuxUnit(id);
        var script = $"systemctl disable --now {unit}.timer || true; systemctl reset-failed {unit}.service || true; "
            + $"unlink /etc/systemd/system/{unit}.service || true; unlink /etc/systemd/system/{unit}.timer || true; systemctl daemon-reload";
        return new DiskImageCommand("pkexec", ["sh", "-c", script], $"sudo systemctl disable --now {unit}.timer", "", "");
    }

    /// <summary>
    /// Windows: a system image with wbadmin (the tool behind "Backup and Restore (Windows 7)"). It includes everything
    /// Windows needs to start (-allCritical) and is restored from Windows Recovery › System Image Recovery.
    /// </summary>
    public static DiskImageCommand Windows(string sourceDrive, string targetDrive, bool system)
    {
        var src = sourceDrive.TrimEnd('\\', '/');
        var dst = targetDrive.TrimEnd('\\', '/');
        var args = new List<string> { "start", "backup", $"-backupTarget:{dst}", $"-include:{src}" };
        if (system) args.Add("-allCritical");
        args.Add("-quiet");
        return new DiskImageCommand("wbadmin", args, "wbadmin " + string.Join(' ', args), $@"{dst}\WindowsImageBackup",
            system
                ? "Restart into Windows Recovery (Settings › System › Recovery › Advanced startup › Restart now), then Troubleshoot › Advanced options › System Image Recovery, and pick this image. You can also boot from a Windows USB stick and choose Repair your computer."
                : $"Open Windows Recovery or run: wbadmin start recovery (choose the version from: wbadmin get versions -backupTarget:{dst}).");
    }

    /// <summary>macOS: a compressed, read-only disk image of a whole disk with hdiutil (the startup disk: use Time Machine).</summary>
    public static DiskImageCommand MacOS(string device, string targetFolder, string name, DateTime nowLocal)
    {
        var file = targetFolder.TrimEnd('/') + "/" + $"{Safe(name)} {nowLocal:yyyy-MM-dd HHmm}.dmg"; // Unix paths, also when tested on Windows
        var args = new List<string> { "create", "-srcdevice", device, "-format", "UDZO", file };
        return new DiskImageCommand("hdiutil", args, $"sudo hdiutil create -srcdevice {device} -format UDZO \"{file}\"", file,
            $"Double-click the .dmg to open it and copy files back, or use Disk Utility › Restore to put the whole image on a disk (this erases that disk). For your startup disk, use Time Machine and restore from macOS Recovery.");
    }

    /// <summary>Linux: a compressed raw image of a partition or disk with dd and gzip, started through pkexec.</summary>
    public static DiskImageCommand Linux(string device, string targetFolder, string name, DateTime nowLocal)
    {
        var file = targetFolder.TrimEnd('/') + "/" + $"{Safe(name)}-{nowLocal:yyyy-MM-dd-HHmm}.img.gz";
        var script = $"dd if={Quote(device)} bs=4M status=progress conv=noerror,sync | gzip -1 > {Quote(file)}";
        return new DiskImageCommand("pkexec", ["sh", "-c", script], "sudo sh -c '" + script + "'", file,
            $"Unmount the target first, then: gunzip -c {Quote(file)} | sudo dd of={Quote(device)} bs=4M status=progress (this overwrites that disk). For a running system disk, boot from a live USB to restore.");
    }

    /// <summary>A name that is safe in a file name (drive labels can contain anything).</summary>
    public static string Safe(string name)
    {
        var invalid = new char[] { '\0' }.Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\'']).ToHashSet();
        var s = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.', '-');
        return s.Length == 0 ? "disk" : s;
    }

    /// <summary>Single-quotes for sh (a ' inside becomes '\'').</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
