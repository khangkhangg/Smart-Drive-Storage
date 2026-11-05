using Miu.Data;

namespace Miu.Data.Tests;

public class RemoteActionsRepositoryTests
{
    [Fact]
    public void Create_get_use_once_and_expire()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var repo = new RemoteActionsRepository(db);
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var id = repo.Create("{\"x\":1}", now.AddHours(24));
        repo.TryTake(id, now).Should().Be("{\"x\":1}");
        repo.TryTake(id, now).Should().BeNull(); // only once
        var old = repo.Create("{}", now.AddHours(24));
        repo.TryTake(old, now.AddHours(25)).Should().BeNull(); // expired
        repo.Peek(old, now).Should().Be("{}"); // still visible before expiry, not taken
        repo.TryTake(9999, now).Should().BeNull();
    }
}
