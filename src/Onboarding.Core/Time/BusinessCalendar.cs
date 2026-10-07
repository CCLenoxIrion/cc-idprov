namespace Onboarding.Core.Time;

/// <summary>
/// Converts business dates to instants in the configured time zone (DECISIONS S5).
/// Never uses the server's local time zone.
/// </summary>
public static class BusinessCalendar
{
    /// <summary>
    /// 00:00 of <paramref name="date"/> in <paramref name="timeZoneId"/> minus
    /// <paramref name="leadTime"/>, as UTC instant.
    /// </summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date, string timeZoneId, TimeSpan leadTime = default)
    {
        if (leadTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leadTime), "Lead time must not be negative.");
        }

        var zone = Resolve(timeZoneId);
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Midnight can fall into a DST gap in some zones; move forward to the first valid instant.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime() - leadTime;
    }

    /// <summary>Calendar date "today" in the given zone.</summary>
    public static DateOnly Today(DateTimeOffset now, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Resolve(timeZoneId)).DateTime);

    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new InvalidOperationException("Time zone is not configured.");
        }

        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }
}
