using System.ComponentModel;
using System.Diagnostics;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Portable;

/// <summary>Runs secret-tool: (arguments, text for stdin) → (exit code, stdout). Secrets go through stdin, never the command line.</summary>
public delegate (int ExitCode, string Output) SecretToolRunner(IReadOnlyList<string> args, string? stdin);

/// <summary>
/// The desktop keyring (GNOME Keyring, KWallet via the Secret Service) through <c>secret-tool</c>. If there is no
/// keyring (a server, a locked or missing service), secrets stay in the owner-only file instead. Secrets that are
/// still in the file move to the keyring the first time they are read.
/// </summary>
public sealed class LinuxKeyring : ISecretStore
{
    private const string App = "miu-smart-storage";
    private readonly SecretToolRunner _run;
    private readonly ISecretStore _file;
    private readonly Lazy<bool> _available;

    public LinuxKeyring(ISecretStore fileFallback, SecretToolRunner? runner = null)
    {
        _file = fileFallback;
        _run = runner ?? RunSecretTool;
        _available = new Lazy<bool>(Probe);
    }

    public bool UsesKeyring => _available.Value;

    public string? Get(string key)
    {
        if (!_available.Value) return _file.Get(key);
        var (code, output) = _run(["lookup", "application", App, "key", key], null);
        if (code == 0) return output;
        if (_file.Get(key) is { } old)
        {
            Set(key, old); // move it into the keyring
            return old;
        }
        return null;
    }

    public void Set(string key, string value)
    {
        if (!_available.Value) { _file.Set(key, value); return; }
        var (code, _) = _run(["store", "--label", $"Miu Smart Storage ({key})", "application", App, "key", key], value);
        if (code != 0) { _file.Set(key, value); return; } // the keyring went away: don't lose the secret
        _file.Delete(key);
    }

    public void Delete(string key)
    {
        if (_available.Value) _run(["clear", "application", App, "key", key], null);
        _file.Delete(key);
    }

    /// <summary>True when a keyring answers: stores, reads back and removes a test value.</summary>
    private bool Probe()
    {
        var probe = "probe-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (_run(["store", "--label", "Miu Smart Storage (check)", "application", App, "key", probe], "ok").ExitCode != 0) return false;
            var ok = _run(["lookup", "application", App, "key", probe], null) is (0, "ok");
            _run(["clear", "application", App, "key", probe], null);
            return ok;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    private static (int, string) RunSecretTool(IReadOnlyList<string> args, string? stdin)
    {
        var psi = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (127, "");
            if (stdin is not null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (124, ""); } // a keyring prompt nobody answers
            return (p.ExitCode, output.Result.TrimEnd('\n'));
        }
        catch (Win32Exception) { return (127, ""); } // secret-tool isn't installed
    }
}
