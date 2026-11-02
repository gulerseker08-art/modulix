# ADR-0025 — Kampanya ve segment yönetimi (EPIC 12)

Durum: kabul edildi (2 Kasım 2026). Uygulama dilimlerle ilerler; her dilimin durumu bölüm 9'dadır. Yalnızca testle doğrulananlar "yapıldı" yazılır.

## 1. Bağlam ve amaç

Akış: müşteri verisi → segment → hedef kitle → uygunluk kontrolü → kampanya uygulaması → etkileşim/yanıt → lead/fırsat → ticari sonuç → performans.
Segment üyeliği gönderim izni vermez; açılma/tıklama satış kanıtı değildir; iletişim noktası pazarlama izni değildir (ADR-0012).

Bu belge isteğin (Kampanya ve Segment epic'i) depoya göre analizidir: ne var, ne yok, nerede duracak, hangi sırayla yapılacak. Tenant başına ayrı veritabanı yoktur (ADR-0020); tek paylaşımlı `Modulix` veritabanı, `TenantId` + EF filtresi + RLS.

## 2. Mevcut durum ve boşluk matrisi (kod kanıtıyla)

| Gereksinim | Durum | Kanıt | Yapılacak | Dilim |
|---|---|---|---|---|
| Segment / liste | **Yok** | `grep` yalnızca `PriceList.Segment` (fiyat listesinde metin eşleşmesi, `PricingEntities.cs:68`) ve destek yönlendirmesinde "segment kuralı yok" notu (ADR-0024 §4.7) bulur | Pazarlama modülü: segment, sürüm, statik üye, koşul motoru | 2 |
| Kampanya modeli | **Yok** | ADR-0014 "CampaignId yok"; `Lead.UtmCampaign` serbest metin | Kampanya, hedef kitle, çalıştırma, anlık görüntü | 4–5 |
| İzin / rıza / engel | **Yok** | ADR-0012 satır 38, ADR-0014 satır 88, README "İzin/rıza altyapısı henüz yoktur" | Çekirdekte ortak tercih + engel kaydı | **1** |
| İletişim noktası | Var, izin değil | `crm.ContactPoint` (`NormalizedValue`, `IsShared`, `IsActive`) | Yalnızca kaynak; izin ayrı kayıt | 1 |
| Kaynak / UTM | Kısmi | `Lead.Source/SourceDetail/Referrer/ExternalSource/UtmSource/UtmMedium/UtmCampaign`, `Company.Source`, `Opportunity.Source` | İlk edinim kaynağı ile dokunuş ayrımı, kampanya referansı | 6–7 |
| Gönderim altyapısı | Kısmi | `IEmailQueue` (tek kanal, SMTP, yeniden deneme); SMS/WhatsApp, sağlayıcı soyutlaması, webhook yok | Kanal adaptörü sözleşmesi + sahte sağlayıcı; gerçek sağlayıcı bağlanmaz | 5 |
| Zamanlanmış iş | Var (örüntü) | `SupportJobsWorker`, `ContractJobsWorker`, `OrderJobsWorker`; `ITransactionLock` | Pazarlama işçisi aynı örüntüyle | 5 |
| Lead / fırsat / aktivite | Var | `Lead`, `Opportunity`, `Activity` ve iş akışları | Yanıttan bağlama (mevcut servisleri çağırır) | 6 |
| Sektör, faaliyet, bölge, büyüklük | Kısmi | `Company.Industry` serbest metin (100), `Party.City`; bölge, ülke (firma), büyüklük **yok** | Olan alanlar segmentte sunulur; olmayan alan çalışıyormuş gibi görünmez | 2 |
| Sözleşme bitişi | Var (başka modül) | `Contract.EndDate`, `Contract.PartyId` | `Contracts` alan kaynağı | 2b |
| Ürün/hizmet sahipliği, lisans durumu | Doğrulanamadı / yok | Sipariş-sözleşme kalemleri ile eşleme yapılmadı; modül hakkı `platform.ModuleEntitlements` tenant'a özgü | İlk sürümde "ilgilenilen ürün" alanı; sahiplik alanı yok | ileri |
| Saat dilimi | Var | `RegionalTime`, `RegionalOptions` | Gün sınırları tenant saat diliminde | 2 |
| Dışa aktarma | Kısmi | Destek talepleri CSV'si var; formül enjeksiyonu koruması ortak değil | Üye dışa aktarma kendi korumasıyla | 3 |
| Onay akışı | Doğrulanamadı | `Core.Contracts/Approvals/ApprovalFlow.cs` var, kampanya kullanımı incelenmedi | Dilim 5'te incelenir | 5 |
| Webhook / sağlayıcı geri çağrısı | **Yok** | Ç11a.4 yol haritasında açık iş | İmzalı geri çağrı sözleşmesi + sahte sağlayıcı testi | 5–6 |
| Raporlama | Kısmi | `SupportReports`, `OpportunityReport` örüntüsü | Pazarlama raporları | 7 |

## 3. Mimari kararlar

**K1 — Yeni modül: `marketing`** (`Modulix.Modules.Marketing`, şema `marketing`, tek `IModule`). CRM'den ayrı çünkü (a) senaryo 5: CRM kapalı tenant'ta da ortak kayıtlar kullanılabilmeli, (b) Core sektöre/CRM'e özgü iş mantığı taşımaz, (c) gelecekte bağımsız paketlenebilir. CRM'e yalnızca **dar bir alan kaynağı sözleşmesiyle** dokunur; CRM tablolarına referans vermez.

**K2 — Ortak iletişim tercihi çekirdektedir** (`core` şeması): `CommunicationPreference` (kanal + normalize adres + amaç → izin durumu) ve `CommunicationSuppression` (engel). Pazarlama, Destek, İK gibi modüller aynı otoriteyi `ICommunicationPreferences` ile **okur**; kampanya modülü tercihin sahibi değildir. Tercih kimliği kişiye değil **iletişim noktasına** bağlıdır (kanal + normalize adres); kişi/taraf isteğe bağlı referanstır. Böylece CRM kapalıyken de çalışır ve aynı adresi paylaşan kayıtlar için "izin belirsiz" doğru modellenir.

**K3 — Alan kaynağı sözleşmesi** (`Core.Contracts/Marketing/ISegmentFieldSource`): modül, kendi tablolarından `IQueryable<Guid>` (eşleşen hedef kimlikleri) döndüren alan tanımları kaydeder. Segment motoru koşul ağacını küme cebrine çevirir (AND = kesişim, OR = birleşim, ilişki var/yok = `EXISTS`). Tek `DbContext` olduğundan alt sorgular tek SQL'e birleşir; modül sınırı korunur (CRM kendi `IModuleData`'sını kendi `Records` klasöründe kullanır). Hedef kimlikleri: firma → `PartyId`, kişi → `PersonId`, lead → `LeadId`. Join çoğalması imkânsızdır çünkü sonuç kimlik kümesidir.

