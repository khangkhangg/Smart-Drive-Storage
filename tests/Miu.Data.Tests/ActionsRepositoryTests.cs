using Miu.Data;

namespace Miu.Data.Tests;

public class ActionsRepositoryTests
{
    private static ActionsRepository Repo()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        return new ActionsRepository(db);
    }

    [Fact]
    public void Add_and_read_back()
    {
        var repo = Repo();
        var id = repo.Add("recycle", [new ActionItem("/a/node_modules", "/trash/files/node_modules", 100)], "app");
        var a = repo.Get(id)!;
        a.Kind.Should().Be("recycle");
        a.Bytes.Should().Be(100);
        a.Items.Single().TrashLocation.Should().Be("/trash/files/node_modules");
        a.Origin.Should().Be("app");
        a.Undone.Should().BeFalse();
        (DateTime.UtcNow - a.TsUtc).TotalMinutes.Should().BeLessThan(1);
    }

    [Fact]
    public void Recent_is_newest_first()
    {
        var repo = Repo();
        var first = repo.Add("recycle", [new ActionItem("/a", "/t/a", 1)], "app");
        var second = repo.Add("recycle", [new ActionItem("/b", "/t/b", 2)], "app");
        repo.Recent(10).Select(a => a.Id).Should().Equal(second, first);
    }

    [Fact]
    public void MarkUndone()
    {
        var repo = Repo();
        var id = repo.Add("recycle", [new ActionItem("/a", "/t/a", 1)], "app");
        repo.MarkUndone(id);
        repo.Get(id)!.Undone.Should().BeTrue();
    }
}
