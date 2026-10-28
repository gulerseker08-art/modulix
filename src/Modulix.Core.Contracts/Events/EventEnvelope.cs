using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Contracts.Events;

/// <summary>
/// Kayıt olaylarının türleri (ADR-0007). Kayıt boru hattı her oluşturma, güncelleme ve silmede birini yazar; durum
/// değişikliği <see cref="RecordUpdated"/> içindeki alan farkından anlaşılır. Modüllerin iş olayları (Ç0.4) modül
/// anahtarıyla başlar (ör. "crm.opportunity.won").
/// </summary>
public static class DomainEventTypes
{
    public const string RecordCreated = "RecordCreated";
    public const string RecordUpdated = "RecordUpdated";
    public const string RecordDeleted = "RecordDeleted";

    /// <summary>
    /// Kayıt başka bir kayda birleştirildi ve kaldırıldı (ADR-0013). Olay kaynak kaydındır; değişiklik alanı
    /// <c>mergedInto</c> yeni kimliği verir. Eski kimliğe başvuran modüller buna göre kendi başvurularını yeniler.
    /// </summary>
    public const string RecordMerged = "RecordMerged";

    /// <summary>
    /// Üretilen olay türleri ve şu anki sürümleri. Sürüm, bir alanın anlamı veya biçimi değişince artar (alan eklemek
    /// artırmaz); katalog, bu sürümü okuyamayan aboneliği açılışta reddeder.
    /// </summary>
    public static IReadOnlyDictionary<string, int> SchemaVersions { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [RecordCreated] = 1,
        [RecordUpdated] = 1,
        [RecordDeleted] = 1,
        [RecordMerged] = 1,
    };
}

/// <summary>Olayın nedeni (<see cref="EventEnvelope.Reason"/>); abonelikler buna göre davranabilir.</summary>
public static class EventReasons
{
    /// <summary>Kayıt devri (ADR-0004): sorumlu toplu değişti. Atama e-postası bunları atlar; devir tek özet gönderir.</summary>
    public const string Transfer = "transfer";
}

/// <summary>
/// Abonenin aldığı olay (ADR-0007).
/// <list type="bullet">
///   <item><see cref="Id"/>: olay kimliği; abone idempotentlik için bunu kullanır.</item>
///   <item><see cref="TenantId"/>: olayı üreten isteğin oturumdan doğrulanmış tenant'ı; abone bu tenant'ın bağlamında çalışır.</item>
///   <item><see cref="Sequence"/>: kaydın değişiklikten sonraki sürümü (silmede son sürüm + 1); kayıt dışı olayda boş.</item>
///   <item><see cref="UserId"/>, <see cref="Source"/>: işlemi yapan kişi ve kaynak (denetim kaydıyla aynı). Abonenin
///   yaptığı değişiklikte kişi boştur (sistem), kaynak arka plan işidir.</item>
///   <item><see cref="CorrelationId"/>: isteğin izi, zincir boyunca taşınır. <see cref="CausationId"/>,
///   <see cref="Depth"/>: olay bir abonenin değişikliğinden doğduysa tetikleyen olay ve zincir derinliği (ilk olay 0).</item>
///   <item><see cref="Payload"/>: değişen alanların <c>{ alan: { old, new } }</c> farkı (sürüm 1). Aboneliğin izniyle
///   görülemeyen hassas alanlar çıkarılmıştır; adları <see cref="HiddenFields"/>'tadır.</item>
/// </list>
/// Abone tanımadığı alanı yok sayar; alan eklemek sürümü değiştirmez.
/// </summary>
public sealed record EventEnvelope(
    Guid Id,
    Guid TenantId,
    string EventType,
    int SchemaVersion,
    string EntityKey,
    Guid RecordId,
    long? Sequence,
    Guid UserId,
    ExecutionSource Source,
    string? CorrelationId,
    Guid? CausationId,
    int Depth,
    JsonObject Payload,
    DateTimeOffset OccurredAt)
{
    /// <summary>Olayın nedeni (ör. <see cref="EventReasons.Transfer"/>); çoğu olayda boş.</summary>
    public Guid? CompanyId { get; init; }

    public string? Reason { get; init; }

    /// <summary>Payload'dan çıkarılan hassas alanların adları: alan değişti ama değeri aboneye gizli.</summary>
    public IReadOnlyList<string> HiddenFields { get; init; } = [];
}
