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

    [Fact]
    public void Refresh_shrinks_folders_whose_contents_were_cleaned_and_drops_what_is_gone()
    {
        var cart = new CleanupCart(PathStyle.Windows);
        cart.Add(new CartItem(@"C:\P\.claude\worktrees\a", "a", 16_000, Verdict.Review, "wt"));
        cart.Add(new CartItem(@"C:\P\.claude\worktrees\b", "b", 14_000, Verdict.Review, "wt"));
        cart.Add(new CartItem(@"C:\gone", "gone", 500, Verdict.Safe, "x"));
        var changes = 0;
        cart.Changed += (_, _) => changes++;

        cart.Refresh([(@"C:\P\.claude\worktrees\a\src-tauri\target", 15_000), (@"C:\elsewhere\target", 9_000)], p => p != @"C:\gone");

        cart.Items.Select(i => (i.Name, i.Bytes)).Should().Equal(("a", 1_000L), ("b", 14_000L));
        cart.TotalBytes.Should().Be(15_000);
        changes.Should().Be(1);
        cart.Refresh([], _ => true);
        changes.Should().Be(1); // nothing changed, no event
    }
}
