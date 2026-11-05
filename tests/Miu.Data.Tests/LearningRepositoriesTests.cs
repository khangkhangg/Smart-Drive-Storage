using Miu.Core.Learning;
using Miu.Core.Rules;
using Miu.Data;

namespace Miu.Data.Tests;

public class LearningRepositoriesTests
{
    private static MiuDatabase Db()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        return db;
    }

    private static readonly DateTime T = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Decisions_roundtrip()
    {
        var repo = new DecisionsRepository(Db());
        repo.Add(Decision.For("/a/b.msi", false, DecisionAction.Delete, "Never used", T, PathStyle.Unix), "app");
        var d = repo.All().Single();
        d.Ext.Should().Be("msi");
        d.ParentPath.Should().Be("/a");
        d.Action.Should().Be(DecisionAction.Delete);
        d.Reason.Should().Be("Never used");
        d.TsUtc.Should().Be(T);
    }

    [Fact]
    public void Rules_roundtrip_toggle_delete()
    {
        var repo = new RulesRepository(Db());
        var rule = new Rule("user-1", new RuleMatch(Under: "/a"), new RuleConditions(), Verdict.Keep, "Work files", RuleSource.User, 900);
        repo.Upsert(rule);
        repo.Upsert(rule with { Reason = "Changed" });
        repo.All().Single().Reason.Should().Be("Changed");
        repo.SetEnabled("user-1", false);
        repo.All().Single().Enabled.Should().BeFalse();
        repo.Delete("user-1");
        repo.All().Should().BeEmpty();
    }

    [Fact]
    public void Suggestions_pending_status_and_known_keys()
    {
        var repo = new SuggestionsRepository(Db());
        var rule = new Rule("learned-x", new RuleMatch(Inside: "/a"), new RuleConditions(), Verdict.Safe, "r", RuleSource.Learned, 700);
        var id = repo.Add(new RuleSuggestion("in|/a|Delete", rule, "You deleted 3 of 3", 0.9));
        repo.Pending().Single().Suggestion.Rule.Match.Inside.Should().Be("/a");
        repo.KnownKeys().Should().Contain("in|/a|Delete");
        repo.SetStatus(id, "rejected");
        repo.Pending().Should().BeEmpty();
        repo.KnownKeys().Should().Contain("in|/a|Delete");
    }
}
