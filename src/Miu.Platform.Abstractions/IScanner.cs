using Miu.Core.Scanning;

namespace Miu.Platform.Abstractions;

public sealed record ScanProgress(long Files, long Directories, long Bytes, string CurrentPath);

/// <summary>Builds a <see cref="ScanTree"/> for a folder or drive.</summary>
public interface IScanner
{
    Task<ScanTree> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct);
}
