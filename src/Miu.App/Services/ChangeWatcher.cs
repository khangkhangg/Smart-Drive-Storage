using System.Collections.Concurrent;
using Miu.Core.Sync;
using Miu.Platform.Portable;

namespace Miu.App.Services;

/// <summary>
/// Live mirroring: watches the source of every enabled "run when files change" job. A change marks the job; the
/// automation tick runs it once things have been quiet for <see cref="Quiet"/> (so saving a big file, or a burst of
/// changes, makes one run, not hundreds).
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
    public static TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, DateTime> _dirty = new();
    private readonly Dictionary<long, (string Source, FileSystemWatcher? Watcher)> _watchers = [];
    private readonly Func<DateTime> _nowUtc;

    public ChangeWatcher(Func<DateTime>? nowUtc = null) => _nowUtc = nowUtc ?? (() => DateTime.UtcNow);

    /// <summary>Starts watching new jobs and stops watching removed or changed ones.</summary>
    public void Refresh(IEnumerable<SyncJob> jobs)
    {
        var wanted = jobs.Where(j => j.Enabled && j.RunOnChange).ToDictionary(j => j.Id, j => j.Source);
        lock (_watchers)
        {
            foreach (var id in _watchers.Keys.ToList())
                if (!wanted.TryGetValue(id, out var src) || src != _watchers[id].Source)
                {
                    _watchers[id].Watcher?.Dispose();
                    _watchers.Remove(id);
                    _dirty.TryRemove(id, out _);
                }
            foreach (var (id, src) in wanted)
                if (!_watchers.ContainsKey(id)) _watchers[id] = (src, Start(id, src));
        }
    }

    public IReadOnlyCollection<long> Watched { get { lock (_watchers) return _watchers.Where(w => w.Value.Watcher is not null).Select(w => w.Key).ToList(); } }

    private FileSystemWatcher? Start(long id, string source)
    {
        try
        {
            if (!Directory.Exists(source)) return null; // a drive that isn't connected: try again on the next refresh
            var w = new FileSystemWatcher(source) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler on = (_, e) => Touch(id, e.FullPath, source);
            w.Created += on;
            w.Changed += on;
            w.Deleted += on;
            w.Renamed += (_, e) => Touch(id, e.FullPath, source);
            w.Error += (_, _) => Mark(id); // too many changes at once: run anyway
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    private void Touch(long id, string fullPath, string source)
    {
        var rel = Path.GetRelativePath(source, fullPath).Replace('\\', '/');
        // Miu's own files (set-aside copies, partial copies) don't count as changes.
        if (rel.StartsWith(SyncLister.TrashFolder, StringComparison.OrdinalIgnoreCase) || rel.EndsWith(SyncLister.PartialSuffix, StringComparison.Ordinal)) return;
        Mark(id);
    }

    public void Mark(long id) => _dirty[id] = _nowUtc();

    /// <summary>Jobs that changed and have been quiet long enough. They are un-marked; mark them again to retry later.</summary>
    public IReadOnlyList<long> TakeDue()
    {
        var now = _nowUtc();
        var due = _dirty.Where(d => now - d.Value >= Quiet).Select(d => d.Key).ToList();
        foreach (var id in due) _dirty.TryRemove(id, out _);
        return due;
    }

    public void Dispose()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers.Values) w.Watcher?.Dispose();
            _watchers.Clear();
        }
    }
}
