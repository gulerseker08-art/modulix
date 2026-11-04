# ADR-0030 — Depo modülü ve gümrüklü antrepo uzantısı

Durum: **taslak** (4 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz. Mevzuat ayrıntıları (tescil numarası biçimi, süreler, antrepo tipleri) saha analizi metninden alınmıştır ve **uzmanla doğrulanmamıştır**; bölüm 9'da listelidir. Doğrulanmamış hukuki kural zorunlu üretim kuralı yapılmaz.

İlgili: yol haritası M3 ve K9 (depo ortak, gümrüklü antrepo seçilebilir uzantı), ADR-0008 (şirket/şube kapsamı), ADR-0016 (lookup), ADR-0020 (tek veritabanı), ADR-0021 (form düzeni), ADR-0029 (sektör/faaliyet modeli; K6 tesis özellikleri, K9 ilişki rolü).

## 1. Bağlam

Antrepo pilotu için saha analizi şunları ister: ithalatçı–gümrük müşaviri–beyanname ilişkisi, GTİP ve kap yapısı, koridor-raf-kat-göz lokasyonu ile serbest/gümrüklü alan ayrımı, zaman damgalı ve kullanıcılı stok hareket izi. Repoda depo/antrepo kodu yoktur (`docs/sektor-faaliyet-analizi.md`: "Depo/Antrepo (yok)"); `Modulix.Modules.Logistics` iskelet modüldür.

Kısıtlar: modüller yalnızca `Modulix.Core.Contracts`'a başvurur ve birbirinin tablosuna erişmez (K5); tek paylaşımlı veritabanı, `TenantId` + EF filtresi + RLS; her tenant tablosu `ITenantEntity` ile `ICompanyShared`/`ICompanyScoped` uygular ve `TenantIsolationSql.AddTable` ile RLS'e eklenir; yazmalar kayıt boru hattından geçer.

## 2. Kararlar

**K1 — Tek modül, ayrık uzantı.** `Modulix.Modules.Warehouse` (anahtar `warehouse`) genel depoyu taşır: tesis, alan, lokasyon, stok kartı, parti, hareket defteri, kabul, çıkış. Gümrük uzantısı aynı projede `Customs/` altında, `warehouse.customs.*` izinleriyle durur. Gümrük alanları genel depo müşterisi için zorunlu değildir; uzantı tesisin uyum profiliyle (ülke, rejim, tesis izni) açılır. Neden: stok defteri ile gümrük statü denetimi aynı transaction'da çalışır, `Core.Contracts`'a koruma arayüzü ve mimari test tablosu değişikliği gerekmez. Alternatif ve reddedilme gerekçesi bölüm 6'dadır.

**K2 — Taraflar Core'dadır.** İthalatçı/malik ve gümrük müşaviri yeni firma tablosu değildir; Core `Company`/`Party` kaydıdır, bir kez girilir. ADR-0029 K9'daki ilişki rolü kataloğuna `ithalatçı/malik` ve `gümrük müşaviri` rolleri eklenir. Depo modülü yalnızca firma kimliğine (`CompanyId`) başvurur.

**K3 — Malik ve faturalanan taraf ayrıdır.** Her parti `OwnerCompanyId` (mal kimin) ve `BillToCompanyId` (ardiye/fatura kime; varsayılan malik) taşır. Aynı ürün farklı malik ve gümrük statüsüyle ayrı bakiyededir (M3.1).

**K4 — Vekâlet.** `PowerOfAttorney`: ithalatçı, müşavir, geçerlilik tarihleri, kapsam, belge, durum. Müşavir yalnızca geçerli vekâleti olduğu ithalatçıların kayıtlarını görür ve yazar; bu kapsam sunucuda sorgu seviyesinde uygulanır, arayüzde gizleme yetki sayılmaz.

**K5 — Müşavir dış erişimi yalnızca ön bildirim ve belge ile sınırlıdır.** Müşavir `ASN` (ön bildirim) taslağı oluşturur/gönderir ve belge yükler (`warehouse.customs.asn.submit`, `warehouse.customs.documents.upload`). Stok hareketi yaratamaz; ASN'yi antrepo personeli kabul eder. Kimlik mekanizması (dış API anahtarı, portal) yol haritasında Ç11 ve Ç19.8'dedir ve henüz yoktur: ilk dilimde müşavir, kısıtlı rollü normal kullanıcıdır; API/portal sonra bağlanır ve aynı izin ve kapsam kurallarını kullanır.

**K6 — Beyanname.** `CustomsDeclaration` (tescil no, tarih, gümrük idaresi, rejim kodu, ithalatçı, müşavir, durum, kaynak, dış referans) ve `CustomsDeclarationLine` (satır no, GTİP, tanım, kap cinsi, kap adedi, brüt/net kg, menşe, miktar+birim, bakiye, fatura değeri). `UNIQUE(TenantId, RegistrationNo)`. Yasal gerçek satırdadır: bakiye, kısmi çıkış ve mutabakat beyanname satırı üzerinden yürür; stok kartındaki GTİP yalnızca varsayılandır. Rejim kodu (7100 vb.) katalogdan (lookup) gelir, koda sabitlenmez.

**K7 — Tescil numarası doğrulaması yapılandırılabilir.** Biçim ve uzunluk kuralı rejim/belge türüne bağlı yapılandırmadır (düzenli ifade + uzunluk), koda yazılmaz. Başlangıçta ihlal **uyarıdır**; uzman doğrulayınca tenant/paket seviyesinde engele çevrilebilir. Elle giriş ve entegrasyon aynı servisten geçer.

**K8 — Entegrasyon sözleşmesi.** Gümrük müşavirliği yazılımlarından (Evrim, Solon vb.) beyanname alımı `ICustomsDeclarationSource` benzeri bir sözleşmeyle ve gelen veri adaptörüyle yapılır (mevcut `IAcceptedQuoteSource` kalıbı). Adaptör aynı doğrulama ve kayıt boru hattından geçer; yinelenen dış referans ikinci kayıt üretmez. Sağlayıcıların gerçekten uygun bir API sunup sunmadığı doğrulanmamıştır; ilk sürümde yalnızca elle giriş vardır.

**K9 — GTİP kataloğu global, sürümlüdür.** Bakanlık verisi tenant'a ait olmadığı için `platform` şemasında, `TenantId`'siz, yürürlük tarihli ve sürümlü tutulur (ADR-0029 K3 kalıbı); platform yöneticisi Excel/XML ile toplu yükler. Bakanlıktan canlı çekme ilk aşamada yoktur (kamuya açık servis doğrulanmadı). Kod **metin** saklanır (baştaki sıfırlar kaybolmasın); ilk 6 hane HS kodudur; 12–14 hane desteklenir. Katalogda bulunmayan kod uyarı üretir, engellemez. Stok kartındaki GTİP varsayılan ve düzenlenebilir bir önerisidir.

**K10 — Kap cinsi ve menşe.** Kap cinsi (palet, koli…) ADR-0016 lookup'ıdır; menşe ülke kodu lookup'ıdır. Net kilo brüt kilodan büyük olamaz.

**K11 — Lokasyon.** Hiyerarşi: Tesis → Alan (gümrüklü/serbest) → Koridor → Raf → Kat → Göz (örn. `ANT-A-02-04-C`). Parçalar ayrı sütundur; `Code` bunlardan üretilir; `UNIQUE(TenantId, FacilityId, Code)`. Kod deseni tenant bazında yapılandırılır. Kapasite ve kısıtlar lokasyonda tutulur. Alan türü sunucuda hareket sırasında denetlenir: gümrük altındaki mal serbest alana yerleştirilemez. Tesise özgü ülke özellikleri (antrepo tipi vb.) ADR-0029 K6 uyarınca tesis kaydında değer olarak tutulur.

**K12 — Serbest/gümrüklü aynı depoda.** Yol haritası M3.8 gereği hukuki doğrulama olmadan etkinleştirilmez: varsayılan kapalı tesis bayrağı, `Doğrulanmadı` etiketiyle.

**K13 — Stok hareket defteri.** `StockMovement` yalnızca eklenir: hareket türü (giriş, çıkış, transfer, rezervasyon, serbest bırakma, düzeltme), parti, kaynak/hedef lokasyon, beyanname satırı, miktar ve kilo, `OccurredAt` (UTC), `UserId`, `MembershipId` (bileşik FK), `CorrelationId`, neden kodu, belge referansı, **`IdempotencyKey`** (`UNIQUE(TenantId, Key)`; barkod/terminal tekrarı çift hareket üretmez). Düzeltme güncelleme değil ters kayıttır (`ReversesMovementId`). Bakiye defterden türetilir; eşzamanlı çıkış/rezervasyon `rowversion` ile korunur ve eksi stok politikasını aşamaz; bakiyeyi doğrudan değiştiren uç nokta yoktur. Genel denetim kaydı kayıt boru hattında ayrıca yazılır; hareket defteri alanın kendi kayıt defteridir.

**K14 — Gümrük statüsü durum makinesidir.** Mal statüsü (antrepoda, serbest dolaşıma girdi, transit vb.) bir durum makinesidir; geçiş belge gerektirir (ör. çıkış beyannamesi) ve `CustomsStatusTransition` olarak kim/ne zaman/neden/belge ile kaydedilir. Statü sıradan alan düzenlemesiyle veya alan kuralıyla değişmez (M3.7). Karantina ve gümrük engeli ayrı `StockHold` kaydıdır; engelli mal sevk edilemez (M3.4).

**K15 — Yetki ve kapsam.** İzinler `warehouse.*` ve `warehouse.customs.*` önekiyle; fatura değeri gibi alanlar alan seviyesi yetkiyle korunur. İş ortağı/müşteri portalı kullanıcısı başka maliklerin malını göremez (M3.9). Her varlık için `ICompanyShared`/`ICompanyScoped` seçimi ADR-0008'e göre uygulama diliminde yapılır.

## 3. Dilimler

1. Bu ADR'nin kabulü ve uzman doğrulama listesi (bölüm 9).
2. Tesis, alan, lokasyon ve kod deseni.
3. İlişki rolleri, vekâlet, stok kartı, lookup'lar (kap cinsi, menşe, rejim), GTİP kataloğu ve Excel yükleme.
4. Beyanname başlık/satır ve tescil no kuralı.
5. ASN, kabul (eksik/fazla/hasar), yerleştirme, hareket defteri ve bakiye.
6. Statü makinesi, kısmi çıkış, beyanname mutabakatı.
7. Müşavir dış erişimi (Ç11/Ç19.8'e bağlı).
8. Entegrasyon adaptörü, süre uyarıları, teminat, resmi rapor taslakları.
9. Ardiye ücreti (M3.6).

Her dilim dört testle teslim edilir (mutlu yol, doğrulama, tenant izolasyonu, yetki); yeni tenant tablosu `TenantIsolationSql.AddTable` ile RLS'e eklenir.

## 4. Riskler

- Stok kartı depo modülünde başlar; ortak ürün tanımı Core'a taşınınca (ADR-0029 açık soru 2) kart/ürün eşlemesi göç gerektirir.
- Hareket defterinde bakiye güncellemesi ile eşzamanlılık hataya açıktır: yük ve yarış testi zorunludur.
- Tescil no ve süre kuralları yanlış sabitlenirse yasal hata üretir; bu yüzden yapılandırma ve uyarı-önce yaklaşımı.
- Dış API kimliği (Ç11/Ç19.8) gecikirse müşavir erişimi kısıtlı kullanıcıyla kalır.
- Tek paylaşımlı veritabanında hareket tablosu büyür; dizin ve bölümleme ihtiyacı ölçülmelidir (doğrulanmadı).

## 5. Seçenek: SQL Server ledger tablosu

Hareket defteri için SQL Server'ın append-only *ledger table* özelliği kurcalamaya karşı kanıt verebilir. EF Core, RLS ve migration zinciriyle uyumu **bilinmiyor**; karar öncesi kısa bir deneme gerekir. Başarısız olursa `INSTEAD OF UPDATE/DELETE` tetikleyicisi ve ilgili DB izinleriyle append-only zorlanır.

## 6. Reddedilen seçenekler

- **İki ayrı proje (`Warehouse` + `Customs`):** bağımsızlık artar ama koruma arayüzü `Core.Contracts`'a girmek zorunda kalır ve stok denetimi ile statü denetimi aynı transaction'da kolay yapılamaz. İlk teslimde maliyeti faydasından yüksek; ikinci bir gümrük tüketicisi çıkarsa yeniden değerlendirilir.
- **Beyannameyi CRM'e koymak:** modüller arası tablo erişimi yasağı ve sektör mantığının Core/CRM'e sızması.
- **GTİP'i tenant tablosu yapmak:** her tenant'a kopya ve sürüm sapması.
- **Bakiye sütununu doğrudan güncellemek:** hareket izi ve yarış güvenliği kaybolur.
- **Tescil no biçimini koda sabitlemek:** doğrulanmamış hukuki kural.

## 7. Açık sorular

1. Tek modül (bu taslak) mü, iki ayrı proje mi?
2. Müşavir kimliği ilk sürümde kısıtlı kullanıcı olarak kalsın mı, yoksa Ç11/Ç19.8 beklenmeli mi?
3. Stok kartı ne zaman Core ürün tanımına bağlanacak?
4. Ledger tablosu denemesi bu ADR'den önce mi, sonra mı yapılsın?

## 8. Test beklentileri (özet)

Tenant izolasyonu: A tenant'ı B'nin beyanname/parti/hareketini göremez. Yetki: yetkisiz kullanıcı statü geçişi yapamaz; müşavir vekâlet dışı ithalatçının verisini göremez ve stok hareketi yaratamaz. Doğrulama: net>brüt, yinelenen tescil no, alan türü ihlali reddedilir. Eşzamanlılık: iki eşzamanlı çıkış bakiyeyi eksiye düşüremez. Idempotency: aynı anahtarla tekrar çift hareket üretmez.

## 9. Doğrulanmadı

- Tescil numarası biçimi (örnek `26340000EX...`) ve uzunluğu.
- Antrepo tiplerinin süre, teminat ve alan kuralları; serbest/gümrüklü alanın aynı depoda olup olamayacağı.
- Evrim, Solon vb. yazılımların gerçek API'leri.
- Ticaret Bakanlığı'nın GTİP için makine okunur servisi.
- Pilot (Makaroğlu) akışı: ASN'yi kimin açtığı, kısmi çıkış, ardiye hesabı.
- Mevcut denetim kaydı tablosunun hareket izi ihtiyacına uygunluğu okunmadı.
- Hiçbir kod veya test çalıştırılmadı.
