namespace Miu.Dev;

/// <summary>Folder names that hold rebuildable output, and the stacks for which they are junk.</summary>
public static class DevJunkCatalog
{
    public const string AnyStack = "*";

    private static readonly Dictionary<string, string[]> JunkByName = new(StringComparer.Ordinal)
    {
        ["node_modules"] = ["Node"], [".next"] = ["Node"], [".nuxt"] = ["Node"], [".svelte-kit"] = ["Node"],
        [".output"] = ["Node"], ["storybook-static"] = ["Node"], [".turbo"] = ["Node"], [".parcel-cache"] = ["Node"],
        [".vite"] = ["Node"], [".angular"] = ["Node"], [".vercel"] = ["Node"], [".netlify"] = ["Node"],
        ["coverage"] = ["Node"], [".nyc_output"] = ["Node"], ["dist"] = ["Node"], ["out"] = ["Node"],
        ["build"] = ["Node", "JVM", "Flutter"], ["target"] = ["Rust", "JVM"], ["bin"] = [".NET"], ["obj"] = [".NET"],
        [".gradle"] = ["JVM"], [".venv"] = ["Python"], ["venv"] = ["Python"], ["__pycache__"] = [AnyStack],
        [".pytest_cache"] = [AnyStack], [".mypy_cache"] = [AnyStack], [".ruff_cache"] = [AnyStack], [".tox"] = ["Python"],
        [".dart_tool"] = ["Flutter"], ["vendor"] = ["Go", "PHP"], [".terraform"] = [AnyStack],
    };

    public static IReadOnlySet<string> AllJunkNames { get; } = new HashSet<string>(JunkByName.Keys, StringComparer.Ordinal);

    public static bool IsJunkFolder(string name, IReadOnlyCollection<string> stacks) =>
        JunkByName.TryGetValue(name, out var owners) && owners.Any(o => o == AnyStack || stacks.Contains(o));
}
