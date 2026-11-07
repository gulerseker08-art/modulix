using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Approvals;

/// <summary>
/// Onay vekâleti (ADR-0027 Dilim 4b, Ç6a.1): belirli bir süre <see cref="DelegateUserId"/>, <see cref="PrincipalUserId"/> adına kişiye
/// atanmış adımlara karar verir. Vekâlet zincirlenmez; role atanmış adımlar vekâlete girmez (rolün başka üyeleri vardır). Kayıt silinmez,
/// geri alınır.
/// </summary>
public sealed class ApprovalDelegation : ITenantEntity, ICompanyShared
{
    public const int ReasonMaxLength = 300;
    public const int MaxDays = 365;

    private ApprovalDelegation()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PrincipalUserId { get; private set; }

    public Guid DelegateUserId { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static ApprovalDelegation Create(
        Guid principalUserId, Guid delegateUserId, DateTimeOffset startsAt, DateTimeOffset endsAt, string? reason, Guid createdBy, DateTimeOffset now)
    {
        if (principalUserId == delegateUserId)
        {
            throw new ArgumentException("Kişi kendisine vekâlet veremez.");
        }

        if (endsAt <= startsAt || endsAt <= now)
        {
            throw new ArgumentException("Bitiş, başlangıçtan ve bugünden sonra olmalı.");
        }

        if ((endsAt - startsAt).TotalDays > MaxDays)
        {
            throw new ArgumentException($"Vekâlet en çok {MaxDays} gün sürebilir.");
        }

        string? trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed?.Length > ReasonMaxLength)
        {
            throw new ArgumentException($"Gerekçe en çok {ReasonMaxLength} karakter olabilir.");
        }

        return new ApprovalDelegation
        {
            Id = Guid.CreateVersion7(),
            PrincipalUserId = principalUserId,
            DelegateUserId = delegateUserId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Reason = trimmed,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
