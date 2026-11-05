using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.Core.Cleanup;
using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.App.Tests;

public sealed class LearningServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-learn-" + Guid.NewGuid().ToString("N"));

    public LearningServiceTests()
    {
        void F(string rel, int size, int age)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[size]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-age));
        }
        F("code/app/package.json", 10, 60);
        F("code/app/package-lock.json", 10, 60);
        F("code/app/node_modules/x.js", 3000, 60);
        foreach (var n in new[] { "a", "b", "c", "d" }) F($"Downloads/{n}.iso", 2000, 5);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private async Task<(AppServices S, ScanSession Session)> Setup()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        return (s, await ScanSession.RunAsync(s, _root, null, CancellationToken.None));
    }

    private static CartItem Item(ScanSession s, string path)
    {
        var id = s.FindNode(path)!.Value;
        var v = s.VerdictFor(id);
        return new CartItem(path, Path.GetFileName(path), s.Tree.Size(id), v.Verdict, v.Reason);
    }

    [AvaloniaFact]
    public async Task Keep_creates_rule_and_changes_verdict()
    {
        var (s, session) = await Setup();
        var nm = Path.Combine(_root, "code", "app", "node_modules");
        session.VerdictFor(session.FindNode(nm)!.Value).Verdict.Should().Be(Verdict.Safe);

        new LearningService(s).Teach(nm, isDir: true, DecisionAction.Keep, "Active project", session);

        var v = session.VerdictFor(session.FindNode(nm)!.Value);
        v.Verdict.Should().Be(Verdict.Keep);
        v.Reason.Should().Contain("Active project");
        s.RulesStore.All().Should().ContainSingle(r => r.Source == RuleSource.User);
    }

    [AvaloniaFact]
    public async Task Cleaning_three_similar_suggests_rule_and_accept_applies_it()
    {
        var (s, session) = await Setup();
        var cleanup = new CleanupService(s);
        var isos = new[] { "a", "b", "c" }.Select(n => Item(session, Path.Combine(_root, "Downloads", n + ".iso"))).ToList();
        isos.Should().OnlyContain(i => i.Verdict == Verdict.Review);

        await cleanup.CleanAsync(isos, session, "cart");

        var pending = s.Suggestions.Pending().Should().ContainSingle().Subject;
        pending.Suggestion.Evidence.Should().Contain("3 of 3");
        var learning = new LearningService(s);
        learning.Accept(pending.Id, session);
        var d = session.FindNode(Path.Combine(_root, "Downloads", "d.iso"))!.Value;
        session.VerdictFor(d).Verdict.Should().Be(Verdict.Safe);
        session.VerdictFor(d).RuleId.Should().StartWith("learned-");
        s.Suggestions.Pending().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Cleaning_items_miu_already_called_safe_teaches_nothing()
    {
        var (s, session) = await Setup();
        var nm = Item(session, Path.Combine(_root, "code", "app", "node_modules"));
        nm.Verdict.Should().Be(Verdict.Safe);
        await new CleanupService(s).CleanAsync([nm], session, "quick-clean");
        s.Decisions.All().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Reject_hides_suggestion_for_good()
    {
        var (s, session) = await Setup();
        var cleanup = new CleanupService(s);
        await cleanup.CleanAsync(new[] { "a", "b", "c" }.Select(n => Item(session, Path.Combine(_root, "Downloads", n + ".iso"))).ToList(), session, "cart");
        var learning = new LearningService(s);
        learning.Reject(s.Suggestions.Pending().Single().Id);
        learning.Mine().Should().Be(0);
        s.Suggestions.Pending().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Import_drops_protected_and_dedupes_ids()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var learning = new LearningService(s);
        learning.Teach("/home/u/Clients", true, DecisionAction.Keep, "Work files", null);
        var existingId = s.RulesStore.All().Single().Id;
        var json = $$"""
            { "rules": [
              { "id": "{{existingId}}", "match": { "under": "/home/u/Other" }, "verdict": "Keep", "reason": "x", "source": "User" },
              { "id": "evil", "match": { "path": "/usr/**" }, "verdict": "Safe", "reason": "y", "source": "Protected" },
              { "id": "ok", "match": { "names": ["tmp-build"] }, "verdict": "Safe", "reason": "z", "source": "Builtin" }
            ] }
            """;
        var result = learning.ImportJson(json);
        result.Imported.Should().Be(2);
        result.Skipped.Should().Be(1);
        result.Error.Should().BeNull();
        var all = s.RulesStore.All();
        all.Select(r => r.Id).Should().OnlyHaveUniqueItems().And.HaveCount(3);
        all.Should().OnlyContain(r => r.Source == RuleSource.User || r.Source == RuleSource.Learned);
        learning.ExportJson().Should().Contain("tmp-build");
    }

    [AvaloniaFact]
    public void Bad_json_is_reported()
    {
        var learning = new LearningService(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin()));
        var r = learning.ImportJson("{ not json");
        r.Imported.Should().Be(0);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }
}
