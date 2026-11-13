# ADR-0045 — Yer defteri (Place) ve durak ülkesi

Durum: **taslak** (14 Kasım 2026, tartışma için; kabul edilmedi). **K1 kararı verildi (14 Kasım 2026, ürün sahibi): yer defteri Core ortak kaydıdır, Lojistik'e özel tablo değildir.** Diğer kararlar öneridir. Bu belge tek başına hiçbir şeyi uygulamaz. Numara geçicidir: açık bir PR aynı numarayı almış olabilir, birleştirmeden önce kontrol et. (Not: `main`'de iki ADR "0043" numarasını taşıyor, firma-sektor-katalogu ve sevkiyatta-ulke-ve-uluslararasi-tasima; numaralar ayrıca düzeltilmeli.)

İlgili: ADR-0043 sevkiyatta ülke (K6 yer defteri, K7 durak ülkesi, dilim 3'ü bu belge açar), ADR-0006 (ortak firma, kişi), ADR-0016 (lookup), ADR-0012 (CRM adresi), ADR-0038 (Lojistik, `TripStop`), ADR-0032 (Antrepo, `Facility`), ADR-0033 (içe aktarma), ADR-0034 (arayüz dili).

## 1. Bağlam

ADR-0043 dilim 1-2 ile sevkiyat ve talep ülke kodu taşıyor, harita şehir adını koda gömülü yer kataloğuyla (`LogisticsGazetteer`) eşliyor. Geriye üç boşluk kalıyor:

- **Durak ülkesi yok.** `TripStop` yalnızca `City` ve `Address` (serbest metin) taşır; sefer boyunca ülke geçişi (ör. İstanbul → Kapıkule → Sofya → Viyana) ifade edilemez. Sınır/gümrük durağı (`StopType.Border`) bir ülkeye bağlı değildir.
- **Katalog sınırlıdır.** Firmanın kendi depoları, fabrikaları, antrepoları, limanları katalogda yoktur; harita ülke merkezine ("yaklaşık") düşer. Aynı yeri her sevkiyatta yeniden yazmak hataya açıktır.
- **Adres bilgisi parçalıdır.** CRM'de firmaya ait yapılandırılmış adres vardır (`crm.Addresses`: ülke, şehir, ilçe, posta kodu, satırlar; ADR-0012) ama Lojistik CRM tablosuna dokunamaz (modül sınırı); Antrepo `Facility` tesisi yalnızca ülke kodu taşır; `Party.Address` eski alandır. Taşıma yerinin tek bir ortak tanımı yoktur.

CLAUDE.md "ortak ana veri (Firma, Kişi, Adres) Core'a aittir; bir kez girilir, tüm modüller kullanır" der. Yer, Lojistik'e özgü bir kavram değildir: Antrepo (tesis), Filo (garaj, servis) ve ileride İK (iş yeri) de yere ihtiyaç duyar.

Ürün sahibinin kararı (13 Kasım 2026): uluslararası nakliye yapılacak; dış geocoding servisi kullanılmaz (adresler üçüncü tarafa gitmez, on-prem'de de çalışır).

## 2. Kararlar (öneri)

**K1 — Yer, Core ortak kaydıdır.** `core.Places` (`ITenantEntity` + `ICompanyShared`, RLS altında), `SharedEntities.Place`. Alanlar: ad (zorunlu), tür (Y9b seçim listesi `core.place-kind`; hazır değerler örnektir: depo, fabrika, liman, sınır kapısı, gümrük, diğer; tenant tür ekler; kod türün adına değil kategorisine bakar: `site`, `port`, `border`, `customs`, `other`), ülke (`char(2)`, ISO 3166-1 alfa-2, ADR-0043'teki `CountryCatalog` kümesi, zorunlu), şehir, ilçe, adres satırı, posta kodu, enlem ve boylam (`decimal(9,6)`, isteğe bağlı; ikisi birlikte verilir ya da hiçbiri; enlem -90..90, boylam -180..180), aktif mi. Benzersizlik: `UNIQUE(TenantId, NormalizedName, CountryCode)` (ad kırpılıp küçük harfe ve aksansız biçime çevrilir). Yer silinmez, pasife alınır; kullanımdaki yer silinemez (`SharedRecordReferences`).

**K2 — Konum çözümü üç basamaklıdır.** Haritada nokta: (1) yerin kendi koordinatı; (2) yoksa `LogisticsGazetteer.Find(şehir, ülke)` (ADR-0043 K5); (3) yoksa ülkenin yaklaşık merkezi ("yaklaşık" işaretli). Koordinat zorunlu değildir; elle girilir ya da haritadan seçilir (sonraki dilim). Koordinat dış servisten alınmaz.

**K3 — Sevkiyat ve durak yere isteğe bağlı bağlanır; metin kopyalanır (snapshot).** `CargoEntity` (talep ve sevkiyat) `PickupPlaceId`, `DeliveryPlaceId` (nullable, lookup ADR-0016) kazanır; yer seçilince şehir, ülke ve adres metni alanlara **kopyalanır** ve kayıt yerden bağımsız yaşar: yer sonradan değişse ya da pasife alınsa eski sevkiyat bozulmaz. Serbest metin girişi bozulmaz: yer seçmek zorunlu değildir. Seçili yer kaydedildikten sonra metin elle değiştirilirse bağ korunur ama metin kaynaktır (ekranda "yerden farklı" notu).

**K4 — Durağın ülkesi ve yeri.** `TripStop` iki alan kazanır: `Country` (`char(2)`, `NOT NULL DEFAULT 'TR'`) ve `PlaceId` (nullable). Sevkiyat atanınca yükleme ve boşaltma durakları ülkeyi (ve varsa yeri) sevkiyattan kopyalar; elle eklenen durakta kullanıcı ülke seçer. Sınır/gümrük durağında `Country`, geçişin **çıkış** tarafındaki ülkedir (ör. Kapıkule: `TR`); bir sonraki durak giriş ülkesindedir. Bu kural arayüzde tek cümleyle yazılır.

**K5 — Ülke geçişi ve sınır durağı: varsayılan uyarı, ayarla engel.** Sefer uygunluk denetimine (`TripPlanner.CheckAsync`, ADR-0038 dilim 5) yeni bir kontrol eklenir: ardışık iki durağın ülkesi farklı ve arada sınır/gümrük durağı yoksa uyarı verilir ("Türkiye → Bulgaristan geçişinde sınır durağı yok"). Firma ayarı `logistics.trips.border-check` (`off` / `warn` / `block`, varsayılan `warn`; `logistics.trips.credential-check` ile aynı kalıp). Kural kodda sabit değildir; gümrük belgesi gerektiren ülke çiftleri bu ADR'nin dışındadır (dilim 4).

**K6 — Harita ve pano yeri kullanır.** Güzergâh özeti yer varsa yerin adıyla ve koordinatıyla gruplanır; yoksa şehir ve ülkeyle (bugünkü davranış). Durak bazlı sefer haritası (sefer ekranında) bu ADR'nin dilim 3c'sidir.

**K7 — Yetki.** Yeri okumak sevkiyat, talep ya da sefer okuma izniyle olur (`SharedRecordAccess`, Person örneği gibi). Yazmak için yeni izin `logistics.places.write` ("Yer defterine yer ekler ve düzenler"); Operasyon ve Operasyon müdürü rol şablonlarına eklenir. Başka modül kendi iznini `SharedRecordAccess` ile bağlar (ör. Antrepo tesis izni). Yer hassas alan değildir; adres satırı ve koordinat yer okuyan herkese görünür.

**K8 — Dışa aktarma, silme, içe aktarma.** `core.Places` `TenantId` ile eksiksiz dışa aktarılır ve silinir (`tenant-export`, `tenant-delete`; CLAUDE.md tenant kuralı). İçe aktarma (ADR-0033) yer için sütun eşlemesi alır (ad, tür, ülke, şehir, adres, enlem, boylam); aynı ad+ülke güncellenir, yeni olan eklenir.

**K9 — Geriye uyumlu geçiş.** Tek paylaşımlı veritabanı bütün tenant'ları etkilediği için migration geriye uyumludur: yeni tablo `TenantIsolationSql.AddTable` ile RLS'e eklenir; `CargoEntity` iki nullable yer sütunu alır; `TripStops.Country` `NOT NULL DEFAULT 'TR'` eklenir (sabit varsayılanlı sütun ekleme maliyeti büyük tabloda gerçek veriyle doğrulanır, doğrulanmadan uygulanmaz; ADR-0043 K9 ile aynı koşul). Mevcut duraklar `TR` olur; sevkiyatı yurt dışı olan seferlerin durakları sefer ekranından düzeltilir (toplu düzeltme için "durak ülkesini sevkiyattan al" eylemi, dilim 3b).

**K10 — Sınırlar (bu ADR'nin dışında).** (a) CRM firma adresinden "yer olarak kaydet" eylemi: CRM tablosuna Lojistik doğrudan erişmez; CRM'in public servisiyle ya da arayüzde kullanıcı eylemiyle yapılır (dilim 3d). (b) Antrepo `Facility`'nin isteğe bağlı `PlaceId` bağı Antrepo ADR'sinin (ADR-0032) konusudur. (c) Yer gruplaması (bölge, ağ) ve mesafe/süre hesabı yoktur. (d) Gümrük belgeleri ve ülke çiftine bağlı kurallar (CMR, TIR karnesi, A.TR) ADR-0043 dilim 4'tedir.

## 3. Etki alanı

- **Core:** `core.Places` varlığı, yapılandırması, `SharedEntities.Place`, kayıt deposu, lookup tanımı, `core.place-kind` seçim listesi, denetim kaydı etiketleri, `tenant-export/delete` kapsamı, migration, `TenantIsolationSql.AddTable`.
- **Lojistik:** `CargoEntity.PickupPlaceId/DeliveryPlaceId`, `TripStop.Country/PlaceId`, `LogisticsWorkflow` (talepten sevkiyata kopyalanan alanlar), `TripPlanner` (durak oluşturma, sınır kontrolü), `SharedRecordAccess` ve `SharedRecordReferences`, `logistics.places.write` izni, rol şablonları, ayar `logistics.trips.border-check`, otomasyon alanları, form tanımları.
- **Web:** `CargoEditor` ve durak paneli (`TripStopsPanel`) yer seçici (ortak lookup), durak ülke seçici, yer listesi/ekranı (`/lojistik/yerler` ya da ortak ayar ekranı), harita (`RouteMap`) yer koordinatı.
- **Gazetteer:** `Find(şehir, ülke)` korunur; yer koordinatı varsa önceliklidir.

## 4. Dilimler

1. **3a — Yer defteri çekirdeği:** `core.Places`, seçim listesi, kayıt deposu, izinler, yer ekranı (liste + form), denetim, dışa aktarma/silme, migration ve RLS. Sevkiyata bağlanmaz.
2. **3b — Sevkiyat ve durak bağlantısı:** `PickupPlaceId/DeliveryPlaceId`, `TripStop.Country/PlaceId`, snapshot kopyalama, yer seçici, "durak ülkesini sevkiyattan al".
3. **3c — Rota ve harita:** sınır durağı denetimi ve ayarı, sefer haritası, pano güzergâhında yer koordinatı.
4. **3d — Bağlantılar:** CRM adresinden yer, içe aktarma, Antrepo tesis bağı (kendi ADR'siyle).

## 5. Test planı (her dilim; dört test zorunlu)

- **Mutlu yol:** yer ekle, sevkiyata bağla (metin kopyalanır), sefere ata (durak ülkesi gelir), haritada yerin koordinatı kullanılır.
- **Doğrulama:** geçersiz ülke kodu 422; yalnızca enlem ya da yalnızca boylam 422; aralık dışı koordinat 422; aynı ad+ülke ikinci kez 422; ülke geçişinde sınır durağı yoksa uyarı (ayara göre engel).
- **Tenant izolasyonu:** başka tenant'ın yeri görünmez, seçilemez, sevkiyata bağlanamaz; aynı ad başka tenant'ta serbest.
- **Yetki:** okuma izniyle yer görünür ama yazılamaz; `logistics.places.write` olmadan POST/PUT 403; kullanımdaki yer silinemez; modül kapalıyken 403.
- Birim: ad normalleştirme, koordinat doğrulaması, ülke geçişi kontrolü (durak dizileri).

## 6. Açık sorular ve varsayılan cevaplar

Yanıt gelmezse varsayılan uygulanır.

1. **Yer Core ortak kaydı mı, Lojistik'e özel mi?** **Karar (14 Kasım 2026, ürün sahibi): Core ortak kayıt.** Gerekçe: Core ortak kayıt (CLAUDE.md "ortak ana veri Core'a aittir"; Antrepo ve diğer modüller de kullanır). Alternatif Lojistik'e özel tablodur; sonradan Core'a taşımak veri taşıma ve referans değiştirme gerektirir.
2. **Koordinat zorunlu mu?** Varsayılan: hayır (K2).
3. **Yer türü listesi tenant tarafından genişletilebilir mi?** Varsayılan: evet, Y9b listesi ve kategoriler (K1).
4. **Yer seçilince metin kopyalanır mı, canlı mı?** Varsayılan: kopyalanır (snapshot, K3).
5. **Ülke geçişinde sınır durağı yoksa?** Varsayılan: uyar, ayarla engelle (K5).
6. **Sınır durağının ülkesi giriş mi çıkış mı?** Varsayılan: çıkış (K4).

## 7. Reddedilen seçenekler

- **Yer defterini yalnızca Lojistik'te tutmak:** Antrepo tesisi, Filo garajı ve İK iş yeri aynı yeri ayrı ayrı girer; CLAUDE.md "bir kez girilir" ilkesine aykırıdır.
- **CRM adresini yer olarak kullanmak:** modül sınırını bozar (Lojistik CRM tablosuna dokunamaz) ve adres firmaya aittir, depo/liman/sınır kapısı firma adresi değildir.
- **Dış geocoding ya da harita servisi:** adresler üçüncü tarafa gider, on-prem'de çalışmaz.
- **Yer seçmeyi zorunlu yapmak:** kayıtlı olmayan yere tek seferlik taşıma yapılamaz; serbest metin girişi korunur.
- **Durak türüne ülke gömmek (ör. "Bulgaristan sınır durağı"):** tür sayısı ülke sayısıyla çarpılır; ülke yerin özelliğidir.
