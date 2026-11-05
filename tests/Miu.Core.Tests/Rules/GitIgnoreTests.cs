using Miu.Core.Rules;

namespace Miu.Core.Tests.Rules;

public class GitIgnoreTests
{
    [Theory]
    [InlineData("dist", "dist", true)]
    [InlineData("/build/", "build", true)]
    [InlineData("**/out", "out", true)]
    [InlineData("# dist", "dist", false)]
    [InlineData("src/build", "build", false)]
    [InlineData("[Ll]ibrary/", "Library", true)]
    [InlineData("*.log", "build", false)]
    [InlineData("bin/\nobj/", "obj", true)]
    [InlineData("dist\n!dist", "dist", false)]
    public void IsIgnored(string content, string name, bool expected) =>
        GitIgnore.IsIgnored(content.Replace("\\n", "\n").Split('\n'), name, caseInsensitive: false).Should().Be(expected);
}
