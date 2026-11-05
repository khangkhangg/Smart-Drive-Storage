using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.Core.Tests.Learning;

public class MinerSafetyTests
{
    private static readonly DateTime T = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static Decision D(string path, DecisionAction a, bool isDir = false) => Decision.For(path, isDir, a, null, T, PathStyle.Unix);
    private static IReadOnlyList<RuleSuggestion> Mine(params Decision[] d) => PatternMiner.Mine(d, new HashSet<string>(), PathStyle.Unix);

    [Fact]
    public void Folder_delete_rule_covers_direct_children_only()
    {
        var rule = Mine(
            D("/home/u/Clients/a.tmp", DecisionAction.Delete),
            D("/home/u/Clients/b.log", DecisionAction.Delete),
            D("/home/u/Clients/c.bak", DecisionAction.Delete)).Single().Rule;
        rule.Match.ChildOf.Should().Be("/home/u/Clients");
        rule.Match.Inside.Should().BeNull();
        PatternMiner.Matches(rule.Match, D("/home/u/Clients/acme/brief.pdf", DecisionAction.Keep), PathStyle.Unix).Should().BeFalse();
    }

    [Fact]
    public void Name_delete_rule_is_scoped_to_common_ancestor()
    {
        var rule = Mine(
            D("/home/u/code/a/bin", DecisionAction.Delete, true),
            D("/home/u/code/b/bin", DecisionAction.Delete, true),
            D("/home/u/code/c/bin", DecisionAction.Delete, true)).Single().Rule;
        rule.Match.Names.Should().Equal("bin");
        rule.Match.Inside.Should().Be("/home/u/code");
    }

    [Fact]
    public void Name_delete_rule_too_broad_is_not_suggested() =>
        Mine(
            D("/home/u/a/bin", DecisionAction.Delete, true),
            D("/srv/b/bin", DecisionAction.Delete, true),
            D("/opt/c/bin", DecisionAction.Delete, true)).Should().BeEmpty();

    [Fact]
    public void Keep_decision_anywhere_the_rule_would_reach_blocks_it()
    {
        // The Keep sits in a different group (deeper folder) but the folder-name rule would match it.
        Mine(
            D("/home/u/code/a/build", DecisionAction.Delete, true),
            D("/home/u/code/b/build", DecisionAction.Delete, true),
            D("/home/u/code/c/build", DecisionAction.Delete, true),
            D("/home/u/code/d/sub/build", DecisionAction.Keep, true)).Should().BeEmpty();
    }

    [Fact]
    public void Contradicted_returns_true_for_matching_opposite_decision()
    {
        var s = Mine(
            D("/home/u/Downloads/a.iso", DecisionAction.Delete),
            D("/home/u/Downloads/b.iso", DecisionAction.Delete),
            D("/home/u/Downloads/c.iso", DecisionAction.Delete)).Single();
        PatternMiner.IsContradicted(s.Rule, [D("/home/u/Downloads/a.iso", DecisionAction.Keep)], PathStyle.Unix).Should().BeTrue();
        PatternMiner.IsContradicted(s.Rule, [D("/home/u/Music/a.iso", DecisionAction.Keep)], PathStyle.Unix).Should().BeFalse();
    }
}
