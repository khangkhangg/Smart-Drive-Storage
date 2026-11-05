using Miu.Core.Sync;
using Miu.Data;
using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.App.Services;

/// <summary>Finding, connecting and reconnecting network shares. Passwords go only to the secret store.</summary>
public sealed class NetworkService(AppServices services)
{
    public INetworkDrives Drives => services.NetworkDrives;

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken ct = default) => ShareDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3), ct);

    public async Task<NetResult> MapAsync(string uncText, string? letter, string? user, string? password, bool reconnect, CancellationToken ct = default)
    {
        if (!Drives.IsSupported) return NetResult.Fail(Drives.UnsupportedReason);
        if (!UncPath.TryParse(uncText, out var unc)) return NetResult.Fail(@"Type the shared folder like \\computer\folder (or smb://computer/folder).");
        var root = unc!.ShareRoot; // Windows maps a whole share; sub-folders are opened inside it
        user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        letter = string.IsNullOrWhiteSpace(letter) ? null : letter.Trim().ToUpperInvariant();
        var r = await Drives.MapAsync(root, letter, user, password, reconnect, ct);
        if (!r.Ok) return r;
        var share = new NetworkShare(0, root, letter, user, reconnect);
        services.NetworkShares.Save(share);
        if (reconnect && !string.IsNullOrEmpty(password)) services.Secrets.Set(share.SecretKey, password);
        else services.Secrets.Delete(share.SecretKey);
        return r;
    }

    public async Task<NetResult> UnmapAsync(MappedDrive drive, CancellationToken ct = default)
    {
        var r = await Drives.UnmapAsync(drive.Letter ?? drive.Unc, ct);
        if (r.Ok || !Drives.Mapped().Any(m => m.Unc.Equals(drive.Unc, StringComparison.OrdinalIgnoreCase)))
        {
            services.NetworkShares.Delete(drive.Unc);
            services.Secrets.Delete(NetworkShare.SecretKeyFor(drive.Unc));
        }
        return r;
    }

    /// <summary>At start-up: connects saved shares marked "reconnect" that Windows hasn't connected itself.</summary>
    public async Task<int> ReconnectSavedAsync(CancellationToken ct = default)
    {
        if (!Drives.IsSupported) return 0;
        var mapped = Drives.Mapped();
        var n = 0;
        foreach (var s in services.NetworkShares.All().Where(s => s.Reconnect))
        {
            if (mapped.Any(m => m.Available && m.Unc.Equals(s.Unc, StringComparison.OrdinalIgnoreCase))) continue;
            var letter = s.Letter;
            if (mapped.FirstOrDefault(m => string.Equals(m.Letter, s.Letter, StringComparison.OrdinalIgnoreCase)) is { } holder)
            {
                if (holder.Unc.Equals(s.Unc, StringComparison.OrdinalIgnoreCase)) await Drives.UnmapAsync(s.Letter!, ct); // remembered but not connected
                else letter = null; // the letter belongs to another share now: connect without one
            }
            if ((await Drives.MapAsync(s.Unc, letter, s.UserName, services.Secrets.Get(s.SecretKey), true, ct)).Ok) n++;
        }
        return n;
    }

    /// <summary>Network paths are scanned with fewer threads so the NAS and Wi-Fi aren't flooded.</summary>
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (OperatingSystem.IsWindows()) return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            var best = DriveInfo.GetDrives().Where(d => path.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return best is not null && (best.DriveType == DriveType.Network || best.DriveFormat is "cifs" or "smb3" or "smbfs" or "nfs" or "nfs4" or "fuse.sshfs");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }
}
