using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

public sealed class FreedesktopTrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miu-trash-" + Guid.NewGuid().ToString("N"));
    private readonly string _trash;
    private readonly string _data;

    public FreedesktopTrashTests()
    {
        _trash = Path.Combine(_root, "Trash");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(_data, "proj", "node_modules", "pkg"));
        File.WriteAllBytes(Path.Combine(_data, "proj", "node_modules", "pkg", "i.js"), new byte[300]);
        File.WriteAllBytes(Path.Combine(_data, "crash.dmp"), new byte[100]);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private FreedesktopTrash Trash() => new(_trash);

    [Fact]
    public async Task Recycles_file_and_folder_with_trashinfo()
    {
        var nm = Path.Combine(_data, "proj", "node_modules");
        var dmp = Path.Combine(_data, "crash.dmp");
        var r = await Trash().RecycleAsync([nm, dmp], null, CancellationToken.None);
        r.Failed.Should().BeEmpty();
        r.Recycled.Should().HaveCount(2);
        Directory.Exists(nm).Should().BeFalse();
        File.Exists(dmp).Should().BeFalse();
        r.Recycled.Single(i => i.OriginalPath == nm).Bytes.Should().Be(300);
        Directory.Exists(Path.Combine(_trash, "files", "node_modules")).Should().BeTrue();
        var info = File.ReadAllText(Path.Combine(_trash, "info", "node_modules.trashinfo"));
        info.Should().StartWith("[Trash Info]");
        info.Should().Contain("Path=" + new Uri(nm).AbsolutePath);
        info.Should().MatchRegex(@"DeletionDate=\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}");
    }

    [Fact]
    public async Task Unique_names_on_clash()
    {
        var a = Path.Combine(_data, "a", "crash.dmp");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllBytes(a, new byte[1]);
        await Trash().RecycleAsync([Path.Combine(_data, "crash.dmp")], null, CancellationToken.None);
        var r = await Trash().RecycleAsync([a], null, CancellationToken.None);
        Path.GetFileName(r.Recycled.Single().TrashLocation).Should().Be("crash.2.dmp");
    }

    [Fact]
    public async Task Missing_path_is_reported()
    {
        var r = await Trash().RecycleAsync([Path.Combine(_data, "nope")], null, CancellationToken.None);
        r.Recycled.Should().BeEmpty();
        r.Failed.Single().Reason.Should().Contain("no longer there");
    }

    [Fact]
    public async Task Restore_moves_back_and_removes_info()
    {
        var nm = Path.Combine(_data, "proj", "node_modules");
        var r = await Trash().RecycleAsync([nm], null, CancellationToken.None);
        var back = await Trash().RestoreAsync(r.Recycled, CancellationToken.None);
        back.Restored.Should().Equal(nm);
        File.Exists(Path.Combine(nm, "pkg", "i.js")).Should().BeTrue();
        File.Exists(Path.Combine(_trash, "info", "node_modules.trashinfo")).Should().BeFalse();
    }

    [Fact]
    public async Task Restore_refuses_to_overwrite()
    {
        var dmp = Path.Combine(_data, "crash.dmp");
        var r = await Trash().RecycleAsync([dmp], null, CancellationToken.None);
        File.WriteAllText(dmp, "new");
        var back = await Trash().RestoreAsync(r.Recycled, CancellationToken.None);
        back.Restored.Should().BeEmpty();
        back.Failed.Single().Reason.Should().Contain("already exists");
        File.ReadAllText(dmp).Should().Be("new");
    }

    // ---- Review fixes

    [Fact]
    public async Task Restore_refuses_a_different_item_that_reused_the_trash_name()
    {
        var p1 = Path.Combine(_data, "p1", "node_modules");
        var p2 = Path.Combine(_data, "p2", "node_modules");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);
        File.WriteAllText(Path.Combine(p1, "mine"), "p1");
        File.WriteAllText(Path.Combine(p2, "mine"), "p2");
        var first = await Trash().RecycleAsync([p1], null, CancellationToken.None);
        // The user empties the Trash, then cleans another folder with the same name.
        Directory.Delete(Path.Combine(_trash, "files"), true);
        Directory.Delete(Path.Combine(_trash, "info"), true);
        var second = await Trash().RecycleAsync([p2], null, CancellationToken.None);
        second.Recycled.Single().TrashLocation.Should().Be(first.Recycled.Single().TrashLocation);

        var back = await Trash().RestoreAsync(first.Recycled, CancellationToken.None);

        back.Restored.Should().BeEmpty();
        back.Failed.Single().Reason.Should().Contain("no longer in the Trash");
        Directory.Exists(p1).Should().BeFalse();
        (await Trash().RestoreAsync(second.Recycled, CancellationToken.None)).Restored.Should().Equal(p2);
        File.ReadAllText(Path.Combine(p2, "mine")).Should().Be("p2");
    }

    [Fact]
    public async Task Trashinfo_path_is_percent_encoded_per_spec()
    {
        var odd = Path.Combine(_data, "c%41d 1.txt");
        File.WriteAllText(odd, "x");
        await Trash().RecycleAsync([odd], null, CancellationToken.None);
        var info = File.ReadAllText(Path.Combine(_trash, "info", "c%41d 1.txt.trashinfo"));
        info.Should().Contain("c%2541d%201.txt");
    }

    [Fact]
    public async Task Cancellation_mid_batch_returns_what_was_already_moved()
    {
        var a = Path.Combine(_data, "crash.dmp");
        var b = Path.Combine(_data, "proj", "node_modules");
        using var cts = new CancellationTokenSource();
        var progress = new Inline<int>(_ => cts.Cancel());
        var r = await Trash().RecycleAsync([a, b], progress, cts.Token);
        r.Recycled.Select(i => i.OriginalPath).Should().Equal(a);
        Directory.Exists(b).Should().BeTrue();
    }

    [Theory]
    [InlineData("/homework/big.iso", "/")]
    [InlineData("/home/u/x", "/home")]
    [InlineData("/home", "/home")]
    public void MountOf_matches_whole_path_segments(string path, string expected) =>
        FreedesktopTrash.MountOf(path, ["/", "/home", "/dev/shm"]).Should().Be(expected);

    [Fact]
    public async Task Symlinked_folder_moves_as_a_link()
    {
        if (OperatingSystem.IsWindows()) return;
        var link = Path.Combine(_data, "link-to-proj");
        Directory.CreateSymbolicLink(link, Path.Combine(_data, "proj"));
        var r = await Trash().RecycleAsync([link], null, CancellationToken.None);
        r.Failed.Should().BeEmpty();
        Directory.Exists(Path.Combine(_data, "proj", "node_modules")).Should().BeTrue(); // target untouched
    }

    private sealed class Inline<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
}
