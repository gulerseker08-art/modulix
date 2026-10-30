# ADR-0018: Ürün, hizmet ve fiyat yönetimi

Durum: **Kısmen uygulandı** (29 Ekim 2026). Uygulanan: fiyatlandırma çekirdeği (`Modulix.Core.Contracts/Pricing`), katalog (kategori, birim, ürün), fiyat listesi/kademe, vergi, kur, paket bileşeni, indirim politikası, fiyat hesap uç noktası ve yönetim ekranları. Uygulanmayan kısımlar "Uygulama durumu" bölümünün sonundadır.

## Mevcut durum analizi (kod taramasıyla)

Tarama: `src/**/*.cs` içinde `Product|Price|Currency|Tax|Quote|Discount|Unit` ve "ürün/fiyat/teklif/katalog" metin aramaları,
`docs/yol-haritasi.md`, `CrmModule.cs`, migration'lar. Durum: **Mevcut / Kısmi / Eksik**.

| Süreç | Mevcut durum | Kanıt | Eksik veya risk | Yapılacak | Öncelik |
|---|---|---|---|---|---|
| Tenant başına ayrı DB (tarihsel; ADR-0020 ile tek veritabanına dönüldü, 30 Ekim 2026) | Geri alındı | ADR-0009 → ADR-0020, `ModulixDbContext`, `Persistence/Migrations` | — | Yeni tablolar tenant migration'ına + `TenantIsolationSql.AddTable` | — |
| Ürün/hizmet kartı | **Eksik** | Kodda katalog varlığı yok; yalnız serbest metin `Lead.productInterest`, sektör profilinde özel alan "Ürün grubu" (`SectorProfiles.cs:57`) | Fırsat/teklif ürüne bağlanamıyor | Katalog varlıkları (kod, tür, yaşam döngüsü, birim, kategori, vergi kategorisi) | P0 |
| Kategori, birim grubu | **Eksik** | Yok | — | `UnitGroup/Unit`, `ProductCategory` | P0 |
| Paket | **Eksik** | Yok | Döngü/mükerrer ücret riski | `ProductComponent` + `PackageCalculator` | P1 |
| Fiyat listesi, kademe | **Eksik** | Yok | — | `PriceList(+Version)`, `PriceListItem(+Tier)` | P0 |
| Fiyat seçimi | **Eksik → çekirdek yazıldı** | `Core/Pricing/PriceSelection.cs` | Servis ve veri bağlantısı yok | Uygulama servisi + uç nokta | P0 |
| Para birimi/kur | Kısmi | `RegionalOptions.Currencies`, tenant `Currency` ayarı; fırsat tutarı `decimal(18,2)` + ISO kodu, para birimleri **toplanmıyor** (ADR-0017/7) | Kur tablosu, kaynak, tarih, eski kur davranışı yok | `ExchangeRate` + manuel giriş izni/audit | P1 |
| Vergi | **Eksik** | Vergi altyapısı yok | Oran sabitlenmemeli | Tenant vergi kategorisi/oran tablosu; `LineTotals` oranı dışarıdan alır | P1 |
| İndirim/onay | **Eksik → çekirdek yazıldı** | `DiscountEngine` | Kural kaydı, rol sınırı, onay kuyruğu yok. Genel onay altyapısı (Ç6) yol haritasında, kodda doğrulanamadı | Kural tablosu + onay entegrasyonu | P1 |
| Teklif | **Eksik** | Yol haritası M1.5 "yapılmadı"; kodda `Quote` yok | Snapshot satırı yok | M1.5 ile birlikte; satır snapshot sözleşmesi aşağıda | P0 (bağımlı) |
| Fırsat tutarı ↔ teklif | Kısmi | `Opportunity.Amount` tahmini tutar (ADR-0017/7) | Kesin teklif tutarından ayrım teklif gelince | Teklif tutarı ayrı alan | P2 |
| Yetki/kapsam/audit/özel alan/lookup | Mevcut | `IModule.Permissions`, kayıt boru hattı, `CustomFieldService`, ADR-0016 | — | Yeni kayıt türlerini aynı boru hattına bağla | — |
| Dosya (görsel/doküman) | **Doğrulanamadı** | Genel dosya altyapısı aranmadı/bulunamadı | — | Önce mevcut dosya altyapısı doğrulanmalı | P2 |
| İçe aktarma (CSV/Excel) | Eksik | Yol haritasında M1.2j "yapılmadı" | — | Kararlı anahtar (ürün kodu) + idempotent | P2 |

