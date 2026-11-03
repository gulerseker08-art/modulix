# ADR-0028: Platform lisans ve tenant yönetimi (ürün sahibi paneli)

- **Durum:** Taslak, 3 Kasım 2026 (ticari kararlar bekleniyor, "Açık kararlar" bölümüne bakın)
- **Karar:** Ürün sahibinin tenant'ları ve lisansları yönettiği ayrı bir yeni panel kurulmaz; mevcut platform yönetimi (Y4, `/platform`) genişletilir. Lisans, bugünkü modül başına açık/kapalı bayrağından; durumu, süresi, kaynağı ve limiti olan bir modele taşınır. Etkin hak tek bir çözümleyiciden hesaplanır. Süre bitince veri silinmez: ek süre, salt okunur, askı sırasıyla işler.
- **İlgili:** ADR-0002 ve ADR-0020 (tek paylaşımlı veritabanı), ADR-0007 (olay altyapısı), ADR-0027 (kural altyapısı, modül lisansına bağlı aksiyonlar); yol haritası Y1.1, Y1.3, Y4.1, Y4.2, Y4.3, Y9.3

## Problem

Ürün sahibi hangi firmanın hangi modülü, hangi planla, ne zamana kadar ve hangi limitlerle kullandığını tek yerden görmek ve yönetmek istiyor. Platform yönetimi bunun bir kısmını zaten yapıyor, ama lisans kavramı yok denecek kadar ince.

## Mevcut durum (koddan doğrulandı)

| Alan | Bugün | Kanıt |
| --- | --- | --- |
| Panel | Firmalar, modül hakları, katalog, olaylar, 2FA sıfırlama; `/platform` ve `/api/platform` | `PlatformEndpoints`, `PlatformAdministration`, `Pages/Platform/*` |
| Tenant durumu | Yalnızca `Active` / `Closed` | `Tenant.cs` |
| Lisans | `ModuleEntitlement(TenantId, ModuleKey, IsEnabled, UpdatedAt)`; plan, süre, kaynak, limit yok | `ModuleEntitlement.cs`, `ModuleEntitlements.cs` |
| Platform yöneticisi | Kullanıcıda tek bayrak, yalnızca CLI ile atanır, her istekte okunur | `ModulixUser.IsPlatformAdministrator`, `PlatformAdministratorCommand` |
| Firma verisi | Platform yöneticisi RLS'i atlamaz, firma verisini görmez | README "Platform yönetimi (Y4)" |
| Platform işlem denetimi | Firmanın kendi `core.tenant` kaydına yazılır; tenant'lar arası tek görünüm yok | `PlatformAdministration.Audit` |
| Liste maliyeti | Her firma satırı için ayrı tenant kapsamı ve sorgu (N+1, sayfa başına en çok 50) | `PlatformAdministration.SummarizeAsync` |
| Veri çıkışı/silme | Yalnızca CLI: `tenant-export`, `tenant-delete` | `docs/tenant-isletim.md` |

Doğrulanmadı (uygulamadan önce denetlenecek): yetki ve modül hakkı için istek başına önbellek olup olmadığı; genel uç noktaların (lead formu, teklif paylaşımı, imza, iletişim) yalnızca tenant aktifliğine mi yoksa modül lisansına da mı baktığı; platform yöneticisi için 2FA zorunluluğunun varlığı (kodda yalnızca tenant ayarı `core.security.require-two-factor` bulundu).

## Karar

### 1. Yeni panel yok, Y4 genişler

Ekranlar `/platform` altında kalır, `RequirePlatformAdministratorFilter` ile korunur, `AppCatalog` `PlatformOnly` mekanizmasını kullanır. Platform yöneticisinin firma verisini görmemesi (RLS atlanmaz) ilkesi korunur. Müşteri verisine bakma ihtiyacı bu panelin parçası değildir; "Destek erişimi" olarak ayrı, süreli ve gerekçeli bir mekanizma (Y1.3) tasarlanır.

