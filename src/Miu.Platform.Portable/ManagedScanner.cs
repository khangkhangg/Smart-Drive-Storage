using System.Diagnostics;
using Miu.Core.Scanning;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Portable;

/// <summary>
/// Cross-platform scanner built on System.IO. Directories are walked in parallel; reparse points
/// (symlinks, junctions) are recorded but never followed; unreadable folders are skipped.
/// </summary>
public sealed class ManagedScanner(int maxParallelism = 0) : IScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly int _parallelism = maxParallelism > 0 ? maxParallelism : Math.Max(2, Environment.ProcessorCount);

    public Task<ScanTree> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(rootPath);
        return Task.Run(() =>
        {
            var state = new State(new ScanTree(full), progress, ct, _parallelism);
            Walk(state, state.Tree.Root, new DirectoryInfo(full));
            ct.ThrowIfCancellationRequested();
            state.Tree.ComputeAggregates();
            state.Report(full, force: true);
            return state.Tree;
        }, ct);
    }

    private static void Walk(State s, int id, DirectoryInfo dir)
    {
        s.Ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref s.Dirs);
        var subdirs = new List<(int Id, DirectoryInfo Dir)>();
        try
        {
            foreach (var entry in dir.EnumerateFileSystemInfos("*", Options))
            {
                var modified = SafeTime(entry);
                if (entry is FileInfo f)
                {
                    long len = 0;
                    try { len = f.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    s.Tree.Add(id, f.Name, false, len, modified);
                    Interlocked.Increment(ref s.Files);
                    Interlocked.Add(ref s.Bytes, len);
                }
                else if (entry is DirectoryInfo d)
                {
                    var childId = s.Tree.Add(id, d.Name, true, 0, modified);
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0) subdirs.Add((childId, d));
                }
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        s.Report(dir.FullName, force: false);

        if (subdirs.Count == 0) return;
        if (subdirs.Count == 1) { Walk(s, subdirs[0].Id, subdirs[0].Dir); return; }
        Parallel.ForEach(subdirs, new ParallelOptions { CancellationToken = s.Ct, MaxDegreeOfParallelism = s.Parallelism },
            sd => Walk(s, sd.Id, sd.Dir));
    }

    private static DateTime SafeTime(FileSystemInfo e)
    {
        try { return e.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
    }

    private sealed class State(ScanTree tree, IProgress<ScanProgress>? progress, CancellationToken ct, int parallelism)
    {
        public readonly ScanTree Tree = tree;
        public readonly CancellationToken Ct = ct;
        public readonly int Parallelism = parallelism;
        public long Files, Dirs, Bytes;
        private long _lastReportTicks;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Report(string path, bool force)
        {
            if (progress is null) return;
            var now = _clock.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastReportTicks);
            if (!force && now - last < 100) return;
            if (!force && Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;
            progress.Report(new ScanProgress(Interlocked.Read(ref Files), Interlocked.Read(ref Dirs), Interlocked.Read(ref Bytes), path));
        }
    }
}
