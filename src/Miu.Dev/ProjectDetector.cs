using Miu.Core.Scanning;

namespace Miu.Dev;

/// <summary>Finds code projects in a scan by their marker files and measures how much is rebuildable.</summary>
public static class ProjectDetector
{
    private static readonly (string Pattern, string Stack)[] Markers =
    [
        ("package.json", "Node"), ("pnpm-lock.yaml", "Node"), ("bun.lockb", "Node"),
        ("Cargo.toml", "Rust"),
        ("pyproject.toml", "Python"), ("requirements.txt", "Python"), ("setup.py", "Python"), ("Pipfile", "Python"),
        ("go.mod", "Go"),
        (".csproj", ".NET"), (".fsproj", ".NET"), (".sln", ".NET"), (".slnx", ".NET"),
        ("pom.xml", "JVM"), ("build.gradle", "JVM"), ("build.gradle.kts", "JVM"),
        ("Gemfile", "Ruby"), ("composer.json", "PHP"), ("pubspec.yaml", "Flutter"),
        ("Dockerfile", "Docker"), ("compose.yaml", "Docker"), ("compose.yml", "Docker"),
        ("docker-compose.yml", "Docker"), ("docker-compose.yaml", "Docker"),
    ];

    public static IdleBucket BucketFor(int idleDays) => idleDays switch
    {
        < 14 => IdleBucket.Active,
        <= 90 => IdleBucket.Cooling,
        <= 365 => IdleBucket.Idle,
        _ => IdleBucket.Abandoned,
    };

    /// <summary>Lockfiles prove a project was installed/built by a developer (installed apps rarely ship them).</summary>
    private static readonly HashSet<string> Lockfiles = new(StringComparer.Ordinal)
    {
        "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock", "Cargo.lock", "poetry.lock",
        "uv.lock", "Pipfile.lock", "go.sum", "composer.lock", "Gemfile.lock", "pubspec.lock", "packages.lock.json", "gradle.lockfile",
    };

    /// <summary>Folders that are never walked: installed packages and virtual environments.</summary>
    private static readonly HashSet<string> AlwaysSkip = new(StringComparer.Ordinal)
        { ".git", "node_modules", ".venv", "venv", "site-packages", "dist-packages", "__pycache__" };

    /// <summary>Where installed apps, extensions and packages live. Nothing below these is a user project.</summary>
    private static readonly string[] InstallRootFragments =
    [
        "/appdata/local/programs/", "/.vscode/extensions/", "/.vscode-insiders/extensions/", "/.cursor/extensions/",
        "/.windsurf/extensions/", "/site-packages/", "/dist-packages/", "/application support/", "/program files/",
        "/program files (x86)/", "/windowsapps/",
    ];

    private static readonly string[] InstallRootPrefixes = ["/opt/", "/usr/", "/snap/"];

    public static bool IsInstallRoot(string fullPath)
    {
        var p = fullPath.Replace('\\', '/').ToLowerInvariant().TrimEnd('/') + "/";
        return InstallRootFragments.Any(p.Contains) || InstallRootPrefixes.Any(p.StartsWith);
    }

    public static IReadOnlyList<DetectedProject> Detect(ScanTree tree, DateTime nowUtc)
    {
        var found = new List<DetectedProject>();
        if (IsInstallRoot(tree.RootPath)) return found;
        var stack = new Stack<(int Dir, bool InsideRepo)>();
        stack.Push((tree.Root, false));
        while (stack.Count > 0)
        {
            var (dir, insideRepo) = stack.Pop();
            var children = tree.Children(dir);
            var stacks = StacksOf(tree, children);
            var isProject = stacks.Count > 0 && (insideRepo || HasSourceSignal(tree, children, stacks));
            if (isProject) found.Add(Describe(tree, dir, children, stacks, nowUtc));

            foreach (var c in children)
            {
                if (!tree.IsDir(c)) continue;
                var name = tree.Name(c);
                if (AlwaysSkip.Contains(name) || IsVirtualEnv(tree, c)) continue;
                if (isProject && DevJunkCatalog.IsJunkFolder(name, stacks)) continue;
                if (IsInstallRoot(tree.GetFullPath(c))) continue;
                stack.Push((c, insideRepo || isProject));
            }
        }
        return found.OrderByDescending(p => p.RebuildableBytes).ThenBy(p => p.Path, StringComparer.Ordinal).ToList();
    }

    private static bool HasSourceSignal(ScanTree tree, IReadOnlyList<int> children, List<string> stacks)
    {
        foreach (var c in children)
        {
            var name = tree.Name(c);
            if (tree.IsDir(c) ? name == ".git" : Lockfiles.Contains(name)) return true;
        }
        // .NET restores leave obj/project.assets.json behind.
        return stacks.Contains(".NET") && children.Any(c => tree.IsDir(c) && tree.Name(c) == "obj"
            && tree.FindChild(c, "project.assets.json") is not null);
    }

    private static bool IsVirtualEnv(ScanTree tree, int dir) => tree.FindChild(dir, "pyvenv.cfg") is { } f && !tree.IsDir(f);

    private static List<string> StacksOf(ScanTree tree, IReadOnlyList<int> children)
    {
        var result = new List<string>();
        foreach (var c in children)
        {
            var name = tree.Name(c);
            if (tree.IsDir(c))
            {
                if (name == ".git") Add("Git");
                continue;
            }
            foreach (var (pattern, s) in Markers)
                if (pattern.StartsWith('.') ? name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) : name == pattern)
                    Add(s);
        }
        return result;

        void Add(string s) { if (!result.Contains(s)) result.Add(s); }
    }

    private static DetectedProject Describe(ScanTree tree, int dir, IReadOnlyList<int> children, List<string> stacks, DateTime now)
    {
        long latest = 0, rebuildable = 0;
        foreach (var c in children)
        {
            var name = tree.Name(c);
            if (tree.IsDir(c) && DevJunkCatalog.IsJunkFolder(name, stacks)) { rebuildable += tree.Size(c); continue; }
            if (tree.IsDir(c) && (AlwaysSkip.Contains(name) || IsVirtualEnv(tree, c))) continue;
            latest = Math.Max(latest, tree.NewestModifiedUtc(c).Ticks);
        }

        if (tree.FindChild(dir, ".git") is { } git && tree.FindChild(git, "logs") is { } logs && tree.FindChild(logs, "HEAD") is { } head)
            latest = Math.Max(latest, tree.ModifiedUtc(head).Ticks);
        if (latest == 0) latest = tree.ModifiedUtc(dir).Ticks;

        var last = new DateTime(latest, DateTimeKind.Utc);
        var idle = Math.Max(0, (int)Math.Floor((now - last).TotalDays));
        return new DetectedProject(dir, tree.GetFullPath(dir), tree.Name(dir) is var n && dir == tree.Root ? Path.GetFileName(n.TrimEnd('/', '\\')) : n,
            stacks, last, idle, BucketFor(idle), tree.Size(dir), rebuildable);
    }
}
