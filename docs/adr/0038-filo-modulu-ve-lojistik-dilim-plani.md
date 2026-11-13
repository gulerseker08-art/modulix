# ADR-0038 — Filo modülü ve lojistik (M2) dilim planı

Durum: **Kabul edildi, 11 Kasım 2026** (taslak 7 Kasım 2026). Kabul, ürün sahibinin "lojistiğe geç" talimatıyla ve bölüm 9'daki varsayılan cevaplarla verildi; dilim 1a (Filo: araç ve dorse) bu kabulle birlikte teslim edilir. Mevzuata değen maddeler (bölüm 8) hâlâ uzman doğrulaması ister; doğrulanmamış kural zorunlu üretim kuralı yapılmaz (ADR-0032 ilkesi).

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
| 7 | Navlun ve hakediş (7a, yapıldı: hassas alanlar) → faturalanabilir kalem (7b) | 6, ADR-0037 dilim 1 |
| 8 | Masraf + onay + yakıt alanları (yapıldı) | 5, Ç6a asgari |
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

## 9. Kabul ve açık soruların varsayılan cevapları (11 Kasım 2026, Dilim 0)

Pilot verisi gelene kadar aşağıdaki cevaplarla ilerlenir; her biri pilotla değiştirilebilir ve değişiklik bu bölüme yazılır.

1. **Özmal/taşeron ve parsiyel oranı:** bilinmiyor. Model ikisini de baştan taşır (mülkiyet alanı, taşıyıcı Party; durak zinciri). Dilim 7–9 sırası pilotla netleşir.
2. **Çoklu sürücü ve aktarma:** ilk sürümde çoklu sürücü (sefer başına birden çok sürücü) var, aktarma yok; aktarma yeni sefer + bağlı sevkiyatla elle yapılır.
3. **Süreli belge zorunluluğu:** varsayılan **uyarı**; tenant ayarıyla engele çevrilir (koda sabit yazılmaz).
4. **Web kanalından talep:** ilk sürümde yok; talep elle ve API'den açılır.
5. **Filo lisansı:** Filo ayrı modül anahtarıdır (`fleet`, ADR-0028 lisans kuralları aynen geçerli). Ticari paketleme (lojistik ya da rentcar ile birlikte verilmesi) lisans tanımıdır, kodda bağımlılık yoktur; lojistik ve rentcar Filo kapalıyken araç seçimini sunmaz ama çalışır (yumuşak bağımlılık, Ç20.2).

Dilim 0'ın Core ön koşul kararları: Ç0.4 iş olayları ADR-0039 ile uygulandı; Ç6a asgari onay ADR-0027 dilim 4a ile uygulandı; Ç4 (dosya) ve Ç8.1 (belge serisi) kendi ADR'leriyle gelir ve yalnızca dilim 2, 5, 6, 8'i bekletir. Dilim 1 bunlara bağlı değildir.

Dilim 1 iki parçaya bölündü: **1a** Filo modülü, araç ve dorse kaydı, `IVehicleDirectory`; **1b** bakım kaydı ve planı. Lojistiğin `logistics.vehicle` otomasyon beyanı 1a ile `fleet.vehicle`'a taşınır (bu anahtara bağlı kural yoktur).

## 10. Dilim 1a: Filo modülü ve araç kaydı (11 Kasım 2026)

