using System.Text;

namespace Miu.Platform.Windows;

public sealed record RecycleBinEntry(string OriginalPath, long Size, DateTime DeletedUtc);

/// <summary>
/// Reads the <c>$I…</c> index files Windows writes next to each <c>$R…</c> item in <c>$Recycle.Bin</c>.
/// Version 1 (Vista–8.1): fixed 520-byte path. Version 2 (Windows 10+): length-prefixed path.
/// </summary>
public static class RecycleBinIndex
{
    public static RecycleBinEntry? Parse(byte[] data)
    {
        try
        {
            if (data.Length < 24) return null;
            var version = BitConverter.ToInt64(data, 0);
            var size = BitConverter.ToInt64(data, 8);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(data, 16));
            string path;
            if (version == 1)
            {
                if (data.Length < 24 + 520) return null;
                path = Encoding.Unicode.GetString(data, 24, 520);
            }
            else if (version == 2)
            {
                if (data.Length < 28) return null;
                var chars = BitConverter.ToInt32(data, 24);
                if (chars <= 0 || 28 + chars * 2 > data.Length) return null;
                path = Encoding.Unicode.GetString(data, 28, chars * 2);
            }
            else return null;
            path = path.TrimEnd('\0');
            var nul = path.IndexOf('\0');
            if (nul >= 0) path = path[..nul];
            return path.Length == 0 ? null : new RecycleBinEntry(path, size, deleted);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