## Uygulanan kararlar (çekirdek, `Modulix.Core/Pricing`)

1. **Saf ve deterministik**: veritabanı ve saat bağımlılığı yok; `decimal` kullanır; tüm kurallar API ve domain katmanında yeniden
   kullanılabilir (kritik doğrulama yalnız arayüzde değildir).
2. **Fiyat önceliği**: açık seçim → müşteri → segment/kanal → tenant varsayılanı. Aynı öncelikte birden fazla uygun liste
   rastgele seçilmez, `price-conflict` hatası verilir. Fiyat bulunamazsa `price-not-found` (sıfır fiyat üretilmez). Açık seçilen liste
   geçersizse başka listeye düşülmez. Ücretsiz satış (`PriceTier.IsFree`) eksik fiyattan ayrıdır.
3. **Geçerlilik**: `ValidFrom` dahil, `ValidTo` hariç, `DateTimeOffset` anı; yalnızca yayımlanmış sürümler; para birimi eşleşmesi zorunlu.
4. **Kademe**: aralık `[min, max)`; çakışma reddedilir. `Volume` = tüm miktar tek kademenin fiyatından, `Graduated` = her bant kendi
   fiyatından (bantlar boşluksuz olmalı).
5. **Birim**: grup temel birimi ve pozitif katsayı; ürün özel ambalaj (1 kutu = 12 adet); farklı grup (kg→adet) ürün dönüşümü olmadan
   `unit-group-mismatch`; tam sayı/ondalık miktar doğrulaması. 2 kutu × 12 × 100 TL = 2.400 TL.
6. **İndirim**: ardışık (bileşik) uygulanır, öncelik sırasıyla (%10 + %10 = %19); tutar indirimi kalan fiyatı aşamaz; azami toplam
   sınır; `Stackable=false` indirim başkasıyla birleşemez; her adım açıklama satırı üretir; manuel sınır aşımı `RequiresApproval`.
7. **Vergi/yuvarlama**: oran dışarıdan; vergi dahil/hariç; satır düzeyinde 2 hane sıfırdan uzağa yuvarlama, belge = yuvarlanmış satırların
   toplamı. `Money` farklı para birimlerini toplamaz.
8. **Paket**: sabit fiyatta bileşenler toplama girmez (mükerrer ücret yok); sabit bedel bileşenlere liste bedeliyle orantılı dağıtılır,
   yuvarlama farkı en büyük bileşene (eşitte ilkine) eklenir; doğrudan/dolaylı döngü reddedilir.

## Teklif satırı snapshot sözleşmesi (tasarım; teklif modülü gelince uygulanacak)

Satırda saklanacak: ürün adı/kodu, varyant/paket bileşenleri, miktar+birim+dönüşüm katsayısı, fiyat listesi kimliği+sürümü,
birim fiyat, uygulanan indirim kural kimlikleri ve adımları, vergi oranı/dahil-hariç, para birimi (+kur), ara toplam/vergi/toplam,
hesaplama zamanı ve kural sürümü. Sunucu istemci toplamlarına güvenmez, yeniden hesaplar; taslakta yeniden fiyatlandırma açık
işlem ve fark gösterimli, kesinleşmişte revizyonla.

## Yapılmayanlar (ilk dilim anındaki durum; güncel durum en altta "Uygulama durumu" bölümünde)

Veri modeli/migration, kayıt türleri ve izinler, uç noktalar, yönetim ekranları, kur ve vergi tabloları, indirim kural kaydı ve onay
kuyruğu, içe aktarma, teklif entegrasyonu, eşzamanlılık (RowVersion), maliyet alanı yetkisi ve entegrasyon testleri **yazılmadı**.
Kabul kriterlerinden yalnızca 4–9 ve 13'ün çekirdek kısmı birim testle doğrulandı (`PricingEngineTests`, 34 test); 1–3, 10–12, 14
veri katmanı gerektirir. Backfill gerekmez (mevcut katalog verisi yok); gelecek migration'lar yeni tablo ekler, veri silmez ve
`migrate-tenants` ile tenant başına raporlanır.

