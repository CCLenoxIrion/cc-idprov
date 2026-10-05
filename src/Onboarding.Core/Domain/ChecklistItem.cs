namespace Onboarding.Core.Domain;

/// <summary>
/// Manual checklist entry of a request (SPEC §4.5, §6.2). A snapshot copy of a template,
/// created when the request is created; later template changes do not affect it.
/// </summary>
public sealed class ChecklistItem
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }

    /// <summary>Template the item was copied from (informational, no foreign key).</summary>
    public Guid? SourceTemplateId { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Mandatory { get; set; }
    public int SortOrder { get; set; }
    public ChecklistItemStatus Status { get; private set; } = ChecklistItemStatus.Open;
    public string? CompletedBy { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? Note { get; private set; }

    public bool IsSatisfied => Status is ChecklistItemStatus.Done or ChecklistItemStatus.NotApplicable;

    internal void SetStatus(ChecklistItemStatus status, string? note, string actor, DateTimeOffset now)
    {
        if (status == ChecklistItemStatus.NotApplicable && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("NotApplicable requires a note.", nameof(note));
        }

        Status = status;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (status == ChecklistItemStatus.Open)
        {
            CompletedBy = null;
            CompletedAt = null;
        }
        else
        {
            CompletedBy = actor;
            CompletedAt = now;
        }
    }
}
