using Miu.Core.Formatting;

namespace Miu.Core.Tests.Formatting;

public class FormattingTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1288490188L, "1.2 GB")]
    [InlineData(94_700_000_000L, "88.2 GB")]
    [InlineData(442_381_000_000L, "412 GB")]
    [InlineData(2_199_023_255_552L, "2.0 TB")]
    public void Size(long bytes, string expected) => SizeFormatter.Format(bytes).Should().Be(expected);

    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0.0, "just now")]
    [InlineData(5.0 / 60 / 24, "5 minutes ago")]
    [InlineData(3.0 / 24, "3 hours ago")]
    [InlineData(1.2, "yesterday")]
    [InlineData(5.0, "5 days ago")]
    [InlineData(40.0, "1 month ago")]
    [InlineData(100.0, "3 months ago")]
    [InlineData(800.0, "2 years ago")]
    public void Ago(double daysAgo, string expected) => HumanTime.Ago(Now.AddDays(-daysAgo), Now).Should().Be(expected);

    [Fact]
    public void Ago_unknown_for_min_value() => HumanTime.Ago(DateTime.MinValue, Now).Should().Be("unknown");
}
