namespace Modulix.Core.Contracts.Settings;

/// <summary>
/// Aktif tenant'ın firma ayarlarını okur (ADR-0046). Modüller ve modül uç noktaları ayar değerine bununla erişir;
/// ayar ekranının listeleme ve yazma işlemleri çekirdekteki <c>ITenantSettings</c>'te kalır (yazma
/// <c>core.settings.manage</c> ister ve kayıt boru hattından geçer).
/// </summary>
public interface ITenantSettingReader
{
    /// <summary>Tek ayarın değeri (kaydedilmemişse varsayılan). Tanımsız anahtar için <see cref="ArgumentException"/>.</summary>
    Task<string> GetValueAsync(string key, CancellationToken cancellationToken);
}
