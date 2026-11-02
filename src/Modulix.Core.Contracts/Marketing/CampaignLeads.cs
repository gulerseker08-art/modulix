namespace Modulix.Core.Contracts.Marketing;

// Kampanya yanıtından lead (ADR-0025, dilim 7). Pazarlama modülü CRM'in alanlarını bilmez: gönderim anında çözülen kişi bilgisini bu sözleşmeyle verir, lead'i
// lead alanını sağlayan modül (CRM) açar.

public sealed record CampaignLeadRequest(
    string Subject,
    string? Description,
    string? FirstName,
    string? LastName,
    string? CompanyName,
    string? Email,
    string? Phone,
    Guid? OwnerId,
    string SourceDetail,
    string UtmSource,
    string UtmMedium,
    string UtmCampaign);

/// <summary>Sonuç: oluştuysa lead kimliği, oluşmadıysa nedeni. <see cref="Unavailable"/>: lead modülü bu tenant için kapalı ya da kurulu değil; <see cref="Forbidden"/>: çağıranın lead açma yetkisi yok.</summary>
public sealed record CampaignLeadResult(Guid? LeadId, string? Error, bool Unavailable = false, bool Forbidden = false)
{
    public bool IsCreated => LeadId is not null;
}

/// <summary>
/// Lead açma sözleşmesi. Lead, çağıranın kimliği ve yetkisiyle kayıt boru hattından açılır (izin, kapsam, audit, olay); yetki yetersizse hata sonuçla döner.
/// Sağlayan modül yoksa çağıran "lead kullanılamıyor" durumunu dürüstçe gösterir.
/// </summary>
public interface ICampaignLeads
{
    Task<CampaignLeadResult> CreateAsync(CampaignLeadRequest request, CancellationToken cancellationToken);
}
