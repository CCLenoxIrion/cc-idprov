namespace Onboarding.Core.Domain;

/// <summary>
/// Append-only audit record (SPEC §5, §9). The persistence layer rejects updates and deletes.
/// Details must never contain passwords or tokens.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; init; }
    public string Actor { get; init; } = "";
    public Guid? RequestId { get; init; }
    public string? StepKey { get; init; }
    public string Action { get; init; } = "";
    public string Result { get; init; } = "";
    public string? Details { get; init; }

    public static AuditEntry Create(
        DateTimeOffset timestamp,
        string actor,
        string action,
        string result,
        Guid? requestId = null,
        string? stepKey = null,
        string? details = null) => new()
        {
            Timestamp = timestamp,
            Actor = actor,
            RequestId = requestId,
            StepKey = stepKey,
            Action = action,
            Result = result,
            Details = details,
        };
}

/// <summary>Well-known audit action names.</summary>
public static class AuditActions
{
    public const string RequestCreated = "Request.Created";
    public const string RequestSubmitted = "Request.Submitted";
    public const string RequestApproved = "Request.Approved";
    public const string RequestCancelled = "Request.Cancelled";
    public const string StatusChanged = "Request.StatusChanged";
    public const string NeedsInput = "Request.NeedsInput";
    public const string IdentityResolved = "Request.IdentityResolved";
    public const string SnapshotRefreshed = "Request.SnapshotRefreshed";
    public const string StepRetry = "Step.Retry";
    public const string StepMarkedDone = "Step.MarkedDone";
    public const string ChecklistItemChanged = "Checklist.ItemChanged";
}

public static class AuditResults
{
    public const string Success = "Success";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
}
