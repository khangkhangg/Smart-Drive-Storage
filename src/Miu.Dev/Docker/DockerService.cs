namespace Miu.Dev.Docker;

/// <summary>
/// Docker disk usage and the safe one-tap cleanups. Volumes are never pruned here: they can hold databases,
/// so they are left for the user to review.
/// </summary>
public sealed class DockerService(IProcessRunner runner)
{
    /// <summary>Null when Docker isn't installed or isn't running.</summary>
    public async Task<IReadOnlyList<DockerUsage>?> GetUsageAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync("docker", ["system", "df", "--format", "{{json .}}"], ct);
        if (r.ExitCode != 0) return null;
        var usage = DockerDf.Parse(r.Output);
        return usage.Count == 0 ? null : usage;
    }

    /// <summary>Names of stopped containers (what <see cref="PruneStoppedContainersAsync"/> would remove).</summary>
    public async Task<IReadOnlyList<string>> ListStoppedContainersAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync("docker", ["ps", "-a", "--filter", "status=exited", "--filter", "status=created", "--format", "{{.Names}}"], ct);
        return r.ExitCode != 0 ? [] : r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task<ProcessResult> PruneBuildCacheAsync(CancellationToken ct = default) => runner.RunAsync("docker", ["builder", "prune", "-f"], ct);

    /// <summary>Only dangling images (untagged layers left behind by rebuilds).</summary>
    public Task<ProcessResult> PruneDanglingImagesAsync(CancellationToken ct = default) => runner.RunAsync("docker", ["image", "prune", "-f"], ct);

    public Task<ProcessResult> PruneStoppedContainersAsync(CancellationToken ct = default) => runner.RunAsync("docker", ["container", "prune", "-f"], ct);
}
