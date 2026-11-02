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
| 2 — Pazarlama modülü, segment motoru (arka uç) | Yapıldı; ayrıntı aşağıda |
| 3 — Segment ekranları (liste, detay, koşul düzenleyici, önizleme, üyeler, sürümler) | Yapıldı; ayrıntı aşağıda |
| 4 — Kampanya, hedef kitle, alıcı çözümleme, uygunluk, anlık görüntü (arka uç ve ekranlar) | Yapıldı; ayrıntı aşağıda |
| 5 — İçerik, onay, çalıştırma, gönderim çekirdeği (test sağlayıcısı) | Yapıldı; abonelikten çıkma, otomatik işçi, gerçek sağlayıcı yok (aşağıda) |
| 6–8 | Planlı (abonelikten çıkma ve tercih merkezi dilim 6'ya eklendi) |

### Dilim 2 — uygulama notları (sapmalar ve sınırlar)

- **Yapılan**: `marketing` modülü (`Segment`, `SegmentVersion`, `SegmentMember`; migration `MarketingSegments`, RLS, şema izni), koşul ağacı (`SegmentConditions`: ayrıştırma, derinlik ≤4, koşul ≤25, tip/operatör/değer doğrulaması), motor (`SegmentEngine`: kimlik kümeleri üzerinde kesişim/birleşim, `(koşul ∪ dahil) ∖ hariç`, 30 sn süre sınırı), `ISegmentFieldSource` sözleşmesi ve CRM kaynağı (firma 11, kişi 3, lead 7 alan), `ITenantClock` (gün sınırları tenant saat diliminde), yayım/yenile/arşiv/geri al/kopya eylemleri, `/api/marketing` uç noktaları, dört izin ve üç rol şablonu.
- **SegmentComputation tablosu yapılmadı**: hesaplama geçmişi yerine segmentte yalnızca son hesaplama özeti (zaman, sayı, süre, hata) tutulur. Hesaplama geçmişi ve üye değişimi (eklenen/çıkan) bu yüzden yoktur (K6).
- **Yetki**: alanı görme, alanın ait olduğu kaydı okuma izniyle (`ReadEntityKey`) ve modülün tenant için açık olmasıyla belirlenir; olmayan alan sunulmaz, nedeni `Unavailable` listesinde açıklanır, o alanla önizleme/yayım reddedilir. Sayılar çağıranın veri kapsamıyla hesaplanır. `marketing.recipients.read` ortak firma/kişi kaydını okutur; CRM okuma izni olan kişi zaten okuyabildiği için satırlar ona da görünür. Statik üyeler segmentin sorumlusunun kapsamıyla görünür, üyelerin her birinin kapsamıyla süzülmez.
- **Bilerek yok**: CRM'de bulunmayan alanlar (firma büyüklüğü, bölge, ürün sahipliği), son aktivite alanı, sözleşme bitişi (`Contracts` kaynağı dilim 2b), segmentin başka segmente başvurusu, zamanlanmış otomatik yenileme (sistem yetki bağlamı kararı bekliyor), statik segmente toplu içe aktarma/CSV, üye dışa aktarma, lead satırlarında ad (yalnızca kimlik), ekranlar (dilim 3).
- **Davranış notları**: metin karşılaştırmaları veritabanı harmanına (büyük/küçük harf duyarsız) uyar; "X olmayan" (`ne`) boş değeri de kapsar; yayımlanmış segment silinmez, arşivlenir; hedef türü, tür ve kod değişmez; yayımlanmış sürüm değişmez ve silinmez; dahil/hariç istisna aynı hedef için tekildir (hariç, dahilin önüne geçer).
- **Test**: 36 birim (koşul ağacı), 10 birim (CRM ifade ağacı: NULL, harf, gün sınırları), 9 entegrasyon (AND/OR/NULL/ilişki, sürüm değişmezliği, statik/istisna, doğrulama, yaşam döngüsü, tenant izolasyonu, alan yetkisi/kapalı modül, veri kapsamı, eylem izinleri). Performans ölçümü (100.000 sentetik kişi) yapılmadı; dilim 8.

### Dilim 3 — uygulama notları (ekranlar)

- **Yapılan**: menüde "Pazarlama → Segmentler" (modül ve `marketing.segments.read` izniyle); `/pazarlama/segmentler` (liste: durum, hedef, tür, benim segmentlerim, arama, sayfalama, son hesaplama ve hata), `/pazarlama/segmentler/yeni`, `/pazarlama/segmentler/{id}` (Genel, Koşullar, Üyeler, Sürümler sekmeleri; yayımla, yenile, arşivle/geri al, kopyala). Koşul düzenleyici (`SegmentConditionEditor`) iç içe VE/VEYA gruplarını kurar, alan listesi yalnızca çağıranın kullanabildiklerini gösterir, kullanılamayanları nedeniyle listeler; önizleme sayıyı ve ilk satırları gösterir, hata sunucu mesajıyla özetlenir. `MarketingAccess` (Shared) sabitleri modül izinleriyle testle eşitlenir. Demo seed'i `acme` için pazarlama hakkını açar.
- **Doğrulama**: tarayıcıda (yerel önizleme) oluştur → koşul kur → önizle → taslağı kaydet → yayımla → hariç tut → sürümler akışı ve hata yolu DOM üzerinden denendi; ekran görüntüsü alınamadı (pano gizliydi), bu yüzden görsel düzen, duyarlı görünüm ve koyu tema **elle gözden geçirilmedi**.
- **Bilerek yok**: üye dışa aktarma, statik segmente toplu içe aktarma, istisna listesinde hedef adı (yalnızca kimliğin son 8 karakteri; ad için ortak kayıt çözümleme servisi gerekir), Contracts/aktivite alanları, firma/kişi kartında segment sekmesi, iletişim tercihi ekranı (tercih/engel API'si hazır, ekranı yok).

### Dilim 4 — uygulama notları (kampanya, hedef kitle, anlık görüntü)

- **Yapılan**: `Campaign`, `CampaignAudienceEntry` (dahil/hariç segment ya da elle hedef), `AudienceSnapshot` (migration `MarketingCampaigns`, RLS), üç izin (`marketing.campaigns.read|write|audience`), `/api/marketing/campaigns`, `campaign-audience`, `audience-snapshots`, `POST campaigns/{id}/audience/preview|snapshot`, `archive|restore`, `GET audience-snapshots/{id}/recipients`. Ekranlar: Pazarlama → Kampanyalar (liste, yeni, detay: Genel, Hedef kitle, Dondurulmuş kitleler).
- **Alıcı çözümleme sözleşmesi** (`IRecipientResolver`, Core.Contracts): kişi ve iletişim noktası bilgisi iletişim noktalarını tutan modülündür; pazarlama CRM tablolarına dokunmaz. CRM çözümleyicisi firma hedefinde birincil irtibat, karar verici, belirli rol, tüm aktif kişiler ve firmanın kendi noktaları kurallarını, kişi hedefinde kişinin noktalarını, lead hedefinde lead'in kendi e-posta/telefonunu verir (SMS ve WhatsApp telefon noktalarını kullanır). Kural eşleşmezse rastgele kişiye gidilmez; neden (`no-matching-contact`, `no-contact-point`, `target-not-found`) hedefle birlikte sayılır.
- **Hesap sırası** (`AudienceRules`, saf ve birim testli): hedefler = dahiller ∖ hariçler → alıcı çözümleme → geçersiz adres → paylaşılan adres (otomatik gönderilmez) → aynı kişi → hedef başına sınır (birincil önce) → aynı adres → izin ve engel kararı (`ICommunicationPreferences`; bilinmeyen izin elenir). Sayılar ayrı gösterilir: hedef, alıcısı bulunan hedef, çözümlenen kişi, benzersiz iletişim noktası, uygun, elenen; nedenler iki ayrı dağılımdadır (elenen alıcı / alıcısı bulunamayan hedef).
- **Anlık görüntü**: kayıt değişmez ve silinemez; kişisel veri kopyalanmaz (yalnızca hedef, kişi, iletişim noktası kimlikleri ve kodlar; adres saklanmaz). Alıcı listesi denetim kaydına ve genel okumaya girmez (alan listesinde yok), yalnızca `recipients` uç noktasından ve `marketing.recipients.read` ile okunur. Sınırlar: en çok 20.000 hedef, 50.000 satır; aşılırsa segment daraltılır. Segment ya da izin sonradan değişse dondurulmuş kayıt değişmez; **uygunluk gönderimden önce yeniden değerlendirilecektir (dilim 5)**: bu dilimde hiçbir ileti gönderilmez.
- **Kapsam ve yetki**: sayılar çağıranın veri kapsamı ve alan yetkisiyle hesaplanır; kişi kayıtlarını okuyamayan kişi alıcıları çözemez (sayı bile almaz); CRM kapalıysa neden söylenir. Hedef kitle girdisi aynı hedef türünde ve çağıranın görebildiği segment ya da kayıt olmalı; arşivlenen segment yeni kampanyada seçilemez; etkin olmayan segment hesapta kullanılamaz.
- **Bilerek yok**: kampanya amaç kataloğu (amaç serbest metin), bütçe/KPI, kanal başına birden çok aksiyon (kampanyanın tek kanalı var; dilim 5'te aksiyon kanalı gelir), durum makinesi (onay, planlama, çalışma: dilim 5), tekrar çalıştırma, aynı kişinin farklı kanal tercihleri, Contracts/aktivite tabanlı segment alanları, lead segmentinde kişi seçimi (lead'in kendi adresi kullanılır), anlık görüntüde adres şifreleme/saklama ilkesi (adres saklanmadığı için gerekmedi), istisna listesinde hedef adı.
- **Test**: 10 birim (hedef kitle kuralları), 10 entegrasyon (hedef/alıcı ayrımı ve neden dağılımı, kişi kuralları, sınır ve bilinmeyen izin, rol/karar verici/firma noktası, hariç tutma ve engel, dondurma ve değişmezlik, doğrulama, yaşam döngüsü, tenant izolasyonu, izinler ve CRM kapalı); tarayıcıda oluştur → segment ekle → önizle → dondur → dondurulmuş sekmesi akışı DOM üzerinden denendi (görsel düzen, duyarlı görünüm ve koyu tema elle gözden geçirilmedi).

### Dilim 5 — uygulama notları (içerik, onay, çalıştırma, gönderim çekirdeği)

- **Yapılan**: `CampaignContent` (sürümlü, değişmez), `CampaignRun` (durum makinesi: Draft → PendingApproval → Approved → Running ⇄ Paused → Completed/Cancelled), `CampaignDelivery` (günlük satırı), çekirdekte `FrequencyWindow` (migration `MarketingRuns`, RLS), izinler `marketing.campaigns.approve|send`, ayarlar `marketing.provider` (varsayılan: yok), `marketing.frequency.daily-limit` (1), `marketing.approval.required` (evet), `/api/marketing/campaign-contents`, `campaign-runs` ve `POST campaign-runs/{id}/submit|approve|reject|reopen|start|pause|resume|cancel|process`, `GET …/summary|deliveries`. Ekran: kampanya detayında "İçerik ve çalıştırmalar" sekmesi.
- **Sözleşmeler** (Core.Contracts): `IMessageProvider` (kanal sağlayıcısı; sonuç Accepted/TransientFailure/PermanentFailure/Unknown), `IAddressLookup` (gönderimden önce adresi yeniden çözer; CRM uygular), `IFrequencyLimiter` (atomik günlük sınır; Infrastructure uygular), `IJournalWriter` + `IJournalEntity` (kayıt boru hattına girmeyen yüksek hacimli günlük satırı için toplu yazma; yalnızca işaretli entity yazılır, ana veri ve iş kayıtları boru hattından geçer).
- **Gerçek gönderim kapalı**: varsayılan sağlayıcı "yok"; başlatma ve işleme bloklanır, hiçbir satır açılmaz. Tek sağlayıcı `fake` (test sağlayıcısı) iletiyi hiçbir yere göndermez; adresteki `+fail-permanent`, `+fail-transient`, `+timeout` ile hata sınıfları sınanır. Gerçek e-posta/SMS/WhatsApp sağlayıcısı **yoktur**: abonelikten çıkma ve hukuki kararlar (İYS/KVKK) tamamlanmadan bağlanması sorumsuz olurdu. CI, seed ve migration ileti göndermez.
- **Onay ve görev ayrılığı**: hazırlayan kendi çalıştırmasını onaylayamaz (onay yetkisi olsa bile). İçerik, anlık görüntü ve zaman yalnızca taslakta düzenlenir; onaydan sonra değişiklik için önce gerekçeyle taslağa alınır ve onay yeniden alınır. Başlatmada onaylanan içerik ve hedef kitle ile güncel olanların aynı olduğu doğrulanır. Hazır olmayan çalıştırma (anlık görüntü/içerik yok, uygun alıcı yok, geçersiz içerik, geçmiş zaman) onaya gönderilemez; kampanya dönemi ve planlanan zaman başlatmada denetlenir.
- **Gönderim öncesi yeniden denetim**: her teslimat için adres yeniden çözülür (pasif/silinmiş iletişim noktası "adres yok" olarak atlanır), izin ve engel yeniden değerlendirilir, günlük sıklık hakkı atomik ayrılır (kampanyalar arasında ortak; iki eşzamanlı rezervasyon sınırı aşamaz), sonra sağlayıcıya verilir. Anlık görüntü bu denetimleri atlatmaz.
- **Güvenilirlik**: teslimat anahtarı (tenant, çalıştırma, kanal, normalize adres) benzersizdir; tekrar başlatma ve çift işleme çift satır üretmez. Başlatma/işleme çalıştırma başına kilitle serileşir. Geçici hata en çok 3 denemeyle yeniden sıraya alınır, kalıcı hata yeniden denenmez. **Sağlayıcı kabulünden sonra yanıt kaybolursa satır "belirsiz" olur ve kör yeniden denenmez**; sıklık hakkı korunur. Uzun süre "işleniyor" kalan satır (işlem yarıda kalmış) belirsiz sayılır. "Kabul edildi" teslim edildi anlamına gelmez; teslim bilgisi sağlayıcı olaylarıyla (sonraki dilim) gelecektir.
- **Duraklat/iptal**: duraklatma ve iptal yeni işi durdurur; iptalde sırada kalanlar iptal edilir, sağlayıcıya verilmiş iletiler geri alınmış gibi gösterilmez. Modül kapanınca işleme ve eylemler 403 döner, veriler silinmez.
- **Bilerek yok / açık**: otomatik işçi ve planlanan zamanda kendiliğinden başlatma (şimdilik elle "başlat" ve "sıradaki grubu işle"; tasarım idempotenttir), gerçek sağlayıcı ve webhook (teslim/tıklama/geri dönüş), abonelikten çıkma uç noktası ve tercih merkezi (dilim 6), kişi bazlı değişken ve önizleme (içerik düz metin), test gönderimi ve gönderici profili, HTML içerik, gönderim penceresi ve saatlik hız sınırı, sıklık için haftalık pencere, teslimatın kapanış raporu, kampanya durum makinesi (kampanya hâlâ yalnızca Taslak/Arşiv).
- **Test**: 31 birim (durum makinesi, içerik kuralları, teslimat anahtarı), 13 entegrasyon (onay akışı ve görev ayrılığı, sağlayıcı bloklu başlatma, işleme ve hata sınıfları, yeniden denetim, pasif iletişim noktası, ortak sıklık sınırı, eşzamanlı rezervasyon, duraklat/iptal, planlı zaman ve kampanya dönemi, doğrulama, tenant izolasyonu, izinler ve kapalı modül); tarayıcıda içerik ekle → çalıştırma oluştur → hazır olmayanın reddi akışı denendi (görsel düzen, duyarlı görünüm ve koyu tema elle gözden geçirilmedi; uygun alıcılı çalıştırmanın ekrandan işlenmesi tarayıcıda denenmedi, API testleriyle kapsanıyor).
