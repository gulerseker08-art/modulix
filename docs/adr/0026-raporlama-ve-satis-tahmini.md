# ADR-0026 — Raporlama ve satış tahmini (EPIC 14)

Durum: kabul edildi (2 Kasım 2026). Uygulama dilimlerle ilerler; her dilimin durumu bölüm 8'dedir. Yalnızca testle doğrulananlar "yapıldı" yazılır.

## 1. Bağlam ve amaç

İş soruları: bu ay ne sattık, açıkta ne bekliyoruz, hedefin neresindeyiz, tahminimiz ne kadar isabetli, veri eksikleri sonucu nasıl etkiliyor. Bu belge isteğin (Epic 14) depoya göre analizidir: ne var, ne yok, nerede duracak, hangi sırayla yapılacak. Tenant başına ayrı veritabanı yoktur (ADR-0020); tek paylaşımlı veritabanı, `TenantId` + EF filtresi + Row-Level Security korunur. Rapor, hedef, tahmin ve (ileride) önbellek/dışa aktarma/iş aynı güvenlik sınırını kullanır.

İlke: raporlama **yetki kazandırmaz**. Her sayı, kayıt boru hattının fırsat listesi için uyguladığı izin, veri kapsamı ve hassas alan (tutar/olasılık) kurallarıyla okunur; kapsam dışı kayıt hiçbir toplama, grafik etiketine ya da dışa aktarmaya girmez.

## 2. Mevcut durum ve boşluk matrisi (kod kanıtıyla)

| Gereksinim | Durum | Kanıt | Risk | Çözüm | Dilim |
|---|---|---|---|---|---|
| Fırsat raporu (açık/kazanılan, aşama, döngü, kayıp nedeni) | Var | `Opportunities/OpportunityReport.cs` (`IOpportunityReport`) | Para birimleri ayrı, kur yok; dönem ay/gün aralığı; snapshot yok | Olduğu gibi kalır; yeni metrikler `Reporting/SalesMetrics` | 1 |
| Teklif raporu (son revizyon, kabul oranı, marj) | Kısmi | `Quotes/QuoteReport.cs` | Kabul oranı **karar tarihine** göredir; belge kohort (ilk sunum tarihi) ister | Aile bazlı kohort metriği ayrı eklenir | 2 |
| Lead raporu | Kısmi | `Api/Crm/LeadReport.cs` | Dönüşüm kohortu "olgunlaşma" işaretsiz | Kohort + olgunlaşma | 2 |
| Kur | Var | `crm.ExchangeRates` (`ExchangeRate`, gün bazlı, kaynak/not) | Raporlar kullanmıyor | `RateBook`: gün bazlı çözümleme, kur yoksa dönüştürme yok | **1** |
| Saat dilimi | Var | `RegionalTime`, `core.regional.time-zone` | Modül ayar okuyamaz | `ISalesReportingPolicyProvider` (API sağlar) | **1** |
| Mali takvim | **Yok** | `grep FiscalYear` boş | Çeyrek/yıl kayması | `crm.reporting.fiscal-year-start-month` + `ReportPeriods` | **1** |
| Aşama geçmişi | Var | `OpportunityHistory` (aşama/durum/kapanış tarihi; `Cycle`) | İzleme başlangıcından öncesi yok; geriye dönük üretilmez | Aşama dönüşümü yalnızca geçmiş olan fırsatlarda; başlangıç tarihi gösterilir | 2 |
| Hedef | **Yok** | — | Çakışma, hiyerarşide çift sayım | `SalesTarget` + `SalesTargetRevision` | **1** |
| Sürümlü tahmin | **Yok** (`yol-haritasi` M1.3f: "satış tahmini anlık görüntüsü yapılmadı") | — | Kilitli geçmişin değişmesi | `ForecastSubmission/Item/Adjustment` | **1** |
| Ağırlıklı tahmin ve olasılık | Kısmi | `OpportunityRules.Weighted`, `Opportunity.Probability/ProbabilityManual` | Eksik olasılık sıfır sayılıyordu (raporda ayrı gösteriliyor) | Aynı kural, tahminde işaretli | **1** |
| Kayıt erişimi / alan yetkisi | Var | `IRecordPipeline`, `IRecordAccess.GetOwnersAsync`, `SensitiveFields` (tutar/olasılık) | — | Tüm okumalar boru hattından | **1** |
| Rapor merkezi, kaydedilmiş filtre/görünüm, sınırlı oluşturucu | **Yok** | — | Serbest join / SQL sızıntısı | `DatasetDefinition` sözleşmesi (modül kaydeder) | 3 |
| Dışa aktarma | Kısmi | `OpportunityEndpoints.ExportAsync` (CSV, formül koruması `LeadEndpoints.Csv`, audit) | Ortak iş yok | `ExportJob`, indirme anında yetki | 4 |
| Zamanlanmış rapor, bildirim | Kısmi | `*JobsWorker` örüntüsü, `IEmailQueue` | Sahte gönderim yasak | Yetki kaybında teslim durur | 4 |
| Önbellek / özet tablo | **Yok** | — | Yetki sızıntısı, bayat veri | Gerekirse; anahtar tenant+kapsam+izin sürümü | 4 |
| Business unit / departman / ekip boyutu, kanal/bayi | Doğrulanamadı | Organizasyon yapısı `core` şemasında (ADR-0008, Y8a); fırsatta ekip/birim alanı **yok**, yalnızca `OwnerId` | Bugünkü sahiplik ≠ tarihsel sahiplik | İlk dilimde kullanıcı kapsamı; ekip/birim dilim 2 | 2 |
| Büyük veri performansı | Ölçülmedi | Okuma sınırı 5000 kayıt/okuma, sunucu tarafı toplama **yok** | Hacimde yavaşlık | Dilim 4'te ölçüm ve özet tablo kararı (bölüm 7) | 4 |

