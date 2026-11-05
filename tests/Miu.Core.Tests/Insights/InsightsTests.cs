using Miu.Core.Insights;
using Miu.Core.Scanning;

namespace Miu.Core.Tests.Insights;

public class InsightsTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ScanTree Tree()
    {
        var t = new ScanTree("/r");
        var v = t.Add(t.Root, "videos", true, 0, T);
        t.Add(v, "a.mp4", false, 500, T);
        t.Add(v, "b.mkv", false, 300, T);
        t.Add(t.Root, "doc.pdf", false, 50, T);
        t.Add(t.Root, "setup.msi", false, 120, T);
        t.ComputeAggregates();
        return t;
    }

    [Fact]
    public void Breakdown_sums_by_category()
    {
        var b = TypeBreakdown.Compute(Tree(), 0);
        b.Select(x => x.Category).Should().Equal(FileCategory.Video, FileCategory.Installer, FileCategory.Document);
        b[0].Bytes.Should().Be(800);
        b[0].Files.Should().Be(2);
    }

    [Fact]
    public void Largest_returns_top_n_files_only()
    {
        var t = Tree();
        LargestFiles.Find(t, t.Root, 2).Select(t.Name).Should().Equal("a.mp4", "b.mkv");
    }
}
