# ADR-0033: İçe aktarma takibi ve işlem günlüğü ekranı

- **Durum:** Taslak, 4 Kasım 2026 (kararlar ürün sahibiyle konuşuldu; "Açık noktalar" bölümüne bakın)
- **Karar:** Excel/CSV içe aktarma, iş (`ImportRun`) ve satır hatası (`ImportRowIssue`) kayıtlarıyla izlenir. Bu kayıtlar, mevcut hata kaynaklarıyla (e-posta, otomasyon, olay teslimi, rapor zamanlama) birlikte tek bir **işlem günlüğü** ekranında (`/yonetim/gunluk`) sunulur. Ekran üç kademede görünür: kullanıcı kendi içe aktarmasını, tenant yöneticisi firmanın tamamını, platform yöneticisi bütün firmaların özetini görür; kademeler rol izinleriyle verilir. İçe aktarma kayıt boru hattından geçer; kısmi başarı varsayılandır.
- **İlgili:** ADR-0007 (olay altyapısı), ADR-0013 (mükerrer), ADR-0016 (lookup), ADR-0027 (otomasyon), ADR-0028 (platform yönetimi ve veri görmeme ilkesi); yol haritası Ç7.1–Ç7.5, Y9.1, Ç2.3, Ç23.2, M1.2j, M1.7

## Problem

Excel taşıma, CRM pilotunun ön koşuludur (M1.7) ama bugün içe aktarma yoktur; yalnızca dışa aktarma vardır. Hata alan işlemler de dağınıktır: denetim kaydı `/yonetim/denetim`'de, olay teslim hataları yalnızca platform yöneticisinin ekranında, otomasyon hataları kendi ekranında, e-posta hataları ve rapor zamanlama hataları ise yalnızca `ILogger` çıktısındadır. Firma yöneticisi "ne yüklendi, neden 40 satır alınmadı, hangi e-posta gitmedi" sorusunu tek yerden yanıtlayamaz.

## Mevcut durum (koddan doğrulandı)

| Alan | Bugün | Kanıt |
| --- | --- | --- |
| Denetim kaydı | `core.AuditLog` (tenant verisi, RLS), `ExecutionSource.Import` zaten tanımlı, `CorrelationId` var | `RecordInfrastructure.cs`, `RecordContracts.cs` |
| Olay teslim hatası | Deneme sayısı, `LastError`, yeniden dene/atla; platform ekranı | `EventAdministration.cs`, `Pages/Platform/Events.razor` |
| Otomasyon çalışması | Durum, kısa `Note` ("kişisel veri içermez"), kira/yeniden deneme | `AutomationRun.cs` |
| E-posta kuyruğu | `Pending/Sent/Failed`; hata için kullanıcı ekranı yok | `EmailInfrastructure.cs` |
| Denetim ekranı | `/yonetim/denetim`, `core.audit.read`, CSV (50.000 satır sınırı) | `Pages/Admin/AuditLog.razor` |
| Import | Yok; Lead için "yapılmadı" olarak işaretli | `docs/yol-haritasi.md` M1.2j |
| XLSX kütüphanesi | Bulunamadı (`ReportExports.cs` tam incelenmedi); mevcut paketler: MailKit, QuestPDF, QRCoder vb. | `*.csproj` |

Doğrulanmadı (uygulamadan önce denetlenecek): e-posta ve rapor zamanlama hatalarının ekrana yansıtılabilecek bir alan taşıyıp taşımadığı; XLSX için kullanılacak paketin lisansı ve güvenlik taraması.

## Karar

### 1. İki katman, tek ekran

- **Katman A, içe aktarma takibi:** yeni kayıtlar (aşağıda).
- **Katman B, hatalı işlemler:** mevcut kaynakların salt okunur ortak görünümü. Yeni tablo açılmaz; her kaynak `IOperationFailureSource` benzeri bir sözleşmeyle `(kaynak, zaman, durum, kısa hata, ilgili kayıt)` satırı üretir. Kaynak modül kendi verisini kendi sorgular (modüller birbirinin tablosuna erişmez).

Ekran sekmelidir: **İçe aktarmalar**, **Hatalı işlemler**, **Denetim** (mevcut ekrana bağlantı).

### 2. Veri modeli (Katman A)

`core` şemasında, ikisi de `ITenantEntity` + `ICompanyShared`, `TenantIsolationSql.AddTable` ile RLS'li:

