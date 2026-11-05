using Miu.Core.Rules;

namespace Miu.Core.Tests.Rules;

public class GlobTests
{
    [Theory]
    [InlineData("**/node_modules", @"C:\a\b\NODE_MODULES", true, true)]
    [InlineData("**/node_modules", "/a/node_modules", false, true)]
    [InlineData("**/node_modules", "/a/node_modules/x", false, false)]
    [InlineData("*.dmp", "a.dmp", false, true)]
    [InlineData("*.dmp", "dir/a.dmp", false, false)]
    [InlineData("/a/**", "/a/b/c", false, true)]
    [InlineData("**/Downloads/*.{exe,msi}", @"C:\Users\k\Downloads\setup.MSI", true, true)]
    [InlineData("**/Downloads/*.{exe,msi}", @"C:\Users\k\Downloads\a.zip", true, false)]
    [InlineData("*.csproj", "App.csproj", false, true)]
    [InlineData("**/AppData/Local/Temp/**", "/x/AppData/Local/Temp/f", false, true)]
    [InlineData("**/node_modules", "/a/Node_Modules", false, false)]
    [InlineData("[Ll]ibrary", "Library", false, true)]
    [InlineData("[Ll]ibrary", "library", false, true)]
    [InlineData("[Ll]ibrary", "Xibrary", false, false)]
    public void Matches(string pattern, string text, bool ci, bool expected) =>
        Glob.IsMatch(pattern, text, ci).Should().Be(expected);
}
