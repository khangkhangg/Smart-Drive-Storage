using Miu.Core.Rules;
using static Miu.Core.Tests.Rules.RuleEngineTests;

namespace Miu.Core.Tests.Rules;

public class BuiltinRulesTests
{
    private static readonly IReadOnlyList<Rule> Builtin =
        RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin"));

    private static readonly RuleEngine Engine = new(Builtin, Guard, false);

    [Fact]
    public void Every_builtin_rule_loads()
    {
        Builtin.Should().HaveCountGreaterThan(20);
        Builtin.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        Builtin.Should().OnlyContain(r => r.Source == RuleSource.Builtin && !string.IsNullOrWhiteSpace(r.Reason));
    }

    [Fact]
    public void Idle_node_modules_is_safe_with_hint()
    {
        var r = Engine.Evaluate(Facts("/home/u/code/app/node_modules", idle: 30, siblings: ["package.json"]));
        r.Verdict.Should().Be(Verdict.Safe);
        r.RebuildHint.Should().Be("npm install");
    }

    [Fact]
    public void Active_node_modules_is_review() =>
        Engine.Evaluate(Facts("/home/u/code/app/node_modules", idle: 2, siblings: ["package.json"])).Verdict.Should().Be(Verdict.Review);

    [Fact]
    public void Build_folder_without_package_json_is_review() =>
        Engine.Evaluate(Facts("/home/u/code/py/build", idle: 200, siblings: ["pyproject.toml"])).Verdict.Should().Be(Verdict.Review);

    [Fact]
    public void Bin_folder_in_dotnet_project_is_safe_when_idle()
    {
        var r = Engine.Evaluate(Facts("/home/u/code/api/bin", idle: 40, siblings: ["Api.csproj"]));
        r.Verdict.Should().Be(Verdict.Safe);
        r.RebuildHint.Should().Be("dotnet build");
    }

    [Fact]
    public void Venv_needs_pyvenv_cfg()
    {
        Engine.Evaluate(Facts("/home/u/code/py/.venv", idle: 40, children: ["pyvenv.cfg"])).Verdict.Should().Be(Verdict.Safe);
        Engine.Evaluate(Facts("/home/u/code/py/env", idle: 40, children: ["notes.txt"])).Verdict.Should().Be(Verdict.Review);
    }

    [Fact]
    public void Pycache_is_always_safe() =>
        Engine.Evaluate(Facts("/home/u/code/py/src/__pycache__", idle: 0)).Verdict.Should().Be(Verdict.Safe);

    [Fact]
    public void Old_crash_dump_is_safe() =>
        Engine.Evaluate(Facts("/home/u/crash/app.dmp", isDir: false, ageDays: 10)).Verdict.Should().Be(Verdict.Safe);

    [Fact]
    public void Rust_target_is_safe_when_idle() =>
        Engine.Evaluate(Facts("/home/u/code/rs/target", idle: 100, siblings: ["Cargo.toml"])).RebuildHint.Should().Be("cargo build");
}