Önerilen sıra: (1) birim + kategori + ürün kartı + izinler + RLS, (2) fiyat listesi/sürüm + fiyat servisi uç noktası, (3) vergi/kur,
(4) paket, (5) indirim + onay, (6) ekranlar, (7) teklif (M1.5) entegrasyonu.

## Katalog veri modeli (ikinci dilim)

- **Yer**: CRM modülü içinde (`crm.*` tabloları). Kayıt deposu tabanı (`CrmRecordStore`) CRM'e özgü ve `internal` olduğu için ayrı bir katalog
  modülü şimdilik ikinci bir depo altyapısı gerektirirdi; yol haritasındaki "ortak katalog" (Ç3c) ayrı modüle çıkarılırken tablolar taşınır.
- **Tablolar** (tenant iş DB'si, RLS'li): `ProductCategories` (üst kategori, döngü yok), `MeasureUnits` (grup + pozitif katsayı, CHECK),
  `Products` (benzersiz `(TenantId, Code)`, satış tarihi ve hassasiyet CHECK, FK'ler `(TenantId, Id)` ile tenant sınırını aşmaz).
- **İzinler**: `crm.products.read/write`, `crm.product-costs.read` (maliyet hassas alan, Y8b). Eşzamanlılık: kayıt sürümü (409).
- **Silme**: engeller `RecordConflictException` → 409 (boru hattı silmede doğrulama hatasını yakalamaz).
- **Backfill**: gerekmez (yeni tablolar). Migration `ProductCatalog`; `migrate-tenants` tenant başına uygular ve hatayı raporlar.
- **İkinci dilim sonunda yoktu (sonradan yapılanlar için "Uygulama durumu"na bakın)**: varyant, paket bileşenleri tablosu, fiyat listesi/kademe tabloları ve fiyat servisi uç noktası, vergi/kur tabloları,
  indirim kuralı ve onay, ekranlar, içe aktarma, dosya ilişkisi, teklif entegrasyonu, ürün seçici lookup'ı (ADR-0016) ve kategori/etiket
  lookup tanımı. Katalog kayıtları henüz teklif/fırsat satırından başvurulmadığı için "kullanılmış ürün" yalnızca taslak dışı durumla korunur.

## Uygulama durumu (üçüncü dilim: fiyatlandırma ve ekranlar)

Yukarıdaki analiz tablosu ve "Yapılmayanlar" bölümü ilk dilim anındaki durumu anlatır; güncel durum burasıdır.

**Uygulandı ve entegrasyon testiyle doğrulandı** (`PricingTests`, 7 test; `ProductCatalogTests`, 4 test):

| Konu | Karar / davranış |
|---|---|
| Fiyat listesi | `PriceList` (kapsam: Default / Customer / Segment; durum: Taslak → Yayımlandı → Arşiv; yöntem: Volume / Graduated) ve `PriceListItem` (ürün başına miktar kademesi, temel birim başına fiyat, `IsFree`). Yayımlanan liste başlığı ve satırları değişmez, silinmez; yalnızca arşive alınır, taslağa dönülmez. Satırsız liste yayımlanamaz; çakışan kademe, ters aralık, negatif fiyat reddedilir (DB CHECK + sunucu doğrulaması). Değişiklik için yeni liste açılır. |
| Fiyat seçimi | `POST /api/crm/pricing/calculate` (`crm.prices.read`): sunucu her seferinde baştan hesaplar. Açık seçim → müşteri → segment → varsayılan; aynı öncelikte çakışma `price-conflict`, fiyat yoksa `price-not-found` (422, kararlı `code`), süresi dolan / arşivlenen / başka para birimindeki liste kullanılmaz. Ücretsiz satış eksik fiyattan ayrıdır. |
| Birim | Satış birimi ürünün temel birimine çevrilir (2 kutu × 12 = 24 adet); miktar hassasiyeti ürün kartından doğrulanır. |
| Vergi | `TaxRate` kod + tarih aralığı + oran (0–100). Ürünün `TaxCategory` alanı kodla eşleşir. Aynı kod için aralıklar çakışamaz. Kategori verilmiş ama geçerli oran yoksa `tax-rate-not-found` (sessiz %0 yok). Kategorisiz ürün %0 ve bu açıklamada belirtilir. |
| Kur | `ExchangeRate` (çift + gün benzersiz, kaynak ve not, elle giriş). Hesapta yalnızca `DisplayCurrency` istenirse çeviri yapılır: o güne kadarki en yeni doğrudan kur, yoksa ters kur; hiçbiri yoksa `exchange-rate-not-found` (kur uydurulmaz). Yanıt kuru ve kur gününü taşır. |
| İndirim | `DiscountPolicy` (yüzde ya da tutar, tam biri; öncelik; birleşme; azami toplam %; onay eşiği %; geçerlilik). Hesapta politikalar öncelik sırasıyla ardışık uygulanır, elle indirim sona eklenir, azami sınır aşılırsa sınıra çekilir. Etkin indirim onay eşiğini aşarsa yanıtta `requiresApproval = true`. Eşiği olmayan bir hesapta elle indirim de onay gerektirir olarak işaretlenir. |
| Paket | `ProductComponent` (paket, bileşen, miktar). Kendine, tekrarlı ya da dolaylı döngüye izin verilmez. Hesap yanıtı bileşenleri miktarla çarpılmış olarak listeler. |
| Güvenlik | `crm.prices.read/write` yeni izinlerdir (satış temsilcisi şablonuna yalnızca `read` eklendi); katalog izni fiyat izni vermez. Tüm tablolar RLS'li ve bileşik FK'li; başka tenant'ın liste/ürününe bağlanılamaz, hesapta 404 döner. Maliyet hiçbir hesap yanıtında yoktur. |
| Ekranlar | `Ürün ve hizmetler` (+ paket bileşenleri paneli), `Ürün kategorileri`, `Ölçü birimleri`, `Fiyat listeleri` (satır editörü, yayımla/arşivle), `Vergi ve kur`, `İndirimler`, `Fiyat hesapla`. Derleme doğrulandı; tarayıcıda uçtan uca gezilmedi. |
| Mimari | Fiyatlandırma çekirdeği `Modulix.Core.Contracts/Pricing` altına taşındı (modüller yalnızca Contracts'a başvurur); fiyat hesabı verisini yalnızca `Records` katmanından okur (`ModuleDataAccessRulesTests`). |

**Yapılmadı / sınırlı** (dürüst durum):

- **Varyant**: yok.
- **Onay kuyruğu**: yalnızca `requiresApproval` işareti var; onay isteği oluşturma, onaylayan yetkisi ve kuyruk yok (genel onay altyapısı Ç6 yol haritasında, kodda doğrulanamadı).
- **Paket fiyat modları**: `PackageCalculator` (sabit fiyatın bileşenlere dağıtımı) çekirdekte testli ama hesap uç noktasına bağlanmadı; paketin fiyatı kendi fiyat listesi satırından gelir, bileşenler yalnızca listelenir.
- **Liste yeni sürümü**: "yeni sürüm olarak kopyala" eylemi yok; değişiklik için yeni liste elle açılır.
- **Teklif entegrasyonu ve belge toplamı**: teklif modülü (M1.5) yok; satır snapshot'ı ve belge düzeyinde kur sabitleme uygulanamadı. Hesap yanıtı snapshot sözleşmesine uygun alanları taşır, ama saklayan bir belge yoktur.
- **İçe aktarma** (CSV/Excel), **dosya/görsel ilişkisi**, **ürün seçici lookup tanımı** (ADR-0016) ve kategori/etiket lookup'ı: yok. Genel dosya altyapısı doğrulanamadı.
- **Katalog yöneticisi rol şablonu**: yok; izinler elle role verilir.
- **Test boşlukları**: kabul kriterlerinin tamamı arayüzden doğrulanmadı; maliyet yazma kısıtı testi (`ProductCatalogTests`) 200/403/422'yi kabul eden gevşek bir doğrulama yapıyor.
- **Backfill**: gerekmez (yeni tablolar). Migration `PricingCatalog`; `migrate-tenants` tenant başına uygular.
