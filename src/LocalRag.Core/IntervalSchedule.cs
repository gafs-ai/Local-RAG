namespace LocalRag.Core;

public static class IntervalSchedule
{
    // Keep the original grid; coalesce missed checks after slow discovery or sleep.
    public static DateTimeOffset Next(DateTimeOffset anchor, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (now < anchor) return now + interval;
        return anchor + TimeSpan.FromTicks(checked(((now - anchor).Ticks / interval.Ticks + 1) * interval.Ticks));
    }
}
