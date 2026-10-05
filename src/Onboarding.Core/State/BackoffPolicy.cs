using Onboarding.Core.Configuration;

namespace Onboarding.Core.State;

/// <summary>Retry delays and timeout for waiting steps (SPEC §6).</summary>
public sealed class BackoffPolicy
{
    private readonly IReadOnlyList<TimeSpan> _schedule;

    public BackoffPolicy(IReadOnlyList<TimeSpan> schedule, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Count == 0)
        {
            throw new ArgumentException("Backoff schedule must not be empty.", nameof(schedule));
        }

        if (schedule.Any(d => d <= TimeSpan.Zero))
        {
            throw new ArgumentException("Backoff delays must be positive.", nameof(schedule));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        _schedule = schedule;
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    public static BackoffPolicy FromConfig(ExecutionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new BackoffPolicy(config.BackoffSchedule, config.StepTimeout);
    }

    /// <summary>Delay after the given number of attempts (1-based); the last delay repeats.</summary>
    public TimeSpan DelayAfterAttempt(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        return _schedule[Math.Min(attempts, _schedule.Count) - 1];
    }

    public bool IsTimedOut(DateTimeOffset firstAttemptAt, DateTimeOffset now) => now - firstAttemptAt >= Timeout;
}
