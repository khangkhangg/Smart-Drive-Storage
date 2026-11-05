using FluentAssertions;
using Miu.Core.Sync;

namespace Miu.Core.Tests.Sync;

public class SyncFilterTests
{
    [Theory]
    [InlineData("node_modules/react/index.js", "node_modules", true)]
    [InlineData("web/node_modules/react/index.js", "node_modules", true)]
    [InlineData("web/src/node_modules.txt", "node_modules", false)]
    [InlineData("Users/k/AppData/Local/Temp/x.tmp", "Users/*/AppData/Local/Temp", true)]
    [InlineData("Users/k/AppData/Local/Templates/x", "Users/*/AppData/Local/Temp", false)]
    [InlineData("pagefile.sys", "pagefile.sys", true)]
    [InlineData("Photos/PAGEFILE.SYS", "pagefile.sys", true)]
    [InlineData("Projects/notes.txt", "*.tmp", false)]
    [InlineData("a/b/c.tmp", "*.tmp", true)]
    public void Matches_names_anywhere_and_paths_from_the_root(string rel, string pattern, bool excluded) =>
        SyncFilter.IsExcluded(rel, [pattern], caseInsensitive: true).Should().Be(excluded);

    [Fact]
    public void Parse_ignores_blank_lines_and_comments() =>
        SyncFilter.Parse("node_modules\n\n# caches\n  *.tmp \r\n").Should().Equal("node_modules", "*.tmp");

    [Fact]
    public void Drive_backup_defaults_skip_the_system_but_not_your_files()
    {
        var win = SyncFilter.DriveBackupDefaults(windows: true, mac: false);
        SyncFilter.IsExcluded("pagefile.sys", win, true).Should().BeTrue();
        SyncFilter.IsExcluded("Windows/System32/kernel32.dll", win, true).Should().BeTrue();
        SyncFilter.IsExcluded("$Recycle.Bin/S-1-5/x", win, true).Should().BeTrue();
        SyncFilter.IsExcluded("Users/khang/AppData/Local/Temp/a.log", win, true).Should().BeTrue();
        SyncFilter.IsExcluded("Users/khang/Documents/taxes.pdf", win, true).Should().BeFalse();
        SyncFilter.IsExcluded("Projects/Miu/src/main.rs", win, true).Should().BeFalse();
        var linux = SyncFilter.DriveBackupDefaults(false, false);
        SyncFilter.IsExcluded("proc/1/status", linux, false).Should().BeTrue();
        SyncFilter.IsExcluded("home/k/Pictures/a.jpg", linux, false).Should().BeFalse();
    }
}
