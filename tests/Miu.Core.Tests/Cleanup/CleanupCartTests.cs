using Miu.Core.Cleanup;
using Miu.Core.Rules;

namespace Miu.Core.Tests.Cleanup;

public class CleanupCartTests
{
    private static CartItem I(string path, long bytes, Verdict v = Verdict.Safe) => new(path, Path.GetFileName(path), bytes, v, "r");

    [Fact]
    public void Total_and_items()
    {
        var cart = new CleanupCart(PathStyle.Unix);
        var changes = 0;
        cart.Changed += (_, _) => changes++;
        cart.Add(I("/a/node_modules", 100));
        cart.Add(I("/b/dist", 50));
        cart.Add(I("/b/dist", 50)); // duplicate
        cart.Items.Should().HaveCount(2);
        cart.TotalBytes.Should().Be(150);
        cart.Remove("/a/node_modules");
        cart.TotalBytes.Should().Be(50);
        cart.Clear();
        cart.Items.Should().BeEmpty();
        changes.Should().Be(4);
    }

    [Fact]
    public void Child_of_existing_is_ignored()
    {
        var cart = new CleanupCart(PathStyle.Unix);
        cart.Add(I("/a/node_modules", 100));
        cart.Add(I("/a/node_modules/react", 40));
        cart.Items.Select(i => i.Path).Should().Equal("/a/node_modules");
    }

    [Fact]
    public void Parent_replaces_children()
    {
        var cart = new CleanupCart(PathStyle.Unix);
        cart.Add(I("/a/x/one", 10));
        cart.Add(I("/a/x/two", 10));
        cart.Add(I("/a/xy", 5));
        cart.Add(I("/a/x", 30));
        cart.Items.Select(i => i.Path).Should().BeEquivalentTo(["/a/x", "/a/xy"]);
        cart.TotalBytes.Should().Be(35);
    }

    [Fact]
    public void Protected_rejected()
    {
        var cart = new CleanupCart(PathStyle.Unix);
        var act = () => cart.Add(I("/usr/lib", 1, Verdict.Protected));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Case_insensitive_on_windows_style()
    {
        var cart = new CleanupCart(PathStyle.Windows);
        cart.Add(I(@"C:\Code\App", 10));
        cart.Add(I(@"c:\code\app\node_modules", 5));
        cart.Contains(@"C:\CODE\APP").Should().BeTrue();
        cart.Items.Should().ContainSingle();
    }
}
