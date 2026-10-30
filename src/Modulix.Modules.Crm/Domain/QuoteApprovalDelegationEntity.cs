namespace Modulix.Modules.Crm.Domain;

/// <summary>
/// Teklif onay vekâleti (ADR-0019, dilim 10): <see cref="FromUserId"/> adına <see cref="ToUserId"/> onay verebilir. Vekil, kuralda adı geçen
/// onaylayanın yerine geçer; onay iznine kendisi sahip olmalıdır (vekâlet izin vermez, yalnızca "adına" sayılır). Süreli ya da süresiz olabilir.
/// </summary>
public sealed class QuoteApprovalDelegation : CrmEntity
{
    public const int NoteMaxLength = 300;

    public Guid FromUserId { get; internal set; }

    public Guid ToUserId { get; internal set; }

    public DateTimeOffset StartsAt { get; internal set; }

    /// <summary>Boşsa süresiz.</summary>
    public DateTimeOffset? EndsAt { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    public string? Note { get; internal set; }

    public bool CoversAt(DateTimeOffset at) => IsActive && StartsAt <= at && (EndsAt is null || EndsAt > at);
}
