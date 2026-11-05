using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Miu.App.Services;

/// <param name="DownloadUrl">The file for this system (installer, .dmg, AppImage…), or null to use the release page.</param>
/// <param name="ChecksumUrl">The release's SHA256SUMS file, used to check a download before it's offered.</param>
public sealed record UpdateInfo(string Version, string PageUrl, string? DownloadUrl, string? AssetName, string Notes, string? ChecksumUrl = null, bool IsPrerelease = false);

/// <summary>
/// Asks GitHub for the newest release. Nothing is downloaded or installed without you: Miu shows the version and a
/// Download button for your system. Only the release list is requested (no data about you is sent).
/// </summary>
public sealed class UpdateService(HttpClient http, string repository = "khangkhangg/Smart-Drive-Storage")
{
    /// <param name="includePrereleases">Also offer test builds (pre-releases), for people who want the newest changes first.</param>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default, bool includePrereleases = false)
    {
        var url = includePrereleases
            ? $"https://api.github.com/repos/{repository}/releases?per_page=15"
            : $"https://api.github.com/repos/{repository}/releases/latest";
        using var req = Request(url, currentVersion);
        using var r = await http.SendAsync(req, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        UpdateInfo? best = null;
        foreach (var root in releases)
        {
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var pre = root.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (pre && !includePrereleases) continue;
            var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            if (Compare(version, currentVersion) <= 0 || (best is not null && Compare(version, best.Version) <= 0)) continue;
            var assets = root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => (Name: x.GetProperty("name").GetString() ?? "", Url: x.GetProperty("browser_download_url").GetString() ?? "")).ToList()
                : [];
            var pick = AssetFor(assets.Select(x => x.Name).ToList(), CurrentOs(), RuntimeInformation.OSArchitecture, LinuxInstallKind());
            var download = pick is null ? null : assets.First(x => x.Name == pick).Url;
            var sums = assets.FirstOrDefault(x => x.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)).Url;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            best = new UpdateInfo(version, root.GetProperty("html_url").GetString() ?? "", download, pick,
                notes.Length > 600 ? notes[..600] + "…" : notes, string.IsNullOrEmpty(sums) ? null : sums, pre);
        }
        return best;
    }

    private static HttpRequestMessage Request(string url, string currentVersion)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MiuSmartStorage", currentVersion.Split('+')[0]));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return req;
    }

    /// <summary>Where downloaded updates go: Downloads/Miu updates.</summary>
    public static string DownloadFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Downloads", "Miu updates");
    }

    /// <summary>
    /// Downloads the update for this system into <paramref name="folder"/>, through a .partial file, and checks it
    /// against the release's SHA256SUMS when there is one. A file that doesn't match is deleted, never offered.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo u, string folder, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (u.DownloadUrl is null || u.AssetName is null) throw new InvalidOperationException("This release has no file for your system.");
        if (u.AssetName.IndexOfAny(['/', '\\']) >= 0 || u.AssetName.Contains("..", StringComparison.Ordinal)) throw new InvalidOperationException("Odd file name in the release.");
        Directory.CreateDirectory(folder);
        var final = Path.Combine(folder, u.AssetName);
        var partial = final + ".partial";
        using (var r = await http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            r.EnsureSuccessStatusCode();
            var total = r.Content.Headers.ContentLength;
            await using var src = await r.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }
        if (u.ChecksumUrl is { } sumsUrl)
        {
            var sums = await http.GetStringAsync(sumsUrl, ct);
            var expected = ExpectedHash(sums, u.AssetName);
            string actual;
            await using (var f = File.OpenRead(partial)) actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(f, ct));
            if (expected is null || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidOperationException(expected is null
                    ? "The release's checksum list doesn't mention this file, so Miu didn't keep it."
                    : "The download doesn't match the release's checksum, so Miu deleted it. Try again later.");
            }
        }
        File.Move(partial, final, overwrite: true);
        return final;
    }

    /// <summary>The SHA-256 for a file in a SHA256SUMS list ("hash  name" or "hash *name").</summary>
    public static string? ExpectedHash(string sums, string name) =>
        sums.Split('\n').Select(l => l.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[1].TrimStart('*').Trim() == name).Select(p => p[0]).FirstOrDefault();

    public static string CurrentOs() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Picks the release file for this system: installer on Windows, .dmg on macOS, AppImage (else .deb/.tar.gz) on Linux.</summary>
    /// <summary>How Miu was installed on Linux: "appimage", "deb" or "tar".</summary>
    public static string LinuxInstallKind() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } ? "appimage"
        : (Environment.ProcessPath ?? "").StartsWith("/opt/miu-smart-storage/", StringComparison.Ordinal) ? "deb" : "tar";

    public static string? AssetFor(IReadOnlyList<string> names, string os, Architecture arch, string linuxKind = "appimage")
    {
        string? First(params string[] suffixes) =>
            suffixes.Select(s => names.FirstOrDefault(n => n.EndsWith(s, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n is not null);
        return os switch
        {
            "windows" => First("-windows-x64-setup.exe", "-windows-x64-portable.zip"),
            "macos" => arch == Architecture.Arm64 ? First("-macos-arm64.dmg", "-macos-arm64.zip") : First("-macos-x64.dmg", "-macos-x64.zip"),
            _ => linuxKind switch
            {
                "deb" => First("_amd64.deb", "-x86_64.AppImage", "-linux-x64.tar.gz"),
                "tar" => First("-linux-x64.tar.gz", "-x86_64.AppImage", "_amd64.deb"),
                _ => First("-x86_64.AppImage", "_amd64.deb", "-linux-x64.tar.gz"),
            },
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
