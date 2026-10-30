# ADR-0019: Teklif yönetimi (M1.5) — analiz ve ilk dilim

Durum: Kısmen uygulandı (çekirdek, yaşam döngüsü, ekranlar, PDF, paylaşım/kabul, onay kuralları, karşılaştırma, e-posta, raporlar, kabul sonrası süreçler, küçük tamamlamalar, fırsat entegrasyonu ve çok adımlı onay/vekâlet; sipariş/sözleşme modülü yok). Tarih: 2026-10-30.

## Mevcut durum (analiz anında, kodla kanıtlı; güncel durum en alttaki "Uygulama durumu"nda)

Tarama: `src/**/*.cs` ve `src/Modulix.Web/Pages` içinde `Quote|Teklif|Proposal`. Sonuç: teklif varlığı, tablosu, uç noktası ve ekranı **yoktur**.
`Proposal` yalnızca fırsat aşaması anahtarıdır (`CrmModule.cs:321`), `Opportunity.Amount` tahmini tutardır (ADR-0017/7). Fiyat hesabı
(`IPriceCalculator`, `POST /api/crm/pricing/calculate`) satır başına çalışır ama sonucu saklayan belge yoktur (ADR-0018).
Tenant-başına-DB kabulü (ADR-0009) kodla uyumludur: yeni tablolar `ModulixDbContext`'e (tenant iş DB'si) eklenir.

| Süreç/Ekran | Mevcut | Eksik/risk | Uygulanacak | Öncelik | Bağımlılık | Kabul |
|---|---|---|---|---|---|---|
| Hesaplama | Satır fiyatı var, belge toplamı yok | Genel indirim, çoklu vergi, opsiyonel, tekrarlayan tutar tek yerde hesaplanmıyor | `QuoteCalculator` (yapıldı) | P0 | — | Birim testler (yapıldı) |
| Yaşam döngüsü | Yok | Onaysız sunum, geçersiz sürüm kabulü | `QuoteRules` (yapıldı) + iş akışı servisi | P0 | Veri modeli | Sunucuda engel + entegrasyon testi |
| Veri modeli | Yok | Snapshot, sürüm, aile, RowVersion | `Quote`, `QuoteLine`, onay/gönderim/kabul tabloları, migration + RLS | P0 | — | **Yapılmadı** |
| Revizyon / eşzamanlılık | Yok | Aynı ailede çelişen kabul | Aile başına tek kabul (filtreli benzersiz indeks) | P0 | Veri modeli | **Yapılmadı** |
| İç onay | Genel onay altyapısı yok (ADR-0018) | Eşik/kural yönetimi | Kural tablosu + değerlendirme (dördüncü dilim); sıralı/paralel onay yok | P1 | Veri modeli | **Kısmen uygulandı** |
| PDF, paylaşım, müşteri kabulü | PDF/dosya altyapısı doğrulanamadı | Belirteç, hız sınırı, hash | Üçüncü dilim (aşağıda) | P1 | Veri modeli | **Uygulandı** (gönderim adaptörü hariç) |
| Ekranlar (liste, düzenleme, detay, onay merkezi, karşılaştırma, müşteri sayfası, ayarlar, rapor) | Yok | — | `Pages/Crm/Quote*.razor` | P1 | API | **Yapılmadı** |
| Sipariş/sözleşme dönüşümü | Hedef modül yok | Sahte entegrasyon riski | Yalnızca entegrasyon sözleşmesi | P2 | Teklif kabulü | **Yapılmadı** |

## Bu dilimde yapılanlar

- `Modulix.Core.Contracts/Pricing/QuoteCalculator.cs`: sunucu tarafı tek hesap kaynağı. Sıra: satır tutarı → satır indirimi → genel indirim
  (indirim sonrası tutarla orantılı dağıtım, yuvarlama farkı en büyük satıra) → satır başı vergi → oran gruplarına özet. Vergi dahil/hariç;
  seçilmeyen opsiyonel/alternatif ana toplamdan ayrı (`OptionalNet`); paket bileşenleri toplanmaz; tek seferlik ve tekrarlayan tutarlar
  ayrı, sözleşme süresi bilinmeden genel toplam üretilmez. Oranlar dışarıdan gelir (mevzuat sabiti yok). 14 birim test.