## 3. Mimari kararlar

**K1 — Sorumluluk ayrımı.** Satış metrikleri, hedef ve tahmin kuralları **CRM**'dedir (`Modulix.Modules.Crm/Reporting`); Core'a sektöre özgü kural girmez. Ortak rapor merkezi (dilim 3) Core'da `DatasetDefinition` sözleşmesi olarak gelir ve CRM'e bağımlı olmaz; her modül kendi veri kümesini kaydeder. Lojistik CRM'siz çalışır.

**K2 — Saf hesap çekirdeği.** `SalesMetrics`, `ForecastRules`, `TargetRules`, `ReportPeriods`, `RateBook` veritabanı ve izin bilmeyen saf işlevlerdir. Kart, grafik, detay listesi, tahmin ve ileride dışa aktarma **aynı** `SalesMetrics.Select/Amount/Total` işlevlerini kullanır; "kart toplamı = detay toplamı" yapısal olarak garantidir (`SalesCard`).

**K3 — Okuma kayıt boru hattından.** `SalesOverview` ve `ForecastWorkflow` fırsatları `IRecordPipeline.QueryAsync` ile okur. Tutar izni yoksa tutar toplamları hiç hesaplanmaz (adetler gösterilir). Okuma başına 5000 kayıt sınırı aşılırsa yanıt `Truncated` işaretlenir.

**K4 — Yazma kayıt boru hattından, aynı işlemde.** Hedef ve tahmin kayıt deposudur (`SalesTargetRecordStore`, `ForecastSubmissionRecordStore`); denetim ve olay boru hattında yazılır. Tahmin akışı `ForecastWorkflowGate` ile (fırsat iş akışı kapısı örüntüsü) satır ve düzeltmeleri başlıkla **tek işlemde** yazar. Genel kayıt ucundan tahmin oluşturulamaz (`PrepareNewAsync` kapı ister).

**K5 — Kur politikası.** Açık fırsat (pipeline, beklenen, ağırlıklı) hesap günü kuruyla, gerçekleşen satış **kazanım günü** kuruyla çevrilir; doğrudan çift yoksa ters çift (1/kur) kullanılır; üçüncü para birimi üzerinden çapraz kur **türetilmez**; kur yoksa tutar toplama girmez, `MissingRateCount` ile bildirilir (1 varsayılmaz). Tahmin satırı kullanılan kuru ve gününü saklar; sonradan eklenen kur kilitli sürümü değiştirmez.

