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
