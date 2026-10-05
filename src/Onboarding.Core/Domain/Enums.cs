namespace Onboarding.Core.Domain;

/// <summary>Request type. v1 implements only <see cref="Onboarding"/>; Offboarding follows in v2.</summary>
public enum RequestType
{
    Onboarding,
    Offboarding,
}

/// <summary>Request status (SPEC §5).</summary>
public enum RequestStatus
{
    Draft,
    PendingApproval,
    Approved,
    Running,
    Waiting,
    AwaitingChecklist,
    Completed,
    Failed,
    NeedsInput,
    Cancelled,
}

/// <summary>Step status (SPEC §5).</summary>
public enum StepStatus
{
    Pending,
    Running,

    /// <summary>Precondition not met, retry scheduled.</summary>
    Waiting,
    Done,
    Skipped,
    Failed,

    /// <summary>Cannot be automated; an ITAdmin performs it and marks it done.</summary>
    ManualTask,

    /// <summary>
    /// Target state conflicts with what the step would create (e.g. a logon script changed by
    /// hand, a foreign account with the same sam). An ITAdmin decides (DECISIONS S10).
    /// </summary>
    NeedsInput,
}

public enum ChecklistItemStatus
{
    Open,
    Done,
    NotApplicable,
}

public enum ChecklistScope
{
    Global,
    Area,
    Department,
}

/// <summary>Who is responsible for a checklist item; requesters may tick HR items only.</summary>
public enum ChecklistResponsibility
{
    IT,
    HR,
}

public enum Role
{
    Requester,
    ITAdmin,
}
