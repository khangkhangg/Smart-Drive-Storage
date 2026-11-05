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
        var r = Engine.Evaluate(Facts("/home/u/code/api/bin", idle: 40, siblings: ["Api.csproj"], gitIgnored: true));
        r.Verdict.Should().Be(Verdict.Safe);
        r.RebuildHint.Should().Be("dotnet build");
    }

    [Fact]
    public void Venv_needs_pyvenv_cfg()
    {
        Engine.Evaluate(Facts("/home/u/code/py/.venv", idle: 40, children: ["pyvenv.cfg"], siblings: ["requirements.txt"])).Verdict.Should().Be(Verdict.Safe);
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
        Engine.Evaluate(Facts("/home/u/code/rs/target", idle: 100, siblings: ["Cargo.toml"], gitIgnored: true)).Verdict.Should().Be(Verdict.Safe);

    // ---- Review fixes: generic folder names need evidence that they are build output

    [Theory]
    [InlineData("build")]
    [InlineData("out")]
    [InlineData("dist")]
    [InlineData("coverage")]
    public void Generic_js_output_folder_is_review_unless_gitignored(string name)
    {
        Engine.Evaluate(Facts($"/home/u/Archive2019/{name}", idle: 400, siblings: ["package.json"])).Verdict.Should().Be(Verdict.Review);
        Engine.Evaluate(Facts($"/home/u/code/app/{name}", idle: 400, siblings: ["package.json"], gitIgnored: true)).Verdict.Should().Be(Verdict.Safe);
    }

    [Fact]
    public void Unambiguous_js_folders_do_not_need_gitignore() =>
        Engine.Evaluate(Facts("/home/u/code/app/.next", idle: 40, siblings: ["package.json"])).Verdict.Should().Be(Verdict.Safe);

    [Theory]
    [InlineData("/home/u/code/rs/target", "Cargo.toml")]
    [InlineData("/home/u/code/api/bin", "Api.csproj")]
    [InlineData("/home/u/code/go/vendor", "go.mod")]
    [InlineData("/home/u/code/game/Library", "ProjectSettings")]
    public void Generic_output_folders_need_gitignore(string path, string marker) =>
        Engine.Evaluate(Facts(path, idle: 400, siblings: [marker])).Verdict.Should().Be(Verdict.Review);

    [Fact]
    public void Repo_named_go_build_is_not_safe()
    {
        Engine.Evaluate(Facts("/home/u/code/go-build", idle: 400)).Verdict.Should().Be(Verdict.Review);
        Engine.Evaluate(Facts("/home/u/.cache/go-build")).Verdict.Should().Be(Verdict.Safe);
        Engine.Evaluate(Facts(@"/c/Users/u/AppData/Local/go-build")).Verdict.Should().Be(Verdict.Safe);
    }

    [Fact]
    public void Old_log_files_are_review() =>
        Engine.Evaluate(Facts("/home/u/Documents/irc/2023-chat.log", isDir: false, ageDays: 400)).Verdict.Should().Be(Verdict.Review);

    [Fact]
    public void Notebook_checkpoints_are_review() =>
        Engine.Evaluate(Facts("/home/u/Documents/nb/.ipynb_checkpoints")).Verdict.Should().Be(Verdict.Review);

    [Fact]
    public void Venv_without_dependency_file_is_review() =>
        Engine.Evaluate(Facts("/home/u/code/py/.venv", idle: 40, children: ["pyvenv.cfg"])).Verdict.Should().Be(Verdict.Review);
}