- `Modulix.Modules.Crm/Quotes/QuoteRules.cs`: ticari durum, iç onay ve gönderim durumu ayrı sabitler; eylem ön koşulları
  (onaysız sunum engeli, kabul anında geçerlilik yeniden denetimi, geçersizleşmiş/iptal/sonuçlanmış sürümde kabul engeli, yalnızca taslakta
  ticari düzenleme) açıklama kodlarıyla. 14 birim test.

## Politika kararları (uygulanacak)

- Yeni **taslak revizyon açmak** önceki sunulmuş sürümü hemen geçersiz kılmaz; yeni sürüm **sunulduğunda** önceki `Superseded` olur
  (müşteri açıkken eski teklifi kabul edebilir, sunum anına kadar). Aile başına en fazla bir `Accepted` sürüm (benzersiz indeks).
- Kabul, durum güncellemesi `Version` koşuluyla atomik yapılır; kabul/iptal yarışında yalnızca biri başarılır, tekrar çağrı aynı sonucu döner.
- Sunulan sürümde fiyat, açıklama, vergi, kur ve koşullar kalem/başlıkta kopya olarak saklanır; katalog değişimi etkilemez.
- Maliyet/kârlılık ayrı izinle (`crm.quote-costs.read`) sunucuda süzülür; PDF ve dışa aktarma hiç okumaz.

## Yapılmayanlar (dürüst durum)

Veri modeli, migration, kayıt depoları, izinler, uç noktalar, onay kuralları, PDF, paylaşım/kabul, dönüşüm, ayarlar, raporlar ve
**tüm ekranlar** yazılmadı; entegrasyon testleri yok (SQL Server gerekir). Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (ikinci dilim: veri modeli, sunucu yaşam döngüsü, ilk ekranlar)

**Uygulandı ve entegrasyon testiyle doğrulandı** (`QuoteTests`, 12 test; saf çekirdek için `QuoteCalculatorTests` 14, `QuoteRulesTests` 14):

| Konu | Karar / davranış |
|---|---|
| Veri modeli | `crm.Quotes` (başlık, aile/revizyon, üç ayrı durum alanı, para birimi, vergi biçimi, hesaplama politikası sürümü, koşullar, sunucu toplamları), `crm.QuoteLines` (ürün kopyası: kod/ad/açıklama/birim fiyat/vergi oranı; kararlı anahtar; rol; tekrarlayan dönem; maliyet), `crm.QuoteEvents` (tarihçe). Migration `QuoteManagement`; üç tablo RLS'li; bileşik FK'ler tenant sınırını aşmaz. Backfill gerekmez (yeni tablolar); mevcut kayıt yok. |
| Hesap | Sunucu `QuoteCalculator`; kalem ve başlık toplamı her kalem/indirim değişikliğinde aynı işlemde yeniden hesaplanır. |
| Yetki | İndirim, elle fiyat/serbest kalem, maliyet ayrı izin; ihlal API'de 422 ile reddedilir. Katalog fiyatı listeden gelir; listeden farklı fiyat elle fiyat izni ister. Pasif ürün yeni kaleme eklenemez. |
| Yaşam döngüsü | Onaysız sunum, geçersiz/iptal/süresi dolmuş sürümün kabulü, tekrarlanan kabul/iptal sunucuda engellenir; kabul/iptal yarışında sürüm denetimiyle yalnızca biri kazanır. Kabul anında geçerlilik yeniden denetlenir. |
| Revizyon | Kabul edilmiş sürümden revizyon açılmaz; ailede tek açık taslak; yeni sürüm sunulunca eski `Superseded`. Çoğaltma yeni aile ve yeni numara. |
| Tenant | Başka tenant'ın teklif/kalem/tarihçesi 404; başka tenant'ın firmasıyla teklif açılamaz. |
| Ekranlar | `Teklifler` (hızlı görünümler, süzgeçler, sayfalama, aile bazlı görünüm), teklif formu + kalem editörü + işlem çubuğu + tarihçe + revizyon listesi, `Teklif onayları`. Derleme doğrulandı; **tarayıcıda uçtan uca gezilmedi** (bkz. rapor). |

**Hâlâ yapılmadı (üçüncü dilimden sonra güncel liste en altta):** gönderim adaptörü ve durumları, revizyon karşılaştırma ekranı, onay kuralı yönetimi (eşik dışı kurallar, sıralı/paralel onay, vekâlet, hatırlatma, kural sürümü kaydı), kur sabitleme ekranı, sipariş/sözleşme dönüşümü (hedef modül yok; yalnızca sözleşme), fırsat entegrasyonları (kabulde kazanma politikası, birincil teklif), aktivite entegrasyonu (takip görevi, geçerlilik hatırlatması), zamanlanmış süre dolumu işi, yönetim ayarları ekranı, raporlar, dışa aktarma, kayıtlı görünümler. Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (üçüncü dilim: PDF, paylaşım bağlantısı, müşteri kabulü)

