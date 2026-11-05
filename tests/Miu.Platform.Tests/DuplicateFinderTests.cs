using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miu-dup-" + Guid.NewGuid().ToString("N"));
    private const int Min = 1000;

    public DuplicateFinderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string F(string rel, byte[] content, int ageDays = 0)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    private static byte[] Bytes(int size, byte seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private async Task<IReadOnlyList<DuplicateGroup>> Find()
    {
        var tree = await new ManagedScanner().ScanAsync(_root, null, CancellationToken.None);
        var groups = await DuplicateFinder.FindAsync(tree, tree.Root, Min, null, CancellationToken.None);
        _tree = tree;
        return groups;
    }

    private Miu.Core.Scanning.ScanTree? _tree;
    private string Name(int id) => _tree!.Name(id);

    [Fact]
    public async Task Finds_identical_files()
    {
        var data = Bytes(5000, 1);
        F("a/movie.mp4", data);
        F("b/movie copy.mp4", data);
        F("other.bin", Bytes(5000, 2));
        var g = (await Find()).Should().ContainSingle().Subject;
        g.Nodes.Select(Name).Should().BeEquivalentTo(["movie.mp4", "movie copy.mp4"]);
        g.Size.Should().Be(5000);
        g.WastedBytes.Should().Be(5000);
    }

    [Fact]
    public async Task Same_size_different_content_not_grouped()
    {
        F("x.bin", Bytes(4000, 1));
        F("y.bin", Bytes(4000, 2));
        (await Find()).Should().BeEmpty();
    }

    [Fact]
    public async Task Same_head_and_tail_different_middle_not_grouped()
    {
        var a = Bytes(400_000, 7);
        var b = (byte[])a.Clone();
        b[200_000] ^= 0xFF;
        F("x.bin", a);
        F("y.bin", b);
        (await Find()).Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_vanished_files()
    {
        var data = Bytes(3000, 3);
        F("a.bin", data);
        F("b.bin", data);
        var gone = F("c.bin", data);
        var tree = await new ManagedScanner().ScanAsync(_root, null, CancellationToken.None);
        File.Delete(gone);
        var groups = await DuplicateFinder.FindAsync(tree, tree.Root, Min, null, CancellationToken.None);
        groups.Single().Nodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ignores_node_modules_and_git()
    {
        var data = Bytes(3000, 4);
        F("app/node_modules/x/index.js", data);
        F("app2/node_modules/x/index.js", data);
        F("repo/.git/objects/aa", data);
        (await Find()).Should().BeEmpty();
    }

    [Fact]
    public async Task Small_files_are_ignored()
    {
        var data = Bytes(100, 5);
        F("a.txt", data);
        F("b.txt", data);
        (await Find()).Should().BeEmpty();
    }

    [Fact]
    public async Task Keeps_oldest()
    {
        var data = Bytes(2000, 6);
        F("Downloads/photo (1).jpg", data, ageDays: 1);
        F("Pictures/photo.jpg", data, ageDays: 300);
        var g = (await Find()).Single();
        Name(g.KeepNode).Should().Be("photo.jpg");
    }
}