**K6 — Tarih politikası.** Dönem = tenant saat diliminde, başlangıç dahil sonraki dönem başlangıcı hariç gün aralığı. Ay takvim ayıdır; çeyrek ve yıl mali yıl başlangıç ayından kurulur. Fırsat tarihleri gün olarak saklandığından dönem gün aralığıdır; zaman damgalı alanlar için `ToUtcRange` yerel gece yarısı sınırlarını (yaz saati geçişi dahil) UTC'ye çevirir. Oluşturulma, beklenen kapanış, gerçek kapanış ve kilit tarihi birbirinin yerine kullanılmaz.

**K7 — Tahmin sahiplik ve erişim.** Satır, anlık görüntü anındaki sorumluyu saklar (tarihsel sahiplik) ve fırsatın **bugünkü** sorumlusunu ayrıca gösterir. Okurken satır, çağıranın **bugünkü** veri kapsamına göre süzülür (fırsatın bugünkü sorumlusu kapsamda mı); toplamlar yalnızca gösterilen satırlardan hesaplanır, kaydedilmiş toplam yoktur. Tenant kapsamlı tahmin hazırlama/kilitleme yalnızca veri kapsamı Tümü olan kullanıcıyla yapılır (anlık görüntü eksik kalmasın); kullanıcı kapsamlı tahmin için kapsamında o kullanıcı olmalıdır.

## 4. Metrik sözlüğü (`SalesMetrics`, yanıttaki `definitions`)

