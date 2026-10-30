# ADR-0019: Teklif yönetimi (M1.5) — analiz ve ilk dilim

Durum: Kısmen uygulandı (çekirdek, sunucu yaşam döngüsü, ekranlar, PDF, paylaşım bağlantısı ve müşteri kabul sayfası). Tarih: 2026-10-30.

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
| İç onay | Genel onay altyapısı yok (ADR-0018) | Eşik/kural yönetimi | Kural + istek tabloları | P1 | Veri modeli | **Yapılmadı** |
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
