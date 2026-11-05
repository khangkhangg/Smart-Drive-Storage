using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Windows;

/// <summary>Maps and unmaps SMB shares with the Windows networking API (the same as "Map network drive" in Explorer).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkDrives : INetworkDrives
{
    private const int RESOURCETYPE_DISK = 1;
    private const int CONNECT_UPDATE_PROFILE = 0x1, CONNECT_TEMPORARY = 0x4;
    private const int NO_ERROR = 0, ERROR_MORE_DATA = 234, NERR_Success = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string Name;
        public uint Type;
        public string? Remark;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(ref NETRESOURCE resource, string? password, string? user, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(string name, int flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public bool IsSupported => true;
    public string UnsupportedReason => "";

    public IReadOnlyList<MappedDrive> Mapped()
    {
        var list = new List<MappedDrive>();
        foreach (var letter in AllLetters())
        {
            var sb = new StringBuilder(512);
            var len = sb.Capacity;
            var rc = WNetGetConnectionW(letter, sb, ref len);
            if (rc == NO_ERROR) list.Add(new MappedDrive(letter, sb.ToString(), true));
            else if (rc == 1201) list.Add(new MappedDrive(letter, sb.ToString(), false)); // ERROR_CONNECTION_UNAVAIL: remembered, not connected
        }
        return list;
    }

    public IReadOnlyList<string> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[..2].ToUpperInvariant()).ToHashSet();
        foreach (var m in Mapped()) if (m.Letter is { } l) used.Add(l.ToUpperInvariant());
        return AllLetters().Where(l => !used.Contains(l) && l[0] >= 'D').Reverse().ToList(); // Z: first, like Explorer
    }

    public Task<NetResult> MapAsync(string unc, string? letter, string? user, string? password, bool reconnectAtSignIn, CancellationToken ct) => Task.Run(() =>
    {
        var res = new NETRESOURCE { Type = RESOURCETYPE_DISK, LocalName = letter, RemoteName = unc };
        var flags = letter is not null && reconnectAtSignIn ? CONNECT_UPDATE_PROFILE : CONNECT_TEMPORARY;
        var rc = WNetAddConnection2W(ref res, string.IsNullOrEmpty(password) ? null : password, string.IsNullOrEmpty(user) ? null : user, flags);
        return rc == NO_ERROR
            ? NetResult.Success(letter is null ? $"Connected to {unc}." : $"{unc} is now {letter}.")
            : NetResult.Fail(Explain(rc));
    }, ct);

    public Task<NetResult> UnmapAsync(string letterOrUnc, CancellationToken ct) => Task.Run(() =>
    {
        var rc = WNetCancelConnection2W(letterOrUnc, CONNECT_UPDATE_PROFILE, false);
        return rc == NO_ERROR ? NetResult.Success($"Disconnected {letterOrUnc}.") : NetResult.Fail(Explain(rc));
    }, ct);

    public Task<IReadOnlyList<string>> ListSharesAsync(string server, CancellationToken ct) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var resume = 0;
        var rc = NetShareEnum(server.StartsWith(@"\\", StringComparison.Ordinal) ? server : @"\\" + server, 1, out var buf, -1, out var read, out _, ref resume);
        if (rc is not (NERR_Success or ERROR_MORE_DATA)) return [];
        try
        {
            var size = Marshal.SizeOf<SHARE_INFO_1>();
            var shares = new List<string>();
            for (var i = 0; i < read; i++)
            {
                var s = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * size);
                if ((s.Type & 0xFF) == 0 && !s.Name.EndsWith('$')) shares.Add(s.Name); // disk shares, not hidden/admin ones
            }
            return shares;
        }
        finally { NetApiBufferFree(buf); }
    }, ct);

    private static IEnumerable<string> AllLetters() => Enumerable.Range('A', 26).Select(c => $"{(char)c}:");

    /// <summary>Plain-language reasons for the common errors (never includes the password).</summary>
    public static string Explain(int code) => code switch
    {
        5 => "Access denied. Check the user name and password.",
        53 or 67 => "Couldn't find that computer or shared folder. Check the name and that it is turned on.",
        85 => "That drive letter is already in use. Pick another one.",
        86 or 1326 => "The user name or password is wrong.",
        1219 => "You're already connected to this computer with a different user. Disconnect it first.",
        1203 or 1222 => "No network connection.",
        2250 => "That drive isn't connected.",
        _ => new Win32Exception(code).Message,
    };
}
