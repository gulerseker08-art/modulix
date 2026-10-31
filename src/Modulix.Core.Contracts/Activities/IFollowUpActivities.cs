namespace Modulix.Core.Contracts.Activities;

/// <summary>Takip aktivitesi isteği: konu, açıklama, son tarih, atanan kullanıcı ve aktivitenin bağlanacağı kayıt.</summary>
public sealed record FollowUpRequest(string Subject, string? Description, DateOnly DueDate, Guid? AssigneeId, string LinkedEntityKey, Guid LinkedRecordId);

/// <summary>Sonuç: oluştuysa aktivite kimliği, oluşmadıysa nedeni. <see cref="Unavailable"/>: aktivite modülü bu tenant için kapalı (hata değil, yok sayılabilir durum).</summary>
public sealed record FollowUpResult(Guid? ActivityId, string? Error, bool Unavailable = false)
{
    public bool IsCreated => ActivityId is not null;
}

/// <summary>
/// Başka modüllerin (ör. sözleşme taahhüt ihlali) takip aktivitesi açması için bağlantı sözleşmesi. Aktivite alanı bir modüle aittir (CRM); onu sağlayan modül
/// bunu uygular, sağlayan yoksa (modül kapalı ya da kurulu değil) çağıran servisi alamaz ve "aktivite kullanılamıyor" durumunu kaydeder. Aktivite çağıranın
/// kimliği ve yetkisiyle kayıt boru hattından açılır; yetki yetersizse hata sonuçla döner (çağıran kendi kaydını yine tamamlar).
/// </summary>
public interface IFollowUpActivities
{
    Task<FollowUpResult> CreateAsync(FollowUpRequest request, CancellationToken cancellationToken);
}
