namespace Miu.Core.Layout;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk). Output order matches input order.</summary>
public static class Squarify
{
    public static IReadOnlyList<LayoutRect> Layout(IReadOnlyList<double> values, LayoutRect bounds)
    {
        var result = new LayoutRect[values.Count];
        var total = values.Where(v => v > 0).Sum();
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        var scale = bounds.Width * bounds.Height / total;
        var items = Enumerable.Range(0, values.Count).Where(i => values[i] > 0)
            .Select(i => (Index: i, Area: values[i] * scale)).ToList();

        var free = bounds;
        var row = new List<(int Index, double Area)>();
        foreach (var item in items)
        {
            var side = Math.Min(free.Width, free.Height);
            if (row.Count == 0 || Worst(row, item.Area, side) <= Worst(row, 0, side)) { row.Add(item); continue; }
            free = PlaceRow(row, free, result);
            row.Clear();
            row.Add(item);
        }
        if (row.Count > 0) PlaceRow(row, free, result);
        return result;
    }

    private static double Worst(List<(int Index, double Area)> row, double extra, double side)
    {
        double sum = extra, max = extra, min = extra > 0 ? extra : double.MaxValue;
        foreach (var (_, a) in row) { sum += a; max = Math.Max(max, a); min = Math.Min(min, a); }
        if (sum <= 0 || side <= 0) return double.MaxValue;
        var s2 = sum * sum; var w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static LayoutRect PlaceRow(List<(int Index, double Area)> row, LayoutRect free, LayoutRect[] result)
    {
        var sum = row.Sum(r => r.Area);
        if (free.Width >= free.Height)
        {
            var colWidth = sum / free.Height;
            var y = free.Y;
            foreach (var (i, a) in row)
            {
                var h = a / colWidth;
                result[i] = new LayoutRect(free.X, y, colWidth, h);
                y += h;
            }
            return new LayoutRect(free.X + colWidth, free.Y, Math.Max(0, free.Width - colWidth), free.Height);
        }
        var rowHeight = sum / free.Width;
        var x = free.X;
        foreach (var (i, a) in row)
        {
            var w = a / rowHeight;
            result[i] = new LayoutRect(x, free.Y, w, rowHeight);
            x += w;
        }
        return new LayoutRect(free.X, free.Y + rowHeight, free.Width, Math.Max(0, free.Height - rowHeight));
    }
}
