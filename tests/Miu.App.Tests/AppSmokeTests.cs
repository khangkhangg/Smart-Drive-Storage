using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;

namespace Miu.App.Tests;

public sealed class AppSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miu-app-" + Guid.NewGuid().ToString("N"));

    public AppSmokeTests()
    {
        var app = Path.Combine(_root, "code", "app");
        Directory.CreateDirectory(Path.Combine(app, "node_modules", "react"));
        File.WriteAllText(Path.Combine(app, "package.json"), "{}");
        File.WriteAllText(Path.Combine(app, "package-lock.json"), "{}");
        File.WriteAllBytes(Path.Combine(app, "node_modules", "react", "index.js"), new byte[200_000]);
        Directory.CreateDirectory(Path.Combine(_root, "videos"));
        File.WriteAllBytes(Path.Combine(_root, "videos", "trip.mp4"), new byte[500_000]);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [AvaloniaFact]
    public async Task MainWindow_opens_and_scans_temp_folder()
    {
        var services = AppServices.Create(":memory:");
        var vm = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.Analyze.PathInput = _root;
        await vm.Analyze.ScanCommand.ExecuteAsync(null);

        vm.Analyze.Items.Should().HaveCount(2);
        vm.Analyze.Items[0].Name.Should().Be("videos");
        vm.Analyze.TotalText.Should().NotBeNullOrWhiteSpace();
        vm.DevProjects.Projects.Should().ContainSingle(p => p.Name == "app");
        vm.Home.SafeTotalBytes.Should().Be(0); // project is active (just created), so node_modules is Review
    }

    [AvaloniaFact]
    public async Task Drill_down_and_verdicts()
    {
        var services = AppServices.Create(":memory:");
        var vm = new MainWindowViewModel(services);
        vm.Analyze.PathInput = _root;
        await vm.Analyze.ScanCommand.ExecuteAsync(null);

        var code = vm.Analyze.Items.Single(i => i.Name == "code");
        vm.Analyze.OpenCommand.Execute(code);
        var app = vm.Analyze.Items.Single();
        vm.Analyze.OpenCommand.Execute(app);
        var nm = vm.Analyze.Items.Single(i => i.Name == "node_modules");
        nm.VerdictText.Should().Be("Review first");
        nm.RebuildHint.Should().Be("npm install");
        vm.Analyze.Breadcrumbs.Select(b => b.Name).Should().EndWith(["code", "app"]);
        vm.Analyze.GoUpCommand.Execute(null);
        vm.Analyze.Items.Single().Name.Should().Be("app");
    }

    [AvaloniaFact]
    public async Task Scan_root_is_never_suggested_for_removal()
    {
        // The temp folder matches the "temporary files" rule, but the folder being scanned must never be the suggestion.
        var temp = Path.Combine(Path.GetTempPath(), "miu-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var old = Path.Combine(temp, "old.tmp");
        File.WriteAllBytes(old, new byte[1000]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        try
        {
            var session = await ScanSession.RunAsync(AppServices.Create(":memory:"), temp, null, CancellationToken.None);
            session.SafeItems.Should().NotContain(s => s.NodeId == session.Tree.Root);
        }
        finally { Directory.Delete(temp, true); }
    }

    [AvaloniaFact]
    public async Task Folders_show_how_much_inside_is_safe()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "miu-safe-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "code", "old-app");
        Directory.CreateDirectory(Path.Combine(app, "node_modules"));
        var files = new[] { Path.Combine(app, "package.json"), Path.Combine(app, "package-lock.json"), Path.Combine(app, "node_modules", "lib.js") };
        File.WriteAllText(files[0], "{}");
        File.WriteAllText(files[1], "{}");
        File.WriteAllBytes(files[2], new byte[300_000]);
        foreach (var f in files) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-60));
        try
        {
            var vm = new MainWindowViewModel(AppServices.Create(":memory:"));
            await vm.Analyze.ScanPathAsync(root);
            var code = vm.Analyze.Items.Single();
            code.SafeInsideBytes.Should().Be(300_000);
            code.BadgeText.Should().Be("293 KB to clean");
            code.ShowsBadge.Should().BeTrue();
            vm.Home.TopSafe.Single().Name.Should().Be("old-app › node_modules");
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Packages_inside_an_active_projects_env_are_never_safe()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "miu-env-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var widgets = Path.Combine(app, "env", "lib", "python3.11", "site-packages", "widgets");
        Directory.CreateDirectory(Path.Combine(widgets, "dist"));
        Directory.CreateDirectory(Path.Combine(app, ".git"));
        File.WriteAllText(Path.Combine(app, "pyproject.toml"), "");
        File.WriteAllText(Path.Combine(app, "env", "pyvenv.cfg"), "");
        foreach (var f in new[] { "package.json", "package-lock.json", "dist/w.js" })
        {
            var p = Path.Combine(widgets, f);
            File.WriteAllBytes(p, new byte[5000]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-60));
        }
        try
        {
            var session = await ScanSession.RunAsync(AppServices.Create(":memory:"), root, null, CancellationToken.None);
            session.SafeItems.Select(s => session.Tree.GetFullPath(s.NodeId)).Should().NotContain(p => p.Contains("site-packages"));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Overlapping_scans_end_in_a_consistent_state()
    {
        var other = Path.Combine(_root, "videos");
        var vm = new MainWindowViewModel(AppServices.Create(":memory:"));
        var first = vm.Analyze.ScanPathAsync(_root);
        var second = vm.Analyze.ScanPathAsync(other);
        await Task.WhenAll(first, second);
        vm.Analyze.IsScanning.Should().BeFalse();
        vm.Analyze.Session!.Tree.RootPath.Should().Be(other);
        vm.Analyze.Items.Single().Name.Should().Be("trip.mp4");
    }
}
