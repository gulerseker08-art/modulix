# ADR-0017: Fırsat yönetimi (CRM)

Durum: Kabul edildi (29 Ekim 2026). Kapsam: CRM → Fırsat. Uygulama sınırları ve yapılmayanlar en altta.

## Bağlam

Fırsat; başlık, firma, kişi, aşama (seçim listesi), tutar, olasılık ve kayıp nedeni taşıyan basit bir kayıttı. Aşama, PUT ile
serbestçe değişiyordu; kazanma/kaybetme yalnızca aşama kategorisinden anlaşılıyordu. Tarihçe yalnızca genel audit'teydi,
kapanış bilgisi (tarih, gerçekleşen tutar, neden), kişi rolleri, rakipler, rapor ve ayrı yetkiler yoktu.

## Kararlar

1. **Tenant başına ayrı veritabanı** (ADR-0009; **ADR-0020 ile tek veritabanına dönüldü**, en alttaki güncel durum notuna bakın) zaten mimaridir; fırsat bu mimariyi değiştirmez. Yeni tablolar tenant iş
   veritabanındadır (`crm.OpportunityContacts`, `crm.OpportunityCompetitors`, `crm.OpportunityHistory`), RLS'e eklenmiştir.
2. **Durum ayrı bir kavramdır** (`Open`, `OnHold`, `Won`, `Lost`, `Cancelled`) ve aşama kategorisiyle şöyle ilişkilidir:

   | Durum | Aşama kategorisi |
   |---|---|
   | Open, OnHold, Cancelled | `open` (iptal, son açık aşamayı korur) |
   | Won | `won` |
   | Lost | `lost` |

   Tutarsız birleşim hiçbir yoldan kaydedilemez: depo her yazımda denetler. Beklemeye alma kapanış değildir (gerekçe + yeniden
   değerlendirme tarihi ister, açık toplamlara ve tahmine girmez). İptal kayıp değildir (kazanma oranına girmez).
3. **Tek yazma yolu.** Durum, aşama (oluşturma sonrası) ve kapanış alanları genel `PUT`/kayıt uçlarından yazılamaz. Yalnızca
   `IOpportunityWorkflow`, eylemin izinleriyle iş akışı kapısını açarak kayıt boru hattından yazar. Pano (Kanban), API ve ileride
   yapay zekâ araçları aynı servisi kullanır. Aşama koşulları, tutarlılık, olasılık türetmesi ve tarihçe **kayıt deposunda**, kaydın
   kendi işleminde uygulanır (atomik).
4. **Yetkiler.** Mevcut `read`/`write` + `opportunity-amounts.read` korunur. Yeni: `change-stage` (aşama değiştir, beklet/çıkar),
   `close` (kazan/kaybet/iptal), `reopen`, `export`. *Uyumluluk kararı:* `write` izni olan mevcut roller aşama değiştirmeye ve
   kapatmaya devam eder (davranış bozulmasın); yeniden açma ve dışa aktarma kesinlikle ayrı izindir, hiçbir hazır rolde yoktur.
   Eylem izni, kayıt bulunduktan sonra denetlenir (kapsam dışı kayıt 404, izinsiz eylem 403).
5. **Olasılık kuralı.** Aşamanın varsayılan olasılığı bir tenant ayarıdır (`crm.opportunity.stage-probabilities`). Kullanıcı
   olasılığı elle girdiyse (`probabilityManual`) aşama değişince ezilmez; elle girilmediyse aşama değişince aşamanın varsayılanına
   geçer. Kazanılanda 100, kaybedilen ve iptalde 0 (kapanışta elle işareti sıfırlanır). Yeniden açılan fırsata 0/100 taşınmaz:
   verilen olasılık ya da aşamanın varsayılanı (yoksa bilinmiyor).
6. **Aşama koşulları** (`crm.opportunity.stage-requirements`, ör. `Proposal=amount|expectedCloseDate`): aşamaya *geçerken*
   uygulanır, oluşturmada uygulanmaz (kullanıcı kayıt açarken engellenmez). Tutar izni olmayan kullanıcı için de doğru çalışır
   (koşul depoda, tam kayıt üzerinde denetlenir).
