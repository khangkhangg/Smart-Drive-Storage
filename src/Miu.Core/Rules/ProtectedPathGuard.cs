namespace Miu.Core.Rules;

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

    /// <summary>A plain-language reason the path is protected, or null when it is not.</summary>
    public string? Explain(string path)
    {
        var reason = ExplainNormalized(PathNormalizer.Normalize(path, _style));
        if (reason is not null) return reason;

        // Follow links (only meaningful for paths on this machine's own path style).
        if (_style != PathNormalizer.Current) return null;
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && ExplainNormalized(PathNormalizer.Normalize(target.FullName, _style)) is not null)
                    return "This is a link into a protected system location.";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private string? ExplainNormalized(string n)
    {
        foreach (var t in _trees)
            if (n.Equals(t, _cmp) || n.StartsWith(WithSep(t), _cmp))
                return "Part of the operating system or installed programs. Remove apps through Settings instead.";
        foreach (var e in _exact)
            if (n.Equals(e, _cmp))
                return "A drive, users folder or your profile folder itself can't be deleted.";
        var prefix = WithSep(n);
        foreach (var t in _trees.Concat(_exact))
            if (t.StartsWith(prefix, _cmp))
                return "Contains protected system or profile folders.";
        return null;
    }

    private string WithSep(string p) => p.EndsWith(_sep) ? p : p + _sep;

    public static ProtectedPathGuard ForWindows(string systemDrive, string windir, string programFiles, string programFilesX86,
        string programData, string usersRoot, string userProfile, IEnumerable<string>? extraDriveRoots = null)
    {
        string J(string a, string b) => a.TrimEnd('\\', '/') + @"\" + b;
        var trees = new[]
        {
            windir, programFiles, programFilesX86, J(programData, "Microsoft"),
            J(systemDrive, "System Volume Information"), J(systemDrive, "$Recycle.Bin"), J(systemDrive, "Recovery"),
            J(systemDrive, "pagefile.sys"), J(systemDrive, "hiberfil.sys"), J(systemDrive, "swapfile.sys"),
            J(systemDrive, "Boot"), J(systemDrive, "bootmgr"),
        };
        var exact = new List<string> { systemDrive, usersRoot, userProfile };
        if (extraDriveRoots is not null) exact.AddRange(extraDriveRoots);
        return new ProtectedPathGuard(trees, exact, PathStyle.Windows);
    }

    public static ProtectedPathGuard ForUnix(string home, bool caseInsensitive = false) => new(
        ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/sbin", "/sys", "/usr", "/var/lib",
         "/run", "/snap", "/System", "/Library", "/Applications", "/private/var/db"],
        ["/", "/home", "/Users", "/root", "/var", "/opt", "/mnt", "/media", "/Volumes", home],
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
                DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName));
        }
        return ForUnix(profile, caseInsensitive: OperatingSystem.IsMacOS());
    }
}
