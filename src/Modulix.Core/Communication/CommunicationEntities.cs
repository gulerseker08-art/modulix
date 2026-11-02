using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Communication;

/// <summary>Tercih ve izin kaynakları. İçe aktarma, sağlayıcı ve sistem kaynakları reddedilmiş bir izni geri açamaz.</summary>
public static class ConsentSources
{
    /// <summary>Yetkili kullanıcının elle kaydı.</summary>
    public const string Manual = "manual";

    /// <summary>Kişinin doldurduğu form (ör. web formu onay kutusu).</summary>
    public const string Form = "form";

    /// <summary>Kişinin tercih merkezinden yaptığı değişiklik.</summary>
    public const string PreferenceCenter = "preference-center";

    /// <summary>Abonelikten çıkma bağlantısı.</summary>
    public const string Unsubscribe = "unsubscribe";

    public const string Import = "import";
    public const string Provider = "provider";
    public const string System = "system";

    public static IReadOnlyList<string> All { get; } = [Manual, Form, PreferenceCenter, Unsubscribe, Import, Provider, System];

    /// <summary>Reddedilmiş izni yeniden açabilen kaynaklar: kişinin kendisi ya da yetkili kullanıcı. Toplu kaynaklar açamaz.</summary>
    public static IReadOnlyList<string> CanReGrant { get; } = [Manual, Form, PreferenceCenter];
}

/// <summary>
/// Bir iletişim noktasının bir amaç için güncel izin durumu (ADR-0025). Kimlik: (tenant, kanal, normalize adres, amaç).
/// Geçmiş <see cref="ConsentEvent"/> satırlarındadır; bu satır yalnızca güncel durumdur. Kişi/taraf bağı isteğe bağlı ve bilgi
/// amaçlıdır: karar adrese göre verilir, çünkü aynı adres birden fazla kayda bağlı olabilir.
/// </summary>
public sealed class CommunicationPreference : ITenantEntity, ICompanyShared
{
    public const int AddressMaxLength = CommunicationAddress.MaxLength;
    public const int SourceMaxLength = 30;
    public const int EvidenceMaxLength = 200;
    public const int ReasonMaxLength = 500;

    private CommunicationPreference()
    {
        // EF Core için
    }

    public CommunicationPreference(CommunicationChannel channel, string address, CommunicationPurpose purpose, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        Channel = channel;
        Address = address;
        Purpose = purpose;
        CreatedAt = now;
        ChangedAt = now;
        Status = ConsentStatus.Unknown;
        Source = ConsentSources.System;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public CommunicationChannel Channel { get; private set; }

    /// <summary>Normalize adres (<see cref="CommunicationAddress.Normalize"/>).</summary>
    public string Address { get; private set; } = string.Empty;

    public CommunicationPurpose Purpose { get; private set; }

    public ConsentStatus Status { get; private set; }

    public string Source { get; private set; } = string.Empty;

    /// <summary>İzin kanıtına referans (form sürümü, kayıt numarası, onay metni sürümü). Kanıtın kendisi burada tutulmaz.</summary>
    public string? EvidenceRef { get; private set; }

    /// <summary>Son bilinen taraf (bilgi amaçlı).</summary>
    public Guid? PartyId { get; private set; }

    /// <summary>Son bilinen kişi (bilgi amaçlı).</summary>
    public Guid? PersonId { get; private set; }

    public Guid? ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Apply(ConsentStatus status, string source, string? evidenceRef, Guid? partyId, Guid? personId, Guid? changedBy, DateTimeOffset now)
    {
        Status = status;
        Source = source;
        EvidenceRef = evidenceRef;
        PartyId = partyId ?? PartyId;
        PersonId = personId ?? PersonId;
        ChangedBy = changedBy;
        ChangedAt = now;
    }
}

/// <summary>Tercihin her değişikliği (değişmez satır): kim, ne zaman, hangi kaynak ve kanıtla, hangi gerekçeyle.</summary>
public sealed class ConsentEvent : ITenantEntity, ICompanyShared
{
    private ConsentEvent()
    {
        // EF Core için
    }

    public ConsentEvent(
        Guid preferenceId, ConsentStatus from, ConsentStatus to, string source, string? evidenceRef, string? reason, Guid? changedBy, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        PreferenceId = preferenceId;
        FromStatus = from;
        ToStatus = to;
        Source = source;
        EvidenceRef = evidenceRef;
        Reason = reason;
        ChangedBy = changedBy;
        ChangedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PreferenceId { get; private set; }

    public ConsentStatus FromStatus { get; private set; }

    public ConsentStatus ToStatus { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public string? EvidenceRef { get; private set; }

    public string? Reason { get; private set; }

    public Guid? ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }
}

/// <summary>
/// İletişim noktası engeli (ADR-0025): izin durumunu ezer. Kaldırma gerekçeyle yapılır ve satır silinmez. İçe aktarma veya
/// segment değişimi engeli kaldırmaz. Geçerlilik: kaldırılmamış ve başlangıç ≤ şimdi &lt; bitiş.
/// </summary>
public sealed class CommunicationSuppression : ITenantEntity, ICompanyShared
{
    public const int NoteMaxLength = 500;

    private CommunicationSuppression()
    {
        // EF Core için
    }

    public CommunicationSuppression(
        CommunicationChannel channel, string address, SuppressionReason reason, string source, string? note, DateTimeOffset startsAt, DateTimeOffset? endsAt,
        Guid? createdBy, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        Channel = channel;
        Address = address;
        Reason = reason;
        Source = source;
        Note = note;
        StartsAt = startsAt;
        EndsAt = endsAt;
        CreatedBy = createdBy;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public CommunicationChannel Channel { get; private set; }

    public string Address { get; private set; } = string.Empty;

    public SuppressionReason Reason { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LiftedAt { get; private set; }

    public Guid? LiftedBy { get; private set; }

    public string? LiftReason { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) => LiftedAt is null && StartsAt <= now && (EndsAt is null || EndsAt > now);

    public void Lift(string reason, Guid? liftedBy, DateTimeOffset now)
    {
        LiftedAt = now;
        LiftedBy = liftedBy;
        LiftReason = reason;
    }
}
