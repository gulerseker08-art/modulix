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

**Dilim 2 (uygulandı, `gseker/platform-panel-dilim2`):**

- **Genel bakış:** `GET /api/platform/overview` (durumlara göre firma sayıları; süresi geçmiş ve 30/60/90 günde dolacak açık haklar) ve `/platform` sayfasında başarısız olay sayısıyla birlikte gösterim. Yalnızca platform tablolarından okunur.
- **Süre takvimi:** `GET /api/platform/expirations?days=` ve `/platform/sure-takvimi`. Açık firmaların bitiş tarihi olan açık hakları; geçmiş tarihliler üstte. Bitiş hâlâ uygulanmaz.
- **Gerekçeli işlemler:** `close` (sunucuda zorunlu), modül kapatma ve süre uzatma (bitişi ileri alma ya da kaldırma) gerekçe ister; gerekçe platform denetim kaydına yazılır. `SetTenantModuleRequest.Reason` eklendi. Kısaltma ve ilk kez tarih vermek gerekçe istemez. Askı gerekçesi Dilim 1'den beri zorunludur. Toplu işlemler bu dilimde yoktur.
- **Hak koşulları arayüzde düzenlenir** (modül, kaynak, bitiş, not, gerekçe) ve firma sayfasında "Lisans ve işlem geçmişi" bölümü firmanın platform kaydını gösterir. Tam liste `/platform/denetim` (işlem süzgeciyle).
- **Yönetici sayısı:** firma listesi artık firma başına tenant kapsamı açmaz; `administratorCount` listede `null`, detayda dolu. Sayaç tutmak (4 farklı rol atama yolu) yerine bu seçildi; liste artık sayfa boyutundan bağımsız sabit sayıda sorgu çalıştırır. "Yönetici yok" uyarısı yalnızca firma detayındadır. Listede bu uyarıya ihtiyaç doğarsa sayaç ayrı bir iştir.
- İki adımlı doğrulama sıfırlama platform denetim kaydına yazılır (`user.two-factor-reset`, hedef firma yok).

