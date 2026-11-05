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

    public static IReadOnlyList<DetectedProject> Detect(ScanTree tree, DateTime nowUtc)
    {
        var found = new List<DetectedProject>();
        var stack = new Stack<int>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var children = tree.Children(dir);
            var stacks = StacksOf(tree, children);
            if (stacks.Count > 0) found.Add(Describe(tree, dir, children, stacks, nowUtc));

            foreach (var c in children)
            {
                if (!tree.IsDir(c)) continue;
                var name = tree.Name(c);
                if (name == ".git" || DevJunkCatalog.AllJunkNames.Contains(name)) continue;
                stack.Push(c);
            }
        }
        return found.OrderByDescending(p => p.RebuildableBytes).ThenBy(p => p.Path, StringComparer.Ordinal).ToList();
    }

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
            if (tree.IsDir(c) && (name == ".git" || DevJunkCatalog.AllJunkNames.Contains(name))) continue;
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
