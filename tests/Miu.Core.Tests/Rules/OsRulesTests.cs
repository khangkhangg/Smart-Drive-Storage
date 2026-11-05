using Miu.Core.Rules;
using static Miu.Core.Tests.Rules.RuleEngineTests;

namespace Miu.Core.Tests.Rules;

public class OsRulesTests
{
    private static readonly RuleEngine Engine = new(RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin")), Guard, false);

    private static Verdict V(string path, bool isDir = true, int age = 60) => Engine.Evaluate(Facts(path, isDir: isDir, ageDays: age, idle: age)).Verdict;

    [Fact]
    public void Mac_developer_and_app_caches()
    {
        V("/Users/k/Library/Developer/Xcode/DerivedData/MyApp-abcdef").Should().Be(Verdict.Safe);
        V("/Users/k/Library/Developer/Xcode/iOS DeviceSupport/17.4 (21E219)").Should().Be(Verdict.Review);
        V("/Users/k/Library/Caches/Homebrew/downloads/x.tar.gz", isDir: false).Should().Be(Verdict.Safe);
        V("/Users/k/Library/Caches/com.example.app").Should().Be(Verdict.Review);
        V("/Users/k/Projects/site/.DS_Store", isDir: false, age: 0).Should().Be(Verdict.Safe);
    }

    [Fact]
    public void Linux_caches()
    {
        V("/home/k/.cache/thumbnails/large/abc.png", isDir: false).Should().Be(Verdict.Safe);
        V("/home/k/.cache/mozilla/firefox/xyz/cache2/entries/1", isDir: false).Should().Be(Verdict.Safe);
        V("/home/k/.var/app/org.gimp.GIMP/cache/x", isDir: false).Should().Be(Verdict.Safe);
        V("/home/k/.cache/someapp").Should().Be(Verdict.Review);
        V("/home/k/projects/core.12345", isDir: false, age: 10).Should().Be(Verdict.Review);
    }
}
