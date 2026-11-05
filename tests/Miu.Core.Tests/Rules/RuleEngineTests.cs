using Miu.Core.Rules;

namespace Miu.Core.Tests.Rules;

public class RuleEngineTests
{
    internal static readonly DateTime Now = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly ProtectedPathGuard Guard = ProtectedPathGuard.ForUnix("/home/u");

    internal static ItemFacts Facts(string path, bool isDir = true, int? idle = null, string[]? siblings = null,
        string[]? children = null, int ageDays = 0, long size = 0, bool gitIgnored = false) =>
        new(path, Path.GetFileName(path), isDir, size, Now.AddDays(-ageDays), Now, idle,
            m => (siblings ?? []).Any(s => Glob.IsMatch(m, s, false)),
            m => (children ?? []).Any(s => Glob.IsMatch(m, s, false)),
            () => gitIgnored);

    private static Rule R(string id, Verdict v, RuleSource src = RuleSource.Builtin, int prio = 500, string? path = null,
        string[]? names = null, RuleConditions? cond = null) =>
        new(id, new RuleMatch(Path: path, Names: names), cond ?? new RuleConditions(), v, id + " reason", src, prio);

    [Fact]
    public void Protected_beats_any_rule()
    {
        var e = new RuleEngine([R("evil", Verdict.Safe, RuleSource.User, 9999, path: "/usr/**")], Guard, false);
        var r = e.Evaluate(Facts("/usr/share/x"));
        r.Verdict.Should().Be(Verdict.Protected);
        r.RuleId.Should().Be("protected");
        r.Confidence.Should().Be(1);
    }

    [Fact]
    public void User_tier_beats_builtin()
    {
        var e = new RuleEngine([
            R("b", Verdict.Safe, RuleSource.Builtin, 900, names: ["dist"]),
            R("u", Verdict.Keep, RuleSource.User, 100, path: "/home/u/proj/**")], Guard, false);
        e.Evaluate(Facts("/home/u/proj/dist")).RuleId.Should().Be("u");
    }

    [Fact]
    public void Higher_priority_wins_within_tier()
    {
        var e = new RuleEngine([
            R("low", Verdict.Review, prio: 100, names: ["dist"]),
            R("high", Verdict.Safe, prio: 600, names: ["dist"])], Guard, false);
        e.Evaluate(Facts("/home/u/p/dist")).RuleId.Should().Be("high");
    }

    [Fact]
    public void No_match_is_review()
    {
        var r = new RuleEngine([], Guard, false).Evaluate(Facts("/home/u/stuff"));
        r.Verdict.Should().Be(Verdict.Review);
        r.RuleId.Should().BeNull();
        r.Reason.Should().Contain("review");
    }

    [Fact]
    public void ProjectIdleDays_condition()
    {
        var e = new RuleEngine([R("idle", Verdict.Safe, names: ["dist"], cond: new RuleConditions(ProjectIdleDays: 14))], Guard, false);
        e.Evaluate(Facts("/home/u/p/dist", idle: 30)).RuleId.Should().Be("idle");
        e.Evaluate(Facts("/home/u/p/dist", idle: 3)).RuleId.Should().BeNull();
        e.Evaluate(Facts("/home/u/p/dist", idle: null)).RuleId.Should().BeNull();
    }

    [Fact]
    public void RequiresSiblingMarker_glob()
    {
        var e = new RuleEngine([R("net", Verdict.Safe, names: ["bin"], cond: new RuleConditions(RequiresSiblingMarker: ["*.csproj"]))], Guard, false);
        e.Evaluate(Facts("/home/u/p/bin", siblings: ["App.csproj"])).RuleId.Should().Be("net");
        e.Evaluate(Facts("/home/u/p/bin", siblings: ["main.py"])).RuleId.Should().BeNull();
    }

    [Fact]
    public void OlderThan_and_MinSize_conditions()
    {
        var e = new RuleEngine([R("old", Verdict.Safe, names: ["*.dmp"], cond: new RuleConditions(OlderThanDays: 7, MinSizeMB: 1))], Guard, false);
        e.Evaluate(Facts("/home/u/a.dmp", false, ageDays: 10, size: 2 << 20)).RuleId.Should().Be("old");
        e.Evaluate(Facts("/home/u/a.dmp", false, ageDays: 2, size: 2 << 20)).RuleId.Should().BeNull();
        e.Evaluate(Facts("/home/u/a.dmp", false, ageDays: 10, size: 100)).RuleId.Should().BeNull();
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var e = new RuleEngine([R("off", Verdict.Safe, names: ["dist"]) with { Enabled = false }], Guard, false);
        e.Evaluate(Facts("/home/u/p/dist")).RuleId.Should().BeNull();
    }

    [Fact]
    public void RequiresGitIgnored_condition()
    {
        var e = new RuleEngine([R("gi", Verdict.Safe, names: ["build"], cond: new RuleConditions(RequiresGitIgnored: true))], Guard, false);
        e.Evaluate(Facts("/home/u/p/build", gitIgnored: true)).RuleId.Should().Be("gi");
        e.Evaluate(Facts("/home/u/p/build", gitIgnored: false)).RuleId.Should().BeNull();
    }

    [Fact]
    public void Under_matches_self_and_children()
    {
        var rule = new Rule("u", new RuleMatch(Under: "/home/u/Clients"), new RuleConditions(), Verdict.Keep, "Work files", RuleSource.User);
        var e = new RuleEngine([rule], Guard, false);
        e.Evaluate(Facts("/home/u/Clients")).RuleId.Should().Be("u");
        e.Evaluate(Facts("/home/u/Clients/acme/brief.pdf", isDir: false)).RuleId.Should().Be("u");
    }

    [Fact]
    public void Under_does_not_match_sibling_prefix()
    {
        var rule = new Rule("u", new RuleMatch(Under: "/home/u/Clients"), new RuleConditions(), Verdict.Keep, "Work files", RuleSource.User);
        new RuleEngine([rule], Guard, false).Evaluate(Facts("/home/u/Clients2/x")).RuleId.Should().BeNull();
    }

    [Fact]
    public void Under_is_case_insensitive_when_engine_is()
    {
        var rule = new Rule("u", new RuleMatch(Under: @"C:\Users\k\Clients"), new RuleConditions(), Verdict.Keep, "r", RuleSource.User);
        var guard = new ProtectedPathGuard([], [], PathStyle.Windows);
        new RuleEngine([rule], guard, true).Evaluate(Facts(@"c:\users\K\clients\a")).RuleId.Should().Be("u");
    }

    [Fact]
    public void Keep_wins_a_tie_with_safe()
    {
        var keep = new Rule("k", new RuleMatch(Under: "/home/u/p"), new RuleConditions(), Verdict.Keep, "k", RuleSource.User, 900);
        var safe = new Rule("s", new RuleMatch(Under: "/home/u/p"), new RuleConditions(), Verdict.Safe, "s", RuleSource.User, 900);
        new RuleEngine([safe, keep], Guard, false).Evaluate(Facts("/home/u/p/x")).RuleId.Should().Be("k");
    }
}
