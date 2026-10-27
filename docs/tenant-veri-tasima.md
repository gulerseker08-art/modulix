# Tenant veri taşıma, cutover ve geri dönüş (sözleşme — ayrı PR)

ADR-0009 ile mimari "ortak platform DB + tenant başına ayrı iş DB" modeline geçti. Bu belge, **mevcut ortak
`Modulix` veritabanındaki geçmiş iş verisini** tenant başına ayrı veritabanlarına taşıyan aracın sözleşmesini
tanımlar. Araç bu PR'ın **kapsamı dışındadır** (ADR-0009 "Sonuçlar"); sıfır kurulum ve provisioning bu PR'da
tamamlanmıştır. Bu belge, aracı uygulayacak ayrı PR'ın uyması gereken davranışı sabitler.

## Neden ayrı

Geçmiş veri taşıma, üzerinde çalışılacak gerçek çok-tenantlı veri gerektirir; güvenli doğrulaması (kaynak/hedef
sayımları, ilişki bütünlüğü) ve cutover prosedürü, sıfır kurulumdan bağımsız olarak ayrı doğrulanmalıdır (ADR-0002/0009).
Bu PR'daki tenant baseline'ı **sıfır kurulum** içindir; eski ortak DB'den yükseltme bu aracın sorumluluğundadır.

## Araç gereksinimleri

Varsayılan olarak **yazma yapmayan bir ön kontrol/dry-run** modu ve açık komutla çalışan bir taşıma modu:

- Tenant sahipliği olmayan, çelişen veya tenantlar arası ilişki kuran kayıtları raporlar.
- İlişkili verileri bağımlılık sırasıyla taşır; mevcut ID'leri ve ilişkileri korur.
- Mükerrer kayıt üretmeden tekrar çalıştırılabilir (idempotent).
- Kaynak ve hedef kayıt sayılarını, ilişkileri ve kritik içerikleri doğrular.
- Kaynak veriyi **silmez**.
- Doğrulama tamamlanmadan tenant→DB eşleştirmesini (ve dolayısıyla `ProvisioningStatus = Active`) değiştirmez.

## Cutover

1. Bakım penceresi / yazma durdurma: tenant askıya alınır (`Suspended`) veya yazmalar reddedilir.
2. Devam eden işler (outbox/olay teslimleri, e-posta kuyruğu) boşaltılır veya taşınır.
3. Taşıma apply modunda çalışır; doğrulama geçer.
4. Tenant→DB eşleştirmesi tenant DB'sine çevrilir; defter önbelleği geçersizleştirilir (`ITenantDatabaseRegistry.Invalidate`).
5. Tenant `Active` yapılır; yazmalar açılır.

Eşleştirme önbelleği kısa ömürlüdür (30 sn) ve değişiklikte geçersizleşir; cutover bunu dikkate alır.

## Geri dönüş

- Tenant DB'sine **yazılmaya başlanmadan** önce geri dönüş güvenlidir: eşleştirme eski ortak DB'ye döndürülür.
- Tenant DB'sine yazılmaya başlandıktan sonra yalnızca eski bağlantıya dönmek **veri kaybına** yol açar (yeni DB'deki
  yazılar eski DB'de yoktur). Bu durumda geri dönüş, yeni DB'den eski DB'ye ters taşıma + doğrulama gerektirir; düz
  "bağlantıyı geri al" yapılmaz.
- Bu yüzden cutover adım 4 (eşleştirme değişimi) dönülmez noktadır; öncesi dry-run ile kanıtlanmalıdır.

## Bu PR'da hazır olan dayanaklar

- `ITenantProvisioningService`: tenant DB'si oluşturma + migration + yetki.
- `ITenantDatabaseRegistry` / `ITenantConnectionResolver`: tenant→DB defteri ve güvenli çözümleme (+ önbellek geçersizleştirme).
- `TenantMigrationRunner`: tenant DB'lerini sürümleme.
- `Tenant.ProvisioningStatus` durum makinesi (`Pending/Provisioning/Active/Failed/Suspended`).

Taşıma aracı bunların üzerine kurulacaktır.
