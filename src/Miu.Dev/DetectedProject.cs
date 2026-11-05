namespace Miu.Dev;

public enum IdleBucket { Active, Cooling, Idle, Abandoned }

public sealed record DetectedProject(
    int NodeId,
    string Path,
    string Name,
    IReadOnlyList<string> Stacks,
    DateTime LastActivityUtc,
    int IdleDays,
    IdleBucket Bucket,
    long TotalBytes,
    long RebuildableBytes);