- **Modül:** `Modulix.Modules.Fleet` (anahtar ve şema `fleet`, yalnızca `Core.Contracts`'a başvurur; mimari referans tablosuna eklendi). İzinler `fleet.vehicles.read/write`, rol şablonu "Filo sorumlusu".
- **Araç (`fleet.Vehicles`, `ICompanyShared`):** plaka (boşluk ve tire atılır, büyük harf; `UNIQUE(TenantId, Plate)`), araç türü (Y9b listesi `fleet.vehicle-kind`, kategori `powered`/`towed`; hazır değerler çekici, kamyon, kamyonet, panelvan, dorse, römork), mülkiyet (`owned`, `leased`, `subcontracted`; sabit, çünkü kod buna göre davranır), taşıyıcı firma (yalnızca taşeron araçta ve zorunlu; firmanın `tasiyici` ilişki rolü olmalı, rol taşıyıcı ya da mülkiyet değişince denetlenir), marka, model, model yılı, şasi no (17 karakter, I/O/Q yok; verilirse `UNIQUE(TenantId, Vin)` süzgeçli), kapasite (kg, m³, LDM, palet), durum (`active`, `maintenance`, `passive`), notlar, özel alanlar. Silme 409 döner (K12). Migration `FleetVehicles`: tablo + `TenantIsolationSql.AddTable` + şema yetkisi; `init.sql` güncellendi.
- **`IVehicleDirectory` (Core.Contracts.Fleet):** `IsAvailableAsync` (Filo bu tenant'ta açık mı) ve `GetAsync(ids)` → `VehicleSummary` (tür kategorisi, mülkiyet, taşıyıcı, durum, kapasite). Tenant filtresi + RLS altında; Filo kapalıysa boş döner (yumuşak bağımlılık).
- **Lojistik:** `logistics.vehicle` otomasyon beyanı ve "sonraki bakım tarihi" alanı kaldırıldı; Filo `fleet.vehicle` varlığını (oluşturuldu/güncellendi olayları; durum, mülkiyet, model yılı alanları) beyan eder. Bakım tarihi alanı bakım kaydıyla (1b) gelir.
- **Arayüz:** "Filo" uygulaması (Operasyon grubu), `/filo/araclar` grid şablonuyla (görünüm sekmeleri: tümü, etkin, bakımda, pasif, taşeron; süzgeç: tür, mülkiyet; sıralanabilir ikonlu başlıklar; sütun seçici; kişisel görünüm; ⋯ menüsü) ve sekmeli araç kartı (ADR-0021 düzeni: Genel bilgiler, Kapasite, Notlar ve ek bilgiler). Yeni CSS sınıfı ya da token yok. Demo firmasında (acme) Filo açılır.

**Bilerek yapılmadı:** bakım kaydı ve planı (1b), araç belgeleri (dilim 2, Core süreli belge), araç–dorse eşleştirmesi (sefer atamasında, dilim 5), içe aktarma tanımı (ADR-0033; ayrı küçük iş), rentcar bağlantısı (rentcar modülü yok).

### Test sonuçları (Dilim 1a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Fleet.Tests` | 6: katalog yalnızca Core + Filo ile kurulur ve araç kural varlığı Filo'dadır, izin/rol şablonu modüle ait, tür kategorileri ve varsayılan, sekmeli varsayılan form, taşıyıcı lookup'ı rol süzgeçli, Shared sabitleri sunucuyla aynı |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 4: lojistik beyanından araç kaldırıldı |
| `dotnet test tests/Modulix.IntegrationTests --filter "FleetVehicleTests\|ModulesAndRolesTests"` (SQL Server; CI koşar) | 14: mutlu yol (plaka/şasi biçimi, dorse ve taşeron araç, süzgeç, arama, sıralama, güncelleme, denetim kaydı, `IVehicleDirectory`), doğrulama (zorunlu alan, plaka/şasi benzersizliği ve biçimi, bilinmeyen tür ve mülkiyet, taşıyıcı zorunluluğu ve rolü, silme 409, eski sürüm 409), tenant izolasyonu (aynı plaka iki tenant'ta serbest, araç ve taşıyıcı başka tenant'tan görünmez, dizin yalnızca kendi tenant'ını okur), yetki (izinsiz 403, okuyan yazamaz, modül kapalıyken 403 ve dizin boş); modül listesi `fleet` ile güncellendi |
| `dotnet test tests/Modulix.ArchitectureTests` / `tests/Modulix.Web.Tests` | 69 / 120: referans kuralı, modül kaydı, otomasyon tarafsızlığı; menü testi (Filo yalnızca modül ve izinle görünür) |

Yerel veritabanında `TableInventoryTests` başka bir oturumdan kalan `platform.LicensePolicies` tablosu yüzünden düşer (bu dalın migration'larında yok); CI temiz veritabanında koşar.

## 11. Dilim 1b: Bakım kaydı ve sonraki bakım (11 Kasım 2026)

- **Bakım kaydı (`fleet.MaintenanceRecords`, `ICompanyShared`):** araç (yalnızca oluştururken; `(TenantId, VehicleId)` yabancı anahtarıyla), bakım türü (Y9b listesi `fleet.maintenance-kind`, kategori `planned`/`unplanned`; hazır değerler periyodik bakım, lastik, muayene hazırlığı, arıza onarımı, hasar onarımı), bakım tarihi (ileri gün olamaz; plan "sonraki bakım" alanına yazılır), kilometre, servis firması (`core.Party`, rol süzgeci yok; servis/tedarikçi rolleri katalogda netleşince eklenir, K4), tutar + para birimi (tutar varsa zorunlu, ödeme finans işidir), sonraki bakım tarihi ve kilometresi (bakımdan sonra / büyük), açıklama, özel alanlar. Düzeltme için silinebilir (K12 araç, sefer ve masrafı kapsar; bakım kaydı kapsamaz).
- **Türetilmiş araç alanları:** `LastMaintenanceOn`, `OdometerKm` (bakımlardaki en yüksek km), `NextMaintenanceDate`, `NextMaintenanceKm` en son bakımdan (tarih, sonra kayıt anı) hesaplanır; bakım kaydı oluşturma, düzeltme ve silmede aynı işlemde yeniden yazılır (depodaki defter → bakiye kalıbı). Araç kaydında salt okunur hesaplanmış alanlardır; genel uçtan gelen değer yok sayılır. Bu türetilmiş yazım araç sürümünü artırır, denetim kaydına araç değişikliği olarak yazılmaz (kaynak bakım kaydı denetlenir).
- **Otomasyon:** `fleet.vehicle` varlığına tarama kaynağı (`FleetAutomationRecordSource`: zamanlanmış = pasif olmayan araçlar, tarihe göre = `nextMaintenanceDate`) ve `plate`, `nextMaintenanceDate`, `odometerKm` alanları eklendi; "bakıma 7 gün kala" kuralı koda değil firmanın kuralına yazılır (eşik gün sayısı tenant kararı).
- **Arayüz:** "Bakım kayıtları" ekranı (`/filo/bakimlar`, grid şablonu: Tüm bakımlar, Son 30 gün, Bu yıl, kişisel görünüm; tür süzgeci; sonraki bakım rozeti gecikti/yaklaşıyor/ileride), sekmeli bakım kartı; araç listesinde "Sonraki bakım" sütunu ve "Bakımı yaklaşan" görünümü (30 gün içinde ya da gecikmiş; görsel eşik), araç kartı özetinde kilometre ve sonraki bakım. Yeni CSS sınıfı ya da token yok. Migration `FleetMaintenance` (araç kolonları null'lanabilir, yeni tablo + RLS).

**Bilerek yapılmadı:** kilometreye göre hatırlatma (sayaç okuması/telematik yok; km yalnızca bakımda girilir), bakım planı şablonları (araç türüne göre periyot), bakım belgesi/fatura dosyası (Ç4), bakım maliyetinin kârlılığa girmesi (dilim 9), servis firması rol süzgeci.

### Test sonuçları (Dilim 1b)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Fleet.Tests` | 7: tür listeleri ve kategorileri, iki sekmeli varsayılan form, lookup hedefleri ve yalnızca taşıyıcıda rol süzgeci, sonraki bakım tarihi kural alanı + tarama kaynağı, Shared sabitleri |
| `dotnet test tests/Modulix.IntegrationTests --filter "FleetMaintenanceTests\|FleetVehicleTests"` (SQL Server; CI koşar) | 10: mutlu yol (türetilmiş araç alanları, türetilmiş alan formdan yazılamaz, silmede önceki bakıma dönüş, "bakımı yaklaşan" süzgeci, arama, denetim kaydı; **bakıma 7 gün kala kuralı taramayla yalnızca eşleşen aracı bulur**), doğrulama (zorunlu alan, tarih/km tutarlılığı, para birimi, ileri tarih, araç değiştirilemez), tenant izolasyonu (başka tenant'ın aracına bakım yazılamaz, kayıt görünmez ve silinemez), yetki (okuyan yazamaz, araç izni bakım ekranını açmaz) |
| `dotnet test tests/Modulix.ArchitectureTests` / `tests/Modulix.Web.Tests` | 69 / 120: menüde bakım ekranı yalnızca bakım okuma izniyle |

## 12. Dilim 3a: Sürücü kartı (11 Kasım 2026)

Sıra kararı: dilim 2 (Core süreli belge) dosya ekleri ADR'sini (Ç4, PR #285, başka oturum) beklediği için sürücü kartı belgelerden önce, yetkinliksiz başlatıldı; ehliyet, SRC ve psikoteknik dilim 2 ile karta eklenir. Taşıyıcı koşulu (`CarrierTerms`) dilim 3b'ye bırakıldı.

- **Sürücü (`logistics.Drivers`, `ICompanyShared`):** `PersonId` (ortak kişi; `UNIQUE(TenantId, PersonId)`, yalnızca oluştururken), tip (`employee` kadrolu / `subcontracted` taşeron; sabit, çünkü kod buna göre davranır), taşıyıcı firma (yalnızca taşeronda ve zorunlu; `tasiyici` rolü oluşturmada ya da tip/taşıyıcı değişince denetlenir), varsayılan araç, durum (`active`, `onLeave`, `passive`), notlar, özel alanlar. Silme 409 (K12). Migration `LogisticsDrivers` (RLS + şema yetkisi; `logistics` şeması init.sql'de vardı).
- **Kişi:** sürücünün adı, telefonu ve e-postası kişi kaydındadır, sürücü kartına kopyalanmaz. Lojistik kişiye `SharedRecordAccess(core.person, drivers.read, drivers.write)` bildirir: sürücü yazma izni olan, CRM izni olmadan da sürücü için kişi açabilir; kişi sürücü kartında kullanıldıkça silinemez (`SharedRecordReference`).
- **Araç bağı (modüller arası):** varsayılan araç Filo'nun kaydıdır; lojistik Filo projesine başvurmaz ve araç tablosuna yabancı anahtar kurmaz. Kayıt sırasında `IVehicleDirectory` ile denetlenir: araç bu tenant'ta olmalı, kategorisi `towed` (dorse/römork) ve durumu pasif olmamalı; Filo kapalıysa araç seçilemez ama kart çalışır (yumuşak bağımlılık). Araç seçimi `fleet.vehicle` hedefli lookup'la yapılır.
- **Arayüz:** "Lojistik" uygulaması artık gerçektir (panelde pasif "Lojistik" kartı kaldırıldı). Operasyon grubunda "Sürücüler" ekranı (`/lojistik/suruculer`: Tüm sürücüler, Etkin, İzinde, Taşeron, Pasif; soyada göre sıralama; ad, telefon ve e-posta araması) ve sekmeli sürücü kartı ("Kayıtlı kişi / Yeni kişi" seçimi; yeni kişi açılıp sürücü kaydı başarısız olursa tekrar denemede aynı kişi kullanılır). Yeni CSS sınıfı ya da token yok.

**Bilerek yapılmadı:** yetkinlik belgeleri (dilim 2), taşıyıcı koşulu (3b), İK çalışan bağı (M4.1), sürücü portalı (Ç16), çalışma süreleri.

### Test sonuçları (Dilim 3a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 8: sekmeli form, kişi uzantısı ve Filo aracına yalnızca anahtarla bağ (Filo derleme başvurusu yok), rol şablonu ve katalog, Shared sabitleri |
| `dotnet test tests/Modulix.IntegrationTests --filter "LogisticsDriverTests\|FleetVehicleTests\|ModulesAndRolesTests"` (SQL Server; CI koşar) | 18: mutlu yol (kişi + kart, taşeron, Filo aracı, ada ve telefona göre arama, sıralama, süzgeçler, denetim kaydı, kişi kullanımdayken silinemez), doğrulama (tek kart, taşıyıcı zorunluluğu ve rolü, dorse, pasif ve bilinmeyen araç, Filo kapalıyken araç seçilemez ama kart güncellenir, silme 409), tenant izolasyonu (başka tenant'ın kişisi ve aracı kullanılamaz, kart görünmez), yetki (okuyan yazamaz ve kişi açamaz, sürücü yazma izni kişi açmaya yeter, modül kapalıyken 403) |
| `dotnet test tests/Modulix.ArchitectureTests` / `tests/Modulix.Web.Tests` | 69 / 121: Lojistik menüsü yalnızca modül ve izinle; pasif kartlarda yalnızca İK kaldı |

## 13. Dilim 3b: Taşıyıcı anlaşması (11 Kasım 2026)

K4 uygulandı: taşıyıcı firma kimliği `core.Party`'dir ("Taşıyıcı" ilişki rolü); lojistik yalnızca ilişkinin **koşulunu** tutar. Modülün mevcut `logistics.carriers.read/write` izinleri bu kaydı korur (izin açıklamaları "taşıyıcı anlaşması" olarak güncellendi).

- **Anlaşma (`logistics.CarrierTerms`, `ICompanyShared`):** taşıyıcı (yalnızca oluştururken; rol oluşturmada denetlenir), anlaşma adı, numara (büyük harf; `UNIQUE(TenantId, CarrierPartyId, AgreementNo)` süzgeçli), başlangıç, bitiş (boş = süresiz), para birimi (ISO 4217), ödeme vadesi (0-365 gün), ücret anlaşması ve performans notu (serbest metin), etkin bayrağı, özel alanlar. Migration `LogisticsCarrierTerms` (RLS).
- **Çakışma kuralı:** aynı taşıyıcının **etkin** anlaşmalarının `[başlangıç, bitiş]` aralıkları kesişemez; bir günde tek geçerli anlaşma olur. Pasif anlaşma (taslak teklif, eski koşul) denetime girmez. İleti çakışan anlaşmanın adını verir.
- **Süzgeçler:** `validOn` o gün geçerli etkin anlaşmaları, `expiredBefore` o günden önce biteni verir; liste taşıyıcı adına göre, aynı taşıyıcıda en yeni dönem üstte sıralanır. Düzeltme için silinebilir (K12 anlaşmayı kapsamaz; hakediş gelince kullanılan anlaşmanın silinmesi o dilimde engellenir).
- **Arayüz:** "Taşıyıcı anlaşmaları" ekranı (`/lojistik/tasiyici-anlasmalari`: Tüm anlaşmalar, Bugün geçerli, Süresi dolan, Pasif; durum rozeti geçerli / bitiyor (30 gün, görsel eşik) / süresi doldu / başlamadı / pasif) ve sekmeli anlaşma kartı (Genel bilgiler; Ücret ve performans). Yeni CSS sınıfı ya da token yok.

**Bilerek yapılmadı:** tarife ve ücret hesabı (Ç18a.2), hakediş (dilim 7), anlaşma belgesi dosyası (Ç4, PR #285), anlaşma bitişine kala uyarı kuralı (Lojistik'in otomasyon kayıt kaynağı yok; sevkiyat diliminde eklenir), taşıyıcı performans puanı.

### Test sonuçları (Dilim 3b)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 11 (3 yeni): sekmeli anlaşma formu, taşıyıcı izinleri ve firma bağı, Shared sabitleri; taşıyıcı seçen her lookup rol süzgeçli |
| `dotnet test tests/Modulix.IntegrationTests --filter "LogisticsCarrierTermsTests\|LogisticsDriverTests"` (SQL Server; CI koşar) | 8: mutlu yol (birbirini izleyen dönemler, bugün geçerli / süresi dolan süzgeçleri, taşıyıcıya göre sıralama ve arama, denetim kaydı, silme), doğrulama (zorunlu alanlar, taşıyıcı rolü, tarih sırası, para birimi, numara benzersizliği, süresiz anlaşmayla çakışma; pasif anlaşma çakışmaz), tenant izolasyonu (başka tenant'ın taşıyıcısı ve anlaşması görünmez), yetki (okuyan yazamaz ve silemez, sürücü izni anlaşmaları açmaz) |
| `dotnet test tests/Modulix.ArchitectureTests` / `tests/Modulix.Web.Tests` | 69 / 121: anlaşma ekranı yalnızca taşıyıcı okuma izniyle |

## 14. Dilim 2a: Core süreli belge (12 Kasım 2026)

K3 uygulandı. Dosya ekleri (ADR-0040, PR #285) main'e girdiği için belge dosyası da bu dilimde bağlandı. Yaklaşan bitiş uyarısı (`DateRelative` kuralı) dilim 2b'ye ayrıldı: Core kayıt türünün otomasyon kayıt kaynağı ayrı bir incelenebilir adımdır.

- **Kayıt (`core.Credentials`, `ICompanyShared`):** sahip kayıt türü ve kimliği (yalnızca oluştururken), belge türü, belge no (`UNIQUE(TenantId, OwnerEntityKey, OwnerId, TypeKey, DocumentNo)` süzgeçli), veren kurum, başlangıç ve bitiş (boş = süresiz), durum (`active` / `revoked`), notlar. "Süresi doldu" saklanmaz, tarihten hesaplanır. Migration `CoreCredentials` (RLS). Sahip kayda veritabanı yabancı anahtarı yoktur; bağ kayıt deposunda denetlenir.
- **Sahip türünü modül bildirir:** `IModule.CredentialOwners` (ör. Filo `fleet.vehicle` → `vehicle`, Lojistik `logistics.driver` → `driver`). Katalog anahtarın modüle ait olduğunu ve kategorinin tanımlı olduğunu denetler. Core'da sektör koşulu yoktur.
- **Belge türü** `core.credential-type` (Y9b) listesidir. Kategorileri: `vehicle`, `driver`, `party`, `facility`. Hazır değerler örnektir (muayene, trafik sigortası, kasko, egzoz, takograf kalibrasyonu; ehliyet, SRC, psikoteknik, ADR, takograf kartı; yetki belgesi, vekâletname; faaliyet izni). Türün kategorisi sahip türün kategorisiyle aynı olmalıdır.
- **Erişim sahip kaydın erişimidir** (ADR-0040 K2 ile aynı ilke; ayrı `core.credentials.*` izni açılmadı, bölüm 5'teki taslaktan sapma):
  - Sahip türü okuyabilen o türün belgelerini görür. Okuyamadığı türün belgeleri sorgudan elenir ve tekil okumada 404 döner.
  - Ekleme, değiştirme ve silme için sahip kayıtta güncelleme izni ve sahip modülün yazılabilir lisansı gerekir. Yoksa 403 döner; bunun için yeni `RecordAccessDeniedException` eklendi.
  - Sahip modülü kapanınca belgeler de görünmez.
  - Dosya ekleme ve silme aynı kurala uyar: yeni `IRecordWriteGuard` sözleşmesini dosya deposu da sorar. Böylece araç yazan biri sürücü belgesine dosya ekleyemez.
  - Sahip türü veri kapsamlı (sorumlu ya da şirket alanı) olamaz. Depo bu durumda satır düzeyi sahip kapsamı uygulamaz, ilk kullanımda hata verir; bugünkü sahipler (araç, sürücü) kapsamsızdır.
- **Silme:** yanlış girilen belge silinebilir. Dosyası olan belge silinmez (409): önce dosya silinir ya da belge "İptal edildi" yapılır, böylece dosya erişilemez kalmaz.
- **Süzgeçler:** sahip (tür, kimlik), tür, durum, `validOn` (o gün geçerli), `expiresBy` (o güne kadar bitecek ya da bitmiş geçerli belgeler), `expiredBefore`. Varsayılan sıra bitişi en yakın olandan başlar; süresiz belgeler sondadır.
- **Arayüz:**
  - Araç ve sürücü kartının altında "Araç belgeleri" / "Sürücü belgeleri" paneli var. Durum rozetleri: geçerli, "N gün kaldı" (30 gün, yalnızca görsel eşik), süresi doldu, başlamadı, iptal. Özet satırı yaklaşan ve süresi dolan belgeleri sayar.
  - Sekmeli belge formu (Belge; Notlar) ve ortak `AttachmentPanel` (ADR-0040 dilim 1b'nin ilk bileşeni: liste, yükleme, indirme, silme).
  - Yeni CSS sınıfı ya da token yok.

**Bilerek yapılmadı:** yaklaşan bitiş uyarı kuralı (dilim 2b), belge zorunluluğu (sefer atamasında uyarı/engel, dilim 5, bölüm 9 madde 3), firma ve tesis sahipleri (Party belgesi ve antrepo izni; kategori hazır, sahip bildirimi sonra), tüm belgelerin tek listesi (sahip adını göstermek için sahip etiketi sözleşmesi gerekir; 2b ile), vekâletin taşınması (K3: zorunlu değil).

### Test sonuçları (Dilim 2a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Fleet.Tests` | sahip bildirimi ve katalog doğrulaması, belge türü listesi ve formu, Shared sabitleri |
| `dotnet test tests/Modulix.IntegrationTests --filter "CredentialTests\|FileTests"` (SQL Server; CI koşar) | mutlu yol (araç ve sürücü belgeleri, bitişe göre sıra, geçerli / bitecek / bitmiş süzgeçleri, arama, iptal, denetim kaydı, dosya eki, dosyalı belge silinmez), doğrulama (zorunlu alanlar, sahip olmayan tür, bilinmeyen kayıt, kategori, liste dışı tür, tarih sırası, numara tekilliği, sahip değişmez, eski sürüm), tenant izolasyonu (başka tenant'ın aracına belge eklenemez, belgesi görünmez, değiştirilemez, silinemez, dosya eklenemez), yetki (araç okuyan görür ama yazamaz ve dosya ekleyemez; sürücü yazan araç belgesini görmez; araç yazan sürücü belgesini değiştiremez ve dosya ekleyemez; modül kapanınca belgeler gizlenir; izinsiz 403) |

## 15. Dilim 2b: Bitişe kala uyarı ve belge takibi (12 Kasım 2026)

K3'ün uyarı kısmı uygulandı: eşik koda değil firmanın kuralına yazılır.

- **Kural türü `core.credential`** (ADR-0027):
  - Tarama kaynağı `CredentialAutomationRecordSource`, yalnızca geçerli (iptal edilmemiş) belgeleri tarar.
  - Tarihe bağlı tetikleyici `validTo` / `validFrom` alanlarında çalışır, ör. "bitişine 30 gün kala".
  - Kayıt olayları: eklendi, güncellendi.
  - Alanlar: belge no (etiket), tür anahtarı, sahip türü, veren kurum, tarihler, durum.
- **Erişim kuralı:** belge kuralı bütün sahip türlerinin belgelerini kapsar.
  - Kural düzenleyicide "Süreli belge" türünü yalnızca tenant'ta modülü açık **her** sahip türünü okuyabilen görür; bunun için en az bir sahip modülü açık olmalıdır.
  - Kural bu okuma izinleriyle çalışır (`ModuleCatalog.AutomationOwnerReads`, `AutomationRuleIdentity`). Yayınlayanın sahip olmadığı bir okuma izni kurala geçmez.
  - Sahip modülü olmayan tenant'ta tür görünmez.
- **Core aksiyonu "Bildirim gönder"** (`core.notify`): sektörden bağımsızdır, her kayıt türünün kuralında kullanılır.
  - Parametreler: alıcı (Core üye lookup'ı `core.notification.recipient`; kaydın sorumlu alanına da bağlanabilir) ve ileti (en çok 200 karakter, `{{alan}}` değişkenli).
  - Alıcı kaydı **kendi yetkisiyle** göremiyorsa bildirim gönderilmez, adım atlanır. Böylece başlıktaki kayıt değişkenleri görmediği bilgiyi sızdırmaz.
  - Aynı adım aynı kayıt için aynı alıcıya bir kez yazar. Tekilleştirme anahtarı adımın idempotency anahtarının SHA-256 özetidir.
  - Kural motorunun çağrı kimliğinde kullanıcı yoktur (`UserId` boş); bu yüzden alıcı zorunludur.
- **Sahip etiketi sözleşmesi:** `IRecordLabelProvider`. Araç kaydı plakayı, sürücü kaydı kişinin adını soyadını verir. Belge listesi her satıra salt okunur `ownerLabel` ekler; bu alan yalnızca listede bulunur, tekil okuma ve denetim kaydında yoktur.
- **Arayüz:** "Belge takibi" ekranı iki menüde: Filo › Araç belgeleri (`/filo/belgeler`) ve Lojistik › Sürücü belgeleri (`/lojistik/surucu-belgeleri`).
  - Aynı bileşen, sahip türü rotadan gelir.
  - Görünümler: Bitişi yaklaşan (varsayılan: bugün geçerli ve 30 gün içinde bitecek), Süresi dolan, Bugün geçerli, Tüm belgeler, İptal edilen.
  - Grid şablonu (`ViewTabs`, sıralanan başlıklar, sütun seçici, ⋯ menüsü, sayfalama). Belge kartın panelinden eklenir, buradan düzenlenir.
  - Yeni CSS sınıfı ya da token yok.

**Bilerek yapılmadı:**
- Hazır kural şablonu ("bitişine 30 gün kala bildir"): alıcı tenant'a özgüdür, şablon alıcısız yayınlanamaz.
- E-posta ile bildirim: aksiyon yalnızca uygulama içi; e-posta tercihi ayrı iştir.
- Bildirim bağlantısı: belgenin kendi sayfası yok; sahip kartına bağlantı, sahip türüne göre rota sözleşmesi ister.
- Sahip modülü sonradan açılırsa kural o türün okuma iznini de kazanır; yayın anındaki küme saklanmaz. Kabul edilen risk: bildirim yine alıcının kendi yetkisiyle süzülür.

### Test sonuçları (Dilim 2b)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Fleet.Tests` | 14: belge kural türü, tarih alanı, tarama kaynağı, "Bildirim gönder" ve alıcı lookup'ı katalogda; sahip okuma izni modülün olmalı |
| `dotnet test tests/Modulix.Core.Tests` | Core aksiyon listesi ve abonelik kayıt türleri süreli belgeyle güncellendi |
| `dotnet test tests/Modulix.IntegrationTests --filter "CredentialExpiryTests\|CredentialTests"` (SQL Server; CI koşar) | mutlu yol ("bitişine 30 gün kala" kuralı yalnızca o güne düşen geçerli belgeyi bulur, iptal edilen ve 31 gün kalan taranmaz, bildirim kayıt değişkeniyle bir kez yazılır; listede plaka ve ad soyad), doğrulama (metin alanına tarih tetikleyicisi, boş ileti, alıcısız adım yayınlanmaz), tenant izolasyonu (tarama başka tenant'ın belgesini bulmaz, onun üyesine bildirim gitmez), yetki (aracı göremeyen alıcıya araç belgesinin bildirimi gitmez; sürücü izni araç belgelerini göstermez) |

## 16. Dilim 4: Taşıma talebi ve sevkiyat (12 Kasım 2026)

K6 ve K7'nin ilk yarısı uygulandı: talep → onay → sevkiyat → planlama → yükleme → yol. Teslim (K8) dilim 6'da (bölüm 18), sefer ve durak (K5) dilim 5'te (bölüm 17) uygulandı.

- **Ortak yük ve güzergâh** (`CargoEntity`):
  - Taraflar: müşteri (zorunlu), gönderen, alıcı (`core.Party`).
  - Taşıma türü: `logistics.transport-mode` listesi (Y9b). Kategoriler `full` (komple) ve `partial` (parsiyel); hazır değerler FTL, Ekspres, LTL, Grupaj.
  - Güzergâh: yükleme ve teslim şehri (zorunlu) ve adresi.
  - Tarih: yükleme günü (zorunlu) ve en geç teslim; teslim günü yükleme gününden önce olamaz.
  - Yük: tanım (zorunlu), kg, m³, palet, LDM, tehlikeli madde (ADR) bayrağı.
  - Notlar, durum ve gerekçe; özel alanlar.
  - Adres bugün metindir; durak adresi lookup'ı sefer diliminde gelir.
- **Taşıma talebi** (`logistics.TransportRequests`):
  - Kaynak `manual` / `api`.
  - Dış referans kaynak içinde tekildir: `UNIQUE(TenantId, Source, ExternalRef)` süzgeçli. Böylece API'nin yeniden denediği istek çift talep açmaz.
  - Yalnızca "Yeni" talep düzenlenir.
- **Sevkiyat** (`logistics.Shipments`):
  - Genel uçtan açılmaz; yalnızca talep onayıyla doğar.
  - Her talepten en çok bir sevkiyat: `UNIQUE(TenantId, RequestId)`. Talebe aynı modül içinde bileşik yabancı anahtarla bağlıdır.
  - Yükleme başlayana kadar (Planlama bekliyor, Planlandı) yük ve tarih düzeltilebilir.
  - Migration `LogisticsRequestsShipments` (RLS).
- **Durum yalnızca komutla** (K7):
  - Geçiş tablosu `LogisticsTransitions`; iş akışı `LogisticsWorkflow`.
  - Uçlar: `GET/POST /api/logistics/{requests|shipments}/{id}/actions`.
  - Durum ve gerekçe alanları genel güncellemede yok sayılır. Komut, depoya "iş akışı kapısı"nı açarak kayıt boru hattından yazar (izin, sürüm denetimi 409, denetim kaydı, outbox). Bu, Siparişlerin `OrderWorkflowGate` kalıbıdır.
- **Talep eylemleri** (onay mekanizması):
  - **Onayla**: ayrı izin `logistics.requests.approve`. Aynı işlemde (`RunAtomicAsync`) sevkiyatı talebin yük ve güzergâhıyla açar, talebi "Onaylandı" yapar ve sevkiyata bağlar (`shipmentId`).
  - **Reddet**: aynı izin, gerekçe zorunlu.
  - **İptal**: yazma izni, gerekçe zorunlu, yalnızca yeni talep.
  - Onaylayan, sevkiyat yazma izni olmadan da onaylayabilir; sevkiyat onayın parçasıdır.
- **Sevkiyat eylemleri:**
  - Planlandı → Yükleme başladı → Yola çıktı.
  - İptal: teslimden önce ve yola çıkmadan, gerekçeli.
  - Teslim durumlarına bu komutlarla geçilmez (birim testle denetlenir).
  - "Planlandı" bugün elle verilir; sefer ataması gelince otomatik olur.
- **Rol şablonları:**
  - "Operasyon": talep açar, sevkiyat yürütür, onaylamaz.
  - Yeni "Operasyon müdürü": onaylar ve reddeder.
- **Kural yeteneği:** iki kural türü `logistics.transport-request` ve `logistics.shipment`.
  - Tarama kaynağı yalnızca açık kayıtları tarar.
  - Tarihe bağlı tetikleyici `pickupDate` / `deliveryBy`, ör. "yükleme gününe 1 gün kala planlanmamış sevkiyatı bildir". Durum geçişleri güncelleme olayıdır.
  - Eski iskelet beyanındaki `documentStatus` ve `capacityUsagePercent` alanları kayıtta olmadığı için kaldırıldı; uygunluk sefer atamasındadır.
- **Arayüz:** Lojistik › Taşıma talepleri (`/lojistik/talepler`) ve Sevkiyatlar (`/lojistik/sevkiyatlar`).
  - Grid şablonu. Görünümler: Onay bekleyen / Onaylanan / Reddedilen / Tümü; Açık / Planlama bekleyen / Yolda / Kapanan / Tümü.
  - Sekmeli kart: Taraflar ve güzergâh, Yük, Notlar. Kartın üstünde eylem çubuğu var; gerekçe isteyen eylem önce gerekçe sorar, yapılamayan eylem nedeniyle pasif görünür.
  - Kapanan kayıt salt okunur.
  - Yeni CSS sınıfı ya da token yok.

**Karar (sapma):** talep ve sevkiyat bölüm 3'teki öneriye göre `ICompanyScoped` olacaktı. Kod tabanında henüz şirket kapsamlı tablo yok ve boru hattının şirket süzgeci uçtan uca denenmedi. Bu yüzden bu dilimde `ICompanyShared` kaldı. İşletici şirket alanı ilk şirket kapsamlı tabloyla birlikte ayrı bir dilimde eklenir (boş sütun + geri doldurma).

**Bilerek yapılmadı:** navlun ve para birimi (dilim 7, alan yetkisiyle), sefer ve durak bağı (dilim 5), teslim ve kanıt (dilim 6), web formu ve sipariş köprüsü (K6, dilim 11), talep numarası serisi (Ç8.1), kendi talebini onaylama yasağı ayarı (Siparişlerdeki gibi; ilk ihtiyaçta ayar olarak).

### Test sonuçları (Dilim 4)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 14: geçiş tablosu durum listesiyle tutarlı, teslim durumlarına komutla geçilmez, iptal gerekçeli ve yoldan önce, onay ayrı izin ve yalnızca müdür şablonunda; sekmeli formlar; kategorili taşıma türü; Shared sabitleri; kural alanları ve tarama kaynağı |
| `dotnet test tests/Modulix.IntegrationTests --filter "LogisticsShipmentTests\|SharedRecordTests\|LookupTests"` (SQL Server; CI koşar) | mutlu yol (talep → onay → sevkiyat aynı işlemde ve bağlı → planlandı → yükleniyor → yolda, durum süzgeci, denetim kaydı), doğrulama (zorunlu alanlar, tarih sırası, referans tekilliği, genel güncelleme durumu yazamaz, gerekçesiz ret, bilinmeyen eylem, eski sürüm, sevkiyat doğrudan açılmaz, silme yok, kapanan talep düzenlenmez, sırasız geçiş, yoldaki sevkiyat iptal edilmez ve düzenlenmez), tenant izolasyonu (başka tenant'ın müşterisi kullanılamaz, talebi görünmez, eylemleri 404, aynı referans başka tenant'ta serbest), yetki (operasyon onaylayamaz ve eylem listesinde onay görmez, okuyan yazamaz ve eylem yapamaz, izinsiz 403, yalnızca onay izniyle onaylanır, modül kapalıyken 403) |

## 17. Dilim 5: Sefer, durak zinciri, atama ve uygunluk (12 Kasım 2026)

K5 ve K7'nin ikinci yarısı uygulandı. Sevkiyatın "Planlandı" durumu artık sefer atamasıdır; dilim 4'teki elle "Planlandı" eylemi kaldırıldı.

- **Sefer** (`logistics.Trips`):
  - Numara sunucuda verilir: `SF-yıl-sıra`, `UNIQUE(TenantId, Number)`. Ç8.1 belge serisi henüz yok; aynı anda açılan iki sefer aynı numarayı alırsa benzersizlik dizini ikincisini reddeder. Seri gelince numara oraya taşınır.
  - Alanlar: planlanan başlangıç ve bitiş; Filo'dan araç ve dorse (`IVehicleDirectory`: araç motorlu, dorse çekilen araç olmalı; pasif araç seçilemez; Filo kapalıyken araç seçilemez); sürücü ve ikinci sürücü (bölüm 9 madde 2; aynı kişi iki kez seçilemez, pasif sürücü seçilemez); taşeron seferde "Taşıyıcı" rollü firma (K4); notlar.
  - Sürücüye aynı modül içinde bileşik yabancı anahtar vardır. Filo aracına veritabanı bağı kurulmaz.
  - Yalnızca "Planlanıyor"ken düzenlenir. Silinmez (K12).
- **Durak** (`logistics.TripStops`):
  - Sıra sefer içinde tekildir: `UNIQUE(TenantId, TripId, Sequence)`. Verilmezse son sıranın 10 fazlası olur, böylece araya durak eklenebilir (ör. 25).
  - Türler: yükleme, boşaltma, sınır/gümrük, mola, boş dönüş.
  - Alanlar: şehir, adres, planlanan varış ve gerçekleşen varış.
  - Plan yalnızca sefer planlanırken değişir; varış yalnızca yoldaki seferde girilir ve ileri bir zaman olamaz; kapanan seferin durağı değişmez.
  - Sevkiyat durağı elle silinmez ve türü değişmez.
  - Migration `LogisticsTrips` (iki tablo RLS'te; `Shipments.TripId` sütunu ve yabancı anahtarı).
- **Atama** (`ITripPlanner`):
  - Uçlar: `POST /api/logistics/trips/{id}/shipments` ve `DELETE /api/logistics/trips/{id}/shipments/{shipmentId}`.
  - Ekleme: planlama bekleyen sevkiyat için tek işlemde (`RunAtomicAsync`) sevkiyatın güzergâhından yükleme ve boşaltma durağı açılır ve durağa bağlanır; sevkiyat "Planlandı" olur ve sefere bağlanır.
  - Çıkarma: yalnızca henüz yüklenmemiş sevkiyat çıkarılır; durakları kalkar ve sevkiyat "Planlama bekliyor"a döner.
  - İkisi de yalnızca planlanan seferde yapılır. Her yazma kayıt boru hattından geçer (sefer yazma izni, denetim kaydı, outbox, sürüm).
- **Uygunluk denetimi** (`GET /api/logistics/trips/{id}/check`):
  - Engeller:
    - Sevkiyat yok.
    - Yükleme ya da boşaltma durağı yok, ya da bir sevkiyatın boşaltma durağı yüklemesinden önce.
    - Kendi seferinde araç ya da sürücü eksik.
    - Araç bakımda ya da pasif; sürücü izinde ya da pasif.
    - Kapasite aşımı: yük toplamı, araç ve dorsenin girilmiş kapasitelerinin toplamını aşıyor (kg, palet, LDM, m³). Kapasitesi girilmemiş ölçü denetlenmez.
  - Süreli belge (K3, bölüm 9 madde 3): araç, dorse ve sürücülerin her belge türünde en uzun geçerli belgesi sefer bitmeden doluyorsa bulgudur.
    - Firma ayarı `logistics.trips.credential-check` belirler: uyar (varsayılan) ya da engelle.
    - Hangi belge türünün zorunlu olduğu henüz ayarlanamaz; sonraki dilimde.
    - Modüller belgeleri Core'un yeni okuma sözleşmesiyle okur: `ICredentialDirectory` (Core.Contracts; kayıt izni sormaz, çağıran modül kendi iznini denetler).
  - Uyarılar: aynı araç, dorse ya da sürücü tarihleri çakışan başka açık seferde; seferde ADR yükü var.
- **Durum yalnızca komutla** (K7; sefer de genel eylem ucunu kullanır: `/api/logistics/trips/{id}/actions`):
  - Akış: Planlanıyor → Hazır → Yolda → Tamamlandı.
  - **Planı onayla:** denetimden geçer; engel varsa 409 ve bulgular iletide. Uyarılar seferin notuna yazılır ("Uyarılarla onaylandı: …").
  - **Planlamaya geri al:** hazır seferi yeniden düzenlemeye açar.
  - **Sefere çıkar:** denetim yinelenir (onaydan sonra araç bakıma girmiş olabilir).
  - **Seferi tamamla:** bütün duraklara varış girilmiş olmalı.
  - **İptal:** gerekçeli ve yoldan önce. Aynı işlemde seferdeki planlanmış sevkiyatlar "Planlama bekliyor"a döner ve seferden ayrılır. Yüklemesi başlamış sevkiyatı olan sefer iptal edilemez. Duraklar geçmiş olarak kalır.
  - Bütün sefer eylemleri `logistics.trips.write` iznindedir. Operasyon ve Operasyon müdürü rol şablonlarına sefer izinleri eklendi.
- **Kural yeteneği:** `logistics.trip` kural türü (durum, numara, planlanan başlangıç ve bitiş; tarama yalnızca açık seferler). Örnek: "sefere 1 gün kala hâlâ hazır olmayan sefer".
- **Arayüz:** Lojistik › Seferler (`/lojistik/seferler`).
  - Grid şablonu. Görünümler: Açık / Planlanan / Hazır / Yolda / Kapanan / Tümü.
  - Sütunlar: numara, tarih, araç (+ dorse), sürücü, taşıyıcı, durum.
  - Sekmeli kart: Plan ve atama, Notlar. Kartın üstünde eylem çubuğu ve son geçiş notu var.
  - Kartın altında iki panel:
    - **Uygunluk denetimi:** yük/kapasite kartları (aşan ölçü tehlike tonunda), engeller ve uyarılar.
    - **Duraklar ve sevkiyatlar:** sevkiyat ekleme, ara durak ekleme, seferden çıkarma, yoldaki seferde "Vardı".
  - Sürücü seçimi bugün açılır listedir (ilk 200 kart; ad kişi kaydından). Araç, dorse ve taşıyıcı lookup'larla seçilir: `logistics.trip.vehicle`, `.trailer`, `.carrier` (rolle süzülür).
  - Yeni CSS sınıfı ya da token yok.

**Test yalıtımı:** `FieldRulePackageTests`'in yayımladığı ürün paketi aynı veritabanındaki bütün lojistik firmalarına ulaşır. Paketin "durum = Planlama bekliyor" koşulu, sevkiyatı planlamaya döndüren çıkarma ve iptal testlerini bozuyordu. Koşul, henüz hiçbir komutun ulaşmadığı "Teslim edilemedi" durumuna çevrildi.

**Bilerek yapılmadı:**
- Zorunlu belge türleri ayarı. Bugün bütün türler bakılır; "SRC şart" gibi tür bazlı zorunluluk sonra gelecek.
- Belge serisinden sefer numarası (Ç8.1).
- Sefer başlayınca sevkiyatların otomatik "Yolda" olması. Sevkiyat eylemleri ayrı kalır; teslimle birlikte dilim 6'da ele alınacak.
- Durak sırasını sürükle-bırak ile değiştirme. Bugün sıra numarasıyla yapılıyor.
- Durak adresi lookup'ı.
- Araç takvimi ve kapasite planlama panosu.
- Sürücü seçiminde arama (büyük filolar).
- İşletici şirket alanı (bölüm 16'daki sapma sürüyor).

### Test sonuçları (Dilim 5)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 15: geçiş tablosu bütün durum listeleriyle tutarlı, sevkiyatı elle planlayan eylem yok, sefer geçişleri ve iptal kuralı, belge ayarı varsayılanı uyarı, rol şablonlarında sefer izni, sekmeli sefer formu, Shared sabitleri (sefer durumları, durak türleri, lookup'lar), kural türleri |
| `dotnet test tests/Modulix.IntegrationTests --filter "LogisticsTripTests\|LogisticsShipmentTests\|FieldRulePackageTests"` (temiz SQL Server veritabanında; CI koşar) | 13/13. **Mutlu yol:** numara, iki sevkiyat → dört durak ve "Planlandı", ara durak araya girer, denetim temiz, onay → çıkış → varışlar → tamamlama, sürücü süzgeci, denetim kaydı. **Doğrulama:** dorse/çekici türü, aynı sürücü, tarih sırası; sevkiyatsız ve kapasite aşan sefer onaylanmaz; çıkarma durakları kaldırır; süresi dolan belge uyarıyla onaylanır, ayarla engellenir; hazır sefer, durak, varış ve silme reddedilir; geri alma; gerekçesiz iptal 422; iptal sevkiyatı planlamaya döndürür; sefer silinmez; planlanmış sevkiyat ikinci kez eklenmez; varışsız tamamlama ve yoldaki iptal 409; bakımdaki araç engel. **Tenant izolasyonu:** başka tenant'ın seferi, durakları, denetimi ve eylemleri görünmez (404); aracı ve sürücüsü kullanılamaz; sevkiyatı sefere eklenemez; seferine durak açılamaz. **Yetki:** okuyan denetimi görür ama yazamaz, eylem yapamaz, sevkiyat ve durak ekleyemez; sefer izni olmayan sevkiyatçı seferi göremez ve ekleyemez; izinsiz 403; modül kapalıyken 403 |
| `... --filter "TableInventoryTests\|TenantIsolationTests\|TenantDataTransferTests\|ScopeLimitTests\|AutomationRuleTests\|SharedRecordTests\|LookupTests\|CredentialTests\|CredentialExpiryTests\|LogisticsDriverTests\|LogisticsCarrierTermsTests\|FleetVehicleTests\|StarterPackageTests"` | 78/78 |


## 18. Dilim 6: Teslim kanıtı ve `ShipmentDelivered` iş olayı (12 Kasım 2026)

K8 uygulandı. Yoldaki sevkiyat teslim edilir, kısmen teslim edilir ya da teslim edilemez; üç sonuç yalnızca teslim komutuyla verilir (genel eylem ucu teslim durumlarına geçmez, birim testle denetlenir).

- **Teslim kanıtı** (`logistics.DeliveryProofs`, kayıt türü `logistics.delivery-proof`):
  - Alanlar: sevkiyat (bileşik yabancı anahtar), sonuç (`delivered`, `partial`, `failed`), teslim alan, teslim zamanı (**sunucu zamanı**, istemciden gelmez), hasar ve eksik işareti, başarısız teslim nedeni, not.
  - **Değişmez:** güncellenmez ve silinmez (409); genel uçtan oluşturulamaz (409). Bir sevkiyatın birden çok kanıtı olabilir (düzeltme yeni kanıtla; düzeltme komutu sonraki dilim), bu yüzden `ShipmentId` benzersiz değildir.
  - Kurallar depodadır, komut atlanamaz: yalnızca yoldaki sevkiyata yazılır; teslim ve kısmi teslimde teslim alan, kısmi teslimde açıklama (not), başarısız teslimde neden zorunludur; başarısız teslimde teslim alan, hasar ya da eksik işaretlenmez.
  - Migration `LogisticsDeliveryProofs` (RLS).
  - Okuma izni `logistics.shipments.read`, yazma izni `logistics.shipments.write` (yeni izin yok; rol şablonları değişmedi).
- **Teslim komutu** (`IShipmentDelivery`, `POST /api/logistics/shipments/{id}/delivery`, gövde `{ version, outcome, receivedBy, damaged, shortage, failureReason, notes }`):
  - Tek işlemde (`RunAtomicAsync`) kanıtı yazar, sevkiyatın durumunu değiştirir ve iş olayını yayınlar: birlikte yazılır ya da hiçbiri.
  - Sevkiyat yoldaysa ve sürüm güncelse çalışır; değilse 409, bilinmeyen sonuç ya da eksik zorunlu alan 422, izin yoksa 403, başka tenant'ın sevkiyatı 404.
  - Sevkiyatın "son geçiş notu" kısmi teslimde açıklama, başarısız teslimde nedendir.
  - Teslim edilen, kısmi ya da başarısız sevkiyatın yük bilgisi artık değişmez (dilim 4 kuralı).
- **İş olayları** (ADR-0039, beyanlı):
  - `logistics.shipment.delivered` v1: teslim ve kısmi teslim. ADR-0037 "teslim → faturalanabilir kalem" köprüsünün ve `DomainEvent` tetikleyicisinin dayanağıdır. Yük: sevkiyat alanları (müşteri, gönderen, alıcı, sefer, taşıma türü, teslim şehri, kg, m³, palet, durum) + `proofId, outcome, receivedAt, receivedBy, damaged, shortage`.
  - `logistics.shipment.delivery-failed` v1: başarısız teslim; faturalanabilir kalem üretmez. Yük: sevkiyat alanları + `proofId, outcome, receivedAt, failureReason`.
  - Olayın sıra numarası sevkiyatın komuttan sonraki sürümüdür; payload yalnızca beyan edilmiş alanları taşır.
- **Dosyalar:** fotoğraf ve imza kanıta Core dosya deposuyla eklenir (`POST /api/files`, `entityKey=logistics.delivery-proof`). Erişim ADR-0040 uyarınca kaydın erişimidir: kanıtı okuyan listeler ve indirir, sevkiyat yazma izni olan ekler.
- **Arayüz:** sevkiyat kartında (yoldaki ya da kapanmış sevkiyat) "Teslim" paneli. Yoldaki sevkiyatta "Teslimi kaydet" formu (sonuç, teslim alan, hasar / eksik, neden, not); altında her kanıt için özet ve "Fotoğraf ve imza" dosya paneli. Yeni CSS sınıfı ya da token yok. **Açık ve koyu tema ekran görüntüsü bu dilimde alınmadı.**
- **Test yalıtımı:** `FieldRulePackageTests`'in yayımladığı ürün paketinin koşulu "Teslim edilemedi" durumuydu; bu durum artık teslim komutuyla gerçekten üretildiği için paket aynı veritabanındaki teslim testlerini bozuyordu. Koşul, hiçbir testin üretmediği bir yük tanımına (`__alan-kurali-testi__`) çevrildi.

**Bilerek yapılmadı:**
- Teslim düzeltme komutu (yeni kanıt açma): kanıt modeli hazır; ilk ihtiyaçta komut eklenir.
- Hasar işaretli tesliminde Destek şikâyet taslağı önerisi (isteğe bağlı köprü, K8).
- Sefer başlayınca sevkiyatların otomatik "Yolda" olması ve sevkiyat teslim edilince seferin bütün sevkiyatları kapanınca otomatik tamamlanması. Eylemler ayrı kalır.
- Teslim → faturalanabilir kalem (ADR-0037 dilim 5) ve navlun (dilim 7).
- İmza çizimi / mobil sürücü uygulaması (dosya olarak eklenir).
- Teslim alanın alıcı firmanın kişilerinden seçilmesi (bugün serbest metin).

### Test sonuçları (Dilim 6)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 18: teslim sonuçları Shared sabitleriyle aynı ve etiketli, iş olayı beyanları (modül anahtarı, sürüm 1, sevkiyat kaydı, alan ayrımı), teslim durumlarına geçiş tablosundan ulaşılamaz ve kanıt denetim kaydında |
| `dotnet test tests/Modulix.IntegrationTests --filter "LogisticsDeliveryTests"` | 5/5. **Mutlu yol:** teslim kanıtı, sunucu zamanı, durum, tek iş olayı ve outbox yükü, kanıta fotoğraf, denetim kaydı; kısmi teslim; başarısız teslim ayrı olay ve faturalanabilir olay yok. **Doğrulama:** yoldaki olmayan sevkiyat 409, teslim alan / not / neden zorunlu, başarısızda teslim alan olamaz, bilinmeyen sonuç, hatalı denemeler hiçbir şey yazmaz, eski sürüm 409, genel eylem ucu teslime geçmez, kanıt genel uçtan açılmaz, ikinci teslim 409, kanıt değişmez ve silinmez. **Tenant izolasyonu:** başka tenant'ın sevkiyatı teslim edilemez (404), kanıtı görünmez, listelenmez, dosyası eklenemez. **Yetki:** okuyan teslim kaydedemez ve dosya ekleyemez ama kanıtı ve dosyalarını okur, izinsiz kişi 403, modül kapalıyken 403 |
| `... --filter "FieldRulePackageTests\|LogisticsTripTests\|LogisticsShipmentTests\|LogisticsDeliveryTests"` (temiz veritabanı) | 18/18 |
| `... --filter "TableInventoryTests\|TenantIsolationTests\|TenantDataTransferTests\|AutomationRuleTests\|BusinessEventTests\|ModulesAndRolesTests\|SharedRecordTests\|LookupTests\|StarterPackageTests\|CredentialTests\|LogisticsDriverTests\|FleetVehicleTests"` (temiz veritabanı) | 77/77 |


## 19. Dilim 7a: Navlun ve taşeron hakedişi (12 Kasım 2026)

Dilim 7 ikiye bölündü: bu dilim yalnızca tutar alanlarını ve yetkisini getirir; "teslim → faturalanabilir kalem" köprüsü (dilim 7b) ADR-0037 kabul edilip `core.BillableItems` yazılınca gelir (ADR-0037 henüz taslaktır ve main'de yoktur). Ölçüm sözlüğü (K11): navlun operasyon değeridir, gelir ya da fatura değildir.

- **Navlun** (sevkiyat): `freightAmount` (decimal 18,2) + `freightCurrency` (ISO 4217, `char(3)`). Müşteriye uygulanan taşıma bedeli, elle girilir (tarife hesabı Ç18a.2 sonraya bırakıldı). **Talepte tutulmaz:** onayda sevkiyata kopyalanırsa, navlun izni olmayan onaylayanın isteğinde alan hiç gelmeyeceği için sessizce kaybolurdu; navlun sevkiyat açıldıktan sonra girilir.
- **Hakediş** (sefer): `settlementAmount` + `settlementCurrency`. Taşeron seferde taşıyıcıya ödenecek anlaşmalı tutar; yalnızca `carrierPartyId` seçiliyken girilir (taşıyıcısız seferde 422). Tarife ve anlaşma (`CarrierTerms`) ile otomatik hesap yoktur.
- **Kural** (`MoneyRules`, iki yerde ortak): tutar varsa para birimi zorunlu, para birimi varsa tutar zorunlu; tutar 0 ile 9.999.999.999.999.999,99 arasında, en çok iki ondalık; para birimi üç harfli kod, büyük harfe çevrilir. Tutar silinince para birimi de silinir.
- **Hassas alan** (`IModule.SensitiveFields`, izin `logistics.freight.read`): dört alan da (para birimleri dahil: yetkisiz kişi mevcut tutarın para birimini değiştirmesin). İzin yoksa alan yanıtta gelmez, yazılamaz, denetim kaydında gizlenir; yazma ayrıca sevkiyat/sefer yazma iznini gerektirir (navlun izni tek başına yazma hakkı vermez). Rol şablonu: yalnızca "Operasyon müdürü" taşır; "Operasyon" taşımaz.
- **Düzenleme penceresi:** navlun, sevkiyatın yük bilgisiyle aynı kilide bağlıdır (Planlama bekliyor, Planlandı); hakediş sefer planlanırken düzenlenir. Yükleme başladıktan sonra düzeltme ayrı, denetimli bir komut olarak sonraya kaldı.
- **İş olayı yükü değişmedi:** `ShipmentDelivered` navlun taşımaz; hassas alan olay yükünü gerekmeden yetkisiz abonelere açardı. Faturalanabilir kalem köprüsü (7b) sevkiyatı yetkili bağlamda okuyacak.
- **Migration:** `LogisticsFreightAndSettlement` yalnızca boş bırakılabilir dört sütun ekler (geriye uyumlu, mevcut tabloda RLS politikası zaten var).
- **Arayüz:** sevkiyat formunda "Navlun" bölümü (Yük sekmesi), sefer formunda "Hakediş" bölümü; alanlar yalnızca `logistics.freight.read` izni olan kullanıcıya çizilir ve kaydet isteğine yalnızca onun için girer. Sefer formunda taşıyıcı seçilmeden hakediş alanları pasiftir. Yeni CSS sınıfı ya da token yok. **Açık ve koyu tema ekran görüntüsü alınmadı.**

**Bilerek yapılmadı:** faturalanabilir kalem köprüsü (7b), tarife ve anlaşmadan otomatik navlun/hakediş hesabı, yükleme sonrası navlun düzeltme komutu, navlun/hakediş için liste sütunu ve süzgeç, olay yüküne tutar, kâr/marj görünümü (dilim 9), talepte fiyat teklifi.

### Test sonuçları (Dilim 7a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 21 (3 yeni): hassas alan beyanı ve izin, formlarda alan yerleşimi (talep formunda yok), yalnızca müdür rol şablonu navlun izni taşır |
| `dotnet test tests/Modulix.ArchitectureTests` | 69/69 |
| `... --filter "LogisticsFreightTests"` (temiz veritabanı) | 4/4. **Mutlu yol:** navlun girilir, para birimi büyük harfe çevrilir, silinir; talep onayı navlunsuz sevkiyat açar; taşeron seferde hakediş. **Doğrulama:** tutarsız para birimi, tutarsız tutar, geçersiz kod, eksi ve üç ondalıklı tutar, taşıyıcısız hakediş, para birimsiz hakediş, yüklemesi başlayan sevkiyatın navlunu kilitli; hatalı denemeler hiçbir şey yazmaz. **Tenant izolasyonu:** başka tenant'ın sevkiyat ve seferi 404, listede yok, değiştirilemez. **Yetki:** navlun izni olmayan alanları görmez, yazamaz, diğer alanları düzenlemeye devam eder ve navlun korunur, denetim kaydında tutarı görmez, izinsiz kişi 403 |
| `... --filter "LogisticsDelivery\|LogisticsTrip\|LogisticsShipment\|LogisticsDriver\|LogisticsCarrierTerms\|FieldRulePackage\|AutomationRule\|ModulesAndRoles\|TableInventory\|TenantDataTransfer\|BusinessEvent\|SharedRecord\|TenantIsolation"` (temiz veritabanı) | 85/85 |

## 20. Dilim 8: Sefer masrafı, onay ve yakıt alanları (13 Kasım 2026)

K10 uygulandı. Masraf operasyon kaydıdır; ödeme, kasa, banka ve sürücü hesabı finans/İK işidir ve burada yoktur.

- **Masraf (`logistics.TripExpenses`, `ICompanyShared`):** sefer (yalnızca oluştururken; `(TenantId, TripId)` bileşik yabancı anahtar), tür (Y9b listesi `logistics.expense-kind`), açıklama (zorunlu, 200; onay isteğinde kaydın adı), masraf tarihi (ileri gün olamaz), tutar (KDV dahil toplam, sıfırdan büyük), para birimi (üç harf), KDV tutarı (tutarı aşamaz), tedarikçi (`core.Party`, rol süzgeci yok), durum, durum gerekçesi. İptal edilmiş sefere masraf açılmaz (409).
- **Tür ve kategori:** hazır değerler yakıt, köprü/otoyol geçişi, harcırah, trafik cezası, otopark, yolda onarım/lastik, diğer; kategoriler `fuel`, `toll`, `allowance`, `fine`, `other`. Kod türün adına değil **kategorisine** bakar; firma kendi türlerini ekler. Varsayılan "Diğer": yakıt ek alan ister, bilinçli seçilmelidir.
- **Yakıt (ayrı defter yok):** `fuel` kategorisinde litre zorunlu (0-999.999,999), kilometre sayacı isteğe bağlı; başka kategoride ikisi de girilemez (422). Litre fiyatı (`unitPrice`, KDV dahil) tutardan ve litreden türetilir, kaydedilmez. Araç yakıt raporu bu kayıtlardan türetilecek (rapor dilim 9).
- **Ceza:** `chargeToDriver` yalnızca `fine` kategorisinde işaretlenir; sürücüye rücu **yalnızca bilgidir**, borç kaydı üretmez.
- **Durum ve karar:** Beklemede → Onaylandı / Reddedildi; yalnızca iş akışı komutuyla (`/api/logistics/expenses/{id}/actions`: `approve`, `reject`). Ret gerekçe ister (422). Karar verilmiş masraf değişmez (onay tutara bağlıdır) ve silinmez (K12): hatalı masraf reddedilir, düzeltme yeni masrafladır. Makbuz ve fiş masrafa Core dosya deposuyla eklenir (okuma = masraf okuma izni, ekleme = masraf yazma izni).
- **İzinler:** `logistics.expenses.read/write/approve`. "Operasyon" okur ve girer; yalnızca "Operasyon müdürü" onaylar. Giren, eylem listesinde onay ve ret düğmesini görmez; kendi masrafını onaylama yasağı bu dilimde yoktur (aynı kişiye hem yazma hem onay izni verilebilir; ayrım rol kararıdır).
- **Genel onay (ADR-0027 Ç6a):** masraf `IModule.ApprovalTargets` ile onaya gönderilebilir (tutar ve para birimi kayıttan); "Masrafı onaylama" işlemi onaya bağlanabilir. Firma bir onay sürecini bu işleme bağlarsa `approve` komutu önce onay kapısına sorar (409, neden iletide); ret her zaman serbesttir. Onay sürecini bağlamayan firmada müdür doğrudan onaylar.
- **Kural altyapısı:** masraf için kayıt olayları (`logistics.trip-expense.created/updated`), durum, tür, tutar, para birimi, tarih, sefer alanları ve tarama kaynağı (zamanlanmış ve masraf tarihine bağlı; yalnızca beklemedeki masraflar) beyan edildi: "tutarı eşiği aşan masraf onaya gitsin" ya da "3 gündür onay bekleyen masraf için hatırlat" gibi kurallar firmanın otomasyonudur, koda eşik yazılmadı.
- **Migration:** `LogisticsTripExpenses` (yeni tablo, RLS politikası `TenantIsolationSql.AddTable` ile).
- **Arayüz:** sefer kartında "Masraflar" paneli (`TripExpensesPanel`): ekleme/düzenleme formu (yakıtta litre ve kilometre, cezada rücu işareti), tablo, onaylı/bekleyen toplam (para birimine göre), onay-ret eylem çubuğu, makbuz ekleme. Sayfa içi küçük eylem formu olduğu için sekmesizdir; yeni CSS sınıfı ya da token yok. **Açık ve koyu tema ekran görüntüsü alınmadı.**

**Bilerek yapılmadı:** masraf listesi ekranı ("Bekleyen masraflar" onay kuyruğu; onay merkezi bağlantı adresi bu yüzden seferler sayfasına gider), ödeme ve kasa/banka, sürücü avansı ve hesabı (İK M4), yakıt tüketimi/sapma raporu ve eşikleri (dilim 9), makbuz zorunluluğu, masraf için özel alan ve form tasarımı, kendi masrafını onaylama yasağı, kur dönüşümü (para birimleri ayrı toplanır).

### Test sonuçları (Dilim 8)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 27 (6 yeni): masraf türü listesi ve kategorileri (varsayılan yakıt değil), yalnızca müdür şablonu onaylar, geçiş tablosu (karar yalnızca beklemeden, ret gerekçeli, iptal/yeniden açma yok), onay hedefi ve onaya bağlanabilir komut, kural altyapısı ve denetim etiketleri, Shared sabitleri sunucuyla aynı. Mevcut üç testin beklentisi masraf varlığını kapsayacak şekilde genişletildi |
| `dotnet test tests/Modulix.ArchitectureTests` / `tests/Modulix.Web.Tests` | 69/69 / 122/122 |
| `... --filter "LogisticsExpenseTests"` (temiz veritabanı) | 4/4. **Mutlu yol:** operasyon yakıt ve geçiş masrafı girer, litre fiyatı türer, beklemedeki masraf düzenlenir, müdür onaylar ve gerekçeyle reddeder, karar verilmiş masraf değişmez ve silinmez, bekleyen listesi, denetim kaydı. **Doğrulama:** tutar, para birimi, KDV, tarih, açıklama, bilinmeyen tür ve sefer, kategoriye bağlı litre/kilometre/rücu kuralları, iptal edilmiş sefer (409), gerekçesiz ret (422), çifte karar (409). **Tenant izolasyonu:** başka tenant'ın masrafı görünmez, güncellenmez, onaylanmaz, başka tenant'ın seferine masraf açılmaz. **Yetki:** yalnızca okuyan giremez, giren onaylayamaz ve eylem listesinde onay/ret görmez, okuma izni olmayan göremez |
| Etkilenen diğer entegrasyon testleri | **Yerelde tamamlanamadı.** İlk çalıştırmada 36 test geçti; "Approval" kalıbıyla toplanan ve bu dilimle ilgisiz bir Sözleşmeler testi bellek tükenmesiyle (`OutOfMemoryException`) düşüp çalıştırmayı durdurdu. Sonraki çalıştırmalarda makinenin belleği (≈750 MB boş) ve Docker'ın SQL Server kapı iletişimi yüzünden bağlantılar zaman aşımına uğradı (sunucu kapsayıcı içinden yanıt veriyordu). Kalanlar CI'da (temiz veritabanı, 4 parça) çalışır |

## 21. Dilim 9a: Operasyon panosu (13 Kasım 2026)

Dilim 9 ikiye bölündü. Bu dilim yalnızca operasyon panosunu getirir. Kârlılık görünümü (dilim 9b) masraf (dilim 8) ve faturalanabilir kalem (dilim 7b) gelince eklenecek. Panoda tutar yoktur: navlun hassas alandır (dilim 7a) ve pano bütün operasyon ekibine açıktır.

- **Uç:** `GET /api/logistics/dashboard` (`ILogisticsDashboard`). Modül yalnızca kendi tablolarını okur; sürücü belgeleri Core'un `ICredentialDirectory` sözleşmesiyle okunur. Gün firmanın saat dilimindedir (`ITenantClock`).
- **Bölümler** her biri kendi okuma iznine bağlıdır; izni olmayan bölüm `null` gelir, hiçbirini okuyamayana 403:
  - **Talepler** (`logistics.requests.read`): onay bekleyen sayısı ve en eskisinin bekleme süresi (gün).
  - **Sevkiyatlar** (`logistics.shipments.read`):
    - Planlama bekleyen; bunlardan yüklemesi yarına kadar (ya da geçmiş) olan.
    - En geç teslim günü geçmiş açık sevkiyat ve en çok geciken altı kayıt.
    - Yoldaki sayısı ve açık durumlara göre dağılım.
  - **Seferler** (`logistics.trips.read`): planlanan, hazır, yolda ve bugün başlayacak sefer sayısı. Öne çıkan sekiz sefer şu sırayla listelenir: yoldakiler, hazırlar, bugün-yarın başlayacak planlananlar. Her birinde durak ilerlemesi (varış girilen durak / bütün duraklar) ve sıradaki durak var.
  - **Teslimler** (`logistics.shipments.read`): son 30 günün teslim, kısmi, başarısız ve hasar sayıları; başarı oranı (teslim / bütün sonuçlar); son 14 günün günlük dağılımı. Teslim zamanı firma gününe çevrilerek gruplanır.
  - **Sürücü belgeleri** (`logistics.drivers.read`): etkin sürücülerin her belge türündeki en uzun geçerli belgesinin süresi dolmuş ya da 30 gün içinde doluyorsa sayılır; ilk altısı listelenir.
- **Arayüz:** Lojistik › Pano (`/lojistik/pano`), menüde ilk sırada.
  - Üst bant: "N sefer yolda", hazır ve bugün başlayan sefer sayısı; yanında onay bekleyen talep, planlama bekleyen ve teslim günü geçmiş sevkiyat.
  - Dört KPI kutusu: onay bekleyen talep, planlama bekleyen sevkiyat, yoldaki sefer, teslim başarısı (14 günlük eğilim çizgisiyle).
  - Kartlar: sefer ilerleme çubukları, açık sevkiyat halka grafiği, günlük teslim sütun grafiği, teslim günü geçenler, sürücü belgeleri.
  - Ortak pano sınıfları ve grafik bileşenleri kullanılır. Yeni CSS sınıfı ya da token yok.
- **Sözleşme denetimi:** uç sunucu kaydını doğrudan JSON'a yazar, arayüz Shared DTO'suyla okur. İki tarafın alan adları ve iç içe yapısı birim testle karşılaştırılır.

**Bilerek yapılmadı:**
- Kârlılık, masraf ve navlun toplamları (dilim 9b).
- Eşik kuralları (ör. "geciken sevkiyat sayısı 5'i geçince bildir"): bugün ADR-0027 kurallarıyla tek tek kayıt üzerinden kurulabilir; toplam eşiği sonra gelecek.
- Panodan kayda doğrudan bağlantı: talep, sevkiyat ve seferin kayıt başına adresi yok, bağlantılar listeye gider.
- Araç belgeleri: araçlar Filo'dadır, lojistik araç listesini okumaz.
- Tarih aralığı seçimi.

### Test sonuçları (Dilim 9a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 23 (2 yeni): sunucu kaydı ve Shared DTO aynı yapıda; panoyu açan izin kümesi her bölümün okuma iznini kapsar |
| `dotnet test tests/Modulix.ArchitectureTests` | 69/69 |
| `dotnet test tests/Modulix.Web.Tests` | 122/122 (menüde pano ilk sırada, sürücü ya da sefer izniyle görünür) |
| `... --filter "LogisticsDashboardTests"` | 4 test yazıldı. **Mutlu yol:** talep, sevkiyat, geciken, sefer ilerlemesi, teslim oranı ve günlük dağılım, belge. **Boş firma:** sıfırlar, oran yok. **Tenant izolasyonu:** başka firmanın kayıtları sayılmaz. **Yetki:** yalnızca sefer izni olan yalnızca sefer bölümünü alır, izinsiz 403, modül kapalıyken 403. **Yerelde çalıştırılamadı** (SQL Server 14333 bağlantısı zaman aşımında); CI doğrular. |
