using Miu.Core.Scanning;

namespace Miu.Core.Tests.Scanning;

public class ScanTreeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (ScanTree tree, int a) Sample()
    {
        var t = new ScanTree("/root");
        var a = t.Add(t.Root, "a", true, 0, T0);
        t.Add(a, "f1", false, 100, T0.AddDays(1));
        t.Add(a, "f2", false, 50, T0.AddDays(5));
        t.Add(t.Root, "f3", false, 10, T0.AddDays(2));
        t.ComputeAggregates();
        return (t, a);
    }

    [Fact]
    public void Aggregates_sum_file_sizes_up_the_tree()
    {
        var (t, a) = Sample();
        t.Size(t.Root).Should().Be(160);
        t.FileCount(t.Root).Should().Be(3);
        t.Size(a).Should().Be(150);
        t.FileCount(a).Should().Be(2);
    }

    [Fact]
    public void Children_sorted_by_size_desc()
    {
        var (t, a) = Sample();
        t.Children(t.Root).Select(t.Name).Should().Equal("a", "f3");
        t.Children(a).Select(t.Name).Should().Equal("f1", "f2");
    }

    [Fact]
    public void GetFullPath_joins_with_platform_separator()
    {
        var (t, a) = Sample();
        var f1 = t.FindChild(a, "f1")!.Value;
        t.GetFullPath(f1).Should().Be(Path.Combine("/root", "a", "f1"));
        t.GetFullPath(t.Root).Should().Be("/root");
        t.Parent(f1).Should().Be(a);
        t.Parent(t.Root).Should().Be(-1);
    }

    [Fact]
    public void NewestModifiedUtc_is_max_of_descendants()
    {
        var (t, a) = Sample();
        t.NewestModifiedUtc(a).Should().Be(T0.AddDays(5));
        t.NewestModifiedUtc(t.Root).Should().Be(T0.AddDays(5));
    }

    [Fact]
    public void Add_is_thread_safe()
    {
        var t = new ScanTree("/r");
        var d = t.Add(t.Root, "d", true, 0, T0);
        Parallel.For(0, 100_000, i => t.Add(i % 2 == 0 ? t.Root : d, "f" + i, false, 1, T0));
        t.ComputeAggregates();
        t.FileCount(t.Root).Should().Be(100_000);
        t.Size(t.Root).Should().Be(100_000);
    }
}