**Uygulandı** (`QuoteSharingTests` 10 entegrasyon testi, `QuoteSharingRulesTests` birim testleri; PDF görünümü ve müşteri sayfası tarayıcıda/PNG çıktısında gözle denetlendi):

| Konu | Karar / davranış |
|---|---|
| PDF kütüphanesi | **QuestPDF** (kullanıcı seçimi). Community lisansı yıllık geliri 1 milyon USD altındaki şirketlere ve açık kaynağa ücretsizdir; üstü ücretli lisans ister. Lisans uygunluğu ürün sahibinin sorumluluğundadır. |
| Belge | Teklif sunulunca (`present` uç noktası) sabit belge üretilir: PDF baytları (`varbinary(max)`, tenant DB, RLS), baytların SHA-256 özeti, şablon sürümü ve müşteri sayfasının gösterdiği veri özeti (JSON). Revizyon başına tek belge (benzersiz indeks); eklenir, değiştirilmez. Üretilemezse sunum geri alınmaz; belge `POST /documents` ile ya da ilk bağlantıda üretilir (o durumda firma kartı güncel haliyle okunur). Maliyet ve iç not belgeye hiç girmez. |
| Paylaşım bağlantısı | 256 bit rastgele belirteç; veritabanında yalnızca SHA-256 özeti, yöneticiye son 4 karakter. Ham belirteç yalnızca oluşturma yanıtında bir kez döner. Süre (varsayılan geçerlilik+7 gün, en çok 180), iptal (gerekçeli), ilk görüntüleme kaydı. Belirteç URL'nin `#` parçasındadır (`/teklif/{firma}#t=...`) ve API'ye gövdede gider: sunucu erişim günlüğüne ve Referer'a girmez. |
| Müşteri sayfası | Oturumsuz; tenant kısa adından sunucuda çözülür, "crm.quote-customer" sistem kimliğiyle (yalnızca teklif okuma + karar kaydı izinleri) çalışır. Yalnızca belgenin sabit verisini gösterir. Hız sınırı (IP+tenant, karar için daha sıkı), 8 KB gövde sınırı, `no-store`/`no-referrer`/`noindex`, bilinmeyen/bozuk/başka tenant belirteci için tek tip 404. |
| Kabul/ret | Açık kabul eylemi: ad soyad, onay kutusu ve onay cümlesi (sunucuda üretilir ve karar kaydına yazılır), görülen belgenin özeti (eskiyse reddedilir). Ret gerekçe ister. Karar teklif geçişiyle **aynı işlemde** ve sürüm denetimiyle yazılır; eşzamanlı iki kabulden biri 409 alır. Geçerlilik kabul anında yeniden denetlenir. Kanıt: imzalayan, ünvan, e-posta, belge özeti, onay metni, maskeli istemci adresi, kullanıcı aracısı. |
| Durumlar | Yeni revizyon sunulunca eski bağlantı yalnızca bilgi mesajı gösterir; iptal/süre dolmuş bağlantı içerik göstermez; kabul/ret sonrası sonuç sayfası. |
| Yetki | Yeni `crm.quotes.share` (belge üretimi sunum izniyle de yapılır; bağlantı oluşturma/iptal/listeleme yalnızca `share`). Belge okuma ve müşteri kararları `crm.quotes.read`. |
| Yazma modeli | Belge, bağlantı ve karar teklif toplamının parçasıdır; ayrı kayıt türü değildir. Teklif güncellemesiyle aynı işlemde (`QuoteWorkflowGate.Effect`) yazılırlar: tarihçe satırı ve sürüm denetimi ortaktır. Teklif değerleri değişmediği için kayıt sürümü artmaz. |
| Ayarlar | `crm.quote.seller-details`, `crm.quote.footer-note` (belge başlığı/alt bilgisi); satıcı adı `core.profile.display-name`. |

