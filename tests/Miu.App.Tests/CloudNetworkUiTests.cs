using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;

namespace Miu.App.Tests;

public sealed class CloudNetworkUiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-cnui-" + Guid.NewGuid().ToString("N"));
    private readonly NetworkServiceTests.FakeDrives _drives = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private MainWindowViewModel Shell() =>
        new(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets(), networkDrives: _drives),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram());

    [AvaloniaFact]
    public async Task Page_renders_and_maps_a_drive()
    {
        var vm = Shell();
        var window = new MainWindow { DataContext = vm, Width = 1300, Height = 900 };
        window.Show();
        vm.Navigate(vm.CloudNetwork);
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<CloudNetworkView>().Should().ContainSingle();

        var cn = vm.CloudNetwork;
        cn.CanMap.Should().BeTrue();
        cn.Letters.Should().Contain(["Z:", CloudNetworkViewModel.NoLetter]);
        cn.UncText = @"\\nas\photos";
        cn.UserName = "khang";
        cn.Password = "pw";
        await cn.MapCommand.ExecuteAsync(null);
        cn.NetworkMessageIsError.Should().BeFalse();
        cn.Password.Should().BeEmpty();
        cn.Mapped.Single().Title.Should().Contain(@"\\nas\photos");

        await cn.UnmapCommand.ExecuteAsync(cn.Mapped.Single());
        cn.HasMapped.Should().BeFalse();

        cn.UncText = "nonsense";
        await cn.MapCommand.ExecuteAsync(null);
        cn.NetworkMessageIsError.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Sync_job_save_preview_run_and_undo_from_activity()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dst, "old.txt"), "old");

        var vm = Shell();
        var cn = vm.CloudNetwork;
        cn.JobSource = src;
        cn.JobTarget = src; // same folder: refused
        await cn.SaveJobCommand.ExecuteAsync(null);
        cn.SyncMessageIsError.Should().BeTrue();
        cn.HasJobs.Should().BeFalse();

        cn.JobTarget = dst;
        cn.Frequency = "Only when I press Run";
        await cn.SaveJobCommand.ExecuteAsync(null);
        cn.Jobs.Should().ContainSingle();
        cn.HasPreview.Should().BeTrue();
        cn.PreviewCanRun.Should().BeTrue();
        cn.PreviewOps.Select(o => o.Action).Should().BeEquivalentTo("Copy →", "Set aside (target)");

        await cn.RunPreviewCommand.ExecuteAsync(null);
        cn.HasPreview.Should().BeFalse();
        cn.SyncMessage.Should().Contain("Copied 1 file");
        File.Exists(Path.Combine(dst, "a.txt")).Should().BeTrue();
        cn.Jobs.Single().LastText.Should().Contain("Copied 1 file");

        vm.Activity.Load();
        var row = vm.Activity.Rows.First();
        row.Summary.Should().Contain("set aside 1");
        row.CanUndo.Should().BeTrue();
        await row.UndoCommand.ExecuteAsync(null);
        File.ReadAllText(Path.Combine(dst, "old.txt")).Should().Be("old");
        vm.Notice!.Message.Should().Contain("Put back 1 file");
    }

    [AvaloniaFact]
    public async Task An_empty_source_is_refused_without_run_anyway_but_mass_archive_offers_it()
    {
        var src = Path.Combine(_root, "empty");
        var dst = Path.Combine(_root, "full");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(dst, $"keep{i}.txt"), "x");
        var vm = Shell();
        var cn = vm.CloudNetwork;
        cn.JobSource = src;
        cn.JobTarget = dst;
        await cn.SaveJobCommand.ExecuteAsync(null);
        cn.PreviewCanRun.Should().BeFalse();
        cn.PreviewRefused.Should().BeFalse(); // an empty source is never overridden
        cn.PreviewText.Should().Contain("empty");

        File.WriteAllText(Path.Combine(src, "keep0.txt"), "x");
        // Same content and date as the target's copy, so it counts as unchanged on any machine.
        File.SetLastWriteTimeUtc(Path.Combine(src, "keep0.txt"), File.GetLastWriteTimeUtc(Path.Combine(dst, "keep0.txt")));
        await cn.PreviewAsync(cn.Jobs.Single());
        cn.PreviewRefused.Should().BeTrue(); // "set aside 29 of 30": you may decide
        await cn.RunAnywayCommand.ExecuteAsync(null);
        Directory.GetFiles(dst).Should().ContainSingle();
        Directory.GetFiles(Path.Combine(dst, ".miu-sync-trash"), "*", SearchOption.AllDirectories).Should().HaveCount(29); // keep0 already matched
    }

    [AvaloniaFact]
    public async Task Typing_just_a_machine_lists_its_shared_folders()
    {
        var vm = Shell();
        var cn = vm.CloudNetwork;
        cn.ScanPathText = @"\\pi";
        await cn.ScanPathCommand.ExecuteAsync(null);
        cn.NetworkMessageIsError.Should().BeFalse();
        cn.NetworkMessage.Should().Contain("pi shares 1 folder");
        cn.Shares.Should().Equal("photos");
        cn.PickShareCommand.Execute("photos");
        cn.UncText.Should().Be(@"\\pi\photos");
        cn.ScanPathText.Should().Be(@"\\pi\photos");

        cn.UncText = "smb://nas";
        await cn.MapCommand.ExecuteAsync(null);
        _drives.Calls.Should().BeEmpty(); // nothing mapped: it listed the shares instead
        cn.UncText.Should().Be(@"\\nas\");
    }
}
