using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.Core.Tests.Learning;

public class PatternMinerTests
{
    private static readonly DateTime T = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Decision D(string path, DecisionAction a, string? reason = "Never used", bool isDir = false) =>
        Decision.For(path, isDir, a, reason, T, PathStyle.Unix);

    [Fact]
    public void Three_consistent_deletes_in_folder_suggest_rule()
    {
        var s = PatternMiner.Mine([
            D("/home/u/Downloads/a.msi", DecisionAction.Delete),
            D("/home/u/Downloads/b.msi", DecisionAction.Delete),
            D("/home/u/Downloads/c.msi", DecisionAction.Delete)], new HashSet<string>(), PathStyle.Unix);
        var only = s.Should().ContainSingle().Subject;
        only.Rule.Verdict.Should().Be(Verdict.Safe);
        only.Rule.Source.Should().Be(RuleSource.Learned);
        only.Rule.Match.Under.Should().Be("/home/u/Downloads");
        only.Rule.Match.Ext.Should().Equal("msi");
        only.Evidence.Should().Contain("3 of 3").And.Contain("Never used");
        only.Rule.Reason.Should().Contain("Never used");
    }

    [Fact]
    public void Two_decisions_are_not_enough() =>
        PatternMiner.Mine([
            D("/home/u/Downloads/a.msi", DecisionAction.Delete),
            D("/home/u/Downloads/b.msi", DecisionAction.Delete)], new HashSet<string>(), PathStyle.Unix).Should().BeEmpty();

    [Fact]
    public void Contradiction_blocks()
    {
        PatternMiner.Mine([
            D("/home/u/Downloads/a.msi", DecisionAction.Delete),
            D("/home/u/Downloads/b.msi", DecisionAction.Delete),
            D("/home/u/Downloads/c.msi", DecisionAction.Delete),
            D("/home/u/Downloads/d.msi", DecisionAction.Keep)], new HashSet<string>(), PathStyle.Unix).Should().BeEmpty();
    }

    [Fact]
    public void Known_keys_not_repeated()
    {
        var decisions = new[]
        {
            D("/home/u/Downloads/a.msi", DecisionAction.Delete),
            D("/home/u/Downloads/b.msi", DecisionAction.Delete),
            D("/home/u/Downloads/c.msi", DecisionAction.Delete),
        };
        var key = PatternMiner.Mine(decisions, new HashSet<string>(), PathStyle.Unix).Single().Key;
        PatternMiner.Mine(decisions, new HashSet<string> { key }, PathStyle.Unix).Should().BeEmpty();
    }

    [Fact]
    public void Narrow_key_wins_over_folder_key()
    {
        var s = PatternMiner.Mine([
            D("/home/u/Downloads/a.msi", DecisionAction.Delete),
            D("/home/u/Downloads/b.msi", DecisionAction.Delete),
            D("/home/u/Downloads/c.msi", DecisionAction.Delete),
            D("/home/u/Downloads/x.zip", DecisionAction.Delete)], new HashSet<string>(), PathStyle.Unix);
        s.Should().ContainSingle(r => r.Rule.Match.Ext != null);
        s.Should().NotContain(r => r.Rule.Match.Ext == null && r.Rule.Match.Under == "/home/u/Downloads");
    }

    [Fact]
    public void Folder_children_keep_suggests_keep_rule()
    {
        var s = PatternMiner.Mine([
            D("/home/u/Clients/acme", DecisionAction.Keep, "Work files", isDir: true),
            D("/home/u/Clients/globex", DecisionAction.Keep, "Work files", isDir: true),
            D("/home/u/Clients/initech", DecisionAction.Keep, "Work files", isDir: true)], new HashSet<string>(), PathStyle.Unix);
        var r = s.Single().Rule;
        r.Verdict.Should().Be(Verdict.Keep);
        r.Match.Under.Should().Be("/home/u/Clients");
    }

    [Fact]
    public void Folder_name_anywhere()
    {
        var s = PatternMiner.Mine([
            D("/home/u/a/.cache-x", DecisionAction.Delete, "Rebuildable", isDir: true),
            D("/home/u/b/.cache-x", DecisionAction.Delete, "Rebuildable", isDir: true),
            D("/home/u/c/.cache-x", DecisionAction.Delete, "Rebuildable", isDir: true)], new HashSet<string>(), PathStyle.Unix);
        var r = s.Single().Rule;
        r.Match.Names.Should().Equal(".cache-x");
        r.Match.IsDir.Should().BeTrue();
        r.Match.Under.Should().BeNull();
    }

    [Fact]
    public void Learned_folder_delete_rule_never_matches_the_folder_itself()
    {
        var rule = PatternMiner.Mine([
            D("/home/u/old/a", DecisionAction.Delete, isDir: true),
            D("/home/u/old/b.bin", DecisionAction.Delete),
            D("/home/u/old/c", DecisionAction.Delete, isDir: true)], new HashSet<string>(), PathStyle.Unix).Single().Rule;
        var engine = new RuleEngine([rule], ProtectedPathGuard.ForUnix("/home/u"), false);
        Facts("/home/u/old").Should().NotBeNull();
        engine.Evaluate(Facts("/home/u/old")).RuleId.Should().BeNull();
        engine.Evaluate(Facts("/home/u/old/new-thing")).Verdict.Should().Be(Verdict.Safe);
    }

    private static ItemFacts Facts(string path) =>
        new(path, Path.GetFileName(path), true, 0, T, T, null, _ => false);
}
