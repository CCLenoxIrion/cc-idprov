using Onboarding.Core.State;
using Onboarding.Core.Time;
using Onboarding.Tests.TestSupport;

namespace Onboarding.Tests.Core;

public sealed class BackoffAndCalendarTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 15)]
    [InlineData(6, 15)]
    [InlineData(100, 15)]
    public void Backoff_schedule_repeats_last_delay(int attempts, int minutes)
    {
        var policy = BackoffPolicy.FromConfig(TestConfig.Global().Execution);

        Assert.Equal(TimeSpan.FromMinutes(minutes), policy.DelayAfterAttempt(attempts));
    }

    [Fact]
    public void Timeout_is_measured_from_first_attempt()
    {
        var policy = BackoffPolicy.FromConfig(TestConfig.Global().Execution);
        var first = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

        Assert.False(policy.IsTimedOut(first, first.AddHours(23).AddMinutes(59)));
        Assert.True(policy.IsTimedOut(first, first.AddHours(24)));
    }

    [Fact]
    public void Empty_schedule_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new BackoffPolicy([], TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Start_of_day_in_berlin_winter_time()
    {
        // CET = UTC+1
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero),
            BusinessCalendar.StartOfDayUtc(new DateOnly(2026, 11, 2), "Europe/Berlin"));
    }

    [Fact]
    public void Start_of_day_in_berlin_summer_time()
    {
        // CEST = UTC+2
        Assert.Equal(
            new DateTimeOffset(2026, 7, 31, 22, 0, 0, TimeSpan.Zero),
            BusinessCalendar.StartOfDayUtc(new DateOnly(2026, 8, 1), "Europe/Berlin"));
    }

    [Fact]
    public void Start_of_day_on_dst_change_day()
    {
        // 2026-03-29: clocks jump 02:00 → 03:00; midnight is still CET.
        Assert.Equal(
            new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
            BusinessCalendar.StartOfDayUtc(new DateOnly(2026, 3, 29), "Europe/Berlin"));
    }

    [Fact]
    public void Lead_time_moves_start_earlier()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 17, 0, 0, TimeSpan.Zero),
            BusinessCalendar.StartOfDayUtc(new DateOnly(2026, 11, 2), "Europe/Berlin", TimeSpan.FromHours(6)));
    }

    [Fact]
    public void Missing_time_zone_is_a_configuration_error()
    {
        Assert.Throws<InvalidOperationException>(() => BusinessCalendar.StartOfDayUtc(new DateOnly(2026, 1, 1), ""));
    }

    [Fact]
    public void Today_uses_configured_zone_not_utc()
    {
        // 23:30 UTC on 1 Nov is already 2 Nov in Berlin.
        var now = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 11, 2), BusinessCalendar.Today(now, "Europe/Berlin"));
    }
}