**Bu dilimde de yapılmadı (dürüst durum):** e-posta/gönderim adaptörü ve teslim durumları (`DeliveryStatus` hep `NotSent`; bağlantıyı kullanıcı iletir), sunumda belge üretilemezse otomatik yeniden deneme, belge şablonu özelleştirme (logo, çok dilli), müşteri sayfasında revizyon karşılaştırması, görüntüleme sayacı (yalnızca ilk görüntüleme), eşzamanlı belge üretimi yarışının (benzersiz indeks ihlali) entegrasyon testi, kabul sonrası fırsat/sipariş/sözleşme etkileri, klavye-yalnız ve ekran okuyucu geçişinin elle denenmesi. Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (dördüncü dilim: onay kuralları ve revizyon karşılaştırma)

**Uygulandı** (`QuoteRuleTests` 5 entegrasyon testi, `QuoteApprovalAndComparisonTests` 8 birim testi; ekranlar tarayıcıda gezildi):

| Konu | Karar / davranış |
|---|---|
| Onay kuralı | `crm.QuoteApprovalRules` (RLS'li, migration `QuoteApprovalRules`): ad, öncelik, etkin, indirim eşiği %, genel toplam eşiği, para birimi, isteğe bağlı onaylayan. Yönetim izni `crm.quote-rules.manage`. En az bir koşul zorunlu (veritabanı check kısıtı + depo doğrulaması). |
| Değerlendirme | Saf `QuoteApprovalEvaluator`: bir kuralın girilen koşulları birlikte sağlanmalı (VE), kurallardan biri yeter (VEYA); eşikler "aşarsa" (büyüktür); tutar koşulu para birimi belirtilmişse yalnızca o para biriminde. Mevcut genel eşik ayarları korunur ve kurallarla birlikte uygulanır (biri yeterli). Eşleşme yoksa onaya gönderim gönüllüdür (davranış değişmedi). |
| Sunum engeli | Eşleşen kural ya da eşik varken onay alınmadan sunum sunucuda engellenir (409). Onay gereksinimi `GET /api/crm/quotes/{id}/approval-requirements` ile döner; teklif detayı ve onay merkezi gerekçeyi gösterir. |
| Kural sürümü kaydı | Onaya gönderim tarihçesine eşleşen kuralların adı, sürümü ve eşleşme gerekçesi yazılır (ör. "Yüksek indirim (sürüm 1): İndirim %20 (eşik %10)"). |
| Onaylayan kısıtı | Eşleşen bir kural onaylayan atamışsa onay/ret/değişiklik isteği yalnızca atananlardan kabul edilir (API 403; eylem listesi `approver-restricted` nedenini verir). Kendi gönderdiğini onaylama yasağı ayrıca geçerlidir. Onaylayan atanmamış kural onay izni olan herkese açıktır. |
| Karşılaştırma | `GET /api/crm/quotes/{id}/compare?with={otherId}`: aynı ailenin iki revizyonu; kalemler kararlı anahtarla eşlenir (ad değişimi "değişti"dir), başlık/geçerlilik/koşul ve tutar farkları. Maliyet ve iç not girmez; iki teklif de okuma kapsamında olmalı (başka tenant 404). Ekran `crm/teklifler/{id}/karsilastir`, revizyon listesinden "Bununla karşılaştır". |
| Ekranlar | `Teklif kuralları` (`crm/teklif-kurallari`), karşılaştırma, onay merkezinde gerekçe ve yetkisiz onaylayan için pasif düğmeler. |
| Düzeltme | Bool'a bağlı `select` kutuları ilk açılışta boş görünüyordu (`True`/`true` uyuşmazlığı); katalog sayfalarında da düzeltildi. |

**Dördüncü dilimde de yapılmadı (dürüst durum):** sıralı/paralel çok adımlı onay (aynı teklifte birden çok onaylayanın hepsi), vekâlet/devir, onay hatırlatması ve zaman aşımı, marj tabanlı kural (maliyet gerekir), kural değişikliğinde bekleyen onayların yeniden değerlendirilmesi (değerlendirme karar anında canlı yapılır, gönderim anındaki kural yalnızca tarihçededir), kural yönetimi ekranının klavye-yalnız denemesi, karşılaştırmada alan bazlı metin farkı vurgusu. Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (beşinci dilim: e-posta gönderimi ve gönderim durumu)

**Uygulandı** (`QuoteDeliveryTests` 3 entegrasyon testi; arayüz ve yerel Mailpit'te gönderim tarayıcıda denendi):

| Konu | Karar / davranış |
|---|---|
| Gönderim | Paylaşım bağlantısı oluştururken "e-postayla gönder" seçilirse müşteriye bağlantı e-postası platform kuyruğuna (`IEmailQueue`) yazılır ve arka plan işleyicisi gönderir. Ham belirteç sonradan okunamadığı için gönderim yalnızca bağlantı oluşturulurken yapılır; yeniden gönderim yeni bağlantı demektir (eskisi gerekirse iptal edilir). Alıcı e-postası zorunlu (422), `crm.quotes.share` izni gerekir. |
| Şablon | Konu tek satır; ad, başlık ve firma adı HTML'e kodlanarak eklenir. İçerik yalnızca bağlantı, teklif no/başlık, geçerlilik tarihi ve satıcı adıdır; PDF eklenmez, maliyet/iç not girmez. |
| İzleme | `EmailMessage.Reference` / `platform.EmailMessages.Reference` (platform migration `EmailReference`, boş değerli sütun + indeks): `crm.quote-link:{bağlantı}`. Tenant DB ile platform DB arasında FK/join yoktur; durum tenant filtresiyle sorgulanır. |
| Dürüst durum | `NotSent` (e-posta yok), `Pending` (kuyrukta), `Processing` (deneme başladı/tekrar bekliyor), `Succeeded` = **sağlayıcı (SMTP sunucusu) kabul etti** — alıcıya ulaştı anlamına gelmez, `Failed` (kalıcı hata ya da deneme hakkı bitti; hata kısaltılarak gösterilir). Gönderen/SMTP yapılandırılmamışsa e-posta `Pending` bekler; sahte başarı yoktur. Kuyruğa yazılamazsa bağlantı yine oluşur, durum `Failed` olur. |
| Teklif durumu | `DeliveryStatus` başlık alanı e-postası olan en son bağlantının durumuna eşitlenir. Eşitleme bağlantı listesi okunurken yapılır (kuyruk işleyicisi tenant DB'sine yazmaz); değişim tarihçeye "Gönderim durumu güncellendi" olarak yazılır. |
| Geliştirme güvenliği | Geliştirme SMTP'si yerel Mailpit'tir (`appsettings.Development.json`); gerçek müşteriye e-posta gitmez. Testler gönderim durumunu kuyruk satırı üzerinden doğrular. |

**Beşinci dilimde de yapılmadı (dürüst durum):** teslim/okundu bildirimi (SMTP bunu vermez; "teslim edildi" hiçbir yerde iddia edilmez), geri dönen/sıçrayan e-posta (bounce) işleme, alıcıya hatırlatma ve süre dolumu bildirimi, e-posta şablonu özelleştirme/çok dilli, gönderimde PDF eki, toplu gönderim, durumun arka planda eşitlenmesi (şimdi liste okunurken), kalıcı hatalı bir e-postanın yeniden denenmesi için ekran düğmesi. Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (altıncı dilim: raporlar ve dışa aktarma)

**Uygulandı** (`QuoteReportTests` 3 entegrasyon testi; rapor ekranı gerçek geliştirme verisinde tarayıcıda denendi):

| Konu | Karar / davranış |
|---|---|
| Rapor | `GET /api/crm/quotes/report?from&to&ownerId` (`crm.quotes.read`; ekran `crm/teklif-raporu`). Kayıt boru hattından okunur: izin ve veri kapsamı liste ekranıyla aynıdır, kapsam dışı teklif hiçbir toplamda yer almaz (temsilci yalnızca kendi tekliflerini görür). Yalnızca ailelerin **son revizyonu** sayılır; revizyonlu aile tek kez. |
| İçerik | "Şu an": taslak, iç onay bekleyen, müşteride bekleyen (adet ve para birimi bazlı tutar). Dönem: sunulan, kabul, ret, iptal, süresi dolan, kabul oranı (kabul/(kabul+ret)), ortalama karar süresi, ortalama indirim, kabulde ortalama revizyon, sorumlu bazında dağılım. Dikkat listeleri: süresi yaklaşan (7 gün), süresi geçmiş ama hâlâ sunulmuş, iç onay bekleyen, müşteriye gönderilmemiş/gönderimi başarısız. Tanımlar ekranda yazılıdır. |
| Para birimi | Toplamlar para birimi bazındadır; kur dönüşümü yoktur, birimler karıştırılmaz. |
| Güvenlik | Kalem maliyeti, marj ve iç not rapora ve dışa aktarmaya hiç girmez (marj raporu bilinçli olarak yoktur). Tutarlar vergi dahil genel toplamdır. |
| Dışa aktarma | `GET /api/crm/quotes/export` (`crm.quotes.export`, yeni izin): CSV, kayıt boru hattı ve süzgeçlerle (ör. `latestOnly=true`), en çok 5000 satır, formül karakteriyle başlayan hücre etkisizleştirilir, çıkış denetim kaydına yazılır. |
| Filtreler | `presentedFrom/To`, `decidedFrom/To` (UTC gün sınırı) kayıt süzgeci olarak eklendi. |

**Altıncı dilimde de yapılmadı (dürüst durum):** marj/kârlılık raporu (maliyet izni ve ayrı rapor gerekir), geçmiş dönem anlık görüntüsü (rapor bugünkü kayıt değerlerindendir), teslim/okundu verisi, ret nedeni analizi (serbest metin; yapılandırılmış neden listesi yok), kayıtlı görünümler ve zamanlanmış rapor e-postası, grafikler, kapsam dışı ürün/kategori kırılımı. Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (yedinci dilim: kabul sonrası süreçler)

**Uygulandı** (`QuotePostAcceptTests` 5 entegrasyon testi; birim kuralları mevcut kümelerde):

| Konu | Karar / davranış |
|---|---|
| Devir sözleşmesi | `Modulix.Core.Contracts.Sales.IAcceptedQuoteSource` / `AcceptedQuote`: kabul edilen sürümün değişmez görünümü (kalemler kararlı anahtarla, toplamlar, kabul eden, belge özeti, koşullar; maliyet ve iç not yok). Sipariş/sözleşme modülü henüz yok; o modül CRM'e bağlanmadan yalnızca Core.Contracts üzerinden okur. Kabul olayı olarak `crm.quote` güncelleme olayı (durum `Accepted`) kullanılır. Görünürlük çağıranın izin/kapsamıyla; kabul edilmemişse ya da kapsam dışıysa `null`. `GET /api/crm/quotes/{id}/handover` aynı görünümü verir. **Gerçek sipariş/sözleşme oluşturma yapılmadı.** |
| Fırsat kazanma | Ayar `crm.quote.on-accept-win-opportunity` (varsayılan **hayır**, davranış değişmez). Açıksa kabul (müşteri bağlantısıyla ya da iç kayıtla) olayından sonra bağlı **açık** fırsat kazanıldı olarak kapanır; kazanılan tutar teklifin net toplamıdır, gerekçe teklif numarasıdır. Fırsat açık değilse (kapalı/beklemede), bulunamazsa ya da kapanış kuralları engellerse fırsata dokunulmaz ve nedeni teklif tarihçesine `OpportunityWinSkipped` olarak yazılır; kabul asla bozulmaz. İşaret satırı tekrar teslimde ikinci kazanmayı önler. Abonelik `crm.quote-lifecycle` yalnızca gereken izinlerle çalışır (tutar yazımı için `crm.opportunity-amounts.read` dahil). |
| Takip aktivitesi | Ayar `crm.quote.follow-up-days` (varsayılan 0 = kapalı): teklif sunulunca bu kadar gün sonrasına 09:00 için teklif sorumlusuna "Teklif takibi" aktivitesi açılır (firma ve varsa fırsata bağlı). Tarihçe işaretiyle tek kez. |
| Süre dolumu işi | `QuoteExpiryWorker` (`QuoteExpiry:WorkerEnabled`, varsayılan açık; `PollIntervalSeconds` 300): kurulu her tenant için sistem kimliğiyle (`crm.quote-expiry`) geçerliliği bitmiş "sunuldu" teklifleri `Expired` yapar (karar tarihi, gerekçe, tarihçe; sürüm denetimli, kabul/iptal yarışında biri kazanır). **Kabul anındaki süre denetimi bu işe bağlı değildir**; iş geç kalsa da süresi dolmuş teklif kabul edilmez. CRM kapalı tenant atlanır. |
| Süre dolumu hatırlatması | Ayar `crm.quote.expiry-reminder-days` (varsayılan 0 = kapalı): geçerliliği bitmeden bu kadar gün önce, hâlâ bekleyen teklif için sorumluya hatırlatma aktivitesi (bir önceki gün 09:00). Aktivite ve işaret tek işlemde; tek kez. Teklif kabul/ret/iptal edilmişse hatırlatma açılmaz (iş yalnızca "sunuldu" olanlara bakar). |
| Güvenlik | İki sistem kimliği yalnızca sayılan izinlerle çalışır; kimlik doğrulama yoktur, ağdan erişilemez. Tüm yan etkiler kayıt boru hattından geçer (izin, kapsam, audit, olay). |

**Yedinci dilimde de yapılmadı (dürüst durum):** sipariş/sözleşme oluşturma (hedef modül yok; yalnızca sözleşme ve devir görünümü), "birincil teklif" kavramı ve fırsat tutarının tekliften otomatik eşitlenmesi, takip aktivitesinin teklif sonuçlanınca otomatik kapatılması (takip aktivitesi serbest bir görevdir), kayıp teklifte fırsatı kaybedildi yapma, hatırlatma için e-posta/bildirim (yalnızca aktivite), süre dolumu için zaman dilimi/iş günü hesabı (UTC gün sınırları), işin birden çok uygulama örneğinde yük dağıtımı (sürüm denetimi çift işlemi önler, ama kilitleme yoktur). Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.

## Uygulama durumu (sekizinci dilim: küçük tamamlamalar)

**Uygulandı** (`QuotePostAcceptTests` 6 test; ilgili kümeler):

| Konu | Karar / davranış |
|---|---|
| Teklif bağ hedefi | Aktiviteler teklife `ActivityLink` ile bağlanabilir (`entityKey = crm.quote`; görünürlük teklif okuma izni + veri kapsamı; başlık yalnızca ad/numara). Tekliften açılan takip ve hatırlatma aktiviteleri otomatik bağlanır. |
| Otomatik kapatma | Teklif sonuçlanınca (kabul, ret, iptal, süre dolumu olayı) teklife bağlı, **tekliften açılmış** (konu öneki "Teklif takibi: " / "Teklif süresi doluyor: ", tür FollowUp) ve hâlâ açık aktiviteler gerekçeyle iptal edilir; tarihçeye `FollowUpsClosed` yazılır (tek kez). Kullanıcının elle açıp teklife bağladığı aktivitelere dokunulmaz. Eski revizyonun `Superseded` olması ayrı olay üretmediği için o revizyonun takibi kapatılmaz (bilinçli sınır). |
| Sorumluya e-posta | Süre dolumu hatırlatması açıldığında ve teklif `Expired` olduğunda teklif sorumlusuna e-posta kuyruğa yazılır (`platform.EmailMessages.Reference = crm.quote-reminder:{id}` / `crm.quote-expired:{id}`). Sorumlu aktif tenant'ın etkin üyesi değilse atlanır. İçerik yalnızca başlık, numara, tarih ve arayüz bağlantısı; HTML'e kodlanır. Sistem kimliği kapsamında kuyruk satırları turun sonunda açıkça kaydedilir. |
| İş kilidi | Süre dolumu turu tenant veritabanında oturum kilidiyle (`sp_getapplock`, `modulix.crm.quote-expiry`) tek örnekte koşar; kilidi alamayan örnek turu atlar (`Skipped`). Durum geçişleri zaten sürüm denetimliydi; kilit gereksiz çakışmayı ve çift e-postayı önler. |

**Bu dilimde de yapılmadı:** takip aktivitesinin teklif yeniden açılınca geri açılması, süresi dolan teklif için müşteriye bildirim (bilinçli: müşteriye yalnızca bağlantı e-postası gider), e-posta şablonu özelleştirme, zaman dilimine göre "09:00" (UTC kullanılır).

## Uygulama durumu (dokuzuncu dilim: fırsat entegrasyonu)

**Uygulandı** (`QuoteOpportunityTests` 2 entegrasyon testi):

| Konu | Karar / davranış |
|---|---|
| Esas teklif | `Opportunity.PrimaryQuoteId` (migration `OpportunityPrimaryQuote`, boş değerli sütun + indeks). Fırsatın tutar ve raporlamada esas aldığı tek teklif. Sistem alanı: genel güncelleme ucuyla yazılamaz; `POST /api/crm/opportunities/{id}/primary-quote` (`crm.opportunities.write`, fırsat sürümü ister) ile seçilir ya da kaldırılır. Yalnızca aynı fırsata bağlı, sunulmuş ya da kabul edilmiş teklif esas olabilir. |
| Otomatik esas | Fırsatın esas teklifi yokken ilk sunulan teklif esas olur; esas teklifin yeni revizyonu sunulunca esas yeni revizyona geçer. Başka bir aileden teklif sunulması esası değiştirmez. Tarihçe işareti `OpportunitySynced` (tek kez). |
| Tutar eşitleme | Ayar `crm.quote.sync-opportunity-amount` (varsayılan **hayır**): açıksa esas teklif bağlandığında/değiştiğinde **açık** fırsatın tutarı ve para birimi teklifin net toplamıyla eşitlenir; kapalı/beklemede fırsata dokunulmaz. Kapalıyken fırsat tutarı elle girilir (eski davranış). |
| Ekranlar | Fırsat detayında "Teklifler" bölümü (liste, esas rozeti, esas yap/kaldır, yeni teklif bağlantısı); teklif detayında "Fırsatın esas teklifi" rozeti. |

**Dokuzuncu dilimde de yapılmadı:** ret/süre dolumunda fırsata otomatik aşama/durum değişikliği (yalnızca kabulde kazanma politikası var), esas teklif olmadan fırsat tutarının tekliften türetilmesi, fırsat raporunda esas teklif kırılımı, esas teklif değişikliğinin fırsat tarihçesine ayrı satır olarak yazılması (yalnızca audit).

## Uygulama durumu (onuncu dilim: çok adımlı onay, vekâlet, onay hatırlatması)

**Uygulandı** (`QuoteMultiStepApprovalTests` 4 entegrasyon testi, `QuoteApprovalStepsTests` 4 birim testi):

| Konu | Karar / davranış |
|---|---|
| Adımlar | Onay kuralında `Step` (1–9, varsayılan 1; migration `QuoteApprovalSteps`). Eşleşen kurallar adıma göre gruplanır ve adımlar **sırayla** tamamlanır. Bir adımda adlı onaylayan varsa **hepsinin** (ya da vekillerinin) onayı gerekir (paralel); adlı onaylayan yoksa onay izni olan herhangi birinin tek onayı yeter. Adlı ve adsız kural aynı adımdaysa adlılar belirleyicidir. Genel eşik ayarı tek başına tetiklendiyse tek, adsız adım vardır. |
| İlerleme | Ara onaylar tarihçeye `StepApproved` olarak yazılır (teklif `Pending` kalır); son adımı tamamlayan onay teklifi `Approved` yapar. İlerleme, son onaya gönderimden sonraki `StepApproved` olaylarından hesaplanır: ticari değişiklik ya da yeni gönderim sayacı sıfırlar. Sıradaki adımın onaylayanı olmayan kullanıcı 403 alır; eylem listesi `approver-restricted` nedenini verir. Ret/değişiklik isteği her zaman akışı bitirir. |
| Vekâlet | `crm.QuoteApprovalDelegations` (veren → vekil, başlangıç/bitiş, etkin, not; RLS'li). Yönetim `crm.quote-rules.manage` (`/api/crm/quote-approval-delegations`, ekran `crm/onay-vekaletleri`). Vekil, adlı onaylayanın yerine karar verebilir; onay **veren adına** sayılır ve tarihçe "vekâleten" der. Vekilin kendisinin onay izni olmalıdır; vekâlet izin vermez. Kişi kendine vekâlet veremez. Süresi dolmuş/pasif vekâlet sayılmaz (onay anındaki vekâletler esas alınır). |
| Hatırlatma | Ayar `crm.quote.approval-reminder-hours` (0 = kapalı): onay bu kadar saat bekleyince şu anki adımın henüz onaylamamış adlı onaylayanlarına ve vekillerine bir kez e-posta (`Reference = crm.quote-approval-reminder:{id}`); tarihçe `ApprovalReminderSent`. Adsız adımda alıcı belirlenemediği için gönderilmez. Süre dolumu işi içinde çalışır. |
| Ekranlar | Kural formunda adım alanı ve sütunu; onay merkezinde adım rozetleri ve "vekâleten" notu; teklif detayında adım özeti; vekâlet yönetimi sayfası. |

**Onuncu dilimde de yapılmadı:** onaylayanın yerine geçen rol/grup tabanlı onay (yalnızca kişi), adım başına ayrı süre sınırı ve otomatik yükseltme (escalation), onay yorumu zorunluluğu, vekâletin kişi tarafından kendi hesabından verilmesi (yalnızca yönetici), ret sonrası aynı gönderimde yeniden onay (yeni gönderim gerekir), ara onay geri çekme.
