namespace Miu.Core.Rules;

public enum ProtectionKind { None, InsideProtectedTree, ProtectedExact, AncestorOfProtected }

/// <summary>
/// The last line of defence: decides whether a path may never be deleted. A path is protected when it
/// is inside a protected tree, equals a protected exact path, or is an ancestor of either (deleting it
/// would delete them). No rule can override this.
/// </summary>
public sealed class ProtectedPathGuard
{
    private readonly string[] _trees;
    private readonly string[] _exact;
    private readonly PathStyle _style;
    private readonly StringComparison _cmp;
    private readonly char _sep;

    public ProtectedPathGuard(IEnumerable<string> protectedTrees, IEnumerable<string> protectedExact, PathStyle style, bool? caseInsensitive = null)
    {
        _style = style;
        _sep = PathNormalizer.Separator(style);
        _cmp = (caseInsensitive ?? style == PathStyle.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _trees = protectedTrees.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => PathNormalizer.Normalize(p, style)).Distinct().ToArray();
        _exact = protectedExact.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => PathNormalizer.Normalize(p, style)).Distinct().ToArray();
    }

    public PathStyle Style => _style;
    public bool CaseInsensitive => _cmp == StringComparison.OrdinalIgnoreCase;

    public bool IsProtected(string path) => Explain(path) is not null;

    /// <summary>Why a path is protected (by its normalized form, without following links).</summary>
    public ProtectionKind Classify(string path)
    {
        if (PathNormalizer.IsAmbiguous(path, _style)) return ProtectionKind.InsideProtectedTree; // fail closed
        if (_style == PathStyle.Windows && PathNormalizer.TryExpandShortName(path) is { } longPath) path = longPath;
        var n = PathNormalizer.Normalize(path, _style);
        foreach (var t in _trees)
            if (n.Equals(t, _cmp) || n.StartsWith(WithSep(t), _cmp)) return ProtectionKind.InsideProtectedTree;
        foreach (var e in _exact)
            if (n.Equals(e, _cmp)) return ProtectionKind.ProtectedExact;
        var prefix = WithSep(n);
        foreach (var t in _trees.Concat(_exact))
            if (t.StartsWith(prefix, _cmp)) return ProtectionKind.AncestorOfProtected;
        return ProtectionKind.None;
    }

    /// <summary>A plain-language reason the path is protected, or null when it is not.</summary>
    /// <param name="checkLink">Also resolve the item if it is a symlink/junction. Costs file-system calls; pass false
    /// when the caller already knows the item is not a link.</param>
    public string? Explain(string path, bool checkLink = true)
    {
        var reason = ExplainNormalized(path);
        if (reason is not null) return reason;

        // Follow links (only meaningful for paths on this machine's own path style).
        if (!checkLink || _style != PathNormalizer.Current) return null;
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && ExplainNormalized(target.FullName) is not null)
                    return "This is a link into a protected system location.";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private string? ExplainNormalized(string path) => Classify(path) switch
    {
        ProtectionKind.InsideProtectedTree => "Part of the operating system or installed programs. Remove apps through Settings instead.",
        ProtectionKind.ProtectedExact => "A drive, users folder or your profile folder itself can't be deleted.",
        ProtectionKind.AncestorOfProtected => "Contains protected system or profile folders.",
        _ => null,
    };

    private string WithSep(string p) => p.EndsWith(_sep) ? p : p + _sep;

    public static ProtectedPathGuard ForWindows(string systemDrive, string windir, string programFiles, string programFilesX86,
        string programData, string usersRoot, string userProfile, IEnumerable<string>? extraDriveRoots = null, string? localAppData = null)
    {
        static string J(string a, string b) => a.TrimEnd('\\', '/') + @"\" + b;
        var trees = new List<string>
        {
            windir, programFiles, programFilesX86, programData,
            J(systemDrive, "Windows.old"), J(systemDrive, "$WINDOWS.~BT"), J(systemDrive, "$WINDOWS.~WS"), J(systemDrive, "$WinREAgent"),
            J(systemDrive, "Recovery"), J(systemDrive, "Boot"), J(systemDrive, "bootmgr"),
            J(systemDrive, "pagefile.sys"), J(systemDrive, "hiberfil.sys"), J(systemDrive, "swapfile.sys"),
        };
        var exact = new List<string> { systemDrive, usersRoot, userProfile };
        if (localAppData is not null) trees.Add(J(localAppData, "Programs"));
        foreach (var drive in new[] { systemDrive }.Concat(extraDriveRoots ?? []))
        {
            exact.Add(drive);
            trees.AddRange([J(drive, "Program Files"), J(drive, "Program Files (x86)"), J(drive, "WindowsApps"),
                J(drive, "System Volume Information"), J(drive, "$Recycle.Bin")]);
        }
        return new ProtectedPathGuard(trees, exact, PathStyle.Windows);
    }

    public static ProtectedPathGuard ForUnix(string home, bool caseInsensitive = false) => new(
        ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/sbin", "/sys", "/usr", "/var",
         "/run", "/snap", "/System", "/Library", "/Applications", "/private", "/nix", "/srv", "/opt", "/cores"],
        ["/", "/home", "/Users", "/root", "/mnt", "/media", "/Volumes", home],
        PathStyle.Unix, caseInsensitive);

    public static ProtectedPathGuard ForCurrentOs()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return ForWindows(
                Path.GetPathRoot(windir) ?? @"C:\", windir,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.GetDirectoryName(profile) ?? @"C:\Users", profile,
                DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        return ForUnix(profile, caseInsensitive: OperatingSystem.IsMacOS());
    }
}
