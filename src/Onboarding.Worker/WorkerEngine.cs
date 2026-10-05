using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Domain;
using Onboarding.Core.Security;
using Onboarding.Core.State;
using Onboarding.Core.Steps;
using Onboarding.Core.Workflow;
using Onboarding.Data;
using Onboarding.Steps.Execution;

namespace Onboarding.Worker;

public sealed class WorkerOptions
{
    /// <summary>Requests processed in parallel per poll.</summary>
    public int MaxParallelRequests { get; set; } = 4;

    /// <summary>Maximum duration of one executor call; exceeding it counts as Waiting (retry with backoff).</summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Safety limit of steps executed per request and poll.</summary>
    public int MaxStepsPerRequestAndPoll { get; set; } = 50;
}

/// <summary>
/// One worker pass over all requests (SPEC §6). Requests run in parallel and independently; per
/// request at most one step is Running at a time, a Waiting step does not block other due steps
/// (P1). Steps are claimed with optimistic concurrency, so two engines never run the same step.
/// </summary>
public sealed class WorkerEngine(
    IDbContextFactory<OnboardingDbContext> dbFactory,
    StepExecutorRegistry executors,
    ISecretDecryptor decryptor,
    TimeProvider timeProvider,
    WorkerOptions options,
    ILogger<WorkerEngine> logger)
{
    private static readonly RequestStatus[] ProcessableStatuses =
        Enum.GetValues<RequestStatus>().Where(RequestStateMachine.IsProcessable).ToArray();

    private readonly RequestWorkflow _workflow = new(timeProvider);
    private readonly StepPlanRegistry _plans = StepPlanRegistry.Default;

    /// <summary>After a crash: steps left Running become Waiting and due immediately.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var requests = await db.Requests
            .Where(r => ProcessableStatuses.Contains(r.Status) && r.Steps.Any(s => s.Status == StepStatus.Running))
            .ToListAsync(ct);
        foreach (var request in requests)
        {
            foreach (var step in request.Steps.Where(s => s.Status == StepStatus.Running).ToList())
            {
                logger.RecoveringStep(step.StepKey, request.Id);
                db.AuditLog.AddRange(_workflow.RecoverInterruptedStep(request, step));
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Processes all due steps of all requests once.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.Requests.AsNoTracking()
                .Where(r => ProcessableStatuses.Contains(r.Status) && r.Steps.Count > 0)
                .Select(r => r.Id)
                .ToListAsync(ct);
        }

        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelRequests), CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    await ProcessRequestAsync(id, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // A broken request must not affect the others (SPEC §6).
                    logger.RequestFailed(ex, id);
                }
            });
    }

    private async Task ProcessRequestAsync(Guid requestId, CancellationToken ct)
    {
        for (var i = 0; i < options.MaxStepsPerRequestAndPoll; i++)
        {
            var claimed = await ClaimNextAsync(requestId, ct);
            if (claimed is null)
            {
                return;
            }

            var outcome = await ExecuteAsync(claimed, ct);
            await ApplyOutcomeAsync(requestId, claimed.StepKey, outcome, ct);
        }
    }

    /// <summary>Claims the first due step, or applies a pending status evaluation and returns null.</summary>
    private async Task<ClaimedStep?> ClaimNextAsync(Guid requestId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.Requests.SingleAsync(r => r.Id == requestId, ct);
        var due = StepScheduler.DueSteps(request, _plans.For(request.Type), timeProvider.GetUtcNow());
        if (due.Count == 0)
        {
            var audits = _workflow.ApplyEvaluation(request);
            if (audits.Count > 0)
            {
                db.AuditLog.AddRange(audits);
                await TrySaveAsync(db, ct);
            }

            return null;
        }

        var step = due[0];
        db.AuditLog.AddRange(_workflow.ClaimStep(request, step));
        if (!await TrySaveAsync(db, ct))
        {
            logger.ClaimConflict(step.StepKey, requestId);
            return null;
        }

        logger.RunningStep(step.StepKey, requestId, step.Attempts);
        var ciphertext = request.EncryptedInitialPassword;
        var context = new StepContext(
            request.Id,
            request.Input,
            request.Derived ?? throw new InvalidOperationException("Approved request without derived identity."),
            request.ConfigSnapshot!,
            step.ForceRequested,
            request.DirectoryObjectGuid,
            () => ciphertext is null ? null : decryptor.Decrypt(ciphertext),
            request.DirectoryObjectSid);
        return new ClaimedStep(step.StepKey, context);
    }

    private async Task<StepOutcome> ExecuteAsync(ClaimedStep claimed, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ExecutionTimeout);
        try
        {
            return await executors.Get(claimed.StepKey).ExecuteAsync(claimed.Context, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.ExecutionTimeout(claimed.StepKey, options.ExecutionTimeout);
            return StepOutcome.Waiting($"Ausführung nach {options.ExecutionTimeout} abgebrochen; neuer Versuch folgt.", "execution-timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only type and message, never the full exception (could contain arguments).
            logger.StepThrew(claimed.StepKey, ex.GetType().Name);
            return StepOutcome.Failed($"Unerwarteter Fehler ({ex.GetType().Name}): {ex.Message}", "unexpected-error");
        }
    }

    private async Task ApplyOutcomeAsync(Guid requestId, string stepKey, StepOutcome outcome, CancellationToken ct)
    {
        // The web may change the request meanwhile (checklist, admin actions): retry on conflict.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var request = await db.Requests.SingleAsync(r => r.Id == requestId, ct);
            if (request.FindStep(stepKey)?.Status != StepStatus.Running)
            {
                logger.OutcomeDropped(stepKey, requestId);
                return;
            }

            db.AuditLog.AddRange(_workflow.ApplyStepOutcome(request, stepKey, outcome));
            if (await TrySaveAsync(db, ct))
            {
                logger.StepOutcomeApplied(stepKey, requestId, outcome.Kind, outcome.Message);
                return;
            }
        }

        throw new InvalidOperationException($"Could not save the outcome of {stepKey} for request {requestId}.");
    }

    private static async Task<bool> TrySaveAsync(OnboardingDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private sealed record ClaimedStep(string StepKey, StepContext Context);
}
