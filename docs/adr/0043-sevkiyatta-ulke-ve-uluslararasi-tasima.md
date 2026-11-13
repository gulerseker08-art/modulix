# ADR-0043 — Sevkiyatta ülke alanı ve uluslararası taşıma

Durum: **taslak** (13 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz. Numara geçicidir: açık bir PR aynı numarayı almış olabilir, birleştirmeden önce kontrol et.

İlgili: ADR-0038 (Filo ve Lojistik; taşıma talebi ve sevkiyat, bölüm 16), ADR-0006 (ortak firma, kişi, adres), ADR-0016 (lookup), ADR-0021 (form düzeni), ADR-0027 (otomasyon), ADR-0033 (içe aktarma), ADR-0034 (arayüz dili), ADR-0032 (Antrepo; rejim profili ülkeye göre değişir), PR #317 (güzergâh haritası).

## 1. Bağlam

Taşıma talebi ve sevkiyatta yükleme ve teslim yeri bugün yalnızca **serbest metin şehirdir** (`CargoEntity.PickupCity`, `DeliveryCity`; ADR-0038 K5, K6). Ülke ve koordinat yoktur. Sonuçları:

- Uluslararası taşıma kayıt olarak ifade edilemez: "Hamburg" ile "Hamburg, ABD" ayırt edilmez; ihracat, ithalat ve transit ayrımı yapılamaz.
- Lojistik panosunun haritası (PR #317) şehir adını **koda gömülü yer kataloğuyla** (`LogisticsGazetteer`) eşler. Katalog sınırlıdır (81 il, limanlar, sınır kapıları, seçili dünya şehirleri); bulunamayan yer haritada çizilmez, panoda "gösterilemeyen: N" olarak sayılır. Görünüm (Türkiye, Avrupa, Dünya) noktaların ülkesine bakılarak tahmin edilir.
- Ülkeye bağlı iş kuralları (gümrük, rejim, CMR ve TIR belgeleri, sınır geçişi) sabit bir alana dayanamaz; Antrepo rejim profili zaten ülkeye göre değişir (ADR-0032 K16).

Çekirdekte ülke biçimi **mevcuttur**: CRM adresinde `Country`, `char(2)`, ISO 3166-1 alfa-2, büyük harf, varsayılan `TR` (`CrmEntities.cs`). Bu ADR aynı biçimi sevkiyata taşır; yeni bir ülke kavramı icat etmez.

Ürün sahibi, sevkiyatın yalnızca Türkiye içi olmadığını belirtti (13 Kasım 2026, harita görüşmesi): uluslararası nakliye de yapılacak. Dış geocoding servisi kullanılmaz: müşteri adresleri üçüncü tarafa gitmez, on-prem'de de çalışır (CLAUDE.md "on-prem'e hazırlık").

## 2. Kararlar (öneri)

**K1 — Ülke, yükleme ve teslim yerinde ayrı alandır.** `CargoEntity` (talep ve sevkiyatın ortak tabanı) iki alan kazanır: `PickupCountry`, `DeliveryCountry`. Biçim: `char(2)`, ISO 3166-1 alfa-2, büyük harf, `IsUnicode(false)`, `IsFixedLength` (CRM adresiyle aynı). Şehir serbest metin kalır; ülke kodu şehirle birlikte yeri tanımlar.

**K2 — Zorunlu, varsayılanı firmanın ülkesidir.** Yeni kayıtta iki ülke de zorunludur; gönderilmezse tenant ayarı `logistics.default-country` (`Choice` değil, ISO kodu metni; varsayılan `TR`) kullanılır. Bu, API istemcilerini ve içe aktarmayı (ADR-0033) kırmaz: eski istek ülkesiz gelir, `TR` olur. Geçersiz kod 422 döner (ISO listesi dışı).

**K3 — Ülke listesi platform kataloğudur, tenant listesi değildir.** ISO 3166 listesi değişmeyen bir standarttır; "Y9b seçim listesi" (tenant tür ekler) kullanılmaz. Core'da salt okunur `CountryCatalog` (kod + Türkçe ad + İngilizce ad) bulunur; görünen ad arayüz diline göre gelir (ADR-0034). Kod hiçbir yerde ülke adına bakmaz, yalnızca koda bakar.

**K4 — "Uluslararası" türetilir, saklanmaz.** `PickupCountry != DeliveryCountry` ise sevkiyat uluslararasıdır; ihracat/ithalat/transit için sabit liste ya da alan yoktur (ADR-0038 taşıma türü listesi gibi tenant'ın kendi kategorileri varsa, kod yalnızca kategoriye bakar). Otomasyon ve alan kuralı için türetilmiş alan `international` (Boolean) sunulur; böylece "yurt dışı sevkiyatta gümrük belgesi iste" firmanın kuralı olur, kodda `if` olmaz.

**K5 — Yer kataloğu ülkeyle çalışır.** `LogisticsGazetteer.Find(city)` yerine `Find(city, country)`: ülke verilmişse aday yalnızca o ülkeden seçilir (aynı şehir adı iki ülkede olabilir). Şehir katalogda yoksa ülkenin merkez noktasına düşülür ve haritada "yaklaşık" işaretlenir; panoda "gösterilemeyen" sayısı azalır. Harita görünümü (Türkiye / Avrupa / Dünya) artık tahminle değil ülke kodlarıyla seçilir.

**K6 — Tenant'ın kendi yer defteri bu ADR'nin dışındadır.** Katalogda olmayan depo, tesis ve fabrika için tenant'a ait yer tablosu (`Place`: ad, ülke, şehir, enlem/boylam; ADR-0016 lookup ile `pickupPlaceId`) ayrı bir ADR'nin konusudur (bölüm 5, dilim 3). Bu ADR yalnızca ülke alanını getirir.

**K7 — Sefer durağı sonraki dilimde ülke alır.** Sevkiyatın güzergâhından açılan yükleme ve boşaltma durakları (`TripStop`) ülke kodunu sevkiyattan kopyalar; sınır/gümrük durağı (`StopTypeLabels.Border`) ülke geçişini gösterir. Durak ülkesi dilim 3'tedir.

**K8 — Yetki ve gizlilik.** Ülke hassas alan değildir (`SensitiveFields`'a girmez); navlun ve para birimi ayrı korunur (ADR-0038 K11). Alanı yazmak mevcut yazma izinlerine bağlıdır (`logistics.requests.write`, `logistics.shipments.write`); yeni izin yoktur.

**K9 — Geriye uyumluluk ve kesintisiz geçiş.** Tek paylaşımlı veritabanı bütün tenant'ları etkilediği için migration geriye uyumlu olur (CLAUDE.md): iki sütun `NOT NULL DEFAULT 'TR'` olarak eklenir (sabit varsayılanlı sütun eklemenin SQL Server'da çoğunlukla yalnızca meta veri işlemi olduğu varsayılır; tablo kilidi süresi dilim 1'de gerçek veriyle doğrulanır, doğrulanmadan uygulanmaz); mevcut bütün kayıtlar `TR` olur. Eski istemci ülke göndermeden çalışmaya devam eder. Yurt dışı olduğu bilinen eski kayıtları kullanıcı düzeltir; toplu düzeltme için içe aktarmada ülke sütun eşlemesi eklenir.

## 3. Etki alanı (dilim 1 dosyaları)

- `LogisticsEntities.cs` (`CargoEntity`), `LogisticsConfigurations.cs` (`char(2)`, varsayılan, `HasMaxLength(2)`), yeni migration (tek zincir; RLS değişmez, yeni tablo yok).
- `CargoRecordStores.cs`: alan listesi (`pickupCountry`, `deliveryCountry`), süzgeçler (`pickupCountry`, `deliveryCountry`, `international`), sıralama anahtarı, arama; ISO doğrulaması `AppliedAsync` içinde (`RecordValidationException`).
- `LogisticsModule.cs`: denetim kaydı etiketleri (`CargoAudit`), otomasyon alanları (`CargoAutomationFields`: ülke iki alan + `international`), ayar tanımı `logistics.default-country`.
- `LogisticsForms.cs`: form düzenine iki ülke alanı (şehrin yanına; ADR-0021 metadata'dan, Razor'a sabit yazılmaz).
- `Modulix.Shared`: `LogisticsAccess` listeleri, kayıt DTO'ları; `Modulix.Web`: `CargoEditor`, `CargoList` (sütun ve süzgeç), ülke seçici (ortak `CountryCatalog`, arama destekli).
- `Modulix.Core`: `CountryCatalog` (yeni, salt okunur).

## 4. Dilimler

1. **Alan ve geçiş:** `PickupCountry` / `DeliveryCountry`, `CountryCatalog`, `logistics.default-country`, migration, form, liste, süzgeç, denetim kaydı, otomasyon alanları, README.
2. **Harita ve katalog:** `LogisticsGazetteer.Find(city, country)`, ülke merkezine düşme ("yaklaşık"), harita görünümünü ülkeye göre seçme, panodaki "uluslararası sevkiyat" sayacı.
3. **Durak ülkesi ve yer defteri:** `TripStop` ülkesi, `Place` tablosu ve `pickupPlaceId` lookup'ı (ayrı ADR).
4. **Belgeler ve gümrük köprüsü:** CMR, TIR karnesi, A.TR gibi sevkiyat belgeleri (Y9b belge türleri; kategori `shipment`), Antrepo rejim profiliyle köprü (ADR-0032).

## 5. Test planı (dilim 1; dört test zorunlu)

- **Mutlu yol:** ülke göndermeden açılan talep `TR` olur; `DE` verilen talep onaylanınca sevkiyata kopyalanır; `international` süzgeci yalnızca ülkeleri farklı olanı getirir.
- **Doğrulama:** geçersiz kod (`ZZ`, `Turkiye`, 3 harf) 422; küçük harf büyüğe çevrilir; teslim günü kuralı bozulmaz.
- **Tenant izolasyonu:** başka tenant'ın `logistics.default-country` ayarı etkilemez; A'nın uluslararası sevkiyatı B'nin süzgecinde çıkmaz.
- **Yetki:** okuma izniyle ülke görünür ama yazılamaz; izinsiz kullanıcı 403.
- Birim: `CountryCatalog` (kodlar benzersiz, 249 ISO kodu, adlar boş değil), gazetteer ülke ile eşleşme ve ülke merkezine düşme.

## 6. Açık sorular ve varsayılan cevaplar

Yanıt gelmezse varsayılan uygulanır.

1. **Yeni kayıtta ülke zorunlu mu?** Varsayılan: evet, ama gönderilmezse `logistics.default-country` kullanılır (K2).
2. **Mevcut kayıtlar?** Varsayılan: hepsi `TR` olur, yurt dışı olanı kullanıcı düzeltir (K9). Otomatik tahmin yapılmaz.
3. **Ülke adı hangi dilde?** Varsayılan: arayüz diline göre (ADR-0034); kod ülke adına bakmaz.
4. **Tenant yer defteri bu ADR'de mi?** Varsayılan: hayır, ayrı ADR (K6, dilim 3).
5. **Taşıma türüne "ihracat/ithalat" eklenecek mi?** Varsayılan: hayır, `international` türetilir (K4); firma isterse kendi taşıma türünü Y9b listesine ekler.

## 7. Reddedilen seçenekler

- **Şehir metnine "Şehir, Ülke" yazdırmak:** serbest metindir, doğrulanamaz, süzülemez, harita eşleşmesini kırılgan yapar.
- **Dış geocoding servisi:** adres ve müşteri bilgisi üçüncü tarafa gider; on-prem'de çalışmaz.
- **Ülkeyi taşıma türü listesine gömmek:** ülke bir taşıma özelliği değil yerin özelliğidir; tür sayısı ülke sayısıyla çarpılır.
- **Yurt dışı için ayrı varlık (uluslararası sevkiyat tablosu):** ADR-0038 "komple iki duraklı, parsiyel çok duraklı sefer; ayrı model yok" ilkesine aykırıdır; tek model kalır.
