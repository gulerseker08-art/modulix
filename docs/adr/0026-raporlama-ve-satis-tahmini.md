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
| Rapor merkezi, kaydedilmiş filtre/görünüm, sınırlı oluşturucu | **Var (dilim 3)** | `Core.Contracts/Reporting`, `Core/Reporting`, `IModule.Reports` | Serbest join / SQL sızıntısı | `DatasetDefinition` sözleşmesi (modül kaydeder); tek veri kümesi, bildirilmiş sütunlar | 3 |
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

İlk dilimde sunucu tarafı toplama yoktur: fırsatlar boru hattından sayfalanıp bellekte toplanır (okuma başına en fazla 5000). Bu **ölçülmemiş** bir sınırdır; "hızlı" iddiası yoktur. Hedef (5000 açık + 5000 kapanmış fırsatta genel görünüm p95 < 2 sn, tahmin oluşturma p95 < 3 sn; ortam: yerel SQL Server 2025 konteyneri), yük profili ve ölçüm sonuçları dilim 4 bölümünde (bölüm 8) yazılıdır; genel görünüm hedefi karşılanmadı, bu yüzden özet tablo/sunucu tarafı toplama kararı ayrı ADR ile alınacaktır.

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
- Rapor merkezi, kayıtlı rapor/paylaşım, sınırlı oluşturucu, dinamik alan raporu: **dilim 3 (yapıldı, aşağıda)**.
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