**K4 — Koşul ağacı tip güvenli ve izinli**: kullanıcı SQL/kod yazmaz; `SegmentCondition` ağacı (AND/OR/koşul) kayıtlı alanlara, tipiyle uyumlu operatörlere ve parametreli değerlere indirgenir. Derinlik (≤4), koşul sayısı (≤25) sınırlıdır. Alanı görme izni (alan izni) olmayan kişi o alanla filtreleyemez, sayı alamaz, önizleyemez. Kayıt kapsamı (kendi/ekip/hepsi) sayıya ve önizlemeye uygulanır.

**K5 — Sürümlü tanım**: yayımlanan koşul sürümü değişmez; kampanya sürüm numarasına bağlanır (dilim 4). Sonradan segment değişse geçmiş kampanya hedefi yeniden hesaplanmaz (anlık görüntü).

**K6 — Dinamik üyelik hesaplanır, satır olarak saklanmaz** (ilk sürüm). Modüller kaydetmez (mimari test); 100.000 üyelik satırını kayıt boru hattıyla kişi kişi denetim kaydına yazmak anlamsızdır. Segment: son hesaplama zamanı, üye sayısı, süre, hata. Üye *değişimi* (eklenen/çıkan) bu yüzden ilk sürümde yok; ihtiyaç doğarsa toplu yazan bir altyapı servisi ayrı karardır. Kampanya çalıştırması hedefi **dondurur** (dilim 4); donmuş kayıt tarihsel doğruluğu sağlar. Statik segment üyeleri ve dahil/hariç istisnalar satırdır (kayıt boru hattından, denetimli).

