using Miu.Platform.Abstractions;
using Miu.Platform.MacOS;
using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

#pragma warning disable CA1416
public sealed class MacAndLinuxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "miu-maclinux-" + Guid.NewGuid().ToString("N"));

    public MacAndLinuxTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class MemoryStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string value) => Values[key] = value;
        public void Delete(string key) => Values.Remove(key);
    }

    /// <summary>A pretend secret-tool with its own keyring.</summary>
    private sealed class FakeSecretTool
    {
        public Dictionary<string, string> Keyring { get; } = new();
        public List<string> CommandLines { get; } = [];
        public bool Works { get; set; } = true;

        public (int, string) Run(IReadOnlyList<string> args, string? stdin)
        {
            CommandLines.Add(string.Join(' ', args));
            if (!Works) return (1, "");
            var key = args[^1];
            switch (args[0])
            {
                case "store": Keyring[key] = stdin ?? ""; return (0, "");
                case "lookup": return Keyring.TryGetValue(key, out var v) ? (0, v) : (1, "");
                default: Keyring.Remove(key); return (0, "");
            }
        }
    }

    [Fact]
    public void Linux_keyring_is_used_when_it_answers_and_secrets_never_go_on_the_command_line()
    {
        var tool = new FakeSecretTool();
        var file = new MemoryStore();
        file.Set("telegram.token", "OLD-FILE-SECRET");
        var k = new LinuxKeyring(file, tool.Run);
        k.UsesKeyring.Should().BeTrue();
        k.Get("telegram.token").Should().Be("OLD-FILE-SECRET"); // migrated
        tool.Keyring["telegram.token"].Should().Be("OLD-FILE-SECRET");
        file.Values.Should().BeEmpty();
        k.Set("cloud.1", "TOP-SECRET");
        k.Get("cloud.1").Should().Be("TOP-SECRET");
        tool.CommandLines.Should().NotContain(c => c.Contains("TOP-SECRET") || c.Contains("OLD-FILE-SECRET"));
        k.Delete("cloud.1");
        k.Get("cloud.1").Should().BeNull();
        tool.Keyring.Keys.Should().NotContain(x => x.StartsWith("probe-"));
    }

    [Fact]
    public void Without_a_keyring_secrets_stay_in_the_private_file()
    {
        var tool = new FakeSecretTool { Works = false };
        var file = new MemoryStore();
        var k = new LinuxKeyring(file, tool.Run);
        k.UsesKeyring.Should().BeFalse();
        k.Set("a", "b");
        file.Values["a"].Should().Be("b");
        k.Get("a").Should().Be("b");
    }

    [Fact]
    public void Mac_launch_agent_writes_a_plist_with_each_argument()
    {
        var agent = new MacLaunchAgent(_dir);
        agent.IsEnabled.Should().BeFalse();
        agent.Enable("/Applications/Miu Smart Storage.app/Contents/MacOS/Miu", "\"/opt/x y/Miu.dll\" --background");
        agent.IsEnabled.Should().BeTrue();
        var plist = File.ReadAllText(Path.Combine(_dir, "com.duykhang.miu.plist"));
        plist.Should().Contain("<string>/Applications/Miu Smart Storage.app/Contents/MacOS/Miu</string>")
            .And.Contain("<string>/opt/x y/Miu.dll</string>").And.Contain("<string>--background</string>").And.Contain("<key>RunAtLoad</key><true/>");
        MacLaunchAgent.Plist("a&b", []).Should().Contain("a&amp;b");
        agent.Disable();
        agent.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Mac_power_state_reads_pmset()
    {
        MacPowerState.OnBattery("Now drawing from 'Battery Power'\n -InternalBattery-0 (id=1)\t83%; discharging;").Should().BeTrue();
        MacPowerState.OnBattery("Now drawing from 'AC Power'\n -InternalBattery-0 (id=1)\t100%; charged;").Should().BeFalse();
    }

    [Fact]
    public async Task Mac_trash_roundtrip()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var f = Path.Combine(_dir, "to-trash.txt");
        File.WriteAllText(f, "hello");
        var bin = new MacTrash();
        var r = await bin.RecycleAsync([f], null, default);
        r.Failed.Should().BeEmpty();
        File.Exists(f).Should().BeFalse();
        var item = r.Recycled.Single();
        item.TrashLocation.Should().Contain(".Trash");
        item.Bytes.Should().Be(5);
        (await bin.RestoreAsync([item], default)).Restored.Should().Equal(f);
        File.ReadAllText(f).Should().Be("hello");
    }

    [Fact]
    public void Mac_keychain_roundtrip()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var k = new MacKeychain("Miu Smart Storage Test");
        var key = "test-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            k.Set(key, "first");
        }
        catch (IOException) { return; } // a CI machine without an unlocked login keychain
        k.Get(key).Should().Be("first");
        k.Set(key, "second ✓");
        k.Get(key).Should().Be("second ✓");
        k.Delete(key);
        k.Get(key).Should().BeNull();
    }
}