**Dilim 3 — yapıldı (ayrı PR):** ortak rapor merkezi ve sınırlı rapor oluşturucu.
- **Sözleşme (K1):** `Core.Contracts/Reporting` — modül, raporlanabilir kayıt türünü `DatasetDefinition` olarak bildirir (`IModule.Reports`): bildirilmiş sütunlar (tür, kullanım: gruplama/ölçü/süzgeç/gösterim, seçenekler, lookup, para birimi alanı), varsayılan sütunlar, kayda geçiş adresi, hazır raporlar ve modülün kendi rapor ekranlarına bağlantılar. Katalog açılışta doğrular (anahtar önekleri, ölçü/gruplama türü uyumu, seçim listesi ve lookup varlığı, hazır raporların sütunlara uyumu); geçersizse uygulama açılmaz. Core CRM'i bilmez, Lojistik CRM'siz çalışır.
- **Motor:** `ReportSpecValidator` (saf doğrulama; hesaplanan alan, ifade ya da birleştirme yoktur, bu yüzden döngü ya da sınırsız işlem üretilemez; en çok 10 süzgeç, 2 gruplama, 5 ölçü, 20 sütun), `ReportAggregator` (saf süzgeç/gruplama/toplama/sıralama), `ReportRunner` (kayıt boru hattından okur), `ReportColumnResolver` (modül açık mı, kayıt türü okunabilir mi, hangi hassas alanlar görünmez, hangi özel alanlar var), `ReportCatalogService`, `ReportCenter` (kayıtlı rapor, paylaşım, favori). Tek veri kümesi, tek kayıt türü: veri kümeleri arası birleştirme yok; bir ilişkinin teknik olarak var olması izinli yol demek değildir (yalnızca bildirilmiş sütunlar kullanılır, lookup sütunları etiket olarak çözülür).
- **Yetki:** kayıt boru hattı izin, veri kapsamı ve hassas alanları **toplamadan önce** uygular. Çağıranın göremediği (hassas) sütun katalogda yoktur; süzgeçte, gruplamada, toplamda ya da sütun olarak kullanılırsa çalıştırma `forbidden` döner, hiçbir kayıt okunmaz (aksi halde "tutar > 100000" süzgeci ya da toplam tutarı sızdırırdı). Paylaşım yalnızca tanımı görünür kılar; her çalıştırma çağıranın yetkisiyle yapılır, paylaşım/görünürlük her okumada yeniden hesaplanır (kaldırınca erişim hemen biter). Sahibi olmayan raporu değiştiremez, silemez, paylaşımını ayarlayamaz.
- **Tutar ve eksik değer:** tutar sütunları **para birimine göre ayrı** toplanır (ekranda "kur çevrimi" yoktur; kurlu metrikler satış ekranlarındadır); değeri ya da para birimi olmayan kayıt toplama girmez ve `excludedRows` ile sayılır (sıfır varsayılmaz). "Veri yok" (tutar toplamı null) ile gerçek sıfır (adet 0) ayrıdır. Okuma en çok 5000 kayıt; aşılırsa `truncated` ve uyarı. Özet en çok 200 grup (`rowsLimited`).
- **Kart = detay:** özet satırı ve ayrıntı listesi aynı süzülmüş kümeden türer; ayrıntıya inmek grup anahtarlarını süzgeçe ekler, ayrıntı toplamı özet satırının değerine eşittir (birim ve entegrasyon testli).
- **Dinamik alanlar:** özel alan sütunu `custom.<anahtar>`; anahtar değişmediği için yeniden adlandırma raporu bozmaz; arşivlenen alan `columnRemoved` ile raporu anlaşılır biçimde geçersiz işaretler (liste sağlığı `columnsInvalid`, çalıştırma `invalid`), alan geri alınınca rapor yeniden çalışır. Sayı alanı toplanır, seçim/evet-hayır/tarih alanı gruplanır.
- **Veri:** `core.Reports`, `core.ReportShares`, `core.ReportFavorites` (RLS'li, bileşik yabancı anahtar, rapor silinince paylaşım ve favoriler silinir; ad kullanıcı içinde tekil; eşzamanlılık `Version`). Migration `ReportCenter`. Denetim: `core.report` (oluşturma, değiştirme, paylaşım, silme).
- **API/izin/ekran:** `/api/reports` (`catalog`, liste, `{id}`, kaydet, `sharing`, `favorite`, `run`, `{id}/run`); izinler `core.reports.read|manage|share` (hiçbir mevcut role eklenmedi; yönetici hepsine sahiptir, `core.read-only` şablonu `.read` ile bitenleri otomatik toplar). Ekranlar: Rapor merkezi (`/raporlar`: hazır raporlar, modül bağlantıları, raporlarım, benimle paylaşılan, favoriler), oluşturucu (`/raporlar/yeni`, `/{id}/duzenle`), çalıştırma (`/raporlar/{id}`, `/raporlar/hazir/{anahtar}`; tablo + grafik + ayrıntı + paylaşım).
- **CRM katkısı:** `crm.opportunities` (aşama, durum, öncelik, tür, kaynak, kayıp nedeni, sorumlu, firma, tutar, kazanılan tutar, olasılık, tarihler) ve `crm.leads` veri kümeleri; 8 hazır rapor; 6 satış rapor ekranı bağlantısı. Tutar/olasılık sütunları `crm.opportunity-amounts.read` olmadan görünmez.
- **Testler:** 58 birim (`ReportSpecValidatorTests`, `ReportAggregatorTests`, `DatasetCatalogTests`) ve 23 entegrasyon (`ReportCenterTests`: katalog, kart=detay ve para birimi ayrımı, tablo/sayfalama/etiket, veri yok–sıfır ayrımı, eksik değer, lead veri kümesi, geçersiz tanım, hassas alan sızıntısı, veri kapsamı, kaydet/sürüm/ad tekilliği, paylaşım ve anında iptal, sonradan yetki iptali, dinamik alan yeniden adlandırma/arşiv/geri alma, modül kapalı, izin, tenant izolasyonu, denetim).
- **Dilim 3'te yapılmadı / sınırlar:** ayrı "kayıtlı süzgeç" varlığı yok (süzgeçli rapor kayıtlı süzgeçtir); pano/widget yok; kişi, aktivite, sipariş veri kümeleri yok (fırsat, lead, teklif ve firma var; son ikisi dilim 4 sonrası iyileştirmede eklendi); ekip/departman/kanal kırılımı yok (dilim 2 ile aynı neden); tarih ayrıntısı gün/ay/yıl (çeyrek mali takvime bağlı olduğundan yalnızca satış ekranlarında); lookup sütununda süzgeç değeri kimlik (seçici yok); tutar kur çevrimi yok; iş ortağı katkı dağıtımı yok; ekranlar tarayıcıda **görsel doğrulanmadı** (yalnızca sunucu tarafı bileşen çizimiyle sahte yanıt üzerinde denendi; tıklama/klavye/odak etkileşimi denenmedi).
- **Numaralandırma notu:** `docs/adr/` altında iki belge `0026` numaralıdır (departman hiyerarşisi ve bu belge); kod yorumları bu belgeye `ADR-0026` ile başvurur. Birinin yeniden numaralandırılması ayrı bir düzenlemedir.


**Dilim 4 — yapıldı (ayrı PR):** dışa aktarma, zamanlama, önbellek kararı ve performans ölçümü.
- **Dışa aktarma** (`core.reports.export`; `POST /api/reports/{id}/exports`, `POST /api/reports/exports`, `GET /api/reports/exports`, `GET .../{id}/download`, `DELETE`): CSV, **ekrandaki sonuçla aynı** `ReportResult`'tan üretilir (ayrı hesap yok; özet raporda toplam satırları dahil). Çağıranın yetkisiyle çalışır, hassas sütun yetkisizse dışa aktarma `403` verir. Formül enjeksiyonuna karşı `=`, `+`, `-`, `@`, sekme ve satır başı ile başlayan metin hücreleri ve başlıklar `'` ile kaçışlanır (sayısal hücreler değil); UTF-8 BOM, CRLF, virgüllü. Dosya `core.ReportExports`'ta saklanır (en çok 5 MB, kullanıcı başına 20 etkin dosya, 24 saat), yalnızca sahibine verilir (başkasının dosyası "yok" ile aynı yanıtı verir), `Cache-Control: no-store`, `nosniff`, her zaman indirme. Oluşturma ve indirme denetim kaydına yazılır (`Exported`: kim, kaç satır).
- **İndirme anında yetki yeniden denetlenir:** rapor merkezi ve dışa aktarma izni, modül, veri kümesi okuma yetkisi ve hazırlandığı andaki **erişim parmak izi** (izin kümesi + kayıt kapsamı + kapsamdaki kayıt sahipleri, SHA-256). Parmak izi farklıysa `409` ("yeniden dışa aktarın"): ilgisiz bir izin değişikliği bile eski dosyayı geçersiz kılar (güvenli taraf). Süresi geçen dosya `410`, kapsam/izin yoksa `403`. Süresi geçenler arka plan turunda silinir.
- **Zamanlanmış rapor** (`core.reports.schedule`; `/api/reports/schedules`, `{id}/schedules`): günlük/haftalık/aylık (ayın günü 1–28), saat firmanın saat diliminde (yaz saati: olmayan saat ileri kayar, tekrar eden saatte ilk karşılaşma; `ReportScheduleRules` saf ve testli). Kullanıcı başına en çok 10 zamanlama; yalnızca sahibi görür ve yönetir; denetim `core.report-schedule`. Arka plan işçisi (`ReportJobs:WorkerEnabled`, varsayılan 300 sn) her etkin tenant'ı ayrı çalıştırır, tenant başına `sp_getapplock` ile tek örnekte koşar. Her zamanlama **sahibinin bağlamında** çalışır: sahibin etkin üyeliği, `read/export/schedule` izinleri, modül, raporun hâlâ görünür olması ve alıcının etkin üyeliği o anda denetlenir; biri bozuksa zamanlama **nedeniyle durdurulur** (`stoppedReason`), dosya ya da e-posta üretilmez. E-postada veri yoktur (rapor adı, satır sayısı, indirme bağlantısı; ad HTML kodlanır); mevcut e-posta kuyruğu kullanılır, sahte gönderim yoktur.
- **Idempotency/eşzamanlılık:** her (zamanlama, planlı an) için tek `ReportScheduleRuns` kaydı (`UX (TenantId, ScheduleId, Slot)`); yarışan ya da tekrar teslim edilen çalışma ikinci dosya/e-posta üretmez, yarıda kesilen ("Started" ve 15 dakikadan eski) çalışma atomik olarak devralınır, bitmiş çalışmanın takılı kalan zamanlaması bir sonraki ana ilerletilir. Geçici hata çalışmayı `Failed` yapar ve bir sonraki planlı ana geçer (bu dönem yeniden denenmez); kalıcı yetki kaybı durdurur. Bir zamanlamanın hatası diğerlerini ya da başka tenant'ı durdurmaz. Zamanlama sürümlüdür (409).
- **Veri:** `core.ReportExports`, `core.ReportSchedules`, `core.ReportScheduleRuns` (RLS'li; rapor silinince zamanlamalar ve çalışmaları silinir). Migration `ReportDelivery`; yeni tablolar, geriye uyumlu.
- **Önbellek / özet tablo kararı: bu dilimde yok.** Ölçüm (aşağıda) genel görünüm hedefini aştı, ama önbellek tek başına doğru çözüm değil: anahtarı tenant + kullanıcı veri kapsamı + izin sürümü + süzgeç + para birimi + dönem + metrik sürümü olmak zorunda olduğundan her kullanıcı için ayrı girdi ve bayat veri riski doğar. Darboğaz profil çıkarılarak doğrulanmadı; en olası neden, kayıt boru hattından 200'lük sayfalarla en çok 5000 kaydın tüm sütunlarıyla okunması (≈25 sorgu) ve bellekte toplanmasıdır. Çözüm adayı (ayrı ADR ile karara bağlanacak): yetkiyi koruyan sunucu tarafı toplama (yetkili kayıt kimlikleri alt sorgusu üzerinde `GROUP BY`) ya da dönem/sahip bazında özet tablo + gecikme göstergesi. Bu belgede önbellek ya da özet tablo **uygulanmadı**.
- **Performans ölçümü** (`ReportPerformanceMeasurementTests`, `MODULIX_PERF=1` ile; normal koşuda atlanır). Ortam: Windows 10, yerel Docker SQL Server 2025 (aynı makine), API testte süreç içi, EF komut günlüğü konsola açık (süreleri bir miktar şişirir), tek tenant, tek kullanıcı (yönetici), veri SQL ile toplu eklendi (2 sn), okuma yolu gerçek (HTTP + kayıt boru hattı). Yük profili: 5000 açık + 5000 kapanmış fırsat; her işlem 1 ısınma + 10 ölçüm. **10 ölçümde p95 en büyük değerdir** (istatistik olarak zayıf; güven aralığı verilmez).

  | İşlem | p50 (ms) | p95 (ms) | en yüksek (ms) |
  |---|---:|---:|---:|
  | Satış genel görünümü (ay) | 2454 | 3164 | 3164 |
  | Rapor: açık fırsatlar, aşamaya göre özet | 1656 | 1731 | 1731 |
  | Rapor: süzgeçsiz özet (okuma sınırına takılır) | 2893 | 13026 | 13026 |
  | Rapor: açık fırsat tablosu, 1. sayfa | 1636 | 1905 | 1905 |
  | Dışa aktarma: özet CSV | 1692 | 1801 | 1801 |
  | Dışa aktarma: tablo CSV | 1752 | 1884 | 1884 |
  | Tahmin oluşturma (ay, tüm kapsam) | 675 | 837 | 837 |

  **Sonuç:** önerilen hedeflerden (genel görünüm p95 < 2 sn, tahmin oluşturma p95 < 3 sn) tahmin oluşturma karşılandı; **genel görünüm karşılanmadı** (p95 3,2 sn). Süzgeçsiz özetteki 13 sn'lik tek ölçüm açıklanamadı (tek sefer; yeniden üretilmedi, ölçüm gürültüsü ya da veritabanı önbelleği olabilir). Süre okunan kayıt sayısıyla artar (açık fırsat raporlarında 5000 kayıt için ≈1,7 sn; ayrıştırılmadı). Ölçülmeyenler: eşzamanlı çok kullanıcı yükü, 5000'in üstündeki hacim (okuma sınırı kesilir), sıcak/soğuk veritabanı ayrımı, ağ gecikmesi, farklı donanım. Bu sayılar "hızlı" iddiası değildir.
- **Testler:** 26 birim (`ReportDeliveryRulesTests`: takvim/saat dilimi/yaz saati/ay günü sınırları, CSV kaçışı ve biçimi) ve 18 entegrasyon (`ReportDeliveryTests`: dışa aktarma = ekran sonucu, formül kaçışı, hassas alan, indirme anında izin/parmak izi/kapsam/süre, başkasının dosyası, 20 dosya sınırı, tenant izolasyonu, izin ayrımı, denetim, zamanlama doğrulama/sürüm/sahiplik, zamanlayıcı: sahibin yetkisiyle teslim + veri içermeyen e-posta + planlı an başına tek çalışma, yetki kaybında durma, üyelik/rapor/görünürlük kaybı, hatalı zamanlamanın diğerlerini durdurmaması, tenant ayrımı, yarıda kesilen çalışmanın devralınması).
- **Dilim 4'te yapılmadı / sınırlar:** Excel (xlsx) yok, yalnızca CSV; e-postaya dosya eklenmez; zamanlanmış rapor yalnızca sahibine gider (dağıtım listesi yok); çalışma Failed olursa aynı dönem yeniden denenmez; işçi turu her etkin tenant için hafif bir sorgu atar (çok tenant'ta ayrı bir zamanlama dizini gerekir); eşzamanlı iki örnek yarışı gerçek iki süreçle değil, kayıt kısıtı ve devralma kuralının testiyle doğrulandı; dışa aktarma dosyaları veritabanında tutulur (nesne depolama yok); satır limitini aşan veri için asenkron iş kuyruğu yok (okuma sınırı 5000 olduğundan istek içinde üretilir); kullanıcı geri bildirimi toplama yok; ekranlar tarayıcıda görsel doğrulanmadı. Tahmin/hedef/sürümlü anlık görüntü dışa aktarması (CSV) ve `crm` rapor ekranlarının (satış görünümü, analiz) dışa aktarması bu dilimde yok; yalnızca rapor merkezi raporları dışa aktarılır.

**Dilim 4 sonrası iyileştirme (performans ve kapsam):**
- **Darboğaz bulundu** (profil: `Profile_where_the_read_time_goes`, `MODULIX_PERF=1`): 5000 açık fırsatın kayıt boru hattından 200'lük sayfalarla okunması ≈2,5–2,9 sn, aynı satırların tek EF sorgusuyla okunması ≈0,2 sn. Süre satır sayısına değil **sayfa sayısına** bağlıydı: tek sayfa, 1 satır da ≈110 ms sürüyordu. Sayfa başına yalnızca iki SQL komutu çalışıyor (COUNT 7 ms, SELECT ≈117 ms): SELECT, `ORDER BY CreatedAt DESC, Id` + durum süzgecinde mevcut dizinle sıralanamıyor (dizin `Status, ExpectedCloseDate`), bu yüzden her sayfada 5000 satır anahtar aramasıyla alınıp sıralanıyordu. EF komut günlüğünü kapatmak süreyi değiştirmedi (günlük neden değil).
- **Düzeltme 1 — dizin:** `IX_Opportunities_TenantId_Status_CreatedAt (TenantId, Status, CreatedAt DESC, Id)` (migration `OpportunityStatusCreatedAtIndex`, yalnızca dizin, geriye uyumlu): tek sayfa 111 → ≈13–28 ms; 25 sayfalık okuma ≈2,6 → ≈1,15 sn.
- **Düzeltme 2 — toplu sayfa:** `RecordQuery.Bulk` ile sayfa üst sınırı 200 yerine `MaxBulkPageSize = 1000` (yalnızca rapor, dışa aktarma ve satış raporu okuyucusu kullanır; izin, veri kapsamı, şirket kapsamı ve hassas alan kuralları aynen uygulanır, testli). 5000 kayıt 5 sayfada okunur: ≈1,15 → ≈0,36 sn (aynı veri, aynı makine).
- **Yeniden ölçüm** (aynı ortam ve yük profili; bu kez EF komut günlüğü kapalı — günlüğün süreyi değiştirmediği profilde ayrıca görüldü):

  | İşlem | p50 (ms) | p95 (ms) | önce p95 (ms) |
  |---|---:|---:|---:|
  | Satış genel görünümü (ay) | 1325 | 1647 | 3164 |
  | Rapor: açık fırsatlar, aşamaya göre özet | 535 | 642 | 1731 |
  | Rapor: süzgeçsiz özet (okuma sınırına takılır) | 920 | 1034 | 13026 |
  | Rapor: açık fırsat tablosu, 1. sayfa | 487 | 542 | 1905 |
  | Dışa aktarma: özet CSV | 523 | 691 | 1801 |
  | Dışa aktarma: tablo CSV | 574 | 899 | 1884 |
  | Tahmin oluşturma (ay, tüm kapsam) | 638 | 933 | 837 |

  **Sonuç:** genel görünüm p95 hedefi (2 sn) karşılandı (1,65 sn), tahmin oluşturma hedefi (3 sn) karşılanmaya devam ediyor. Kalan süre kayıt başına işleme ve satış genel görünümünün birden çok okumasıdır; **önbellek ve özet tablo yine uygulanmadı** — gerek gösteren bir ölçüm artık yok (50 bin üstü hacim, eşzamanlı yük ve soğuk veritabanı ölçülmedi; bu sınır değişirse karar yeniden açılır). Tahmin oluşturma p95'indeki küçük artış (837 → 933 ms) 10 ölçümlük gürültü sınırı içindedir. 10 ölçümde p95 en büyük değerdir; önceki 13 sn'lik tek ölçüm bu çalıştırmada görülmedi (nedeni hâlâ bilinmiyor).
- **Veri kümeleri:** `crm.quotes` (teklif revizyonu satırı: durum, onay, gönderim, sorumlu, firma, net/brüt toplam para birimine göre ayrı, tarihler; 3 hazır rapor) ve `crm.companies` (durum, kaynak, sektör, şehir, sorumlu, özel alanlar; 3 hazır rapor). Teklif veri kümesi **aile** değil revizyon sayar (aile bazlı kabul oranı Satış analizi ekranındadır); teklif tarih sütunları depo süzgecine bağlanmadı (kapsam bilinçli dar).
- **Arayüz gerçek tarayıcıda denendi** (yerel API, demo kullanıcı, sayfa DOM'u ve ağ istekleri; **ekran görüntüsü alınamadı**, yani görünüm, renk, taşma, dar ekran ve kontrast hâlâ doğrulanmadı): rapor merkezi, hazır rapor çalıştırma, özet tablo + grafik verisi, ayrıntıya inme, dışa aktarma, dışa aktarımlar sayfası ve indirme (indirme sayacı arttı), kayıtlı rapor kaydı, haftalık zamanlama oluşturma ("Her pazartesi 07:00", sonraki çalışma Pazartesi). Bulunan ve düzeltilen hatalar: (1) hazır rapor adresi anahtardaki noktalar yüzünden doğrudan açılışta/yenilemede 404 veriyordu (nokta içeren yol dosya sanılır) → noktalar yol parçasında `~` ile taşınır; (2) ayrıntı toplamı karşılaştırması toplam satırının hücre düzenini yanlış varsayıyordu ("Fark var" yanlış alarmı) → toplam satırı artık özet sütunlarıyla aynı hizada ve karşılaştırma buna göre; (3) dışa aktarma dosya adı Türkçe harfleri düşürüyordu (`açık fırsatlar` → `ack-frsatlar`) → ASCII karşılıklarına çevrilir.

**Hedef ve tahmin dışa aktarması (rapor veri kümeleri):** Satış hedefleri (`crm.sales-targets`) ve tahmin sürümleri (`crm.forecasts`, başlık satırı) rapor merkezinin veri kümeleri oldu; bu yüzden mevcut rapor, dışa aktarma ve zamanlama altyapısının tamamı (izin, indirme anında yetki denetimi, formül kaçışı, zamanlama) hiçbir ek kod olmadan bunlara da uygulanır. Yeni sütun türü `User` (kimlik; etiket firmanın etkin üyesinin adı, katalog üyeleri seçenek olarak verir). Kilit anındaki gerçekleşen tutar (`realizedAtLock`) fırsat tutarlarından türediği için hassas alan olarak bildirildi: tutar izni olmayan için katalogda yoktur, kullanılırsa `forbidden`. Adet hedeflerinin para birimi olmadığından tutar toplamına girmez ve dışlanan kayıt olarak sayılır. Yapılmadı: tahmin sürümünün **fırsat satırları ve düzeltmeleri** (Satış tahmini ekranındadır), hedef revizyon geçmişi, `crm.sales-overview` kartlarının dışa aktarması.

**Lead okuması ölçümü ve dizin:** `Profile_lead_dataset_read_cost` (10 bin lead, ilk 5000'i toplu sayfalarla okuma; aynı ortam): süzgeçsiz ≈3,1–3,5 sn, `status=New` (2 bin) ≈0,6 sn, `source=Referral` (3,3 bin) ≈1,5–1,8 sn. Sıralama (`ORDER BY FirstReceivedAt DESC, Id`) mevcut `(TenantId, FirstReceivedAt)` dizinini kullanamıyordu: dizin `Id`'yi dolaylı ve artan taşır, sorgu `FirstReceivedAt` azalan + `Id` artan ister (fırsatta olduğu gibi her sayfada sıralama). `IX_Leads_TenantId_FirstReceivedAt_Id (TenantId, FirstReceivedAt DESC, Id)` eski dizinin yerini aldı ve `IX_Leads_TenantId_Status_FirstReceivedAt_Id` eklendi (migration `LeadFirstReceivedAtIndexes`; dönem aralığı süzgeçleri aynı önek dizinden yararlanır). Sonuç: süzgeçsiz ≈3,1–3,5 → ≈1,5 sn, `status=New` ≈0,6 sn (değişmedi), `source=Referral` ≈1,5–1,8 sn (kaynak için dizin eklenmedi), `status+source` ≈0,3 sn. Kalan süre sayfa sorgularının kendisidir (≈300 ms/sayfa; lead satırı geniştir: çok sayıda uzun metin sütunu). Bu **ayrıştırılmadı**: sütun daraltma kayıt boru hattında alan seçimi gerektirir ve ayrı iştir. Kaynağa göre süzgeç için dizin gerekirse ölçümle eklenir.

**Kalan işler turu (7 Kasım 2026):**
- **Başarısız zamanlama yeniden denenir:** `ReportSchedule.Attempts` (migration `ReportScheduleAttempts`, varsayılan 0, geriye uyumlu). Başarısızlıkta aynı planlı an için 15 dk, sonra 60 dk gecikmeyle yeniden denenir; üçüncü başarısızlıkta sayaç sıfırlanır ve zamanlama bir sonraki döneme geçer. Teslimde sayaç sıfırlanır. Durdurulan (yetki/üyelik/modül bozuk) zamanlama yeniden denenmez. Testler: `ReportScheduleRetryTests` (birim) ve `One_failing_schedule_...` (entegrasyon, ilk yeniden deneme ≈15 dk sonra).
- **Salt okunur iki veri kümesi:** `crm.forecast-items` (tahmin fırsat satırı) ve `crm.sales-target-revisions` (hedef revizyonu). Depoları kimseye yazma izni vermez (`AnyPermissionFor` boş). Tahmin satırının görünürlüğü fırsat türünün kapsamıyla ve fırsatın **bugünkü** sorumlusuyla belirlenir (K7); tutar, olasılık ve kur sütunları `crm.opportunity-amounts.read` iznine bağlı hassas alandır. Para birimi yalnızca kaynak tutarda satırdadır; raporlama tutarı ve düzeltilmiş tutar sürüm başlığının para birimindedir, bu yüzden gösterilir ama toplanmaz. Test: `Forecast_item_and_target_revision_datasets_...` (mutlu yol, tutar izni, izinsiz okuma, tenant izolasyonu).
- **Yapılmadı:** lead sütun daraltma (önce doğrudan EF alt sınırı ölçülmeli; yoğun makinede güvenilir ölçüm alınamadı), 50 bin üstü hacim ve eşzamanlı yük ölçümü, ekip/departman/kanal kırılımı (Core.Contracts ekip sözleşmesi gerekir), dağıtım listesi, genel görünüm kartları dışa aktarması, elle görsel doğrulama.

**Excel çıktısı ve düzeltme geçmişi (7 Kasım 2026, ikinci tur):**
- **xlsx:** `ReportXlsx` (Core) CSV ile aynı `ReportResult`'tan çıktı üretir; ayrı hesap yoktur. Dış paket (OpenXML SDK, ClosedXML) **kullanılmadı**: bağımlılık, güvenlik taraması ve lisans yüzeyi eklememek için en küçük OOXML paketi elle yazılır (içerik türleri, ilişkiler, çalışma kitabı, stil, tek sayfa). Metin hücresi `t="inlineStr"` olduğu için Excel onu formül saymaz; CSV'deki kesme işareti kaçışı burada uygulanmaz (uygulansaydı hücrede görünürdü). XML'de geçersiz denetim karakterleri atılır, hücre 32.000 karaktere kesilir. Biçim `format` sorgu parametresiyle seçilir (`csv` varsayılan, `xlsx`); `ReportExport.ContentType` biçime göre doludur, indirme ve erişim parmak izi denetimi biçimden bağımsızdır. Zamanlama yalnızca CSV üretir (biçim seçimi zamanlama kaydında ayrı sütun ister; yapılmadı). Doğrulama: birim testleri paket yapısını, hücre düzenini ve formül metninin formül olmadığını XML ile denetler; **dosya gerçek Excel/LibreOffice'te açılarak denenmedi**.
- **`crm.forecast-adjustments`:** kategori/tutar/olasılık düzeltmeleri; eski/yeni değer metin olduğu için tutar iznine bağlıdır ve toplanmaz.

Dilim planı: 1–4 tamamlandı (kalanlar her dilimin "yapılmadı" listesindedir); sunucu tarafı toplama ve özet tablo kararı ayrı ADR ile karara bağlanacaktır.