### 2. Lisans veri modeli (iki aşama)

Aşama A (Dilim 1): `ModuleEntitlement`'a `ValidUntil` (null: süresiz), `Source` (`Manual`, `Plan`, `Trial`, `Exception`) ve `Note` eklenir. Mevcut kayıtlar `Manual`, süresiz olarak taşınır.

Aşama B (Dilim 4): `platform` şemasında üç kavram:

- **Plan:** modül seti ve limitler (ör. "CRM Başlangıç").
- **Abonelik:** tenant'a atanmış plan; başlangıç, bitiş, durum.
- **İstisna:** plana göre fark (modül ekleme/çıkarma, geçici limit artışı); gerekçe ve bitiş zorunlu.

Etkin hak her zaman tek `IEntitlementResolver`'dan okunur; `IModuleEntitlements` imzası geriye uyumlu kalır ve çözümleyiciyi sarar. Plan ve abonelik tabloları `TenantId` taşıyabilir ama RLS dışındadır (platform tablosu); `TableInventoryTests` içinde platform olarak sınıflandırılır ve `tenant-export` / `tenant-delete` kapsamına katalogdan kendiliğinden girer.

### 3. Tenant yaşam döngüsü

`TenantStatus` genişler: `Trial`, `Active`, `GracePeriod`, `ReadOnly`, `Suspended`, `Closed`.

| Durum | Giriş | Okuma | Yazma | Not |
| --- | --- | --- | --- | --- |
| Trial / Active | Evet | Evet | Evet | Deneme süresi `ValidUntil` ile |
| GracePeriod | Evet | Evet | Evet | Yöneticiye uyarı |
| ReadOnly | Evet | Evet | Hayır | Dışa aktarma açık |
| Suspended | Hayır | Hayır | Hayır | Veri durur, silinmez |
| Closed | Hayır | Hayır | Hayır | Bugünkü davranış |

Geçişler platform yöneticisi işlemiyle ya da süre dolumu işçisiyle olur; her geçiş gerekçeli ve denetimlidir. Veri silme geçişle değil, ayrı onaylı `tenant-delete` adımıyla yapılır. Süre dolumu işçisi mevcut işçilerin kalıbını izler: tenant başına `sp_getapplock`, çoklu örnekte güvenli, tenant bağlamıyla çalışır.

### 4. Uygulama noktaları (sunucuda ve sorgu seviyesinde)

Lisans yalnızca arayüzde gizlenmez. Denetim şu noktalarda yapılır: kayıt boru hattının lisans adımı, `.RequireModule`, arka plan işleyicileri (zaten `Status == Active` ve `IsEnabled` süzer; yeni durumlar bu süzgeçlere eklenmelidir), davet (koltuk limiti) ve genel uç noktalar. `ReadOnly` yazma reddi boru hattında tek noktada uygulanır.

### 5. Platform rolleri ve güvenlik

Tek bayrak, platform rollerine genişler: **Destek** (salt okunur, olay yeniden deneme), **Operasyon** (firma aç/kapat, modül), **Finans** (plan, abonelik, istisna), **Platform sahibi** (rol atama). Platform hesapları için 2FA zorunludur (mevcut 2FA altyapısı kullanılır). Platform sahibi atama CLI'da kalır; arayüzden yalnızca atanmış sahip rol verir. Askıya alma, kapatma, istisna ve süre uzatma gerekçe ister.

### 6. Platform denetim kaydı

Platform işlemleri firmanın `core.tenant` kaydına yazılmaya devam eder (tenant yöneticisi kendi firmasında olanı görür) ve ayrıca `platform` şemasında, tenant'lar arası, ekleme-yalnız bir platform denetim tablosuna yazılır (uygulama rolüne `UPDATE/DELETE` yasak, `core.AuditLog` kalıbı). Çift yazım tek işlemdedir.

### 7. Ekranlar

