using Miu.Data;

namespace Miu.Data.Tests;

public class SettingsRepositoryTests
{
    private static SettingsRepository NewRepo()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        return new SettingsRepository(db);
    }

    [Fact]
    public void Settings_roundtrip()
    {
        var s = NewRepo();
        s.Get("x").Should().BeNull();
        s.Set("x", "1");
        s.Set("x", "2");
        s.Get("x").Should().Be("2");
        s.SetBool("b", true);
        s.GetBool("b", false).Should().BeTrue();
    }

    [Fact]
    public void AutoCleanAllowed_defaults_false() =>
        NewRepo().GetBool(SettingKeys.AutoCleanAllowed, false).Should().BeFalse();
}
