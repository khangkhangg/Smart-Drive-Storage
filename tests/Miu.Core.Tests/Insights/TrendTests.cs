using Miu.Core.Insights;

namespace Miu.Core.Tests.Insights;

public class TrendTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long GB = 1L << 30;

    [Fact]
    public void Grew_lists_biggest_growth_including_new_folders()
    {
        var before = new Dictionary<string, long> { ["/u/Downloads"] = 10, ["/u/Docker"] = 100, ["/u/Old"] = 50 };
        var after = new Dictionary<string, long> { ["/u/Downloads"] = 40, ["/u/Docker"] = 900, ["/u/Old"] = 10, ["/u/New"] = 5 };
        SnapshotDiff.Grew(before, after, 3).Should().Equal(("/u/Docker", 800L), ("/u/Downloads", 30L), ("/u/New", 5L));
    }

    [Fact]
    public void Forecast_linear_fill_rate()
    {
        // 1 GB per day, 10 GB left
        var samples = Enumerable.Range(0, 5).Select(d => (T0.AddDays(d), 90 * GB + d * GB)).ToList();
        Forecast.DaysUntilFull(samples, 104 * GB).Should().BeApproximately(10, 0.01);
    }

    [Fact]
    public void Forecast_needs_enough_history_and_growth()
    {
        Forecast.DaysUntilFull([(T0, 10 * GB), (T0.AddDays(1), 11 * GB)], 100 * GB).Should().BeNull();
        Forecast.DaysUntilFull([(T0, 10 * GB), (T0.AddHours(1), 11 * GB), (T0.AddHours(2), 12 * GB)], 100 * GB).Should().BeNull();
        Forecast.DaysUntilFull([(T0, 50 * GB), (T0.AddDays(2), 40 * GB), (T0.AddDays(4), 30 * GB)], 100 * GB).Should().BeNull();
    }

    [Fact]
    public void Forecast_text()
    {
        Forecast.Describe(4.2).Should().Be("Full in ~4 days");
        Forecast.Describe(20).Should().Be("Full in ~3 weeks");
        Forecast.Describe(400).Should().BeNull();
    }

    [Fact]
    public void Digest_summarizes_the_week()
    {
        var d = Digest.Build(freedBytes: 5 * GB, grew: [("/u/Docker", 3 * GB)], pendingSuggestions: 2, devRebuildableBytes: 12 * GB);
        d.Title.Should().Be("Your week with Miu");
        d.Body.Should().Contain("Freed 5.0 GB").And.Contain("Docker grew 3.0 GB").And.Contain("2 rule suggestions").And.Contain("12.0 GB");
    }
}