Genel bakış (durum sayıları, süresi yaklaşanlar, başarısız olaylar); firma detayında lisans zaman çizelgesi, limit/kullanım, istisnalar ve o firmaya yapılan platform işlemleri; planlar; süre takvimi (30/60/90 gün) ve toplu işlemler (gerekçeli); platform denetim kaydı; platform ekibi. Firma listesi N+1 yerine tek toplu sorguyla beslenir.

### 8. On-prem

Müşteri tarafında platform paneli yoktur; lisans orada imzalı bir lisans dosyasıdır. Etkin hak modeli iki kaynaktan (veritabanı aboneliği, imzalı dosya) aynı `IEntitlementResolver` arayüzüyle beslenecek şekilde kurulur. İmza ve doğrulama biçimi bu ADR'nin kapsamı dışıdır, ayrı karar gerektirir.

## Dilimler

1. **Temel:** yeni durumlar, `ValidUntil` / `Source` / `Note`, platform denetim tablosu, firma listesi N+1 düzeltmesi.
2. **Panel:** lisans zaman çizelgesi, süre takvimi, gerekçeli işlemler, genel bakış.
3. **Uygulama:** süre dolumu işçisi, ek süre ve salt okunur davranış, yönetici uyarısı, genel uç noktalarda lisans denetimi.
4. **Plan ve limit:** Plan, Abonelik, İstisna, `IEntitlementResolver`, koltuk limiti, kullanım çubukları.
5. **Platform rolleri** ve platform hesapları için 2FA zorunluluğu.
6. **Sağlık ve kullanım (Y4.3):** kuyruk, sürüm, hata oranı.
7. **Ayrı kararlar:** destek erişimi (Y1.3), faturalama entegrasyonu, on-prem imzalı lisans.

Her dilim tek başına derlenir, ayrı PR olarak incelenir ve dört testle (mutlu yol, doğrulama, tenant izolasyonu, yetki) gelir. Migration'lar geriye uyumludur: önce alan ekle, veriyi taşı, sonra kullan.

## Yük ölçümü (3 Kasım 2026)

Gerçek SQL Server'a bağlı entegrasyon ortamında EF komutları sayıldı (Debug derleme, yerel veritabanı, firma başına bir üye ve bir modül; ağ gecikmesi yok, yani rakamlar iyimser). İkisi de firma sayısıyla doğrusal büyür.

| Ölçüm | 5 | 25 | 50 |
| --- | --- | --- | --- |
| Firma listesi, EF komutu (`GET /api/platform/tenants`, sayfa = 50) | 19 | 79 | 154 |
| Firma listesi, süre | ~100–150 ms | ~360–520 ms | ~650–760 ms |
| Sözleşme işçisi, boş tenant başına komut | 28 | 28 | 28 |
| Sözleşme işçisi, tenant başına süre | ~234 ms | ~193 ms | ~167 ms |

- **Firma listesi:** yaklaşık 3 × firma + 4 komut (üye sayısı, modüller, yönetici listesi için ayrı tenant kapsamı). Hedef: sayfa için sabit, 3–4 toplu sorgu. Dilim 1 düzeltmesinin kabul ölçütü budur.
- **İşçiler:** yapacak işi olmayan tenant bile her turda 28 komut ve yaklaşık 170–230 ms harcar; tenant'lar sırayla işlenir, tur aralığı 15 dakikadır. Yaklaşık 1000 tenant için tur süresi 3 dakika civarına çıkar (tahmin, ölçülmedi). Süre dolumu işçisi ayrı bir tarama olarak eklenmez: mevcut taramaya bağlanır ya da `ValidUntil` üzerinde indeksli sorguyla yalnızca süresi bugün/yarın dolan firmaları seçer.
- **Ölçülmedi:** sipariş, destek ve teklif işçileri (aynı kalıpta oldukları koddan görülüyor), `sp_getapplock` için açılan ham bağlantılar, 50'den fazla firma.