7. **Bilinmeyen ≠ sıfır.** Tutar ve müşteri bütçesi `null` = bilinmiyor; bütçe durumu ayrı (`Unknown/Unconfirmed/Confirmed/None`).
   Ağırlıklı tutar = tutar × olasılık / 100; tutarı ya da olasılığı bilinmeyen fırsat ağırlıklı toplama girmez (adedi ayrıca bildirilir).
   Para: `decimal(18,2)`, para birimi ISO 4217, farklı birimler **toplanmaz** (kur altyapısı yok → para birimi bazında ayrı).
8. **Kapanış.** Kazanıldı: gerçek kapanış tarihi (gelecekte olamaz), kazanılan tutar (verilmezse tahmini tutar; ikisi de yoksa red),
   kazanma nedeni, devir notu. Kaybedildi: yapılandırılmış neden listesi zorunlu; "Diğer" kategorisi açıklama ister; kazanan rakip
   yalnızca "rakibe kaybedildi" kategorisiyle belirtilir (vazgeçme / mevcut çözüm rakibe kayıp değildir); isteğe bağlı yeniden
   iletişim tarihi ve takip aktivitesi. Kapanışta açık aktiviteler **silinmez, devredilmez, kapatılmaz**: açık kalır ve sayısı bildirilir.
9. **Yeniden açma.** Ayrı izin + gerekçe + açık yeni aşama + gelecekte tahmini kapanış. Eski kapanış bilgileri tarihçedeki kapanış
   satırında korunur, kayıttan temizlenir; `ClosureCycle` artar. Raporlar yalnızca fırsatın **güncel** kapanışını sayar (mükerrer sayım yok).
10. **Tarihçe** (`OpportunityHistory`): oluşturma, aşama/durum geçişleri (eski/yeni, kullanıcı, zaman, gerekçe, döngü) ve tahmini
    kapanış tarihi değişimleri. Kapanış satırının `Details` alanı tutar içerir; tutar izni olmayana verilmez. Fırsat silinince birlikte kalkar.
11. **Kişiler/rakipler.** Karar verici/bütçe sahibi/… rolleri kişi kaydında değil `OpportunityContact` ilişkisindedir (bir kişi bir fırsata
    bir kez, çoklu rol, tek ana irtibat). Rakip serbest adlı bir kayıttır (firma olmak zorunda değil), güçlü/zayıf yön ve alternatif teklif taşır.
12. **Son müşteri etkileşimi** (`lastInteractionAt`) gerçekleşen temastan (Lead ile aynı kural: tamamlanmış toplantı, ulaşılan arama,
    gönderilen e-posta) olay aboneliğiyle (`crm.opportunity-interaction`) türetilir; "son güncelleme"den ayrıdır. İdempotenttir.
13. **Dönüşüm ve sipariş devri.** Lead dönüşümü mevcut atomik/idempotent akışı kullanır (tek fırsat veritabanında da benzersizdir);
    fırsata numara ve ilk tarihçe satırı otomatik verilir. Sipariş/sözleşme/proje devri için hedef modül yoktur → **sahte entegrasyon
    kurulmadı**; "devir notu" alanı vardır, sözleşme (`IOpportunityHandover`) yazılmadı.
14. **Rapor** (`GET /api/crm/opportunities/report`) kayıt boru hattından okur (izin + kapsam + hassas alan); tanımları yanıtın
    `definitions` alanındadır. Dışa aktarma (`/export`) ayrı izinli ve audit'lidir. Bir istekte en çok 5000 fırsat okunur (kesilirse bildirilir).

## Migration

`OpportunityManagement` (tenant): yeni kolonlar ve üç tablo. Mevcut kayıtlar korunur: numara (tenant içinde sıralı, `OP-<tarih>-M#####`),
durum (aşama kategorisinden: `Won`/`Lost` anahtarları ve tenant listesindeki won/lost kategorili aşamalar), kapanış tarihi
(= son güncelleme günü), kazanılan tutar (= tahmini tutar), `StageEnteredAt` (= son güncelleme), tarihçe başlangıcı. RLS kapalı tablodaki
toplu geri doldurma için politika yalnızca o işlem boyunca (aynı işlemde) kapatılır. Başarısız tenant migration'ı `migrate-tenants`
çıktısında listelenir, tenant önceki şemada kalır ve komut yeniden çalıştırılır (docs/tenant-isletim.md).

