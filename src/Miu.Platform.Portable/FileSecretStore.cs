using System.Text.Json;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Portable;

/// <summary>
/// A secrets file only your user account can read (mode 0600) in Miu's app-data folder. Used on Linux and macOS
/// until the system keyrings (libsecret, Keychain) are supported.
/// </summary>
public sealed class FileSecretStore(string dir) : ISecretStore
{
    private readonly string _file = Path.Combine(dir, "secrets.json");
    private readonly object _gate = new();

    public string? Get(string key)
    {
        lock (_gate) return Read().GetValueOrDefault(key);
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var all = Read();
            all[key] = value;
            Write(all);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(key)) Write(all);
        }
    }

    private Dictionary<string, string> Read()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private void Write(Dictionary<string, string> all)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, ownerOnly | UnixFileMode.UserExecute);
        var tmp = _file + ".tmp";
        File.Delete(tmp); // a leftover could have looser permissions, and truncating keeps them
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = ownerOnly; // owner-only from the first byte, no window
        using (var fs = new FileStream(tmp, options)) JsonSerializer.Serialize(fs, all);
        File.Move(tmp, _file, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