## Sonuçlar

- Mevcut `SetModuleAsync` ve modül hakları ekranı geriye uyumlu çalışmaya devam eder.
- Süreli lisans, her istekte hak hesaplamasının maliyetini ve değişikliğin ne kadar çabuk yansıyacağını sorun haline getirir; önbellek ve geçersiz kılma stratejisi Dilim 3'te tasarlanır.
- Yeni tenant durumları `Status == Active` süzgeçlerinin hepsini etkiler (işleyiciler, genel uç noktalar); bunlar uygulamadan önce envanterlenir.
- Platform rolleri ve denetim tablosu, bugünkü "platform yöneticisi her şeyi yapar" varsayımına dayanan kodu ve testleri (`PlatformAdministrationTests`, `EndpointProtectionTests`) değiştirir.

## Açık kararlar (ürün sahibi)

1. **Ticari model:** modül bazlı fiyat mı, paket (plan) mı? Paket ise Aşama B gerekli.
2. **Limitler:** hangileri satışa konu (koltuk, depolama, e-posta, AI)?
3. **Süre sonu:** ek süre kaç gün; ardından salt okunur mu, askı mı?
4. **Deneme:** süre, hangi modüller, bitince otomatik askı mı?
5. **Faturalama:** ödeme sağlayıcısına bağlanacak mı, yoksa panel yalnızca lisans kaydı mı tutacak?
6. **Platform ekibi:** kaç kişi, hangi roller gerçekten gerekli?
7. **On-prem:** ilk müşteri on-prem mi (imzalı lisans dosyası dilim sırasını değiştirir)?

## Uygulama durumu

**Dilim 1 (uygulandı, `gseker/platform-lisans-dilim1`):**

- `TenantStatus`: `Trial` ve `Suspended` eklendi; erişim kararı `TenantStatusRules.Operational`'da (Active, Trial). Doğrudan `Status == Active` karşılaştıran işleyiciler, genel uç noktalar, üyelik/hesap sorguları ve olay dağıtıcısı buna taşındı. Olay dağıtıcısı askıdaki firma için "Firma askıda." nedeniyle teslimi atlar.
- Geçişler: `suspend` (gerekçe zorunlu), `activate` (Trial → Active), `close`/`reopen` (isteğe bağlı gerekçe). `reopen` önceki deneme durumunu tutmaz (Active'e döner).
- `ModuleEntitlement`: `Source` (Manual/Plan/Trial/Exception), `ValidUntil`, `Note`. Mevcut kayıtlar Manual ve süresiz taşındı. İstisna için not zorunlu, bitiş gelecekte olmalı. **Bitiş tarihi yalnızca kayıttır; uygulanması Dilim 3'tedir.**
- `platform.PlatformAuditLog`: ekleme-yalnız (uygulama rolüne `UPDATE/DELETE` yasak), firma silinse de saklanır; sütun `TargetTenantId`. Okuma: `GET /api/platform/audit`. Durum geçişlerinde platform kaydı ve firma kaydı aynı `SaveChanges`'te yazılır; modül, davet ve profil işlemlerinde platform kaydı işlemin kendi `SaveChanges`'ine önceden eklenir (tek işlem).
- Firma listesi: üye sayısı ve açık modüller sayfa için tek sorguyla okunur (önceden firma başına iki sorgu). **Yönetici sayısı hâlâ firma başına bir tenant kapsamı açar:** roller RLS altındadır ve çapraz firma toplanamaz; çözüm (ör. platform tablosunda tutulan sayaç) Dilim 2'dedir.

Bilinen eksikler: iki adımlı doğrulama sıfırlama platform kaydına yazılmaz; platform denetim kaydının ekranı yoktur; `tenant-delete` platform kaydına satır eklemez; arayüzde hak koşulları yalnızca okunur (API'den girilir); `GracePeriod`/`ReadOnly` durumları ve süre dolumu işçisi Dilim 3'tedir.
