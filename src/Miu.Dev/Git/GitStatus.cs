using System.Globalization;
using Miu.Dev.Docker;

namespace Miu.Dev.Git;

/// <param name="Changed">Paths (relative to the repo root, '/'-separated) with uncommitted changes: modified, staged, renamed, conflicted.</param>
/// <param name="Untracked">New files git doesn't know about (an untracked folder is listed once, ending in '/').</param>
/// <param name="Ahead">Commits not pushed to the upstream; -1 when the branch has no upstream.</param>
public sealed record GitState(string Root, string? Branch, IReadOnlyList<string> Changed, IReadOnlyList<string> Untracked, int Ahead)
{
    public bool HasUncommitted => Changed.Count > 0 || Untracked.Count > 0;
    public bool HasUnpushed => Ahead > 0;

    /// <summary>No upstream branch: commits here may exist nowhere else.</summary>
    public bool HasNoUpstream => Ahead < 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Changed.Count > 0) parts.Add($"{Changed.Count} uncommitted {(Changed.Count == 1 ? "change" : "changes")}");
        if (Untracked.Count > 0) parts.Add($"{Untracked.Count} new {(Untracked.Count == 1 ? "file" : "files")} not in git");
        if (Ahead > 0) parts.Add($"{Ahead} {(Ahead == 1 ? "commit" : "commits")} not pushed");
        return parts.Count == 0 ? "All committed" : string.Join(" · ", parts);
    }

    /// <summary>Uncommitted or new work inside this folder (relative to the repo root), e.g. a tracked "dist/" with edits.</summary>
    public IReadOnlyList<string> WorkInside(string relFolder)
    {
        var f = relFolder.Replace('\\', '/').Trim('/');
        if (f.Length == 0) return [.. Changed, .. Untracked];
        return Changed.Concat(Untracked).Where(p =>
            p.TrimEnd('/') == f || p.StartsWith(f + "/", StringComparison.Ordinal) ||
            (p.EndsWith('/') && (f + "/").StartsWith(p, StringComparison.Ordinal))) // the item is inside a folder git doesn't know
            .ToList();
    }
}

/// <summary>Reads <c>git status --porcelain=v2 --branch -z</c>.</summary>
public static class GitStatus
{
    public static GitState Parse(string root, string output)
    {
        string? branch = null;
        var ahead = -1;
        var changed = new List<string>();
        var untracked = new List<string>();
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            if (r.Length < 2) continue;
            if (r.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = r[14..];
            else if (r.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var a = r[12..].Split(' ')[0].TrimStart('+');
                ahead = int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }
            else if (r[0] == '1') changed.Add(Field(r, 8));
            else if (r[0] == '2') { changed.Add(Field(r, 9)); i++; } // the next record is the original path of the rename
            else if (r[0] == 'u') changed.Add(Field(r, 10));
            else if (r[0] == '?') untracked.Add(r[2..]);
        }
        return new GitState(root, branch, changed, untracked, ahead);
    }

    /// <summary>The path is the rest of the record after <paramref name="fields"/> space-separated fields.</summary>
    private static string Field(string record, int fields)
    {
        var at = 0;
        for (var n = 0; n < fields && at >= 0; n++) at = record.IndexOf(' ', at) + 1;
        return at <= 0 ? record : record[at..];
    }

    /// <summary>The folder holding ".git" at or above <paramref name="path"/>, or null.</summary>
    public static string? FindRoot(string path)
    {
        try
        {
            for (var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}

/// <param name="State">What git says, when it could be asked.</param>
/// <param name="Problem">Why Miu couldn't check this repository safely (then nothing in it should be cleaned).</param>
/// <param name="GitMissing">git isn't installed, so nothing could be checked.</param>
public sealed record GitCheck(GitState? State, string? Problem = null, bool GitMissing = false);

/// <summary>
/// Asks git about a repository, without letting the repository run anything: settings that make git start programs
/// (fsmonitor, filters, textconv, includes) are refused, fsmonitor is forced off, and no lock files are taken.
/// </summary>
public sealed class GitService(IProcessRunner runner)
{
    private readonly Dictionary<string, GitCheck> _cache = new(StringComparer.Ordinal);

    public async Task<GitState?> StatusAsync(string repoRoot, CancellationToken ct = default) => (await CheckAsync(repoRoot, ct)).State;

    public async Task<GitCheck> CheckAsync(string repoRoot, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(repoRoot, out var known)) return known;
        GitCheck result;
        if (RiskyConfig(repoRoot) is { } why)
            result = new GitCheck(null, $"This repository's git settings start other programs ({why}), so Miu didn't run git on it.");
        else
        {
            try
            {
                var r = await runner.RunAsync("git",
                    ["--no-optional-locks", "-C", repoRoot, "-c", "core.fsmonitor=false", "-c", "core.quotepath=off", "status", "--porcelain=v2", "--branch", "-z"], ct);
                result = r.ExitCode switch
                {
                    0 => new GitCheck(GitStatus.Parse(repoRoot, r.Output)),
                    127 => new GitCheck(null, GitMissing: true),
                    _ => new GitCheck(null, "git couldn't read this repository" + (r.Error.Length > 0 ? ": " + r.Error.Split('\n')[0].Trim() : ".")),
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new GitCheck(null, "git took too long to check this repository.");
            }
        }
        _cache[repoRoot] = result;
        return result;
    }

    /// <summary>Settings in the repository's own config that make "git status" run a program.</summary>
    public static string? RiskyConfig(string repoRoot)
    {
        try
        {
            var dotGit = Path.Combine(repoRoot, ".git");
            var gitDir = dotGit;
            if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var link && link.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                gitDir = Path.GetFullPath(Path.Combine(repoRoot, link[7..].Trim()));
            var config = Path.Combine(gitDir, "config");
            if (!File.Exists(config)) return null;
            var text = File.ReadAllText(config).ToLowerInvariant();
            foreach (var (needle, name) in new[] { ("fsmonitor", "fsmonitor"), ("[filter", "a file filter"), ("textconv", "textconv"), ("[include", "an include"), ("[includeif", "an include") })
                if (text.Contains(needle, StringComparison.Ordinal)) return name;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unreadable settings"; }
    }

    public void Forget() => _cache.Clear();
}