## Yapılmayanlar / bağımlılıklar (kalan işler)

- **Çoklu pipeline** (tenant bazında birden fazla satış süreci), aşama başına yönetilebilir *varsayılan olasılık ve bekleme eşiği*
  alanları: aşama listesi (Y9b) tek ve ek alan taşımıyor. Olasılık ve koşullar şimdilik ayarlarla, aşama anahtarına göre yönetiliyor.
- **Teklif** modülü yok: teklif/revizyon, esas teklif seçimi, tutar kaynağı (elle/teklif) ve teklif kabul–kazanım ilişkisi uygulanamaz.
- **Sipariş/sözleşme/proje devri** (hedef modül yok), kampanya ve referans bağlantısı, dosya eki (Ç4), ekip/business unit sahipliği,
  iş birliği yapan kullanıcılar, ayrı *atama* izni (sorumlu değişimi `write` altında kaldı).
- **Satış tahmini anlık görüntüsü** (snapshot): geçmiş dönem tahmini üretilmez; rapor yalnızca bugünkü kayıt değerlerinden hesaplar.
- **Bildirimler/zamanlanmış işler**: atama bildirimi mevcut genel abonelikten gider; geciken aksiyon, yaklaşan kapanış ve aşamada
  fazla bekleme bildirimleri yazılmadı (zamanlanmış iş/bildirim tercihi altyapısı yok).
- Toplu işlemler, sunucu taraflı sıralama seçimi, kayıtlı görünümler, kur dönüşümü.
- Ekranlar derleniyor ancak tarayıcıda elle doğrulanmadı.

## Güncel durum notu (13 Kasım 2026)

- **Veritabanı modeli:** "Tenant başına ayrı veritabanı" (Kararlar, madde 1) [ADR-0020](0020-tek-veritabanina-donus.md) ile değişti: tek paylaşımlı veritabanı, tenant tabloları EF süzgeci + SQL Server RLS ile korunur. Fırsat tablolarının `TenantId` + RLS ile korunması bu modelde de geçerlidir.
- **Kalan işler listesinden kapananlar:** teklif modülü ve teklif–fırsat entegrasyonu ([ADR-0019](0019-teklif-yonetimi.md), dokuzuncu dilim); sipariş ([ADR-0023](0023-siparis-ve-satis-devri.md)) ve sözleşme ([ADR-0022](0022-sozlesme-yonetimi.md)) devri; dosya eki altyapısı ([ADR-0040](0040-dosya-ve-ekler.md)); bildirim ve zamanlanmış iş altyapısı ([ADR-0027](0027-kural-ve-is-akisi-altyapisi.md)); kayıtlı/kişisel görünümler ve sunucu sıralaması ([ADR-0036](0036-kisisel-liste-gorunumleri.md)); satış tahmini raporları ([ADR-0035](0035-raporlama-ve-satis-tahmini.md)); kazanıldı/kaybedildi iş olayları ([ADR-0039](0039-modul-is-olaylari.md)).
- **Hâlâ geçerli:** çoklu pipeline (ürün kararı), iş birimi sahipliği (A-12), iş birliği yapan kullanıcılar, ayrı atama izni, geciken aksiyon/yaklaşan kapanış bildirimleri (altyapı var, kural yazılmadı).
- **Fırsata kampanya bağı (14 Kasım 2026, A-12):** yapıldı. `campaignId` alanı + lookup `crm.opportunity.campaign` + `campaignId` süzgeci; müşteri adayından dönüşen fırsata adayın kampanyası geçer, fırsat formunda elle de seçilir (ayrıntı [ADR-0025](0025-kampanya-ve-segment-yonetimi.md) "CRM kampanya bağı").
