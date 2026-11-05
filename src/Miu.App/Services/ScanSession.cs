using Miu.Core.Rules;
using Miu.Core.Scanning;
using Miu.Dev;
using Miu.Platform.Abstractions;

namespace Miu.App.Services;

/// <summary>The result of one scan plus everything derived from it: verdicts, projects, safe total.</summary>
public sealed class ScanSession
{
    private RuleEngine _engine;
    private readonly ProtectedPathGuard _guard;
    private readonly bool _ci;
    private Dictionary<int, DetectedProject> _projectsByNode;
    private readonly Dictionary<int, VerdictResult> _verdicts = new();
    private readonly Dictionary<int, long> _safeUnder = new();
    private readonly object _gate = new();

    private ScanSession(ScanTree tree, RuleEngine engine, ProtectedPathGuard guard, bool ci, IReadOnlyList<DetectedProject> projects, DateTime nowUtc)
    {
        Tree = tree;
        _engine = engine;
        _guard = guard;
        _ci = ci;
        Projects = projects;
        NowUtc = nowUtc;
        _projectsByNode = projects.ToDictionary(p => p.NodeId);
    }

    public ScanTree Tree { get; }
    public IReadOnlyList<DetectedProject> Projects { get; private set; }
    public DateTime NowUtc { get; }
    public IReadOnlyList<(int NodeId, VerdictResult Verdict)> SafeItems { get; private set; } = [];
    public long SafeTotalBytes { get; private set; }

    public static async Task<ScanSession> RunAsync(AppServices services, string path, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        // Read the rules here (the database connection is not shared with background threads).
        var rules = services.AllRules();
        var tree = await services.ScannerFor(path).ScanAsync(path, progress, ct);
        return await Task.Run(() =>
        {
            var now = DateTime.UtcNow;
            var engine = new RuleEngine(rules, services.Guard, services.CaseInsensitivePaths);
            var session = new ScanSession(tree, engine, services.Guard, services.CaseInsensitivePaths, ProjectDetector.Detect(tree, now), now);
            session.CollectSafeItems(ct);
            return session;
        }, ct);
    }

    public VerdictResult VerdictFor(int id)
    {
        lock (_gate)
            if (_verdicts.TryGetValue(id, out var cached)) return cached;

        var t = Tree;
        var parent = t.Parent(id);
        var facts = new ItemFacts(
            t.GetFullPath(id), t.Name(id), t.IsDir(id), t.Size(id),
            t.IsDir(id) ? t.NewestModifiedUtc(id) : t.ModifiedUtc(id), NowUtc,
            ProjectIdleDaysFor(id),
            marker => parent >= 0 && t.Children(parent).Any(c => c != id && Glob.IsMatch(marker, t.Name(c), _ci)),
            marker => t.IsDir(id) && t.Children(id).Any(c => Glob.IsMatch(marker, t.Name(c), _ci)),
            () => IsGitIgnored(id),
            t.IsLink(id));
        var result = _engine.Evaluate(facts);
        lock (_gate) _verdicts[id] = result;
        return result;
    }

    /// <summary>
    /// Why an item must not be cleaned now that rules may have changed: it is kept itself, or something inside it is.
    /// Null when it may go.
    /// </summary>
    public string? KeepConflict(string fullPath)
    {
        if (FindNode(fullPath) is not { } id) return null;
        if (VerdictFor(id).Verdict is Verdict.Keep or Verdict.Protected) return "You chose Keep for this.";
        return Tree.IsDir(id) && KeptInside(id) ? "You marked something inside it Keep." : null;
    }

