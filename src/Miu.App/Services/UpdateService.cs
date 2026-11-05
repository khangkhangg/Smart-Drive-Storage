using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Miu.App.Services;

/// <param name="DownloadUrl">The file for this system (installer, .dmg, AppImage…), or null to use the release page.</param>
public sealed record UpdateInfo(string Version, string PageUrl, string? DownloadUrl, string? AssetName, string Notes);

/// <summary>
/// Asks GitHub for the newest release. Nothing is downloaded or installed without you: Miu shows the version and a
/// Download button for your system. Only the release list is requested (no data about you is sent).
/// </summary>
public sealed class UpdateService(HttpClient http, string repository = "khangkhangg/Smart-Drive-Storage")
{
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MiuSmartStorage", currentVersion.Split('+')[0]));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var r = await http.SendAsync(req, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (Compare(version, currentVersion) <= 0) return null;
        var assets = root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => (Name: x.GetProperty("name").GetString() ?? "", Url: x.GetProperty("browser_download_url").GetString() ?? "")).ToList()
            : [];
        var pick = AssetFor(assets.Select(x => x.Name).ToList(), CurrentOs(), RuntimeInformation.OSArchitecture);
        var url = pick is null ? null : assets.First(x => x.Name == pick).Url;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(version, root.GetProperty("html_url").GetString() ?? "", url, pick, notes.Length > 600 ? notes[..600] + "…" : notes);
    }

    public static string CurrentOs() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Picks the release file for this system: installer on Windows, .dmg on macOS, AppImage (else .deb/.tar.gz) on Linux.</summary>
    public static string? AssetFor(IReadOnlyList<string> names, string os, Architecture arch)
    {
        string? First(params string[] suffixes) =>
            suffixes.Select(s => names.FirstOrDefault(n => n.EndsWith(s, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n is not null);
        return os switch
        {
            "windows" => First("-windows-x64-setup.exe", "-windows-x64-portable.zip"),
            "macos" => arch == Architecture.Arm64 ? First("-macos-arm64.dmg", "-macos-arm64.zip") : First("-macos-x64.dmg", "-macos-x64.zip"),
            _ => First("-x86_64.AppImage", "_amd64.deb", "-linux-x64.tar.gz"),
        };
    }

    /// <summary>Semantic-version order: 1.2.10 &gt; 1.2.9, and 1.0.0-beta.2 &lt; 1.0.0.</summary>
    public static int Compare(string a, string b)
    {
        static (int[] Nums, string? Pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V').Split('+')[0];
            var dash = v.IndexOf('-');
            var pre = dash >= 0 ? v[(dash + 1)..] : null;
            var nums = (dash >= 0 ? v[..dash] : v).Split('.').Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
            return (nums, pre);
        }
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            var c = (i < na.Length ? na[i] : 0).CompareTo(i < nb.Length ? nb[i] : 0);
            if (c != 0) return c;
        }
        if (pa is null && pb is null) return 0;
        if (pa is null) return 1;
        if (pb is null) return -1;
        var xa = pa.Split('.');
        var xb = pb.Split('.');
        for (var i = 0; i < Math.Max(xa.Length, xb.Length); i++)
        {
            if (i >= xa.Length) return -1;
            if (i >= xb.Length) return 1;
            var bothNumeric = int.TryParse(xa[i], out var ia) & int.TryParse(xb[i], out var ib);
            var c = bothNumeric ? ia.CompareTo(ib) : string.CompareOrdinal(xa[i], xb[i]);
            if (c != 0) return Math.Sign(c);
        }
        return 0;
    }
}
