using Miu.Platform.Portable;
using Miu.Platform.Windows;

namespace Miu.Platform.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "miu-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void File_store_roundtrip_and_owner_only_permissions()
    {
        var s = new FileSecretStore(_dir);
        s.Get("telegram.token").Should().BeNull();
        s.Set("telegram.token", "123:ABC");
        s.Get("telegram.token").Should().Be("123:ABC");
        if (!OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            var mode = File.GetUnixFileMode(Path.Combine(_dir, "secrets.json"));
#pragma warning restore CA1416
            mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        s.Delete("telegram.token");
        s.Get("telegram.token").Should().BeNull();
    }

    [Fact]
    public void Windows_credential_store_roundtrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = new WindowsCredentialStore("MiuTest");
        s.Set("k", "secret value");
        s.Get("k").Should().Be("secret value");
        s.Delete("k");
        s.Get("k").Should().BeNull();
    }
}
