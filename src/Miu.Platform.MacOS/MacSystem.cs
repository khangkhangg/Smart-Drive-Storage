using System.Diagnostics;
using System.Security;
using System.Text;
using Miu.Platform.Abstractions;

namespace Miu.Platform.MacOS;

/// <summary>Start at login with a LaunchAgent in ~/Library/LaunchAgents (removed when you turn it off).</summary>
public sealed class MacLaunchAgent(string? agentsDir = null) : IStartupRegistration
{
    public const string Label = "com.duykhang.miu";
    private readonly string _file = Path.Combine(agentsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"), Label + ".plist");

    public bool IsEnabled => File.Exists(_file);

    public void Enable(string executablePath, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, Plist(executablePath, SplitArguments(arguments)));
    }

    public void Disable()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    public static string Plist(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append($"  <key>Label</key><string>{Label}</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var a in new[] { exe }.Concat(args)) sb.Append($"    <string>{SecurityElement.Escape(a)}</string>\n");
        sb.Append("  </array>\n  <key>RunAtLoad</key><true/>\n  <key>ProcessType</key><string>Interactive</string>\n</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>Splits "a \"b c\" d" into a, b c, d.</summary>
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        foreach (var c in arguments)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted) { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}

/// <summary>Battery or mains, from <c>pmset -g batt</c>. Unknown counts as plugged in.</summary>
public sealed class MacPowerState : IPowerState
{
    public bool IsOnBattery
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("pmset", "-g batt") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (p is null) return false;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return OnBattery(output);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }
    }

    public static bool OnBattery(string pmsetOutput) => pmsetOutput.Contains("'Battery Power'", StringComparison.Ordinal);
}
