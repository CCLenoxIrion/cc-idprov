using Microsoft.EntityFrameworkCore;
using Onboarding.Data;

namespace Onboarding.Worker;

/// <summary>Polling loop (SPEC §6): recovery on start, then one engine pass per poll interval.</summary>
public sealed class Worker(
    WorkerEngine engine,
    IDbContextFactory<OnboardingDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await engine.RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.PassFailed(ex);
            }

            await Task.Delay(await PollIntervalAsync(stoppingToken), timeProvider, stoppingToken);
        }
    }

    private async Task<TimeSpan> PollIntervalAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var interval = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings.Execution.PollInterval;
        return interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(60);
    }
}
