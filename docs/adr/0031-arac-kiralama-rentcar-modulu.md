# ADR-0031 — Araç kiralama (rentcar) modülü

Durum: **taslak** (4 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz. Rentcar iş kuralları, mevzuat ve entegrasyon ayrıntıları bir rentcar işletmesiyle **doğrulanmamıştır**; bölüm 9'da listelidir. Doğrulanmamış kural zorunlu üretim kuralı yapılmaz.

Numara notu: `docs/adr/` altında 0030 numarası şu an iki belgede kullanılıyor (kayıt türüne göre kapsam ve içe aktarma günlüğü taslağı); depo/antrepo taslağı PR #166'da 0032'ye taşındı. Bu belge 0031'i aldı; kalan 0030 çakışması için yeniden numaralandırma teknik borçtur (yol haritası, teknik borç maddesi).

İlgili: ADR-0006 (ortak firma/kişi), ADR-0008 (şirket ve şube kapsamı), ADR-0016 (lookup), ADR-0019 (teklif; `IAcceptedQuoteSource`), ADR-0020 (tek veritabanı), ADR-0021 (form düzeni), ADR-0022 (sözleşme), ADR-0027 (kural altyapısı), ADR-0028 (lisans), ADR-0029 (sektör/faaliyet modeli ve modül profili); yol haritası K5, K7, K15, Ç18a (tarife), Ç20.

## 1. Bağlam

Araç kiralama sektörü Modulix'e alınırsa iki seçenek var: CRM'i sektöre göre özelleştirmek ya da CRM'in yanına ayrı bir modül koymak. Karar kod okumasına dayanır:

| Konu | Bugünkü durum (kanıt) | Rentcar için sonuç |
|---|---|---|
| Özel alan | 7 tip, kayıt türü başına en çok 30 alan; lookup tipi ve koşullu zorunluluk yok (`Core/Customization/CustomField.cs`) | Kendi yaşam döngüsü olan araç kaydı özel alanla kurulamaz |
| Katalog | `Product`: kod, ad, birim, tek seferlik/dönemsel ücret, satış başlangıç/bitişi; stok ve tahsis alanı yok (`Crm/Domain/CatalogEntities.cs`) | Araç katalog kalemi değil, tekil kayıttır |
| Sektör profili | Yalnızca alan, liste değeri ve modül ekler (`Api/Customization/SectorProfiles.cs`; ADR-0029 "yalnızca ekler" ilkesi) | Davranış, tablo ya da durum makinesi ekleyemez |
| Takvim | Aktivite varlıkları bağlantı ve katılımcı taşır (`ActivityEntities.cs`) | Kaynak (araç) takvimi ve çakışma engeli **bulunamadı** |
| Teklif → sipariş/sözleşme | `IAcceptedQuoteSource` ile kabul edilmiş teklif okunur (`Core.Contracts/Sales/AcceptedQuote.cs`) | Satış kısmı için yeniden kullanılabilir |
| Tahsilat, depozito, fatura | Teklif ve sözleşmede serbest metin "ödeme koşulu" dışında **bulunamadı** (kodda arandı) | Boşluk; kapsam kararı gerekir (K8) |
| Lojistik | İskelet; `logistics.vehicle` otomasyon varlığı ve "sonraki bakım tarihi" alanı beyan edilmiş, kayıt deposu yok (`LogisticsModule.cs`) | Araç sahipliği çakışması riski (K3) |

`CLAUDE.md`: "Core sektöre özel iş mantığı içermez", "kodda `if sektor == ...` yasak". CRM'e rentcar mantığı yazmak bu kuralları bozar.

Kısıtlar (değişmez): modüller yalnızca `Modulix.Core.Contracts`'a başvurur ve birbirinin tablosuna erişmez; tek paylaşımlı veritabanı, `TenantId` + EF filtresi + RLS; her tenant entity'si `ITenantEntity` ile `ICompanyShared`/`ICompanyScoped` uygular ve `TenantIsolationSql.AddTable` ile RLS'e eklenir; yazmalar kayıt boru hattından geçer; yeni özellik dört testle teslim edilir.

## 2. Kararlar

**K1 — Ayrı modül; CRM'e rentcar kodu girmez.** `Modulix.Modules.Rentcar` (anahtar `rentcar`, tablo şeması `rentcar`) filo, tarife, rezervasyon, teslim/iade ve araç belgelerini taşır. CRM değişmez; müşteri, lead, fırsat, teklif, pazarlama ve destek akışları mevcut modüllerde kalır. Yeni proje eklenince `ProjectReferenceRulesTests.AllowedReferences` güncellenir.

**K2 — CRM isteğe bağlıdır, rentcar CRM'e zorunlu bağlanmaz.** Yalnızca rentcar lisanslı tenant CRM olmadan çalışabilir (yol haritası Ç20.2). Müşteri Core `Party`/`Person` kaydıdır (ADR-0006); rezervasyon `PartyId`/`PersonId` taşır. CRM açıksa kabul edilmiş teklif rezervasyona dönüşebilir (K9); CRM kapalıyken rezervasyon elle ya da API ile açılır.

**K3 — Araç kimliği rentcar'dadır; ortak filo sonraya bırakılır.** `Vehicle` ilk sürümde `rentcar` şemasındadır. Lojistik de araç kullanacaksa ortak bir Filo modülü (araç kimliği, belge, bakım) çıkarılabilir; bu ancak ikinci modül gerçekten ihtiyaç duyunca ve ADR-0029 / yol haritasının "ortaklaştırma" sorularına (anlam aynı mı, veri sahibi tek mi, bağımlılık döngüsü yok mu) olumlu cevapla yapılır. Bugün erken ortaklaştırma yapılmaz. Lojistiğin beyan ettiği `logistics.vehicle` otomasyon varlığına dokunulmaz.

**K4 — Müsaitlik ve çakışma sunucuda, veritabanı güvencesiyle.** Aynı araç aynı zaman aralığında iki aktif rezervasyona atanamaz. Uygulama denetimi tek başına yetmez (eşzamanlı istek). Öneri: rezervasyon atama işlemi tek transaction'da aracın satırını kilitler (`UPDLOCK`), çakışan aktif rezervasyon var mı bakar, sonra yazar; benzersizlik/aralık kuralı `TenantId` ile birlikte düşünülür. Alternatif (araç-gün tahsis satırı + benzersiz dizin) saat bazlı kiralamada yetersiz kalır (bölüm 6). Sınıf bazlı rezervasyon (belirli araç sonra atanır) ayrı durumdur: sınıf kapasitesi sayılır, atama teslimde yapılır. **Bu karar eşzamanlılık testi olmadan kabul edilmez.**

**K5 — Rezervasyon yaşam döngüsü durum makinesidir.** Taslak → Onaylı → Teslim edildi → İade edildi → Kapandı; yan durumlar İptal ve Gelmedi. Geçişleri sunucu doğrular (Orders'taki `OrderTransitions` kalıbı). Durum değişimi sıradan alan güncellemesi değildir; her geçiş denetim kaydına ve outbox'a yazılır. Geçiş koşulları (ör. teslim için ehliyet/depozito kontrolü) ADR-0029 alan kuralı katmanı gelince kural olarak ifade edilir; o zamana dek modül kodunda sabit asgari denetim vardır.

**K6 — Teslim ve iade ayrı kayıtlardır.** `CheckOut` (teslim) ve `CheckIn` (iade) km, yakıt seviyesi, durum notu, fotoğraf/imza referansı ve hasar işaretlerini taşır; teslim edilmiş kayıt değişmez, düzeltme yeni kayıtla yapılır (Orders devir kaydındaki "kabul edilen değişmez" ilkesiyle uyumlu). Fotoğraf ve imza için genel dosya deposu henüz yoktur (yol haritası Ç4); ilk sürümde yalnızca referans alanı ve metin notu vardır. Saha (mobil/offline) ekranı ayrı epic'tir (Ç16).

**K7 — Tarife modül içinde, sürümlü ve açıklanabilir.** Tarife sektör hesap kuralıdır ve sahibi modüldür (yol haritası K7). `Rate` + `RateRule`: araç sınıfı, süre kademesi (günlük/haftalık/aylık), sezon aralığı, km sınırı ve aşım ücreti, ek hizmetler (ek sürücü, sigorta, çocuk koltuğu), gecikme ve hasar kalemleri. Kurallar: geçerlilik aralığı, öncelik, sürüm; eşit öncelikli çakışan tarife sessizce seçilmez, önizleme hangi tarifenin kullanıldığını gösterir (Ç18a.2 kabul ölçütü). Ortak tarife altyapısı (Ç18a) gelirse rentcar tarifesi ona taşınır; **bekleme kararı açık soru 3'tedir.** Para birimi her tutarla birlikte saklanır, kur çevrimi yapılmaz.

**K8 — Ödeme ve depozito: ilk sürümde yalnızca kayıt.** Platformda tahsilat altyapısı yoktur. İlk sürüm `Deposit` ve ödeme kaydını **bilgi amaçlı** tutar (tutar, yöntem, durum: alındı/iade edildi/bloke/tahsil edildi); sanal POS, banka ve e-fatura/e-arşiv entegrasyonu yapılmaz ve "çalışıyormuş gibi" gösterilmez. Gerçek tahsilat ayrı karar ve ayrı epic'tir (açık soru 2). Depozito ve araç maliyeti hassas alandır (`rentcar.costs.read`, `IModule.SensitiveFields`).

**K9 — CRM entegrasyonu sözleşme ve olay üzerinden.** Kabul edilmiş teklif → rezervasyon dönüşümü `IAcceptedQuoteSource` sözleşmesi ve domain event ile yapılır; rentcar CRM tablosuna dokunmaz. Teklif kalemi ile araç sınıfı/tarife eşlemesi rentcar ayarıdır. Müşteri kartında kiralama geçmişi, CRM'in başka modül özetlerini gösterdiği mevcut mekanizma (varsa) ya da sonradan eklenecek özet sözleşmesiyle gösterilir; ilk sürümde zorunlu değildir.

**K10 — Kiralama sözleşmesi belgesi: modül kendi şablonunu taşır, genel Sözleşme modülüne bağlanma sonra.** Rezervasyondan üretilen kiralama sözleşmesi/teslim formu PDF'i rentcar içindedir. Genel Sözleşme modülüyle (ADR-0022) birleşme ya da bağlama ayrı karardır (açık soru 4); bu ADR'de bağımlılık yoktur.

**K11 — Mevcut platform yetenekleri aynen kullanılır.** `IModule` üzerinden: izinler, rol şablonları, `AuditedEntities`, `Settings` (rezervasyon numaralama, tampon süre, gecikme toleransı), `SensitiveFields`, `CustomizableEntities` (araç, rezervasyon), `ChoiceLists` (araç durumu, hasar türü, ek hizmet), `Lookups` (araç sınıfı, şube), `Forms` (ADR-0021; çok sekmeli varsayılan düzen birim testle denetlenir), `Automation` (bakım/belge tarihi, gecikmiş iade, ADR-0027) ve `Reports` (doluluk, araç başına gelir; ADR-0026). Arayüz, `docs/design-system/` kurallarına uyar (sekmeli formlar, `record-table`, `ViewSelector`).

**K12 — Sektör profili.** `rentcar` profili modül kombinasyonunu ve varsayılan form/rol/liste değerlerini seçer; veri silosu açmaz ve lisans/izin yükseltmez (yol haritası Y9.3 kabul ölçütü). Faaliyet ağacına bağlanma (araç kiralama faaliyeti, hazır-modül göstergesi) ADR-0029 kabul edilince yapılır; o zamana dek profil kodda tanımlanır. Modül kodunda `if sektor == ...` yoktur.

## 3. Veri modeli taslağı

Tablolar `rentcar` şemasında, hepsi `TenantId` taşır ve RLS politikasına eklenir. Kapsam sınıfı önerisi (ADR-0008): araç ve rezervasyon `ICompanyScoped` (şube/şirket); sınıf, tarife ve katalog benzeri tanımlar `ICompanyShared`. `MembershipId` taşıyan alanlar `platform.Memberships`'e bileşik yabancı anahtarla bağlanır.

| Varlık | İçerik (özet) | Benzersizlik / not |
|---|---|---|
| `VehicleClass` | ad, sınıf kodu, varsayılan tarife | `UNIQUE(TenantId, Code)` |
| `Vehicle` | plaka, şasi no, marka/model/yıl, sınıf, yakıt, vites, mevcut km, durum, şube | `UNIQUE(TenantId, Plate)`, `UNIQUE(TenantId, Vin)` |
| `VehicleDocument` | tür (ruhsat, sigorta, kasko, muayene…), no, başlangıç/bitiş, belge referansı | bitiş tarihi otomasyon alanı |
| `Rate`, `RateRule`, `RateSurcharge` | K7 | sürüm, geçerlilik, öncelik |
| `Reservation` | numara, müşteri (`PartyId`/`PersonId`), araç ya da sınıf, alış/dönüş zamanı ve yeri, durum, toplam, kaynak (elle/CRM teklifi/API) | `UNIQUE(TenantId, Number)`; çakışma güvencesi K4 |
| `ReservationExtra` | ek hizmet satırı ve ücret kopyası | tarife değişse de rezervasyon fiyatı kopyadır |
| `CheckOut`, `CheckIn` | zaman, km, yakıt, not, imza/fotoğraf referansı, teslim eden | rezervasyon başına en çok bir teslim ve iade; değişmez |
| `DamageRecord` | araç, rezervasyon, tür, konum, açıklama, tahmini/gerçek bedel, durum | hasar bedeli hassas |
| `MaintenanceRecord` | tür, tarih, km, maliyet, servis, sonraki bakım | maliyet hassas |
| `TrafficFine`, `TollCharge` | tarih, araç, rezervasyon eşleşmesi, tutar, durum | otomatik eşleme ve yansıtma ayrı karar |
| `Deposit`, `PaymentRecord` | K8 | bilgi amaçlı |

İzinler `rentcar.<kaynak>.read|write` kalıbında: `vehicles`, `classes`, `rates`, `reservations`, `handovers`, `damages`, `maintenance`, `deposits`, `costs`. Rol şablonları (öneri): Rezervasyon temsilcisi, Şube görevlisi (teslim/iade), Filo yöneticisi, Rentcar yöneticisi.

## 4. Dilimler

| Dilim | İçerik | Bağımlılık |
|---|---|---|
| A | ADR kabulü, modül iskeleti (izin, rol şablonu, boş kayıt depoları, mimari test tablosu, lisans anahtarı) | — |
| B | Filo: araç, sınıf, araç belgeleri, durum, bakım kaydı; liste ve sekmeli form | A |
| C | Tarife + rezervasyon + çakışma güvencesi (K4) + önizleme | B; K4 eşzamanlılık testi |
| D | Teslim/iade, hasar, depozito kaydı, kiralama belgesi PDF'i | C |
| E | CRM entegrasyonu: kabul edilmiş teklif → rezervasyon; müşteri kartında özet | C; ADR-0019 sözleşmesi |
| F | Raporlar, otomasyon (belge/bakım/gecikmiş iade), panel göstergeleri | B–D; ADR-0026/0027 |
| G | Sektör profili ve rentcar kurulum paketi | ADR-0029 kabulü |
| H | Gerçek tahsilat, e-belge, ceza/geçiş sorgusu, saha ekranı | Ayrı epic ve ADR'ler |

Her dilim dört testle teslim edilir: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki. Dilim C ek olarak eşzamanlı rezervasyon ve çakışma testi ister. Her adımda `dotnet build` ve mimari testler çalıştırılır; entegrasyon testleri SQL Server ve Mailpit ister.

## 5. Riskler

- **Çakışma güvencesi** (K4) yanlış kurulursa çifte rezervasyon olur; en yüksek teknik risk. Kilit kapsamı geniş tutulursa yoğun şubede bekleme oluşur.
- **Tarife karmaşıklığı** hızla büyür (sezon, kampanya, kurumsal anlaşma fiyatı). Ortak tarife altyapısını beklemek rentcar'ı geciktirir, beklememek iki tarife motoru bırakır.
- **Ödeme boşluğu**: kullanıcı depozito/ödeme ekranını gerçek tahsilat sanabilir; arayüzde "kayıt" olduğu açık yazılmalıdır.
- **Araç sahipliği**: lojistik ve rentcar iki ayrı araç kaydı üretirse sonradan birleştirmek pahalıdır (K3).
- **Mevzuat ve kimlik verisi**: ehliyet, kimlik no ve ödeme bilgisi kişisel veridir; saklama, maskeleme ve KVKK uyumu bu ADR'de çözülmedi.
- **Sektör profili bağımlılığı**: ADR-0029 henüz taslaktır; G dilimi onun kabulüne bağlıdır.
- **Eşzamanlı düzenleme ve tampon süre**: iade ile sonraki teslim arasındaki temizlik/bakım tamponu rezervasyon çakışmasına dahil edilmeli (ayar).

## 6. Reddedilen seçenekler

- **Yalnızca CRM'i özelleştirmek** (araç = özel alanlı kayıt ya da `Product`): müsaitlik, durum makinesi ve tarife CRM'e sızar; `CLAUDE.md` kuralları bozulur; 30 özel alan sınırı ve lookup/koşul eksikliği engel olur.
- **Araç için CRM `Product`'ı genişletmek**: katalog kalemi tekil kaydı temsil etmez; stok/tahsis modeli CRM'de yoktur ve eklemek CRM'i sektöre bağlar.
- **Her sektör için ayrı CRM kopyası**: ADR-0029'da reddedildi.
- **Araç-gün tahsis tablosu ile çakışma engeli**: gün bazlı kiralamada basit ve güvenlidir, saat bazlı kiralamada yetersizdir. Yalnızca saatsiz (tam gün) kiralama garanti edilirse yeniden değerlendirilir.
- **Başlangıçta ortak Filo modülü**: ikinci tüketici henüz yok; yol haritasının ortaklaştırma kriterleri sağlanmadı.
- **Gerçek tahsilatı ilk sürüme almak**: platformda ödeme altyapısı yok; kapsamı belirsiz ve mevzuat yükü yüksek.

## 7. Açık sorular

1. Hedef müşteri kim: küçük/orta rentcar işletmeleri mi (bireysel, kısa dönem), kurumsal filo kiralama mı (uzun dönem, teklif/sözleşme ağırlıklı)? Dilim sırasını değiştirir.
2. Ödeme/depozito: ilk sürümde yalnızca kayıt mı, sanal POS ve e-fatura entegrasyonu şart mı?
3. Tarife: ortak tarife altyapısı (Ç18a) beklensin mi, rentcar kendi tarifesini yazıp sonra taşısın mı?
4. Kiralama sözleşmesi: modül içi PDF yeterli mi, genel Sözleşme modülüne (ADR-0022) bağlanmalı mı?
5. Araç sahipliği: Lojistik ile ortak Filo modülü ne zaman gündeme alınsın?
6. Rezervasyon granülü: saat bazlı mı, yalnızca gün bazlı mı? K4 seçimini belirler.
7. Çoklu şube ve tek yönlü kiralama (farklı şubede iade) ilk sürümde gerekli mi?
8. Müşteri portalı/web rezervasyon: yol haritasındaki portal epic'ine mi bağlanır? Bugün genel portal yoktur.

## 8. Başlangıç öncesi koşullar

- Bir rentcar işletmesiyle süreç ve terim doğrulaması (pilot); lojistikteki Makaroğlu pilotuna benzer.
- Açık sorular 1, 2 ve 6'nın cevabı (kapsamı ve K4'ü belirler).
- K4 için eşzamanlılık prototipi ve testi.
- `ProjectReferenceRulesTests.AllowedReferences` ve `TableInventoryTests` güncellemesi dilim A'da yapılır.

## 9. Doğrulanmadı

- Rentcar iş akışı, terimler, tarife yapısı ve alan listesi bir işletmeyle doğrulanmadı; veri modeli bölüm 3 öneridir.
- Kiralama mevzuatı, ehliyet/kimlik saklama yükümlülükleri, KVKK, e-belge ve trafik cezası/geçiş sistemi sorgusu (HGS/OGS) doğrulanmadı.
- Takvimde kaynak çakışması ve ödeme altyapısının yokluğu kodda arama ile bulunamadı; kapsamlı denetim yapılmadı.
- `Core.Contracts`'ta müşteri kartına modül özeti gösteren hazır bir mekanizmanın varlığı doğrulanmadı (K9).
- K4'teki kilit yaklaşımı denenmedi.
- Hiçbir test çalıştırılmadı; bu belge yalnızca dokümandır.