- `ImportRun`: kullanıcı, kayıt türü (`EntityKey`), dosya adı, boyut, **dosya özeti (SHA-256)**, şablon sürümü, mod (`DryRun` / `Apply`), durum (`Pending`, `Running`, `Succeeded`, `PartiallySucceeded`, `Failed`, `Cancelled`, `RolledBack`), toplam/oluşan/güncellenen/atlanan/hatalı satır sayıları, başlangıç/bitiş, kısmi başarı politikası, `CorrelationId`, kira alanları (işçi).
- `ImportRowIssue`: koşu, satır no, sütun anahtarı, **hata kodu**, ciddiyet (`Error`/`Warning`), kısa ileti. **Ham hücre değeri saklanmaz** (kişisel veri; `AutomationRun.Note` ilkesi). Koşu başına en çok 5.000 satır hatası saklanır; fazlası yalnızca sayılır.

Hata kodları kapalı bir listedir: `required-missing`, `format-invalid`, `lookup-unresolved`, `duplicate-exact`, `duplicate-possible`, `custom-field-invalid`, `permission-denied`, `business-rule`, `file-invalid`, `system-error`. Liste süzgeç ve "en sık hatalar" özeti için kullanılır.

### 3. Dinamik tanım: içe aktarılabilir kayıt türleri

İçe aktarma her kayıt türü için ayrı kodlanmaz. Modül, `IModule.Imports` ile (`Forms`, `Lookups`, `CustomizableEntities` kalıbı; varsayılan boş) kayıt türünü bildirir: `ImportDefinition(EntityKey, görünen ad, yazma izni, eşleşme/anahtar alanları, alan listesi)`. Alan listesi kayıt türünün alan tanımından ve firmanın özel alanlarından (Y9a) türetilir; sütun eşleme, şablon ve doğrulama bu metadatadan çalışır. Yeni bir türü (ör. Teklif, Ürün, Lojistik kayıtları) içe aktarılabilir yapmak, o modüle bir `ImportDefinition` eklemektir; motor, ekran ve günlük değişmez.

Firma yöneticisi, form tasarımındaki gibi (ADR-0021) "İçe aktarma tasarımı" ekranından hangi türlerin içe aktarılabilir olduğunu açar/kapatır, hangi alanların şablona gireceğini ve hangilerinin zorunlu olacağını seçer; kodun zorunlu kıldığı alanlar kapatılamaz. Türün modülü kapalıysa ya da lisansı salt okunursa (ADR-0028) tür listelenmez ve yazılamaz; arka plan işçisi bunu `IModuleLicense.IsWritableAsync` ile denetler.

### 4. Uygulama

