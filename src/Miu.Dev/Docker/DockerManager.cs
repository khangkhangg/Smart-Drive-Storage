using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miu.Dev.Docker;

/// <param name="Project">The Docker Compose project it belongs to (from its labels), or "".</param>
public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, string Ports, string Project, string CreatedAt)
{
    public bool IsRunning => State is "running" or "restarting";
    public bool IsPaused => State == "paused";
    public bool IsStopped => !IsRunning && !IsPaused;

    /// <summary>Host ports you can open in a browser ("0.0.0.0:8080->80/tcp" → 8080).</summary>
    public IReadOnlyList<int> HostPorts => Regex.Matches(Ports, @"(?:0\.0\.0\.0|\[::\]|127\.0\.0\.1|::):(\d+)->")
        .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToList();
}

/// <param name="Containers">How many containers (running or not) use it.</param>
public sealed record DockerImage(string Id, string Repository, string Tag, string Size, string CreatedSince, int Containers)
{
    public string Name => Repository == "<none>" ? $"<untagged> {Id}" : $"{Repository}:{Tag}";
    public bool IsDangling => Repository == "<none>";
}

/// <param name="Links">How many containers use it.</param>
public sealed record DockerVolume(string Name, string Driver, string Size, int Links);

public sealed record DockerInventory(IReadOnlyList<DockerContainer> Containers, IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerVolume> Volumes);

/// <summary>
/// Lists and manages Docker containers, images and volumes through the docker CLI. Names and ids are passed as
/// separate arguments (never through a shell) and checked first.
/// </summary>
public sealed class DockerManager(IProcessRunner runner)
{
    private static readonly Regex SafeRef = new(@"^[A-Za-z0-9][A-Za-z0-9_.:/@-]*$", RegexOptions.CultureInvariant);

    /// <summary>Null when Docker isn't installed or isn't running.</summary>
    public async Task<DockerInventory?> ListAsync(CancellationToken ct = default)
    {
        var ps = await runner.RunAsync("docker", ["ps", "-a", "--no-trunc", "--format", "{{json .}}"], ct);
        if (ps.ExitCode != 0) return null;
        var containers = ParseContainers(ps.Output);
        // One call gives image and volume sizes and how many containers use each.
        var df = await runner.RunAsync("docker", ["system", "df", "-v", "--format", "{{json .}}"], ct);
        var (images, volumes) = df.ExitCode == 0 ? ParseDfVerbose(df.Output) : ([], []);
        return new DockerInventory(containers, images, volumes);
    }

    public static IReadOnlyList<DockerContainer> ParseContainers(string output)
    {
        var list = new List<DockerContainer>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var r = d.RootElement;
                var labels = S(r, "Labels");
                var project = labels.Split(',').Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == "com.docker.compose.project")?[1] ?? "";
                list.Add(new DockerContainer(S(r, "ID"), S(r, "Names"), S(r, "Image"), S(r, "State").ToLowerInvariant(), S(r, "Status"), S(r, "Ports"), project, S(r, "CreatedAt")));
            }
            catch (JsonException) { }
        }
        return list.OrderBy(c => c.Project.Length == 0).ThenBy(c => c.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static (IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerVolume> Volumes) ParseDfVerbose(string output)
    {
        var images = new List<DockerImage>();
        var volumes = new List<DockerVolume>();
        try
        {
            using var d = JsonDocument.Parse(output.Trim());
            if (d.RootElement.TryGetProperty("Images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var i in imgs.EnumerateArray())
                    images.Add(new DockerImage(S(i, "ID"), S(i, "Repository"), S(i, "Tag"), S(i, "Size"), S(i, "CreatedSince"), N(i, "Containers")));
            if (d.RootElement.TryGetProperty("Volumes", out var vols) && vols.ValueKind == JsonValueKind.Array)
                foreach (var v in vols.EnumerateArray())
                    volumes.Add(new DockerVolume(S(v, "Name"), S(v, "Driver"), S(v, "Size"), N(v, "Links")));
        }
        catch (JsonException) { }
        return (images.OrderBy(i => i.IsDangling).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                volumes.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static int N(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.GetString(), out n)) ? n : 0;

    // ---------- Actions ----------

    public Task<ProcessResult> StartAsync(string id, CancellationToken ct = default) => Run(["start", Check(id)], ct);
    public Task<ProcessResult> StopAsync(string id, CancellationToken ct = default) => Run(["stop", Check(id)], ct);
    public Task<ProcessResult> RestartAsync(string id, CancellationToken ct = default) => Run(["restart", Check(id)], ct);
    public Task<ProcessResult> PauseAsync(string id, CancellationToken ct = default) => Run(["pause", Check(id)], ct);
    public Task<ProcessResult> UnpauseAsync(string id, CancellationToken ct = default) => Run(["unpause", Check(id)], ct);

    /// <summary>Removes a container. A running one is stopped first only when <paramref name="force"/> is set.</summary>
    public Task<ProcessResult> RemoveContainerAsync(string id, bool force, CancellationToken ct = default) =>
        Run(force ? ["rm", "-f", Check(id)] : ["rm", Check(id)], ct);

    /// <summary>Removes an image. Docker refuses while a container uses it, which is what we want.</summary>
    public Task<ProcessResult> RemoveImageAsync(string id, CancellationToken ct = default) => Run(["image", "rm", Check(id)], ct);

    /// <summary>Removes a volume and the data in it. Docker refuses while a container uses it.</summary>
    public Task<ProcessResult> RemoveVolumeAsync(string name, CancellationToken ct = default) => Run(["volume", "rm", Check(name)], ct);

    /// <summary>The last lines of a container's output.</summary>
    public Task<ProcessResult> LogsAsync(string id, int tail = 300, CancellationToken ct = default) =>
        Run(["logs", "--tail", tail.ToString(System.Globalization.CultureInfo.InvariantCulture), "--timestamps", Check(id)], ct);

    /// <summary>Starts or stops every container of a Compose project, in one call.</summary>
    public Task<ProcessResult> StartAllAsync(IEnumerable<string> ids, CancellationToken ct = default) => Run(["start", .. ids.Select(Check)], ct);
    public Task<ProcessResult> StopAllAsync(IEnumerable<string> ids, CancellationToken ct = default) => Run(["stop", .. ids.Select(Check)], ct);

    private Task<ProcessResult> Run(string[] args, CancellationToken ct) => runner.RunAsync("docker", args, ct);

    public static string Check(string idOrName) =>
        SafeRef.IsMatch(idOrName) ? idOrName : throw new ArgumentException($"That isn't a Docker name or id: {idOrName}");
}
