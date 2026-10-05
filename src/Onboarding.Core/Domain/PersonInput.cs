namespace Onboarding.Core.Domain;

/// <summary>Request input fields (SPEC §2). The initial password is not part of it (see §9).</summary>
public sealed class PersonInput
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool HasDoctorTitle { get; set; }

    /// <summary>Mail address, pre-filled from the pattern and editable by the requester.</summary>
    public string Mail { get; set; } = "";

    public Guid AreaId { get; set; }
    public Guid DepartmentId { get; set; }

    /// <summary>objectGUID of the manager in AD. The DN is resolved for display only.</summary>
    public Guid ManagerObjectGuid { get; set; }

    /// <summary>Entry date (onboarding) or exit date (offboarding, v2).</summary>
    public DateOnly EffectiveDate { get; set; }

    /// <summary>Phone extension ("Durchwahl"), digits only, optional.</summary>
    public string? Extension { get; set; }
}
