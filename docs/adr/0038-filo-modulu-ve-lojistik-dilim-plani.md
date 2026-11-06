# ADR-0038 — Filo modülü ve lojistik (M2) dilim planı

Durum: **taslak** (7 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz; kod yazılmadı, test çalıştırılmadı. Mevzuata değen maddeler (bölüm 8) uzman doğrulaması ister; doğrulanmamış kural zorunlu üretim kuralı yapılmaz (ADR-0032 ilkesi).

İlgili: yol haritası M2, M2.1–M2.8, M3.2, M4.5, Ç0.4, Ç4, Ç6a, Ç8, Ç11b, Ç16, Ç18a, Ç21.4; ADR-0006 (ortak firma/kişi), ADR-0007 (olay altyapısı), ADR-0008 (şirket kapsamı), ADR-0021 (form düzeni), ADR-0023 (sipariş ve operasyon işi), ADR-0027 (kural altyapısı), ADR-0029 (sektör/faaliyet modeli, ilişki rolleri), ADR-0031 (araç kiralama), ADR-0032 (depo/antrepo), ADR-0033 (içe aktarma), ADR-0037 taslağı (finans sınırı ve faturalanabilir kalem).

## 1. Bağlam

Lojistik, yol haritasının ikinci pilotudur (D2) ve `Modulix.Modules.Logistics` bugün yalnızca modül tanımından oluşur: altı izin, bir rol şablonu ve `logistics.shipment` / `logistics.vehicle` otomasyon beyanı. Entity, kayıt deposu, uç nokta ve ekran yoktur. Yol haritası M2.1–M2.8 kapsamı tanımlar, ama dilim sırası, araç kimliğinin sahibi ve lojistiğin Core ön koşulları tek bir kararda toplanmamıştır.

Üç boşluk bu ADR'yi gerektiriyor:

1. **Araç kimliğinin sahibi.** ADR-0031 K3 araç kaydını rentcar şemasına koyar ve ortak filoyu "ikinci modül gerçekten ihtiyaç duyunca" erteler. Lojistik pilotu o ikinci modüldür; araç, dorse, araç belgesi ve bakım iki modülün aynı anlam ve yaşam döngüsüyle paylaştığı kavramlardır (yol haritası "yalnızca anlamı ve yaşam döngüsü gerçekten ortak olan altyapı" ölçütü).
2. **Süreli belge.** Araç (muayene, sigorta, yetki belgesi), sürücü (ehliyet, SRC, psikoteknik), firma (vekâlet, ADR-0032 K4) ve tesis (antrepo izni) için aynı ihtiyaç var: belge türü, geçerlilik aralığı, dosya ve yaklaşan bitiş uyarısı. Bugün her modül kendi alanını açıyor (`nextMaintenanceDate`, vekâlet tarihleri).
3. **Core ön koşulları.** Teslim kanıtı fotoğrafı ve masraf belgesi için dosya deposu (Ç4), masraf için genel onay (Ç6a), sefer ve irsaliye numarası için belge serisi (Ç8.1), teslim → faturalanabilir kalem köprüsü için iş olayı yayın sözleşmesi (Ç0.4) henüz yok. Sipariş modülü teslim kanıtını küçük bir tabloda tutarak geçici çözüm üretti (ADR-0023 kalan işler); lojistikte bu ölçeklenmez.

## 2. Kararlar

**K1 — Ortak Filo modülü.** `Modulix.Modules.Fleet` (anahtar ve şema `fleet`) araç kimliğini taşır: araç, dorse/römork (ayrı araç türü), mülkiyet (özmal, kiralık, taşeron), araç belgeleri (K3 üzerinden), bakım kaydı ve planı. Lojistik ve rentcar araca `Core.Contracts` içindeki okuma sözleşmesiyle (`IVehicleDirectory`, ad taslaktır) başvurur; araç tablosuna dokunmaz. ADR-0031 K3 bu kararla değişir: rentcar `Vehicle` tablosu açmaz, Filo'dan okur. Lojistiğin `logistics.vehicle` otomasyon anahtarı `fleet.vehicle`'a taşınır (bugün bu anahtara bağlı kural yoktur). Yeni proje için `ProjectReferenceRulesTests.AllowedReferences` güncellenir.

Reddedilen alternatif: aracı lojistikte başlatıp rentcar'a sözleşme açmak. Rentcar lojistik lisansı olmadan çalışamaz hale gelir (Ç20.2 yumuşak bağımlılık ilkesine aykırı).

**K2 — Sürücü tek kimliktir.** Sürücü ayrı bir kişi tablosu değildir; `core.Person` kaydına bağlı lojistik uzantısıdır (`logistics.Drivers`: `PersonId`, sürücü tipi, durum, taşeron sürücüde taşıyıcı firma). Yetkinlikler (ehliyet sınıfı, SRC, psikoteknik) K3 süreli belgesidir. İK modülü gelince çalışan uzantısı aynı `PersonId`'ye bağlanır (M4.1); İK ücreti ve sağlık ayrıntısı sürücü kartında görünmez (M4.5). Temel sürücü kartı İK lisansı gerektirmez (M2.3).

**K3 — Süreli belge Core yeteneğidir.** `core.Credentials` (ad taslaktır): sahip kayıt (modül anahtarı + kayıt türü + kimlik), belge türü (Y9b seçim listesi; tenant tür ekler, kod yalnızca kategoriye bakar: `vehicle`, `driver`, `party`, `facility`), belge no, başlangıç/bitiş, dosya referansı (Ç4), durum (geçerli, süresi doldu, iptal). Sahip modül belgenin **zorunluluğunu** kendi kuralıyla söyler (ör. sefer atamasında geçerli SRC şart); Core yalnızca saklar ve sorgular. Yaklaşan bitiş uyarısı ADR-0027 `DateRelative` tetikleyicisiyle verilir; eşik gün sayısı tenant ayarıdır, koda sabit yazılmaz. Mevcut vekâlet (ADR-0032 K4) kendi tablosunda kalır; belge dosyası bağı K3 ile verilebilir, taşıma zorunlu değildir.

**K4 — Taşıyıcı ve tedarikçi ayrı tablo değildir.** Taşeron taşıyıcı, yakıt tedarikçisi, servis ve sigorta şirketi `core.Party` kaydıdır; ayrım ilişki rolüyle yapılır (ADR-0029 K9). Platform kataloğuna `tasiyici`, `yakit-tedarikcisi`, `servis`, `sigorta` rolleri eklenir. Lojistiğin bugünkü `logistics.carriers.*` izinleri taşıyıcı **ilişki ve koşul** kayıtlarını (ücret anlaşması, belge, performans notu) korur; firma kimliğini değil.

**K5 — Tek sefer modeli: durak zinciri.** `Trip` (sefer) sıralı `TripStop` (durak) listesidir; her durak tür (yükleme, boşaltma, sınır/gümrük, mola, boş dönüş), adres, planlanan ve gerçekleşen zaman taşır. `Shipment` (sevkiyat; müşteri yükü) yükleme ve boşaltma duraklarına bağlanır; bir sefer birden çok müşterinin sevkiyatını taşır, müşteri yalnızca kendi sevkiyatını görür (M2.2). Komple taşıma = iki duraklı sefer; parsiyel = çok duraklı sefer. Ayrı "komple" ve "parsiyel" veri modeli yoktur; taşıma türü sevkiyatın alanıdır. Navlun sevkiyata bağlıdır; sefer toplamı türetilir ve elle değiştirilmez.

**K6 — Taşıma talebi lojistiğin kendi kaydıdır.** `TransportRequest` (M2.1) CRM ve Sipariş kapalıyken elle, web kanalından (Ç19) ya da API'den açılır. Sipariş modülünün teslimat işi (ADR-0023 dilim 4–5) → taşıma talebi dönüşümü **isteğe bağlı köprüdür** (Ç21.4): hedef modül yoksa sipariş akışı etkilenmez; açıksa tekilleştirilmiş (kaynak kayıt + kalem anahtarı benzersiz) ve izlenebilir. Depo bağı aynı kalıpla: sefer varış → ASN/kabul önerisi (M2.8, M3.2).

**K7 — Durum geçişleri sunucuda, komutla.** Sevkiyat ve sefer durumları tablo olarak tanımlanır (ADR-0023 `OrderTransitions` kalıbı): Talep → Planlandı → Yükleniyor → Yolda → Teslim edildi / Kısmi / Başarısız; yan durum İptal. Teslim yalnızca teslim komutuyla (M2.5) verilir; genel kayıt güncellemesi bu alanı yazamaz. İptal yalnızca teslimden önce; teslim sonrası düzeltme ters kalemle (ADR-0037 K5). Her geçiş denetim kaydına ve outbox'a yazılır. "Varış saati geçen seferi kapat" gibi otomatik geçişler koda değil ADR-0027 kuralına yazılır.

**K8 — Teslim kanıtı ve ilk iş olayı.** `DeliveryProof`: alıcı, sunucu zamanı, fotoğraf/imza dosya referansı (Ç4), hasar/eksik işareti, başarısız teslim nedeni. Kayıt değişmez; düzeltme yeni kanıtla. Teslim komutu `ShipmentDelivered.v1` olayını yayınlar; bu, Ç0.4 iş olayı sözleşmesinin ilk gerçek kullanımıdır ve ADR-0037 dilim 5 (teslim → faturalanabilir kalem) ile ADR-0027 `DomainEvent` tetikleyicisinin ön koşuludur. Hasar işaretli teslim Destek modülü açıksa şikâyet taslağı önerir (isteğe bağlı köprü).

**K9 — Navlun ve taşıyıcı maliyeti ayrı, ikisi de faturalanabilir kalem üretir.** Sevkiyat navlunu (satış yönü) ve taşeron hakedişi (alış yönü) M2.6 uyarınca planlanan / gerçekleşen / faturalanan olarak ayrı tutulur; ikisi de teslimde ADR-0037 `IBillableItemWriter` ile kalem üretir. Taşıyıcı maliyeti ilk sürüme **girer** (ADR-0037 açık soru 3'e cevap): taşeron kullanan taşımacıda hakedişsiz kârlılık anlamsızdır. Hesap izi (Ç18a.3) saklanır; tarife altyapısı (Ç18a.2) gelene kadar navlun sözleşme/elle girilir, tarife gelince hesaplanır. Para birimi her tutarla saklanır; dövizli seferde kur sefer kaydına sabitlenir (kaynak tenant ayarı), raporda para birimine göre ayrı toplam verilir (Ç10b.1).

**K10 — Masraf operasyon kaydıdır, ödeme finans işidir.** `TripExpense`: sefer, masraf türü (Y9b listesi; kategori `fuel`, `toll`, `allowance`, `fine`, `other`; tenant alt tür ekler), tutar, KDV, tedarikçi (Party), belge dosyası, durum (Beklemede → Onaylandı / Reddedildi). Yakıt ayrı bir defter değildir: `fuel` kategorili masraf litre, km ve birim fiyat alanlarını taşır; araç yakıt raporu buradan türetilir (tek kaynak). Onay Ç6a asgari onay üzerinden; onaysız masraf kârlılığa girmez, "bekleyen" listelenir. Kasa/banka/veresiye ödeme kaynağı, fiş ve sürücü hesabı **bu ADR'de yoktur** (Muhasebe M5, İK M4; ADR-0037 K10). Trafik cezası masraf türüdür; sürücüye rücu yalnızca bilgi bayrağıdır, borç kaydı üretmez.

**K11 — Kârlılık modül içi görünümdür.** Sefer ve araç kârlılığı (gelir kalemleri, onaylı masraf, hakediş, net, marj, km başı maliyet, boş km oranı) lojistik modülünde hesaplanır; rapor oluşturucu tek kayıt türünden okuduğu için (Ç10b.2 yok) çapraz okuma modül içinde kalır. Ölçüm sözlüğü (Ç10c): navlun operasyon değeridir, gelir değildir; faturalanan bedel ayrıdır. Yakıt sapması, mazot oranı gibi eşikler tenant ayarı + ADR-0027 kuralıdır; koda yüzde yazılmaz.

**K12 — Silme yoktur.** Araç, sürücü, sefer, sevkiyat, masraf ve kanıt fiziksel silinmez; pasife alınır ya da iptal edilir (ADR-0032 ilkesi). Tenant dışa aktarma ve silme (`tenant-export`/`tenant-delete`) bütün tabloları kapsar.

**K13 — Mevcut platform yetenekleri aynen kullanılır.** `IModule` üzerinden izin, rol şablonu, `AuditedEntities`, `Settings` (uyarı eşikleri, kur kaynağı, numara serisi), `SensitiveFields` (navlun, hakediş, maliyet alan yetkisi), `CustomizableEntities`, `ChoiceLists`, `Lookups` (araç, sürücü, taşıyıcı, durak adresi ADR-0016), `Forms` (ADR-0021 çok sekmeli), `Automation`, `Imports` (ADR-0033: araç/sürücü/firma açılış verisi Excel ile), `Reports`. Arayüz `docs/design-system/` kurallarına uyar. Kodda `if sektor == ...` yoktur; yurt içi/uluslararası, tehlikeli madde gibi farklar ADR-0029 hizmet özelliği ve alan kuralıyla gelir.

## 3. Veri modeli taslağı

Hepsi `TenantId` taşır, RLS'e `TenantIsolationSql.AddTable` ile eklenir. Kapsam önerisi (ADR-0008): araç, sürücü, taşıyıcı koşulu ve belge `ICompanyShared`; talep, sevkiyat, sefer, masraf, kanıt `ICompanyScoped` (işletici şirket). Şirket kapsamlı kayıt bugün hiçbir modülde yok; ilk şirket kapsamlı tablo bu modülde açılırsa boru hattının şirket süzgeci (ADR-0008 adım 3) uçtan uca test edilir.

| Şema.Tablo | İçerik (özet) | Benzersizlik / not |
|---|---|---|
| `fleet.Vehicles` | plaka, şasi, tür (çekici, kamyon, dorse, panelvan…), mülkiyet, taşeron firma (Party), kapasite (ton, m³, LDM, palet), durum | `UNIQUE(TenantId, Plate)`, `UNIQUE(TenantId, Vin)` |
| `fleet.MaintenanceRecords` | araç, tür (Y9b), tarih, km, tedarikçi, tutar, sonraki tarih/km | tamamlanınca sonraki plan üretilir |
| `core.Credentials` | sahip (modül, tür, kimlik), belge türü, no, başlangıç/bitiş, dosya, durum | `(TenantId, OwnerKey, Type, No)` |
| `logistics.Drivers` | `PersonId`, tip, durum, taşıyıcı firma | `UNIQUE(TenantId, PersonId)` |
| `logistics.CarrierTerms` | taşıyıcı Party, anlaşma, para birimi, not | Party ilişki rolü `tasiyici` şart |
| `logistics.TransportRequests` | müşteri, taraf rolleri (gönderici/alıcı), yük, taşıma türü, zaman penceresi, kaynak (elle/web/API/sipariş) | kaynak + dış anahtar benzersiz |
| `logistics.Shipments` | talep, müşteri, yük kalemleri, navlun, para birimi, durum, yükleme/boşaltma durağı | |
| `logistics.Trips` | numara (Ç8.1), araç, dorse, sürücü(ler), taşıyıcı, durum, kur | `UNIQUE(TenantId, Number)` |
| `logistics.TripStops` | sefer, sıra, tür, adres, planlanan/gerçekleşen zaman, sevkiyat bağları | `UNIQUE(TenantId, TripId, Sequence)` |
| `logistics.DeliveryProofs` | sevkiyat, alıcı, zaman, dosya, hasar/eksik, başarısız neden | değişmez |
| `logistics.TripExpenses` | sefer, tür, tutar, KDV, tedarikçi, belge, litre/km, durum | onaylı kayıt değişmez |

## 4. Dilimler

Her dilim dört testle teslim edilir (mutlu yol, doğrulama, tenant izolasyonu, yetki); sunucu sıralı grid şablonu (`ViewTabs`, `SortHeader`) ve ADR-0021 form metadata'sı baştan uygulanır.

| Dilim | İçerik | Bağımlılık |
|---|---|---|
| 0 | Bu ADR'nin kabulü; ADR-0031 K3 güncellemesi; ilişki rolleri kataloğu; Ç0.4, Ç4 asgari, Ç6a asgari, Ç8.1 kararları | — |
| 1 | Filo: araç, dorse, bakım; `IVehicleDirectory` | 0 |
| 2 | Core süreli belge + `DateRelative` uyarı kuralı; araç ve sürücü belgeleri | 1, Ç4 asgari |
| 3 | Sürücü kartı, taşıyıcı koşulu | 2 |
| 4 | Taşıma talebi ve sevkiyat | 3 |
| 5 | Sefer, durak zinciri, atama, uygunluk denetimi (belge, kapasite), durum makinesi, sefer numarası | 4, Ç8.1 |
| 6 | Teslim kanıtı + `ShipmentDelivered.v1` | 5, Ç0.4 |
| 7 | Navlun ve hakediş → faturalanabilir kalem | 6, ADR-0037 dilim 1 |
| 8 | Masraf + onay + yakıt alanları | 5, Ç6a asgari |
| 9 | Kârlılık görünümü, eşik kuralları, pano | 7, 8 |
| 10 | Belgeler: taşıma irsaliyesi çıktısı (Ç8.2), e-İrsaliye adaptörü (Ç11b.3), U-ETDS bildirimi | Ç8, Ç11, bölüm 8 doğrulaması |
| 11 | Sipariş → talep ve sefer → depo kabul köprüleri | 4, ADR-0023, ADR-0032 dilim 5 |

Sonraya bırakılanlar (ayrı epic): sürücü saha ekranı ve çevrimdışı (Ç16), müşteri portalı/takip (Ç19.8), tarife altyapısı (Ç18a.2), araç takip/telematik (Ç11b.1), sürücü çalışma süreleri, sürücü hesabı/avans/harcırah (M4), ödeme ve kasa (M5).

## 5. Platform kurallarına uyum

| Kural | Uygulama |
|---|---|
| Tenant izolasyonu | Her tablo `ITenantEntity` + kapsam arayüzü; `TenantIsolationSql.AddTable`; benzersizlik `TenantId` ile |
| Yetki | `fleet.vehicles.*`, `fleet.maintenance.*`, `core.credentials.*`, `logistics.requests/shipments/trips/expenses/proofs.*`, `logistics.expenses.approve`; navlun/hakediş/maliyet alan yetkisi |
| Kayıt boru hattı | Bütün yazmalar kayıt depolarından; durum ve teslim yalnızca komutla |
| Olaylar | `logistics.shipment.*`, `logistics.trip.*`, `ShipmentDelivered.v1` (ADR-0007) |
| Lisans | Salt okunur modülde POST/PUT/DELETE 403; arka plan yolu `IModuleLicense.IsWritableAsync` |
| Dışa aktarma ve silme | Bütün tablolar `tenant-export` / `tenant-delete` kapsamında |
| Dinamik yapılandırma | Belge türleri, masraf türleri, durak türleri, eşikler, kur kaynağı, numara serisi tenant ayarı/listesi; kod kategoriye bakar |

## 6. Riskler

- Filo'yu rentcar'dan önce kurmak ADR-0031'i değiştirir; rentcar başlamadan önce yapılırsa göç yoktur, sonra yapılırsa `rentcar.Vehicles` → `fleet.Vehicles` göçü gerekir.
- Ç4, Ç6a ve Ç0.4 Core ön koşulları lojistiği geciktirir; asgari sürümleri (tek adımlı onay, yerel dosya adaptörü, tek olay türü) yeterlidir ama kapsamı yazılı sabitlenmelidir.
- İlk şirket kapsamlı (`ICompanyScoped`) tablolar burada açılırsa boru hattının şirket süzgeci ilk kez gerçek yük altında çalışır.
- Durak zinciri modeli basit komple taşımada fazla görünebilir; iki duraklı sefer için hızlı form düzeni gerekir.
- Çoklu sürücü, dorse değişimi ve aktarma (sefer ortasında yük başka araca) K5'te açık bırakıldı; pilotla netleşmeli.

## 7. Açık sorular

1. Pilotun özmal/taşeron oranı, yurt içi/uluslararası payı ve parsiyel kullanımı nedir? Dilim 7–9 önceliği buna bağlıdır.
2. Çoklu sürücü ve aktarma (yükün sefer ortasında araç değiştirmesi) ilk sürümde gerekir mi?
3. Süreli belge zorunluluğu (ör. SRC'siz sürücü atanamaz) uyarı mı, engel mi başlasın? Öneri: uyarı; tenant engele çevirir.
4. Taşıma talebi web kanalından (Ç19) ilk sürümde alınır mı?
5. Filo modülü lisansı: lojistik ya da rentcar lisansıyla birlikte gelir mi, ayrı satılır mı (ADR-0028)?

## 8. Doğrulanmadı (mevzuat ve pilot)

- **U-ETDS** (Ulaştırma Elektronik Takip ve Denetim Sistemi): hangi yetki belgesi türleri için, hangi süre içinde, hangi veri setiyle bildirim zorunlu; teknik arayüz.
- **Yetki belgesi ve taşıt kartı**: belge türleri (K1/K2/K3, L, R, C, M, N…), araç kaydı zorunluluğu, kapsam.
- **Taşıma irsaliyesi** (taşıyıcının belgesi) ile sevk irsaliyesi / e-İrsaliye (mal sahibinin belgesi) ayrımı ve nakliyecinin yükümlülüğü.
- **Tehlikeli madde (ADR)**: SRC5, TMGD, araç uygunluk belgesi, yük sınıfı bilgisi.
- Kapasite ölçü seti (tonaj, hacim, LDM, aks yükü) ve denetim sınırları.
- Pilotun işleyişi, kullandığı muhasebe programı ve sürücü belge takibi.
- Tablo, arayüz ve izin adları taslaktır; kodla denenmedi, hiçbir test çalıştırılmadı.
