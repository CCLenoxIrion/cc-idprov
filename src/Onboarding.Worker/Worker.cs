namespace Onboarding.Worker;

/// <summary>Placeholder until phase 3 (polling, step execution with fake executors).</summary>
public sealed class Worker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
