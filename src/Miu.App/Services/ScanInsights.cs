namespace Miu.App.Services;

/// <summary>What Miu remembers after every scan: folder sizes (for "what grew") and how full the drive was (forecast).</summary>
public static class ScanInsights
{
    public static void Record(AppServices services, ScanSession session, DateTime nowUtc)
    {
        var t = session.Tree;
        var folders = new Dictionary<string, long>();
        foreach (var c in t.Children(t.Root))
        {
            if (!t.IsDir(c)) continue;
            folders[t.GetFullPath(c)] = t.Size(c);
            foreach (var g in t.Children(c).Where(t.IsDir).Take(50)) folders[t.GetFullPath(g)] = t.Size(g);
        }
        services.Snapshots.Save(t.RootPath, nowUtc, folders);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(t.RootPath) ?? t.RootPath);
            if (drive.IsReady) services.SpaceSamples.Add(drive.RootDirectory.FullName, nowUtc, drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    /// <summary>Folders that grew since a scan of the same place at least five days ago.</summary>
    public static IReadOnlyList<(string Path, long Delta)> WhatGrew(AppServices services, ScanSession session, DateTime nowUtc, int top = 5)
    {
        var t = session.Tree;
        var before = services.Snapshots.LatestBefore(t.RootPath, nowUtc.AddDays(-5));
        if (before is null) return [];
        var now = new Dictionary<string, long>();
        foreach (var c in t.Children(t.Root).Where(t.IsDir))
        {
            now[t.GetFullPath(c)] = t.Size(c);
            foreach (var g in t.Children(c).Where(t.IsDir).Take(50)) now[t.GetFullPath(g)] = t.Size(g);
        }
        // Only compare like with like (a parent and its child would both show the same growth).
        var grew = Miu.Core.Insights.SnapshotDiff.Grew(before.Value.Folders, now, top * 3);
        var result = new List<(string, long)>();
        foreach (var g in grew)
            if (!result.Any(r => g.Path.StartsWith(r.Item1 + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                result.Add(g);
        return result.Take(top).ToList();
    }
}
