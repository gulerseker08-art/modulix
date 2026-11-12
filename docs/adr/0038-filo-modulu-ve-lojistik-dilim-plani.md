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