**K7 — Uygunluk gönderimden hemen önce yeniden değerlendirilir**: anlık görüntü izni bypass edemez. Değerlendirme sırası: alıcı çözümlenebilir mi → iletişim noktası etkin mi → engel var mı → amaç için izin `Granted` mı → sıklık sınırı (atomik rezervasyon) → kanal sağlayıcısı yapılandırılmış mı. İzni bilinmeyen (`Unknown`) alıcıya pazarlama gönderilmez. Nedenler sayılır ve gösterilir.

**K8 — Gerçek gönderim kapalı**: yapılandırılmış sağlayıcı yoksa çalıştırma "engelli" olur; sahte sağlayıcı yalnızca test ve geliştirmede kullanılır. CI, seed ve migration dış ileti göndermez (sahte sağlayıcı bellekte kaydeder).

**K9 — Hukuki sınır**: Bu belge KVKK/İYS/ETK uyumluluk sertifikası değildir. Kod; izin kaydını, kanıtı, kaynağı, zamanı ve geri alınmayı tutar. Hangi hukuki dayanağın ve hangi İYS entegrasyonunun gerektiği **ürün/hukuk kararıdır** ve açık işler listesindedir (bölüm 8).

## 4. Çekirdek iletişim tercihi (dilim 1)

- **Kanal**: `Email`, `Phone`, `Sms`, `WhatsApp`. **Amaç**: `Marketing`, `Transactional`, `Service`. İşlem bildirimi ayrı amaçtır; pazarlama izni için `Transactional` seçilerek aşılamaz (sunucu, pazarlama gönderimini yalnızca `Marketing` amacıyla değerlendirir).
- **Durum**: `Unknown` (varsayılan; kayıt yoksa da budur), `Granted`, `Denied`. Firma/e-posta bulunması izin değildir.
- **Adres normalizasyonu**: e-posta → küçük harf + kırpma (yerel kısım aksan/nokta birleştirmesi yapılmaz); telefon → yalnızca rakamlar, Türkiye için `+90`/`0` önekleri `90…` biçimine indirgenir. Geçersiz adres reddedilir.
- **Olay geçmişi**: her değişiklik değişmez satırdır (`CommunicationConsentEvent`: kim/sistem, kaynak, zaman, kanıt referansı, gerekçe). Güncel durum `CommunicationPreference`'ta; geçmiş olaylardadır. İzin kaydı kanıt referansı veya gerekçe olmadan `Granted` yapılamaz.
- **Engel (suppression)**: nedenler `HardBounce`, `Complaint`, `InvalidAddress`, `OptOut`, `Manual`; geçerlilik başlangıç/bitiş. Engel, izin durumunu **ezer**; içe aktarma veya segment değişimi engeli silmez. Engel kaldırma ayrı izin ve gerekçe ister, olay satırı bırakır.
- **Soft bounce**: sayaç + eşik; eşik aşılınca engele dönüşür. Teslim olayları olmadan anlamsız olduğundan dilim 5'te yapılır (dilim 1'de yok).
- **Okuma sözleşmesi**: `ICommunicationPreferences.EvaluateAsync(channel, purpose, address)` → `Allowed` | `Denied(neden)` | `Unknown`. Aynı adres birden fazla kişiye bağlıysa karar adres düzeyindedir (belirsiz izin otomatik izin sayılmaz).
- **İzinler** (çekirdek): `core.communication.read`, `core.communication.manage` (izin/ret kaydeder), `core.communication.suppress` (engel ekler/kaldırır). Yetki sunucudadır.

## 5. Pazarlama modülü — segment (dilim 2)

Varlıklar (`marketing` şeması, hepsi `ITenantEntity` + `ICompanyShared`, RLS): `Segment` (ad, kod, açıklama, hedef türü Company/Person/Lead, tür Static/Dynamic, amaç, sorumlu, durum Draft/Active/Archived, güncel yayın sürümü), `SegmentVersion` (koşul JSON, sürüm no, yayın zamanı/yapan; yalnızca yayım eylemiyle oluşur), `SegmentMember` (statik: hedef kimliği, Include/Exclude, gerekçe), `SegmentComputation` (hesaplama geçmişi: zaman, sayı, süre, durum, hata).

