using Miu.Core.Layout;

namespace Miu.Core.Tests.Layout;

public class SquarifyTests
{
    private static readonly double[] Classic = [6, 6, 4, 3, 2, 2, 1];
    private static readonly LayoutRect Box = new(0, 0, 6, 4);

    [Fact]
    public void Areas_proportional_to_values()
    {
        var rects = Squarify.Layout(Classic, Box);
        rects.Should().HaveCount(7);
        var total = Classic.Sum();
        for (var i = 0; i < Classic.Length; i++)
            (rects[i].Width * rects[i].Height).Should().BeApproximately(Classic[i] / total * 24, 0.01);
    }

    [Fact]
    public void Rects_inside_bounds_and_non_overlapping()
    {
        var rects = Squarify.Layout(Classic, Box);
        foreach (var r in rects)
        {
            r.X.Should().BeGreaterThanOrEqualTo(-1e-9);
            r.Y.Should().BeGreaterThanOrEqualTo(-1e-9);
            (r.X + r.Width).Should().BeLessThanOrEqualTo(6 + 1e-9);
            (r.Y + r.Height).Should().BeLessThanOrEqualTo(4 + 1e-9);
        }
        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            var a = rects[i]; var b = rects[j];
            var ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            var oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            (ox > 1e-9 && oy > 1e-9).Should().BeFalse($"rect {i} overlaps {j}");
        }
    }

    [Fact]
    public void Aspect_ratios_reasonable()
    {
        var worst = Squarify.Layout(Classic, Box).Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));
        worst.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Zero_values_get_empty_rects()
    {
        var rects = Squarify.Layout([5, 0, 3], Box);
        (rects[1].Width * rects[1].Height).Should().Be(0);
        (rects[0].Width * rects[0].Height + rects[2].Width * rects[2].Height).Should().BeApproximately(24, 0.01);
    }
}
