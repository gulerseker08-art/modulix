using Modulix.Core.Contracts.Records;
using Modulix.Core.Records;

namespace Modulix.Core.Audit;

/// <summary>
/// Denetim kaydı sorgusu (Y5). Tarih aralığı <see cref="From"/> dahil, <see cref="To"/> hariçtir. Sayfa 1'den başlar.
/// </summary>
public sealed record AuditQuery(
    Guid? UserId = null,
    string? EntityKey = null,
    AuditAction? Action = null,
    ExecutionSource? Source = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? RecordId = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>
/// Değişen bir alan, gösterime hazır: <see cref="Label"/> alanın görünen adı; değerler seçenek adı, kişi adı veya bağlı
/// kaydın adıyla çözülmüştür. Değerler gizliyse ikisi de boştur.
/// </summary>
public sealed record AuditFieldChange(string Field, string Label, string? Old, string? New);

/// <summary>
/// Denetim kaydının bir satırı. <see cref="ValuesHidden"/>: okurun bu kayıt türünü görüntüleme izni yok; değişen
/// alanların adları görünür, değerleri ve kaydın adı gizlidir. <see cref="UserName"/>: firmanın üyesi olmayan platform
/// yöneticisi için "Modülix platform yöneticisi".
/// </summary>
public sealed record AuditLogItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid UserId,
    string UserName,
    string? UserEmail,
    string EntityKey,
    string EntityName,
    Guid RecordId,
    string? RecordTitle,
    AuditAction Action,
    ExecutionSource Source,
    bool ValuesHidden,
    IReadOnlyList<AuditFieldChange> Changes,
    string? CorrelationId);

/// <summary>Denetim kaydında işlem yapmış bir kişi (kullanıcı filtresi için).</summary>
public sealed record AuditActor(Guid UserId, string Name);

public sealed record AuditLogPage(IReadOnlyList<AuditLogItem> Items, int Page, int PageSize, int TotalCount);

/// <summary>Dışa aktarma sınırı aşıldı; arayüz tarih aralığını daraltmayı önerir. API 400.</summary>
public sealed class AuditExportTooLargeException(int count, int limit)
    : Exception($"Seçilen aralıkta {count} kayıt var; bir seferde en fazla {limit} kayıt dışa aktarılabilir. Tarih aralığını daraltın.");

/// <summary>
/// Aktif tenant'ın denetim kaydını okur (Y5). Yalnızca okur: denetim kaydını değiştiren veya silen bir yol yoktur
/// (veritabanında da uygulama hesabına UPDATE ve DELETE yasaktır). Çağıran <c>core.audit.read</c> iznine sahip olmalıdır
/// (API denetler). Okurun görüntüleme izni olmayan kayıt türlerinin değerleri gizlenir (<see cref="AuditLogItem.ValuesHidden"/>);
/// yönetici her şeyi görür.
/// </summary>
public interface IAuditLog
{
    /// <summary>En yeni üstte.</summary>
    Task<AuditLogPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken);

    /// <summary>Kayıtta işlem yapmış kişiler, ada göre sıralı.</summary>
    Task<IReadOnlyList<AuditActor>> ListActorsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Filtreye uyan tüm satırlar (sayfa yok sayılır), en yeni üstte. <paramref name="limit"/>'ten fazlaysa
    /// <see cref="AuditExportTooLargeException"/>. Dışa aktarma da denetim kaydına (<c>core.audit</c>) yazılır.
    /// </summary>
    Task<IReadOnlyList<AuditLogItem>> ExportAsync(AuditQuery query, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Bir kaydın geçmişi (Y5b), en yeni üstte. <c>core.audit.read</c> gerekmez: çağıran kaydı görebildiğini doğrulamış
    /// olmalıdır (API kaydı kayıt boru hattından okur), bu yüzden değerler gizlenmez.
    /// </summary>
    Task<AuditLogPage> GetRecordHistoryAsync(RecordRef record, int page, int pageSize, CancellationToken cancellationToken);
}