    /// <summary>True when a Keep rule (yours or learned) covers something strictly inside the folder.</summary>
    public bool KeptInside(int folder)
    {
        var keeps = _engine.Rules.Where(r => r.Verdict == Verdict.Keep && r.Source is RuleSource.User or RuleSource.Learned).ToList();
        if (keeps.Count == 0) return false;
        var path = Tree.GetFullPath(folder).Replace('\\', '/').TrimEnd('/') + "/";
        var cmp = _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool InsideFolder(string? p) => p is not null && p.Replace('\\', '/').TrimEnd('/').StartsWith(path, cmp);
        if (keeps.Any(r => InsideFolder(r.Match.Under) || InsideFolder(r.Match.Inside) || InsideFolder(r.Match.ChildOf))) return true;
        // Keep rules by name or type (no fixed place) need a look at what is inside.
        if (!keeps.Any(r => r.Match.Under is null && r.Match.Inside is null && r.Match.ChildOf is null)) return false;
        var stack = new Stack<int>(Tree.Children(folder));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (VerdictFor(id).Verdict == Verdict.Keep) return true;
            if (Tree.IsDir(id)) foreach (var c in Tree.Children(id)) stack.Push(c);
        }
        return false;
    }

    /// <summary>Bytes inside this folder that are safe to remove (0 for files and for the safe item itself).</summary>
    public long SafeBytesUnder(int id) => _safeUnder.GetValueOrDefault(id);

    /// <summary>The node for a full path inside this scan, or null when it isn't part of it (or was removed).</summary>
    public int? FindNode(string fullPath)
    {
        var root = Tree.RootPath.TrimEnd(Path.DirectorySeparatorChar);
        var cmp = _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = fullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(path, root, cmp)) return Tree.Root;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, cmp)) return null;
        var id = Tree.Root;
        foreach (var part in path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Tree.Children(id).Cast<int?>().FirstOrDefault(c => string.Equals(Tree.Name(c!.Value), part, cmp));
            if (next is null) return null;
            id = next.Value;
        }
        return id;
    }

    /// <summary>Re-evaluates every verdict after rules changed (a Keep, an accepted suggestion, an import).</summary>
    public void ReplaceRules(IEnumerable<Rule> rules)
    {
        _engine = new RuleEngine(rules, _guard, _ci);
        RemoveAndRefresh([]);
    }

    /// <summary>Takes cleaned items out of the scan and recomputes projects, verdicts and the safe total.</summary>
    public void RemoveAndRefresh(IEnumerable<string> cleanedPaths)
    {
        foreach (var p in cleanedPaths)
            if (FindNode(p) is { } id && id != Tree.Root) Tree.Remove(id);
        Projects = ProjectDetector.Detect(Tree, NowUtc);
        _projectsByNode = Projects.ToDictionary(p => p.NodeId);
        lock (_gate) { _verdicts.Clear(); _gitignoreCache.Clear(); }
        _safeUnder.Clear();
        CollectSafeItems(CancellationToken.None);
    }

    public DetectedProject? ProjectAt(int id) => _projectsByNode.GetValueOrDefault(id);

    /// <summary>
    /// Idle days of the project the item sits directly in. Only direct children count: build output lives at the
    /// project root, and anything deeper (installed packages, nested tools) must not borrow a project's idle time.
    /// </summary>
    private int? ProjectIdleDaysFor(int id) =>
        Tree.Parent(id) is var p && p >= 0 && _projectsByNode.TryGetValue(p, out var project) ? project.IdleDays : null;

    private readonly Dictionary<int, string[]?> _gitignoreCache = new();

    /// <summary>
    /// True when a .gitignore lists the item: the one in its folder (any pattern), or one in an ancestor folder up to
    /// the repository root (non-anchored patterns only).
    /// </summary>
    private bool IsGitIgnored(int id)
    {
        var name = Tree.Name(id);
        var first = true;
        for (var dir = Tree.Parent(id); dir >= 0; dir = Tree.Parent(dir), first = false)
        {
            if (ReadGitIgnore(dir) is { } lines && GitIgnore.IsIgnored(lines, name, _ci, allowAnchored: first)) return true;
            if (Tree.FindChild(dir, ".git") is not null) break;
        }
        return false;
    }

    private string[]? ReadGitIgnore(int dir)
    {
        lock (_gate)
            if (_gitignoreCache.TryGetValue(dir, out var cached)) return cached;
        string[]? lines = null;
        if (Tree.FindChild(dir, ".gitignore") is { } f && !Tree.IsDir(f))
        {
            try { lines = File.ReadAllLines(Tree.GetFullPath(f)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        lock (_gate) _gitignoreCache[dir] = lines;
        return lines;
    }

    private void CollectSafeItems(CancellationToken ct)
    {
        var safe = new List<(int, VerdictResult)>();
        long total = 0;
        var stack = new Stack<int>();
        // The folder being scanned is never itself a suggestion; start with its contents.
        foreach (var c in Tree.Children(Tree.Root)) stack.Push(c);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var id = stack.Pop();
            var v = VerdictFor(id);
            // A Safe folder that holds something you chose to keep is never offered whole: look inside instead.
            if (v.Verdict == Verdict.Safe && Tree.Size(id) > 0 && !(Tree.IsDir(id) && KeptInside(id)))
            {
                safe.Add((id, v));
                total += Tree.Size(id);
                for (var p = Tree.Parent(id); p >= 0; p = Tree.Parent(p))
                    _safeUnder[p] = _safeUnder.GetValueOrDefault(p) + Tree.Size(id);
                continue;
            }
            // Keep looking inside folders that are protected only because they contain protected folders (e.g. C:\Users).
            if (v.Verdict == Verdict.Protected && _guard.Classify(Tree.GetFullPath(id)) == ProtectionKind.InsideProtectedTree) continue;
            if (Tree.IsDir(id)) foreach (var c in Tree.Children(id)) stack.Push(c);
        }
        SafeItems = safe.OrderByDescending(s => Tree.Size(s.Item1)).ToList();
        SafeTotalBytes = total;
    }
}