- **Dosya okuma:** XLSX, Microsoft'un `DocumentFormat.OpenXml` paketiyle (MIT) akışla (SAX) okunur; CSV için mevcut yardımcılar. Tüm dosya belleğe alınmaz. Dosya boyutu, açılmış boyut (zip bombası), satır ve sütun sayısı için sınır konur; dış varlık/makro okunmaz. Paket CI'ın güvenlik taramasından geçmeden eklenmez.
- Her satır **kayıt boru hattından** geçer (izin, kapsam, hassas alan, özel alan, audit, outbox); doğrudan `DbContext` yazımı yoktur. Audit kaydı `Source = Import` ve koşunun `CorrelationId`'sini taşır.
- Arka planda, tenant başına kilitli işçiyle çalışır; checkpoint ile kesintiden sonra işlenmiş satır tekrar yaratılmaz (Ç7.2). Tenant başına eşzamanlı içe aktarma sınırı vardır (Ç2.3).
- **Kuru çalışma** aynı doğrulamayı yazmadan çalıştırır, sonucu aynı ekranda gösterir.
- Mükerrer: vergi no kesin eşleşme hata/atla, olası eşleşme uyarıdır (ADR-0013); otomatik birleştirme yapılmaz.
- Toplu otomasyon tetiklenmesi (ADR-0027): içe aktarma kaynaklı olaylar için kural derinlik/kuyruk davranışı ayrıca belirlenir (Dilim 3'te).

### 5. Kısmi başarı

Varsayılan **"hatasız satırları al"**; firma ayarıyla (dinamik tasarım ilkesi) "hepsi ya da hiç" seçilebilir. Gerekçe: binlerce satırlık taşımada tek hatalı satır tüm yüklemeyi durdurmamalı; hatalı satırlar hata raporuyla düzeltilip yeniden yüklenir. Güvenlik ağı: kuru çalışma önerilir ve toplu geri alma vardır.

**"Hepsi ya da hiç" kuralları:** (a) uygulama, aynı dosya özetiyle (hash) yapılmış ve **hatasız bitmiş** bir kuru çalışmadan sonra, 24 saat içinde başlatılabilir; kuru çalışma dosya büyüklüğüne bakılmaksızın zorunludur. (b) Bu mod en çok 10.000 satıra izin verir; her satır kayıt boru hattı, audit ve outbox yazdığı için tek işlem uzun kilit tutar. Daha büyük dosya yalnızca "hatasız satırları al" modunda, parçalı (checkpoint'li) çalışır. Sınır ayarı platform tarafından belirlenir. "Hatasız satırları al" modunda kuru çalışma önerilir, zorunlu değildir.

### 6. Hata raporu ve geri alma

- **Hata raporu indir:** orijinal satır + "hata" sütunuyla CSV/XLSX; dosya, yükleme anında üretilir ve süreli indirme olarak sunulur, kalıcı saklanmaz. CSV enjeksiyon koruması `ReportCsv` ile aynıdır.
- **Geri alma** (Dilim 4): `ImportRun` ile işaretli, yaratılmış kayıtlar gerekçeli ve yetkili tek komutla silinir; sonradan değiştirilmiş veya başka kayda bağlanmış kayıt silinmez, telafi raporlanır (Ç7.3).

### 7. Görünürlük ve izinler

Üç kademe, hepsi rol izniyle (rol şablonlarına, ADR-0005, eklenir):

| İzin | Kapsam |
| --- | --- |
| `core.imports.run` | İçe aktarma başlatma; kendi koşularını görür |
| `core.imports.read` | Firmanın bütün koşularını ve hatalı işlemleri görür |
| `core.imports.rollback` | Geri alma |
| Platform: `PlatformOnly` ekran | Bütün firmaların özeti (aşağıda) |

Satır hatasında gösterilen sütun/alan, kullanıcının alan düzeyi yetkisine tabidir. Yetki sunucuda ve sorgu seviyesinde uygulanır.

**Platform yöneticisi görünümü ve ADR-0028 çatışması.** ADR-0028, platform yöneticisinin firma verisini görmemesini şart koşar. Bu yüzden platform görünümü **yalnızca özet** gösterir: firma, zaman, kayıt türü, durum, sayılar, hata kodu dağılımı. Dosya adı ve satır hata ayrıntısı gösterilmez (dosya adı müşteri adı içerebilir); ayrıntıya yalnızca "Destek erişimi" (Y1.3) ile girilir. Özet için **ayrı bir platform tablosu** açılır (`platform.ImportRunSummaries`, RLS dışı, `TenantId` taşır; `TableInventoryTests`'te platform tablosu olarak sınıflandırılır ve `tenant-export`/`tenant-delete` kapsamına girer). Mevcut olay ekranına sayaç eklenmez: olay ekranı teslim satırı temellidir, sayaç koşu/zaman/tür kırılımını ve geçmişi veremez, saklama süresi de farklıdır. Satır `ImportRun` kapanırken aynı işlemde yazılır ve yalnızca şunları taşır: firma, kayıt türü, mod, durum, sayılar, süre, hata kodu dağılımı. Dosya adı, kullanıcı ve satır ayrıntısı yoktur. Kişisel veri taşımadığı için saklama süresini platform belirler (varsayılan 365 gün); tenant'ın saklama ayarından etkilenmez.

### 8. Saklama

- Süreleri **her firma kendi belirler** (firma ayarı, Y6). İki ayar `core.imports.*` anahtarlarıyla tanımlanır ve `Choice` tipindedir; seçenek listesi platformun alt/üst sınırlarını belirler, dolayısıyla sınır dışı değer girilemez:
  - Satır hataları: 7 / 14 / **30 (varsayılan)** / 60 / 90 gün.
  - İçe aktarma özeti (`ImportRun`): 90 / 180 / **365 (varsayılan)** / 730 gün.
- Süre kısaltılırsa temizlik işçisi bir sonraki çalışmada eskiyi siler; uzatılırsa silinmiş veri geri gelmez (ayar ekranında uyarı).
- Boyut kaygısı yoktur: satır başına yaklaşık 100–200 bayt, değer saklanmadığı için 100.000 hatalı satır bile birkaç yüz KB ile on MB arasındadır.
- Temizlik, `CommunicationCleanupWorker` kalıbını izler; her silme turu denetim kaydına sayıyla yazılır.

### 9. Dört test

Mutlu yol (yükle, uygula, sonuç); hata/doğrulama (her hata kodu, biçim, dosya düzeyi); tenant izolasyonu (A, B'nin koşu ve hatasını göremez); yetki (izinsiz başlatma, görme ve geri alma reddedilir; kullanıcı başkasının koşusunu göremez).

## Dilimler

1. **Dilim 1:** Bu ADR + `ImportRun`/`ImportRowIssue` modeli, migration (RLS), okuma API'si, "İçe aktarmalar" sekmesi, izinler.
2. **Dilim 2:** `IModule.Imports` tanımı ve metadata güdümlü CSV içe aktarma motoru, kuru çalışma, hata raporu indirme. İlk tanım Lead.
3. **Dilim 3:** Firma ve Kişi tanımları (ortak ana veri, ADR-0006/0011; mükerrer, lookup), "İçe aktarma tasarımı" ekranı, toplu otomasyon davranışı.
4. **Dilim 4:** XLSX desteği (`DocumentFormat.OpenXml`), geri alma, aynı dosya uyarısı, kayıtlı sütun eşlemesi, "hepsi ya da hiç" modu.
5. **Dilim 5:** "Hatalı işlemler" sekmesi (e-posta, otomasyon, olay, rapor zamanlama).
6. **Dilim 6:** Saklama işçisi, bitiş bildirimi, hata oranı eşik uyarısı, platform özet görünümü.

## Sonuçlar

- Firma yöneticisi içe aktarma ve işlem hatalarını tek yerden görür; hata raporu düzeltip yeniden yüklemeyi mümkün kılar.
- Ham değer saklanmadığı için günlük tablosu kişisel veri yükü taşımaz; bedeli, hatalı satırın içeriğinin ekranda değil indirilen raporda görünmesidir.
- Platform görünümü özetle sınırlı kalır; destek ihtiyacı için Y1.3 gerekir.
- Katman B her kaynak modülden küçük bir sözleşme uygulamasını gerektirir.

## Kararlar (4 Kasım 2026, ürün sahibiyle)

- İçe aktarılabilir tür modül metadatasıyla dinamik eklenir (bölüm 3).
- XLSX için ücretsiz ve birinci taraf `DocumentFormat.OpenXml` (bölüm 4). EPPlus (5. sürümden itibaren ticari kullanımda ücretli lisans) elenir.
- Platform özeti ayrı tablodur (bölüm 7).
- "Hepsi ya da hiç": zorunlu hatasız kuru çalışma + 10.000 satır sınırı (bölüm 5).
- Saklama süreleri firma ayarıdır; platform sınırları seçenek listesindedir (bölüm 8).
- İçe aktarılan kayıtlar varsayılan olarak kural çalıştırmaz ve atama e-postası göndermez; firma tür başına açar (6 Kasım 2026).

## Açık noktalar

- `DocumentFormat.OpenXml` sürümünün eklenmeden önce lisans ve NuGet güvenlik taramasıyla doğrulanması.
- Sınırların (10.000 satır, 5.000 hata satırı, dosya boyutu) pilot ölçümüyle doğrulanması.
- Güncelleme ve dış sistem kimliği (Ç7.3): anahtar stratejisi güncelleme eklenirken kesinleşir.

## Uygulama durumu

**Dilim 1:** uygulandı (günlük okuma, modeller, izinler).

**Dilim 2:** uygulandı (`gseker/import-motoru-dilim2`).

- `ImportDefinition` (`IModule.Imports`): kayıt türü, görünen ad, oluşturma izni, sütunlar, `MatchFields`. Tanım açılışta doğrulanır. İlk tanım müşteri adayıdır (`CrmImports`); sorumlu yükleyene atanır, firma ve kişi bağı lookup gerektirdiği için Dilim 3'tedir.
- Sütunlar `ImportSchema` ile tanımdan, firmanın özel alanlarından ve seçim listelerinden türetilir. Görme yetkisi olmayan hassas alan şablona girmez; dosyada varsa tanınmayan sütun gibi yok sayılır.
- Hücre yorumu `ImportRows`'tadır. Sayı Türkçe biçimlidir; "12.5" gibi yanlış okunabilecek yazım reddedilir (Ç7.1).
- Dosya düzeyi denetim istekte yapılır; satırlar arka planda işlenir. Uç nokta 202 döner, ekran koşuyu sorgulayarak izler; ilerleme 50 satırda bir yazılır.
- Her satır kayıt boru hattından geçer. Depo varlığı doğrulama bitmeden izlemeye aldığı için **reddedilen satırdan sonra DI kapsamı atılır**: aksi halde reddedilen kayıt sonraki satırla yazılabilirdi. Başarılı satırlar kapsamı en çok 50 satır paylaşır (ölçüm: satır başına kapsamda her satır yaklaşık 29 SQL çalıştırıyordu, çoğu yetki sorgusu).
- Kuru çalışma aynı yoldan geçer, satırın işlemi geri alınır. Doğrulama birebir aynıdır; yalnızca günlüğe koşu satırı yazılır.
- Mükerrer yalnızca uyarıdır (`duplicate-possible`): `MatchFields` kullanıcının kapsamında sorgulanır, dosya içi tekrar da uyarılır.
- Tenant başına tek koşu `sp_getapplock` oturum kilidiyle sağlanır (409). Kilit koşu sonunda açıkça bırakılır: kapanan bağlantı havuza döner ve SQL oturumu kilidi tutmaya devam eder. Kilit alındığında "çalışıyor" görünen koşu çökmüş sayılır ve `Failed` yapılır.
- Hata raporu bellekte bir saat tutulur, veritabanına yazılmaz ve **yalnızca yükleyene** verilir: dosya, firmanın günlüğünü gören birinin görme yetkisi olmayan değerleri taşıyabilir. Süresi dolunca 410 döner.

**Bu ADR'den bilinçli sapmalar (Dilim 2):**

- Arka plan işi kalıcı kuyruktan değil **uygulama içi görevden** çalışır: dosya yalnızca koşu boyunca bellektedir, checkpoint yoktur ve dosya başına sınır **2.000 satırdır**. Uygulama kapanırken koşu sıradaki satırda durur ve `Cancelled` olur; çökerse sonraki koşu onu `Failed` yapar. Yarıda kalan dosya yeniden yüklenirse alınmış satırlar yinelenir (mükerrer uyarısı çıkar); Ç7.2 kabulü bu yüzden henüz karşılanmıyor. Kalıcı kuyruk geçici dosya deposu ister.
- Hız pilot ölçümü bekliyor: yerel test ortamında (SQL günlüğü açık) kuru çalışma satır başına yaklaşık 0,3 saniye, uygulama 0,7 saniye sürdü. Üretim süresi ölçülmedi.
- Yalnızca oluşturma vardır; `UpdatedCount` ve `SkippedCount` bu dilimde 0 kalır.
- Hata raporu tek uygulama örneği varsayar (`IMemoryCache`). Birden çok örnekte rapor başka örneğe düşerse 410 döner.

**Dilim 3:** uygulandı (`gseker/import-dilim3`).

- Firma ve Kişi tanımları eklendi. Müşteri adayı da firma bağı ve sorumlu sütunu aldı. Yükleme sırası önce firmalar, sonra kişiler ve müşteri adaylarıdır.
- **Bağlantı sütunları:** `ImportFieldType.Reference` süzgeçleri sırayla dener (firma: vergi numarası, sonra tam ad) ve tek kayıt ister; bulunamayan ya da birden çok eşleşen değer `lookup-unresolved` olur. Arama kayıt boru hattından geçer, kullanıcının göremediği kayıt bulunamaz. `ImportFieldType.User` e-postayı firmanın etkin üyesine çözer; destek üyeliği sayılmaz (ADR-0028).
- **Mükerrer (ADR-0013):** `ImportMatchRule`. Vergi numarası kesindir ve `duplicate-exact` satır hatasıdır: kayıtlı olanı depo reddeder (`RecordFieldError.Code`), dosya içi tekrarı motor reddeder, böylece kuru çalışma uygulamayla aynı sonucu verir. Ad, e-posta ve telefon yalnızca `duplicate-possible` uyarısıdır. Bu bölümdeki "hata/atla" seçeneğinden hata seçildi; atlama yoktur.
- **İçe aktarma tasarımı** (`/yonetim/ice-aktarma-tasarimi`, `meta.ImportDesigns`, `core.customization.manage`): firma türü açar/kapatır, sütun çıkarır ve zorunlu kılar; kodun zorunlu tuttuğu sütun kapatılamaz. Her değişiklik denetim kaydına yazılır. Kapalı tür listelenmez, 404 döner.
- **Toplu otomasyon:** varsayılan kapalıdır. Olay `EventReasons.Import` nedeniyle yazılır; kural çalışması ve atama e-postası bu olayları atlar, veriyi tutarlı tutan abonelikler çalışır. Gerekçe: geçmiş veri taşınırken yüzlerce kural ve e-posta üretilmemeli. Firma tasarımda tür başına açabilir; açıkken olay nedensiz yazılır ve tek tek kayıt açılmış gibi davranır. Kural kuyruğu için ayrı hız sınırı yoktur.

**Dilim 3 sınırları:**

- Ad benzerliği içe aktarmada tam eşleşmedir ("Acme A.Ş." ile "ACME Anonim Şirketi" uyarı vermez); ünvan anahtarı mükerrer önerisi ucundadır. Kişide ad-soyad eşleşmesi yoktur: e-postası ve telefonu olmayan kişi dosyası yeniden yüklenirse uyarısız çoğalır.
- Kuru çalışmada dosyadaki yeni firmalar yazılmadığı için aynı dosyanın kişileri sınanamaz; firmalar uygulanmadan kişi kuru çalışması `lookup-unresolved` verir.
- Kişide firma bağı yalnızca birincil firmadır; rol, departman ve ana irtibat elle girilir (ADR-0011).

**Dilim 4a:** uygulandı (XLSX desteği, 12 Kasım 2026).

- `.xlsx` çalışma kitabı CSV ile aynı yoldan geçer (kuru çalışma, boru hattı, günlük, yetki). Biçim dosya adının uzantısından seçilir (`ImportFiles`); satır kurma ve hücre temizliği CSV ve XLSX için ortaktır (`ImportFiles.Build`), yani boş satır numaralaması, kırpma ve 2.000 satır sınırı aynıdır. Okuyucu `IImportWorkbookReader` ile Core'a soyutlanır, gerçeklemesi `Infrastructure/Imports/ImportWorkbookReader`'dadır.
- **Sapma (paket):** bölüm 4 ve 116 `DocumentFormat.OpenXml` diyordu; depoda GTİP içe aktarması için zaten **MiniExcel** vardı ve güvenlik taramasından geçiyor. İkinci bir Excel paketi eklenmedi, aynı okuyucu deseni (`GtipFileReader`) kullanıldı. "OpenXml sürümünün taranması" açık noktası böylece kapandı.
- **Hücre türleri:** Excel hücresi gerçek tiptedir. Sayı Türkçe ondalık metne (`1250,5`), tarih `gg.aa.yyyy`'ye, mantıksal değer `Evet/Hayır`'a çevrilir ve mevcut hücre yorumundan geçer; böylece CSV'deki "12.5 reddedilir" kuralı bozulmaz. Saatli tarih `gg.aa.yyyy SS:dd` olur (tarih sütunu bunu reddeder). **Bilinen sınır:** telefon gibi sayı olarak saklanmış değerin baştaki sıfırı Excel'de zaten yoktur; metin biçimli hücre kullanılmalıdır.
- **Güvenlik:** yalnızca ilk sayfa okunur; zip imzası ("PK") ve açılmış boyut (200 MB, zip bombası) denetlenir; fiziksel satır okuması 100.000'de, sütun okuması 201'de, dolu satır okuması 2.001'de durur (büyük ama boş biçimli sayfa belleği doldurmaz). Formül hücresinde yalnızca kayıtlı sonuç okunur; makro ve dış bağlantı çalışmaz. Bozuk dosya 400 değil, `file-invalid` hatalı `Failed` koşu olarak günlüğe yazılır; `.xls` uzantısı ise uçta 400 ile reddedilir.
- Hata raporu ve şablon CSV kalır (ADR'deki "CSV/XLSX" ikinci biçim sonraya bırakıldı): Türkçe Excel UTF-8 BOM'lu CSV'yi doğrudan açar ve rapor düzeltilip yeniden yüklenebilir.
- **Kalan (Dilim 4):** geri alma, aynı dosya uyarısı, kayıtlı sütun eşlemesi, "hepsi ya da hiç" modu.