Kurallar: kod tenant içinde tekil; statik segmentte aynı hedef iki kez eklenemez; yayımlanmış sürüm değişmez; arşivlenen segment geçmişte kalır ve yeni kampanyada seçilemez; hedef türü sonradan değişmez; karışık tür tek segmentte yoktur.

Koşul operatörleri: `eq`, `ne`, `contains`, `in`, `between`, `empty`, `notEmpty`, `lastDays` (tenant saat dilimi gün sınırı), `before`, `after`, `exists`, `notExists`. NULL: `eq/contains/in/between` boş değerle eşleşmez; `ne` boş değeri de **kapsar** ("X olmayan" = X'e eşit olmayan ya da boş) — arayüzde yazılır.

CRM'in sunduğu alanlar (yalnızca olanlar): firma — ad, şehir, sektör (serbest metin), durum, kaynak, etiket, sorumlu, oluşturma tarihi, açık/kazanılmış fırsat var-yok, son aktivite tarihi; kişi — unvan, firma alanları; lead — durum, kaynak, ülke, şehir, sektör, UTM kampanyası, oluşturma tarihi. Sözleşme bitişi `contracts` kaynağıyla (2b). Olmayan alanlar (firma büyüklüğü, bölge, ürün sahipliği) listelenmez ve nedeni "kaynak yok" olarak gösterilir.

## 6. Sonraki dilimler (taslak sözleşme)

- **Dilim 3**: segment ekranları (liste, düzenleyici, önizleme, üyeler), dışa aktarma.
- **Dilim 4**: kampanya, hedef kitle (dahil/hariç), alıcı çözümleme (kişi seçme kuralı), anlık görüntü, sayı ayrımı ve elenme nedenleri.
- **Dilim 5**: içerik sürümü, onay, planlama, çalıştırma/teslim (idempotency anahtarı), sahte sağlayıcı, sıklık rezervasyonu, duraklat/iptal, abonelikten çıkma uç noktası.
- **Dilim 6**: yanıt çalışma alanı, kaynak/UTM, yanıttan aktivite/lead/fırsat.
- **Dilim 7**: maliyet, attribution (ilk/son dokunuş), raporlar, müşteri kartı sekmeleri.
- **Dilim 8**: kabul/regresyon, performans ölçümü (100.000 sentetik kişi), işletim notu.

## 7. Veri, silme ve gizlilik

Anlık görüntü yalnızca hedef kimliğini, kaynak segment sürümünü ve seçilme/elenme nedenini saklar; ad/e-posta kopyalanmaz. Kişi silinir veya anonimleştirilirse kimlik referansı kalır, ayrıntı çözülemez; raporlar sayıları korur. Tercih kaydı adres taşıdığı için kişi silme/anonimleştirme sürecinde adresin bu tabloda da temizlenmesi ayrı iştir (açık işler).

## 8. Açık işler ve dürüstlük sınırları

- Gerçek e-posta/SMS/WhatsApp sağlayıcısı bağlanmadı; bağlanana kadar çalıştırma bloklu.
- İYS, KVKK aydınlatma/rıza metni sürümü, tek tıkla abonelikten çıkma başlığı (RFC 8058) için hukuki karar ve entegrasyon bekliyor.
- Dinamik segment üye değişimi (eklenen/çıkan) ve zamanlanmış otomatik yenileme: sistem yetki bağlamı kararı bekliyor.
- Firma büyüklüğü, bölge, ürün sahipliği alanları kaynak veri olmadığı için yok.
- Modülix'in kendi satış CRM'i senaryosu için lisans durumu projeksiyonu yok; "ilgilenilen ürün" alanıyla çalışır.

## 9. Uygulama durumu

| Dilim | Durum |
|---|---|
| 0 — Analiz ve ADR | Yapıldı (bu belge) |
| 1 — Çekirdek iletişim tercihi ve engel (arka uç, API, denetim, RLS, 9 entegrasyon + 43 birim testi) | Yapıldı; ekran yok (dilim 3'te segment ekranlarıyla) |
| 2 — Pazarlama modülü, segment motoru (arka uç) | Planlı |
| 3–8 | Planlı |
