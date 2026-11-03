using LocalRag.Core;

namespace LocalRag.Tests;

public class SchedulerTests
{
    [Theory]
    [InlineData(2, 5)]
    [InlineData(5, 10)]
    [InlineData(12, 15)]
    [InlineData(120, 125)]
    [InlineData(-60, -55)]
    public void ChecksStayOnIntervalGridAndMissedTicksAreCoalesced(int elapsed, int expected)
    {
        var start = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
        Assert.Equal(start.AddMinutes(expected), IntervalSchedule.Next(start, start.AddMinutes(elapsed), TimeSpan.FromMinutes(5)));
    }
}