Tutar = fırsatın **vergi hariç, iskonto sonrası net tutarı**; muhasebe geliri, fatura ya da tahsilat değildir. Sipariş/fatura tutarı bu toplamlara **eklenmez** (ayrı metrik ve açık bağlantı gerekir; dilim 2'de siparişe bağlantı).

| Metrik | Tanım | Boş/sıfır davranışı |
|---|---|---|
| Açık pipeline | Durumu **Açık** tüm fırsatların tutarı (kapanış tarihinden bağımsız); Beklemede girmez | Tutarsız fırsat sayılır, toplama girmez, bildirilir |
| Beklenen açık satış | Açık ve beklenen kapanışı dönemde | Kapanış tarihi yoksa hiçbir dönemde yok; bildirilir |
| Ağırlıklı açık tahmin | Aynı fırsatlar için Σ(tutar × olasılık / 100) | Olasılığı olmayan **sıfır sayılmaz**, toplama girmez, sayısı verilir |
| Gerçekleşen satış | Gerçek kapanışı dönemde kazanılmış fırsatların kazanılan tutarı (yoksa fırsat tutarı), kazanım günü kuruyla | Kapanış tarihsiz kazanılmış fırsat bir döneme yerleştirilemez |
| Dönem öngörüsü | Gerçekleşen + ağırlıklı açık | Kazanılmış fırsat ikinci kez eklenmez (açık kümede değildir) |
| Kazanma oranı | Kazanılan / (kazanılan + kaybedilen), dönemde kapanan | Payda 0 ise **tanımsız** (`null`); açık/beklemede/iptal paydaya girmez |
| Hedef gerçekleşme | Gerçekleşen / hedef × 100 | Hedef yok ya da ≤ 0 ise oran yok |
| Kalan hedef | Hedef − gerçekleşen | Negatifse "hedef karşılandı" |
| Pipeline kapsama | Dönemde kapanması beklenen açık tutar / kalan hedef | Kalan ≤ 0 ise oran yok ("hedef karşılandı"), sonsuz gösterilmez |
| Tahmin sapması | Kilitli senaryo − gerçekleşen; mutlak hata ayrıca | Gerçekleşen 0 ise yüzde hata yok; **ufuk (gün)** ile okunur |
| Hedef çözümleme | Dönemi tam ve boşluksuz kaplayan hedef dilimlerinin toplamı; kısmi toplam hedef diye sunulmaz | Boşluk varsa `problem` metni |

**Tahmin kategorileri** birbirini dışlar: Hariç, Pipeline, En İyi Durum, Taahhüt; kazanıldı kategori değil gerçek sonuçtur. Varsayılan kategori olasılıktan (Taahhüt ≥ eşik, En İyi Durum ≥ eşik, 0 Hariç; eşikler ayar). Senaryolar: **Taahhüt** = gerçekleşen + taahhüt; **İyimser** = gerçekleşen + taahhüt + en iyi durum; **Ağırlıklı** = gerçekleşen + Σ(nihai tutar × etkin olasılık). Ağırlıklı ayrı yöntemdir, kategorilerin üstüne eklenmez. Düzeltme kaynak fırsatı değiştirmez: temel tutar, düzeltme (delta), nihai tutar, olasılık düzeltmesi ve kategori ayrı alanlardır; her düzeltme gerekçeli `ForecastAdjustment` kaydıdır.

## 5. Veri modeli (`crm` şeması; migration `SalesPlanning`, yalnızca yeni tablolar)

| Tablo | Not |
|---|---|
| `SalesTargets` | Metrik (`WonAmount`/`WonCount`), kapsam (Tenant/User), para birimi (tutarda zorunlu, adette yok), dönem; aynı kapsam+metrik+para birimi çakışamaz (kayıtta denetim; `UX_SalesTargets_Slot` aynı başlangıcı DB'de engeller) |
| `SalesTargetRevisions` | Değer değişiminin eski/yeni değeri ve gerekçesi; hedef sürüm numarası artar |
| `ForecastSubmissions` | Dönem × kapsam × para birimi × sürüm; Taslak/Sunuldu/Kilitlendi; kilit anındaki gerçekleşen; `UX_ForecastSubmissions_Open` kilitsiz tek sürüm, `UX_ForecastSubmissions_Version` sürüm benzersizliği |
| `ForecastItems` | Fırsat anlık görüntüsü (tutar, para birimi, olasılık+kaynağı, kur+günü, sorumlu, aşama, temel/nihai kategori, düzeltme) |
| `ForecastAdjustments` | Değişmez düzeltme günlüğü (tür, eski, yeni, gerekçe, yapan, zaman) |

Hepsi `ITenantEntity` + `ICompanyShared`, RLS politikasında (`TenantIsolationSql.AddTable`); iyimser eşzamanlılık `Version` ile. Geri dönüş: `dotnet ef migrations` ile `Down` tabloları RLS'ten çıkarıp siler (başka tabloya dokunmaz).

## 6. API, izin, ekran

İzinler (`crm.*`): `sales-reports.read`, `sales-targets.read/manage`, `forecasts.read/write/submit/lock/reopen` — hiçbir mevcut role eklenmedi; yeni hazır rol şablonu **Satış yöneticisi** (`crm.sales-manager`, kapsam Tümü). Ayarlar (CRM Raporlama): mali yıl başlangıç ayı, Taahhüt ve En İyi Durum olasılık eşikleri.

| Uç nokta | İzin |
|---|---|
| `GET /api/crm/sales/overview?kind=&date=&ownerId=&currency=` | `sales-reports.read` |
| `GET /api/crm/sales/overview/cards/{card}` (detay listesi) | `sales-reports.read` |
| `/api/crm/sales-targets` (CRUD; değer değişiminde `changeReason`) | `sales-targets.read/manage` |
| `GET/POST /api/crm/sales/forecasts`, `GET {id}`, `GET {id}/compare` | `forecasts.read` / `write` |
| `POST {id}/refresh`, `adjust`, `PUT {id}/note`, `DELETE {id}` (yalnız taslak) | `forecasts.write` |
| `POST {id}/submit`, `return` | `forecasts.submit` |
| `POST {id}/lock` | `forecasts.lock` |
| `POST {id}/reopen` (gerekçe; yeni sürüm üretir) | `forecasts.reopen` |

Ekranlar: Satış görünümü (`/crm/satis-gorunumu`), Satış hedefleri (`/crm/satis-hedefleri`), Satış tahmini (`/crm/satis-tahmini`, ayrıntı `/{id}`). Yükleme, hata+tekrar dene, boş, kısmi veri, yetkisiz durumları ayrıdır; "veri yok" ile gerçek sıfır ayrı gösterilir.

## 7. Performans ve ölçüm politikası

İlk dilimde sunucu tarafı toplama yoktur: fırsatlar boru hattından sayfalanıp bellekte toplanır (okuma başına en fazla 5000). Bu **ölçülmemiş** bir sınırdır; "hızlı" iddiası yoktur. Dilim 4'te hedef (öneri: 5000 açık + 5000 kapanmış fırsatta genel görünüm p95 < 2 sn, tahmin oluşturma p95 < 3 sn; ortam: yerel SQL Server 2025 konteyneri), yük profili ve sonuçlar bu belgeye yazılır; hedef aşılırsa özet tablo/sunucu tarafı toplama kararı ayrı ADR ile alınır.

## 8. Uygulama durumu

**Dilim 1 — yapıldı (bu PR):** dönem/mali takvim, kur çözümleme, satış metrikleri, hedefler (sürüm, çakışma, hiyerarşi uyarısı), sürümlü tahmin (kategori, düzeltme, sunma, kilit, gerekçeli yeniden açma = yeni sürüm, sürüm karşılaştırma, doğruluk), satış genel görünümü + kart detayı, üç ekran, izinler/ayarlar/rol şablonu. Testler: 42 birim durumu (`SalesMetricsTests`: kabul senaryoları 1, 2, 3, dönem/artık gün/saat dilimi/yaz saati/mali yıl, kur çözümleme, hedef çözümleme, kategori ve senaryo kuralları), 13 entegrasyon (`SalesPlanningTests`: senaryo 1 ve 2 uçtan uca, kur eksikliği ve sonradan kur tanımlama (8'in kur kısmı), kart toplamı = detay toplamı (11'in KPI→detay kısmı), tenant izolasyonu (6; API, hedef, tahmin, liste), rol/kapsam/tutar izni farkları (7; yetki iptali ve dışa aktarma sınanmadı), kilit/yeniden açma/karşılaştırma ve kilitli sürümün değişmemesi (9'un eşzamansız kısmı), hedef çakışma/sürüm/yetki).

**Arayüz doğrulama durumu:** üç ekran derlendi (`dotnet build`, 0 uyarı) ve çağırdığı uç noktalar entegrasyon testlerinde sınandı; ancak ekranlar tarayıcıda **görsel olarak doğrulanmadı** (dar ekran, klavye, kontrast, boş/hata durumları elle denenmedi) ve bileşen testi yoktur. Gerçek veriyle ekran kanıtı merge öncesi elle alınmalıdır.

**Dilim 1'de yapılmadı / sınırlar:**
- Kabul senaryoları **kapsam dışı kalanlar:** 4 (teklif kohortu, dilim 2), 5 (kalem/aktivite/iş ortağı çoğalması: dilim 1 metrikleri fırsat tablosundan tek satır okur, kalem ya da iş ortağı birleşimi yoktur; ayrı test yazılmadı, iş ortağı katkı dağıtımı dilim 2), 10'un aşama geçmişi tarafı (dilim 2), 11'in dışa aktarma/CSV formül/dinamik alan kaldırma kısmı (dilim 3–4), 12 (performans ölçümü, dilim 4). Senaryo 9'da eşzamanlı düzeltme yarışı, olay/iş tekrar teslimi ve sahip/ekip değişikliğinin kilitli geçmişe etkisi uçtan uca sınanmadı; sahiplik değişimi yalnızca "bugünkü sorumluya göre erişim" kuralıyla tasarlandı.
- Kazanma oranı birim testle (senaryo 3) doğrulandı; uçtan uca yalnızca kazanılan sayısı sınandı.
- Dönem sınırı/saat dilimi/mali yıl **birim testlerle** doğrulandı; uçtan uca yalnızca varsayılan Ocak başlangıcı ve İstanbul saat dilimi sınandı.
- Eşzamanlı iki kullanıcının aynı satırı düzeltmesi sürüm kontrolüyle (`version` 409) korunur; paralel istek yarışı entegrasyon testinde **sınanmadı**. Çakışan hedef dönemlerinin eşzamanlı eklenmesi yalnızca aynı başlangıç için DB'de engellenir (aksi halde kayıt anı denetimi; kilit yok).
- Olasılığı olmayan fırsat uçtan uca sınanamadı (yeni fırsat aşama olasılığını alır); eksik olasılık kuralı birim testlidir.
- Aşama geçmişi tabanlı dönüşüm/aşamada kalma süresi, teklif kohortu, lead dönüşümü, ekip/birim/kanal boyutu, ürün/hizmet filtresi, ekip performansı, veri kalitesi ekranı: **dilim 2**.
- Rapor merkezi, kaydedilmiş filtre/paylaşım, sınırlı oluşturucu, dinamik alan raporu: **dilim 3**.
- Dışa aktarma, zamanlama, önbellek, özet tablo, performans ölçümü, kullanıcı geri bildirimi: **dilim 4**.
- Hedef revizyon geçmişi ekranı (uç nokta yok), tahmin listesinde sayfalama, hedef hiyerarşisinde ekip/birim düzeyi.
- Geçmişe dönük tahmin snapshot'ı üretilmez: tahmin doğruluğu, ilk kilitli sürümden itibaren birikir.
- AI açıklaması eklenmedi.

**Dilim 2 — yapıldı (ayrı PR):** `Reporting/CohortMetrics.cs` (saf hesap), `Reporting/SalesAnalytics.cs` (boru hattından okuyan servis), `GET /api/crm/sales/analytics/{quotes|leads|stages|team|quality}` (`sales-reports.read` + alttaki kaydın okuma izni), üç ekran (Satış analizi: Teklif kohortu / Lead dönüşümü / Aşama dönüşümü; Ekip performansı; Veri kalitesi). Yeni izin, tablo ya da migration yoktur.
- **Teklif kohortu:** birim teklif **ailesi**; kohort = ailenin ilk gönderilme anı dönemde olanlar; sonuç kesim anına göre (kesimden sonra kabul edilen aile Beklemede); aile kabul oranı yanında revizyon kabul oranı, gönderilen revizyon sayısı, kabul süresi ortalama/medyan, kabul edilen net tutar (kabul günü kuruyla). Taslaklar girmez.
- **Lead kohortu:** tekil lead, alınma anı kohortu; iki oran (tüm kohort / uygun = elenenler hariç); olgunluk 30 gün; kaynak kırılımı.
- **Aşama dönüşümü:** `OpportunityHistory`'den, oluşturulma kohortuyla; aşamaya giren tekil fırsat (geri dönüş/tekrar bir kez, atlanan aşama sayılmaz), ilerleme (kaybedilen ilerleme değildir), aşamadan kayıp/kazanım, şu an orada; izleme başlangıcı gösterilir, tarihçesi olmayan dönem için dönüşüm üretilmez. Fırsatın **bugünkü** sorumlusu çağıranın veri kapsamındaysa sayılır.
- **Ekip performansı:** kullanıcı (bugünkü sorumlu) bazında gerçekleşen, kazanılan/kaybedilen, kazanma oranı, iş yükü (açık fırsat sayısı), pipeline/ağırlıklı, gecikmiş, döngü ortalama/medyan **ve örneklem adedi**, kullanıcı hedefi ve gerçekleşme. Aktivite sayısı başarı ölçütü olarak yoktur.
- **Veri kalitesi:** açık fırsatlarda eksik tutar, kuru yok, olasılık yok, kapanış tarihi yok, gecikmiş, hareketsiz, tarihçesi yok; sorun başına adet ve örnek kayıt. Tutar izni yoksa tutar/kur/olasılık sorunları hesaplanmaz.
- **Testler:** 14 yeni birim (`CohortMetricsTests`, `DataQualityTests`: kabul senaryosu 4 = %40, kesim sonrası kabul, kohort kuralı, olgunluk, aşama akışı, medyan, veri kalitesi) ve 8 entegrasyon (`SalesAnalyticsTests`: teklif kohortu ve revizyon oranı, kesim, lead dönüşümü, aşama akışı geri dönüş/tekrar/geçmiş dönem, ekip performansı + hedef + tutar izni, veri kalitesi + kur, yetki ve veri kapsamı, tenant izolasyonu).
- **Dilim 2'de yapılmadı:** ekip/birim/kanal-bayi boyutu (modülün erişebildiği bir ekip/birim sözleşmesi `Core.Contracts`'ta yok; şu an kırılım kullanıcı bazındadır), ürün/hizmet ve sektör filtresi, iş ortağı katkı dağıtımı, aşamada kalma süresinin tamamlanmış aralıklardan hesabı, sahiplik değişimine karşı tarihsel sahiplik görünümü, siparişe bağlantı. Lead eleme tarihi kesime göre ayrıştırılmaz (güncel durum). Quote kohortu lower bound'suz okur (ilk sunum tarihi için kesime kadar tüm sunumlar gerekir; okuma sınırı 5000). Ekranlar tarayıcıda görsel doğrulanmadı.



Dilim planı: **3** rapor merkezi ve sınırlı oluşturucu; **4** dışa aktarma, zamanlama, önbellek ve performans.
