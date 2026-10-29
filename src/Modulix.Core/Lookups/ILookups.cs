using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Lookups;

/// <summary>Lookup tanımı ve aktif tenant için geçerli ayarı.</summary>
/// <param name="Searchable">Geçerli değer: firmanın seçimi, yoksa tanımın varsayılanı.</param>
/// <param name="Customized">Firma varsayılandan farklı bir seçim yapmış mı (satırda değer var mı)?</param>
/// <param name="Version">Ayar sürümü; hiç düzenlenmemişse 0.</param>
public sealed record LookupInfo(string ModuleKey, LookupDefinition Definition, bool Searchable, bool Customized, long Version);

/// <summary>Sonuç satırı. Etiket ve ikincil bilgi kullanıcının görebildiği alanlardan gelir; erişilemeyen kayıt için etiket null'dır.</summary>
public sealed record LookupItem(Guid Id, string? Label, string? Secondary, bool Unavailable);

public sealed record LookupPage(IReadOnlyList<LookupItem> Items, int Page, int PageSize, int TotalCount, bool Searchable, bool CanView);

/// <param name="ParentId">Bağımlı lookup'ta üst alanın değeri (ör. firma); sunucuda süzgeç olarak uygulanır.</param>
public sealed record LookupSearch(string Key, string? Text, int Page, int PageSize, Guid? ParentId);

/// <summary>
/// Lookup ayarları (ADR-0016), aktif tenant için. Okuma her üyeye açıktır (formlar davranışı buradan öğrenir); yazma
/// <c>core.customization.manage</c> ister (uç noktada). Yalnızca açık modüllerin lookup'ları görünür ve düzenlenir.
/// Değişiklik denetim kaydına yazılır; çakışmada <see cref="RecordErrorCode.Conflict"/> döner.
/// </summary>
public interface ILookupSettings
{
    Task<IReadOnlyList<LookupInfo>> ListAsync(CancellationToken cancellationToken);

    Task<LookupInfo?> FindAsync(string key, CancellationToken cancellationToken);

    /// <param name="searchable">true/false: firmanın seçimi; null: varsayılana dön.</param>
    /// <param name="version">Ekranın gördüğü sürüm (hiç düzenlenmemişse 0).</param>
    Task<RecordResult<LookupInfo>> SaveAsync(string key, bool? searchable, long version, CancellationToken cancellationToken);
}

/// <summary>
/// Lookup arama ve çözümleme. Sonuçlar kayıt boru hattından geçer: tenant, modül hakkı, izin, veri kapsamı ve hassas
/// alanlar hedef kaydın kurallarıyla uygulanır; arama, sayfalama, toplam ve kimlikten çözümleme aynı kuralları kullanır.
/// "Aranabilir" kapalıysa metin araması reddedilir (<see cref="RecordErrorCode.ValidationFailed"/>, alan "search");
/// kimlikten çözümleme ve sayfalı gezinme çalışır.
/// </summary>
public interface ILookupSearch
{
    Task<RecordResult<LookupPage>> SearchAsync(LookupSearch request, CancellationToken cancellationToken);

    /// <summary>En çok <see cref="RecordLookupLimits.MaxResolveIds"/> kimlik; erişilemeyen kimlik için <see cref="LookupItem.Unavailable"/>.</summary>
    Task<RecordResult<IReadOnlyList<LookupItem>>> ResolveAsync(string key, IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
}