Kalan eksikler: `tenant-delete` platform kaydına satır eklemez (CLI'da işlemi yapan kullanıcı yok); toplu işlemler ve süre takviminden doğrudan uzatma yok; bitiş tarihi uygulanmaz, `GracePeriod`/`ReadOnly` ve işçi Dilim 3'tedir; arayüz yalnızca derlenip incelendi, tarayıcıda gezilmedi.

**Dilim 3 (uygulandı, `gseker/platform-lisans-dilim3`):**

- **Sapma, gerekçesiyle:** ADR "süre dolumu işçisi" ve firma düzeyinde `GracePeriod`/`ReadOnly` durumları öngörüyordu. Bunun yerine **evre modül hakkı düzeyinde ve bitiş tarihinden her okumada hesaplanır** (`EntitlementPhases.At`); saklanan durum ve işçi yoktur. Sebep: işçiye bağlı geçiş gecikir ve çoklu örnekte tutarsızlaşır, hesaplanan evre tarih değişince anında doğrudur; firma durumları (`Suspended`/`Closed`) elle yönetilmeye devam eder. Tenant yaşam döngüsü tablosundaki `GracePeriod`/`ReadOnly` satırları modül hakkı için geçerlidir.
- **Varsayılanlar (ürün kararı 3 ve 4 için, ürün sahibi onayıyla):** ek süre 14 gün (`License:GraceDays`), ardından salt okunur; askı otomatik değildir. Deneme modülleri aynı evrelerden geçer (otomatik askı yok).
- **Uygulama noktaları:** `RequireModule` (yazma yöntemleri salt okunurda 403; `/preview` ile biten POST'lar hariç); modül işçileri (5); oturumsuz uçlar: web formu gönderimi, teklif bağlantısı (görüntüleme açık, karar yazma kapalı), anket (görüntüleme açık, yanıt kapalı), imza webhook'u. Daha önce oturumsuz uçlar modül kapalıyken de çalışıyordu; bu da kapandı. İletişim tercihi/abonelikten çıkma bağlantıları bilerek lisansa bağlanmadı.
- **Yönetici uyarısı:** `GET /api/tenant/access` yanıtına `licenses` eklendi; arayüzde yalnızca firma yöneticisine banner. E-posta uyarısı yapılmadı (tekrar göndermemek için gönderim durumu saklamak gerekir).
- Süre takvimi ve genel bakış evre ve salt okunur sayısı gösterir.

Bilinen sınırlar: POST ile okuma yapan ve adı `/preview` ile bitmeyen uçlar (ör. rapor çalıştırma modül grubundaysa) salt okunurda reddedilir; yeni bulunursa adlandırma ya da süzgeç istisnasıyla çözülür. Evreyi hesaplayan okuma her yazma isteğinde bir ek sorgudur (önbellek yok; ölçülmedi). Arka plan işleyicilerinden (EventDispatcher, otomasyon) salt okunurda yazılanlar bu dilimde engellenmedi; olay teslimi modül açık mı bakar, yazma evresine bakmaz. Arayüz tarayıcıda gezilmedi.

**Dilim 4 (uygulandı, `gseker/platform-plan-dilim4`; Dilim 3 üzerine yığılı):**

- **Değişmez ilke (ürün sahibi, 4 Kasım 2026): veri hiçbir zaman silinmez, yalnızca erişim sınırlanır.** Plan kapatılır (silinmez), abonelik kapatılır (geçmiş kalır), istisna geri alınır, plan değişince kapanan modülün verisi yerinde kalır, koltuk sınırı mevcut kullanıcıları kapatmaz.
- Tablolar (`platform` şeması, RLS dışı): `Plans`, `PlanModules`, `PlanLimits`, `TenantSubscriptions` (firma başına tek geçerli kayıt, filtreli benzersiz indeks), `LimitOverrides`. Migration yalnızca tablo ekler (geriye uyumlu).
- **Tasarım kararı:** plan modül hakları `ModuleEntitlements`'a kaynağı `Plan` olarak **yazılır** (materyalize); böylece Dilim 3'ün süre/salt okunur uygulaması ve bütün mevcut denetim noktaları değişmeden çalışır. `IEntitlementResolver` yalnızca limitleri çözer; modüller için tek kaynak `IModuleEntitlements` kalır.
- **Varsayılanlar (ürün kararı 1 ve 2 için bekleyen cevaba karşı):** ticari model paket (plan) tabanlıdır; satışa konu tek limit koltuktur. Depolama, e-posta ve AI limitleri kullanım ölçümü olmadığı için tanımlanmadı; `LimitKeys` listesine eklenip kullanımı ölçülünce açılır.
- Plan değişikliği mevcut abonelerin modüllerine yansımaz (yeniden atama gerekir): toplu etkiyi ve fiyatlama sonuçlarını önlemek için bilinçli.

Bilinen sınırlar: plan atama birden fazla ayrı işlemdir (modüller, sonra abonelik); ortada hata olursa modüller değişmiş abonelik yazılmamış kalabilir, yeniden denemek aynı sonucu verir. İstisna yalnızca koltuk için arayüzde var. Plana toplu atama, fiyat ve fatura yok (ürün kararı 5). Arayüz tarayıcıda gezilmedi.

**Dilim 5 (uygulandı, `gseker/platform-roller-dilim5`; Dilim 4 üzerine yığılı):**

- **Roller:** `Support`, `Operations`, `Finance`, `Owner` (bayrak birleşimi). Kullanıcıya `PlatformRoles` sütunu eklendi (migration yalnızca sütun ekler, varsayılan 0). **Geriye uyum:** bayrak açık + rol boş = Owner; mevcut hesaplar ve CLI yetkisini korur.
- **Karar tablosu:** `PlatformAccessRules` (okuma her role açık, yazma uçları tek tabloda). Tabloda olmayan yazma ucu Owner'a özeldir (varsayılan ret); bir mimari-benzeri test bütün `/api/platform` yazma uçlarının tabloda olmasını zorlar (bu test yazılırken `POST /api/platform/team/` sondaki "/" yüzünden eşleşmeyen gerçek bir açığı yakaladı; anahtar normalleştirildi).
- **Rol varsayımları (ürün kararı 6 henüz cevaplanmadığı için):** ADR'deki dört rol aynen; Destek ayrıca iki adımlı doğrulama sıfırlar (Operasyon da yapar); modül hakkı Operasyon ve Finans'ta ortak. Ekip yönetimi yalnızca Owner; Owner ataması CLI'da kalır ve arayüzden değiştirilemez.
- **İki adımlı doğrulama zorunluluğu:** `Platform:RequireTwoFactor` (varsayılan açık). Doğrulaması kapalı platform hesabının bütün platform istekleri 403 + `two-factor-required`; durum her istekte okunur.
- **Veri silinmez:** ekipten çıkarmak yalnızca platform erişimini kapatır (bayrak ve roller), hesap ve firma üyelikleri kalır.

Bilinen sınırlar: **dağıtımda mevcut platform yöneticileri iki adımlı doğrulamayı açana kadar platform alanına giremez** (ilk kurulumda `Platform:RequireTwoFactor=false` ile geçiş yapılabilir). Arayüz yalnızca derlendi; rol bazlı gizleme yalnızca Owner'a özel "Platform ekibi" kutusu ve ekip sayfasındadır, diğer ekranlar yetkisiz düğmeleri gizlemez (sunucu 403 döner). `platform-admin` komutu rol parametresi almaz. Owner'ı arayüzden düşürme ya da son Owner koruması yok (Owner'a arayüzden dokunulamadığı için gerekmedi). Platform hesabının oturum kapatma/yeniden doğrulama (step-up) akışı yok.

**Dilim 6 (uygulandı, `gseker/platform-saglik-dilim6`):**

- `GET /api/platform/health` ve `/platform/saglik`: sürüm, kuyruk (e-posta, olay), son 24 saat hata oranı, kullanım ve güvenlik olayları. Ayrıntı README'dedir.
- **Y4.3 kabul ölçütü ("Destek ödeme bilgisini veya tüm müşteri dosyalarını göremez") yapıdan sağlanır:** rapor yalnızca sayı, zaman ve firma adı taşır, hiçbir firma verisi ya da ödeme bilgisi okunmaz; test, firma kullanıcısının adı ve e-postasının yanıtta olmadığını doğrular. Okuma her platform rolüne açıktır (rol tablosunda GET varsayılanı).
- **Yapılmayanlar (bilerek):** yedek durumu (uygulamanın bilgisi yok; veritabanı sunucusunda), ödeme bilgisi (faturalama ayrı karar, ürün kararı 5), tenant araması (firma listesi zaten arama sunuyor), dış izleme/uyarı (e-posta/pager).

Bilinen sınırlar: e-postada başarısızlık anı saklanmadığı için e-posta hata oranı "son 24 saatte kuyruğa girip kalıcı başarısız olan" ile ölçülür (yaklaşık). "En eski bekleyen" yaşı bekleyenin oluşma anına göredir (yeniden deneme zamanına değil). Rapor her istekte hesaplanır (önbellek yok; birkaç toplu sorgu). Arayüz tarayıcıda gezilmedi.

**Dilim 7a: destek erişimi (uygulandı, `gseker/platform-destek-erisimi-dilim7`; Dilim 6 üzerine yığılı):**

- **Karar (ürün sahibi onayladı, 5 Kasım 2026: "onay beklemeden"):** erişim **kendi kendine açılır** (Destek, Operasyon veya Owner rolü, gerekçeyle), firma yöneticisinin onayı beklenmez; bunun yerine firma yöneticilerine e-posta gider, firmanın denetim kaydına yazılır ve firma yöneticisi erişimi erken kapatabilir. Onaylı model (yönetici onaylamadan açılmaz) istenirse `GrantAsync` bir "bekliyor" durumuna ve firma tarafı onay ekranına genişler.
- **Tasarım:** destek erişimi gerçek bir üyelik üzerinden kurulur (`MembershipStatus.Support`), böylece izin, kapsam, RLS ve denetim mekanizmaları değişmeden çalışır. Bağlam yalnızca geçerli `SupportAccessGrant` varken kurulur (`MembershipDirectory`); süre dolumu zamanlayıcıya bağlı değildir. "Etkin" olmayan durum olduğu için koltuk, kullanıcı listesi, kayıt sahibi ve bildirim sorguları destek üyeliğini kendiliğinden dışarıda bırakır; kullanıcı yönetimi uçları ayrıca açıkça dışlar.
- **Salt okunur:** rol yalnızca açık modüllerin `*.read` izinlerini taşır; çekirdek (kullanıcılar, roller, denetim, ayarlar) ve dışa aktarma kasıtlı olarak dışarıdadır ("hassas işlemler ayrıca kısıtlı" ölçütü yapıdan sağlanır).
- **Veri silinmez:** erişim kaydı ve üyelik kalır; kapanış yalnızca erişimi keser.

Bilinen sınırlar: okumalar artık tek tek kaydedilir (aşağıdaki not); hassas alan izinleri (ör. fırsat tutarı) ".read" ile bittiği için ilk sürümde destek rolüne sızıyordu; düzeltildi (hassas alan izinleri role verilmez, test: `Support_access_does_not_see_sensitive_fields`) ve rol her açılışta yeniden kurulduğundan eski roller de düzelir. Destek rolü her açılışta açık modüllere göre güncellenir; erişim sürerken modül açılırsa yeni izin bir sonraki açılışta eklenir. Platform arayüzü tarayıcıda gezilmedi. **Dilim 7b (on-prem imzalı lisans) ve 7c (faturalama entegrasyonu) yapılmadı:** ikisi dış karar ister (imza biçimi/anahtar yönetimi; ödeme sağlayıcısı).

**Dilim 7b: on-prem imzalı lisans (uygulandı, `gseker/platform-imzali-lisans-dilim7b`; varsayılan karar, ürün sahibi onayı bekliyor):**

- **Karar:** imza biçimi ECDSA P-256 + SHA-256 (IEEE P1363), zarf `{version, payload(base64), signature(base64)}`; imza base64'e giren baytlar üzerindedir, JSON kanonikleştirme gerekmez. Ed25519 yerine P-256 seçildi çünkü .NET'in kutudaki kriptografisinde vardır, ek paket gerektirmez. Genel anahtar `License:PublicKey` ayarıyla verilir (tek imaj, müşteriye özel kod yok); özel anahtar lisansı düzenleyende kalır ve müşteri ortamına, depoya, imaja girmez. Anahtar döndürme (birden fazla genel anahtar) ve iptal listesi bu dilimde yok; gerekirse `version` artırılarak eklenir.
- **Tasarım:** lisans ayrı bir hak kaynağı değildir. `license-apply`, firmanın lisans planını (`lisans-{kısa ad}`) kurup güncelleyerek `AssignPlanAsync` ile atar; böylece hak çözümü, ek süre/salt okunur evreleri (Dilim 3), koltuk limiti (Dilim 4) ve "silinmez" kuralı değişmeden çalışır. Aynı `IEntitlementResolver` iki kaynağı (veritabanı aboneliği, lisans dosyası) bu yolla besler.
- **İşleyen:** komut satırı olduğu için platform denetimi sistem kimliği (`Guid.Empty`) adına yazılır; lisans numarası gerekçede ve ayrıntıda durur.
- **Bilinen sınırlar:** eski bir lisans dosyasını yeniden uygulamak engellenmez (bitişi geçmişse zaten reddedilir; bitişi gelecekteyse daha dar haklara dönüş yapılabilir, ama dosyayı uygulayan zaten sunucu sahibidir). Lisansın saat tabanlı kontrolü sunucu saatine güvenir. Anahtar döndürme/iptal yok. Platform arayüzü lisans kaynağını ayrıca göstermez (abonelik gerekçesinde "Lisans dosyası …" görünür).

**Plan modül değişikliğinin yayılması (Dilim 4 sınırı kaldırıldı):** plan güncellenirken modül seti değiştiyse geçerli ve süresi dolmamış her abonelik için modül hakları plana göre yeniden ayarlanır (`AssignPlan` ile aynı kural: eklenen modül Plan kaynağıyla açılır, çıkan modül kapanır, elle/istisna haklara dokunulmaz, veri silinmez). Süresi dolmuş (ek süre/salt okunur) abonelikler yenilenene kadar olduğu gibi kalır; her firma için ayrı platform ve firma denetim kaydı yazılır.

**Destek erişimi okuma günlüğü (Dilim 7a sınırı kaldırıldı):** destek oturumundaki her API isteği (yöntem + yol) `platform.SupportAccessReads` tablosuna, **istek işlenmeden önce** yazılır; yazılamazsa istek durur (izsiz okuma olmaz). Reddedilen deneme (ör. 403 alan yazma) de görünür. Sorgu değerleri ve yanıt içeriği kaydedilmez (arama metni kişisel veri taşıyabilir); kayıt ekleme-yalnızdır. Platform ve firma yöneticisi "İşlemler" ile görür. Bedeli: destek oturumunda istek başına bir ek yazma (yalnızca destek üyeliğinde; olağan üyeleri etkilemez).

**Dilim 7c: ödeme kaydı (uygulandı, `gseker/destek-okuma-gunlugu-ve-odeme-kaydi`; varsayılan karar, ürün sahibi onayı bekliyor):**

- **Karar:** ödeme sağlayıcısı seçilmediği için sağlayıcıdan bağımsız bir **defter** kuruldu (dönem, tutar, para birimi, vade, referans; Bekliyor/Ödendi/İptal). Tahsilat ya da fatura Finans rolüyle elle işlenir. Gerçek sağlayıcı (ör. iyzico) ya da e-fatura entegrasyonu mevzuat ve sözleşme kararı ister; eklenirse bir bağdaştırıcı aynı kaydı `MarkPaid` ile kapatır, defter değişmez. Finans sınırı (ADR-0037) korunur: bu bir muhasebe değil, faturalanabilir kalem takibidir.
- **Erişimle bağ yok:** vadesi geçen kayıt erişimi kesmez; yalnızca uyarı gösterir. Lisans bitişi plan/abonelikte (Dilim 3-4) açıkça yönetilir. Otomatik kesme istenirse ayrı karar olur.
- **Silinmez:** iptal gerekçesiyle kalır; ödenen/iptal edilen kayıt yeniden açılmaz.
- **Bilinen sınırlar:** KDV/vergi, kısmi ödeme, tekrarlayan fatura üretimi, firma tarafında görüntüleme yoktur.
