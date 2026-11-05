using Miu.Core.Scanning;
using Miu.Dev;

namespace Miu.Dev.Tests;

public class ProjectDetectorTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Ago(int days) => Now.AddDays(-days);

    private static int Dir(ScanTree t, int parent, string name, int ageDays = 0) => t.Add(parent, name, true, 0, Ago(ageDays));
    private static void File(ScanTree t, int parent, string name, long size, int ageDays) => t.Add(parent, name, false, size, Ago(ageDays));

    [Fact]
    public void Detects_node_project_with_rebuildable_size()
    {
        var t = new ScanTree("/code");
        var app = Dir(t, t.Root, "app");
        File(t, app, "package.json", 1_000, 120);
        File(t, app, "package-lock.json", 0, 120);
        var src = Dir(t, app, "src");
        File(t, src, "index.ts", 9_000, 100);
        var nm = Dir(t, app, "node_modules");
        File(t, nm, "big.js", 500_000, 100);
        var dist = Dir(t, app, "dist");
        File(t, dist, "bundle.js", 50_000, 100);
        t.ComputeAggregates();

        var p = ProjectDetector.Detect(t, Now).Should().ContainSingle().Subject;
        p.Name.Should().Be("app");
        p.Stacks.Should().Contain("Node");
        p.TotalBytes.Should().Be(560_000);
        p.RebuildableBytes.Should().Be(550_000);
        p.IdleDays.Should().Be(100);
        p.Bucket.Should().Be(IdleBucket.Idle);
        p.Path.Should().Be(Path.Combine("/code", "app"));
    }

    [Fact]
    public void Ignores_package_json_inside_node_modules()
    {
        var t = new ScanTree("/code");
        var app = Dir(t, t.Root, "app");
        File(t, app, "package.json", 10, 1);
        File(t, app, "package-lock.json", 10, 1);
        var nm = Dir(t, app, "node_modules");
        for (var i = 0; i < 50; i++)
        {
            var pkg = Dir(t, nm, "pkg" + i);
            File(t, pkg, "package.json", 10, 1);
        }
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Should().ContainSingle();
    }

    [Fact]
    public void Detects_nested_monorepo_projects()
    {
        var t = new ScanTree("/code");
        var mono = Dir(t, t.Root, "mono");
        File(t, mono, "package.json", 10, 5);
        Dir(t, mono, ".git", 5);
        var apps = Dir(t, mono, "apps");
        var web = Dir(t, apps, "web");
        File(t, web, "package.json", 10, 5);
        var api = Dir(t, apps, "api");
        File(t, api, "Api.csproj", 10, 5);
        Dir(t, api, "bin");
        t.ComputeAggregates();
        var found = ProjectDetector.Detect(t, Now);
        found.Select(p => p.Name).Should().BeEquivalentTo(["mono", "web", "api"]); // nested projects count because they are inside a repo
        found.Single(p => p.Name == "api").Stacks.Should().Contain(".NET");
    }

    [Fact]
    public void Last_activity_ignores_node_modules_mtime()
    {
        var t = new ScanTree("/code");
        var app = Dir(t, t.Root, "app");
        File(t, app, "package.json", 10, 200);
        File(t, app, "yarn.lock", 10, 200);
        var nm = Dir(t, app, "node_modules");
        File(t, nm, "fresh.js", 10, 0);
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Single().IdleDays.Should().Be(200);
    }

    [Fact]
    public void Git_logs_head_counts_as_activity()
    {
        var t = new ScanTree("/code");
        var app = Dir(t, t.Root, "app");
        File(t, app, "Cargo.toml", 10, 300);
        var git = Dir(t, app, ".git");
        File(t, git, "index", 10, 0);
        var logs = Dir(t, git, "logs");
        File(t, logs, "HEAD", 10, 3);
        t.ComputeAggregates();
        var p = ProjectDetector.Detect(t, Now).Single();
        p.IdleDays.Should().Be(3);
        p.Stacks.Should().Contain(["Rust", "Git"]);
    }

    [Fact]
    public void Build_folder_only_rebuildable_for_node()
    {
        var t = new ScanTree("/code");
        var py = Dir(t, t.Root, "py");
        File(t, py, "pyproject.toml", 10, 100);
        File(t, py, "uv.lock", 10, 100);
        var build = Dir(t, py, "build");
        File(t, build, "script.py", 1000, 100);
        var cache = Dir(t, py, "__pycache__");
        File(t, cache, "x.pyc", 300, 100);
        t.ComputeAggregates();
        var p = ProjectDetector.Detect(t, Now).Single();
        p.RebuildableBytes.Should().Be(300);
        p.IdleDays.Should().Be(100); // build/ is not junk for Python, so it counts as activity — it is 100 days old here
    }

    [Theory]
    [InlineData(0, IdleBucket.Active)]
    [InlineData(13, IdleBucket.Active)]
    [InlineData(14, IdleBucket.Cooling)]
    [InlineData(90, IdleBucket.Cooling)]
    [InlineData(91, IdleBucket.Idle)]
    [InlineData(365, IdleBucket.Idle)]
    [InlineData(366, IdleBucket.Abandoned)]
    public void BucketFor_boundaries(int days, IdleBucket expected) => ProjectDetector.BucketFor(days).Should().Be(expected);

    [Fact]
    public void Results_sorted_by_rebuildable_desc()
    {
        var t = new ScanTree("/code");
        foreach (var (name, size) in new[] { ("small", 10L), ("big", 1000L) })
        {
            var p = Dir(t, t.Root, name);
            File(t, p, "package.json", 1, 50);
            File(t, p, "pnpm-lock.yaml", 1, 50);
            var nm = Dir(t, p, "node_modules");
            File(t, nm, "x", size, 50);
        }
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Select(p => p.Name).Should().Equal("big", "small");
    }

    // ---- Review fixes: installed software must not look like an idle project

    [Fact]
    public void Installed_app_without_git_or_lockfile_is_not_a_project()
    {
        var t = new ScanTree("/u/AppData/Local/Programs");
        var app = Dir(t, Dir(t, Dir(t, t.Root, "Code"), "resources"), "app");
        File(t, app, "package.json", 10, 60);
        File(t, Dir(t, app, "node_modules"), "x.js", 10, 60);
        File(t, Dir(t, app, "out"), "main.js", 10, 60);
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Should().BeEmpty();
    }

    [Theory]
    [InlineData(".vscode/extensions")]
    [InlineData("AppData/Local/Programs")]
    [InlineData("lib/python3.11/site-packages")]
    [InlineData("Library/Application Support/Cursor")]
    public void Install_roots_are_never_projects(string installRoot)
    {
        var t = new ScanTree("/home/u");
        var parent = t.Root;
        foreach (var seg in installRoot.Split('/')) parent = Dir(t, parent, seg);
        var ext = Dir(t, parent, "ms-python-1.0");
        File(t, ext, "package.json", 10, 60);
        File(t, ext, "package-lock.json", 10, 60);
        Dir(t, ext, ".git", 60);
        File(t, Dir(t, ext, "dist"), "ext.js", 10, 60);
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Should().BeEmpty();
    }

    [Fact]
    public void Packages_inside_a_venv_named_env_are_not_projects()
    {
        var t = new ScanTree("/code");
        var app = Dir(t, t.Root, "app");
        File(t, app, "pyproject.toml", 10, 0);
        Dir(t, app, ".git", 0);
        var env = Dir(t, app, "env");
        File(t, env, "pyvenv.cfg", 10, 60);
        var widgets = Dir(t, Dir(t, Dir(t, env, "lib"), "site-packages"), "widgets");
        File(t, widgets, "package.json", 10, 60);
        File(t, widgets, "package-lock.json", 10, 60);
        File(t, Dir(t, widgets, "dist"), "w.js", 10, 60);
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Select(p => p.Name).Should().Equal("app");
    }

    [Fact]
    public void Repo_kept_under_a_folder_named_build_is_detected()
    {
        var t = new ScanTree("/home/u");
        var repo = Dir(t, Dir(t, t.Root, "build"), "myrepo");
        File(t, repo, "package.json", 10, 3);
        Dir(t, repo, ".git", 3);
        t.ComputeAggregates();
        ProjectDetector.Detect(t, Now).Select(p => p.Name).Should().Equal("myrepo");
    }
}
