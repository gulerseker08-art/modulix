using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Settings;

/// <summary>
/// Bir ayarın aktif tenant'taki değeri. Kaydedilmemişse <see cref="Value"/> varsayılandır ve <see cref="Version"/> 0'dır.
/// </summary>
public sealed record TenantSettingValue(string ModuleKey, SettingDefinition Definition, string Value, long Version);

/// <summary>Kaydedilecek değer ve okunduğu sürüm (kaydedilmemiş ayar için 0); sürüm değişmişse çakışma.</summary>
public sealed record SettingWrite(string Value, long Version);

/// <summary>
/// Aktif tenant'ın firma ayarları (Y6). Okuma her üyeye açıktır; yazma <c>core.settings.manage</c> ister ve kayıt boru
/// hattından geçer (izin, sürüm, denetim kaydı). Yalnızca açık modüllerin tanımlı ayarları görünür ve yazılır.
/// </summary>
public interface ITenantSettings
{
    /// <summary>Açık modüllerin ayarları, tanım sırasıyla, kaydedilmemişler varsayılanla.</summary>
    Task<IReadOnlyList<TenantSettingValue>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Değerleri tek işlemde kaydeder; biri başarısız olursa hiçbiri yazılmaz. Değişmeyen değer yazılmaz (denetim kaydı
    /// da yoktur). Tanımsız veya modülü kapalı anahtar için <see cref="ArgumentException"/> (ParamName anahtardır).
    /// Hatalar: doğrulama (alan adı ayar anahtarı), çakışma, yetki.
    /// </summary>
    Task<RecordResult<IReadOnlyList<TenantSettingValue>>> SaveAsync(
        IReadOnlyDictionary<string, SettingWrite> values, CancellationToken cancellationToken);
}
