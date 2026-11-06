# ADR-0032 — Depo modülü ve gümrüklü antrepo uzantısı

Durum: **taslak** (4 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz. Mevzuat ayrıntıları (tescil numarası biçimi, süreler, antrepo tipleri) saha analizi metninden alınmıştır ve **uzmanla doğrulanmamıştır**; bölüm 9'da listelidir. Doğrulanmamış hukuki kural zorunlu üretim kuralı yapılmaz.

Güncelleme (4 Kasım 2026): tek modül kararı teyit edildi (K1). Proje çok sektörlü olduğu için **bütün kurallar tenant başına yapılandırılabilir tasarlanır**; yeni K16–K20 bunu tanımlar ve K7, K11, K13, K14 buna göre değiştirildi. Sabit kalan seçimler gerekçesiyle K17'dedir.

İlgili: yol haritası M3 ve K9 (depo ortak, gümrüklü antrepo seçilebilir uzantı), ADR-0008 (şirket/şube kapsamı), ADR-0016 (lookup), ADR-0020 (tek veritabanı), ADR-0021 (form düzeni), ADR-0029 (sektör/faaliyet modeli; K6 tesis özellikleri, K9 ilişki rolü).

## 1. Bağlam

Antrepo pilotu için saha analizi şunları ister: ithalatçı–gümrük müşaviri–beyanname ilişkisi, GTİP ve kap yapısı, koridor-raf-kat-göz lokasyonu ile serbest/gümrüklü alan ayrımı, zaman damgalı ve kullanıcılı stok hareket izi. Repoda depo/antrepo kodu yoktur (`docs/sektor-faaliyet-analizi.md`: "Depo/Antrepo (yok)"); `Modulix.Modules.Logistics` iskelet modüldür.

Kısıtlar: modüller yalnızca `Modulix.Core.Contracts`'a başvurur ve birbirinin tablosuna erişmez (K5); tek paylaşımlı veritabanı, `TenantId` + EF filtresi + RLS; her tenant tablosu `ITenantEntity` ile `ICompanyShared`/`ICompanyScoped` uygular ve `TenantIsolationSql.AddTable` ile RLS'e eklenir; yazmalar kayıt boru hattından geçer.

## 2. Kararlar

**K1 — Tek modül, ayrık uzantı.** `Modulix.Modules.Warehouse` (anahtar `warehouse`) genel depoyu taşır: tesis, alan, lokasyon, stok kartı, parti, hareket defteri, kabul, çıkış. Gümrük uzantısı aynı projede `Customs/` altında, `warehouse.customs.*` izinleriyle durur. Gümrük alanları genel depo müşterisi için zorunlu değildir; uzantı tesisin uyum profiliyle (ülke, rejim, tesis izni) açılır. Neden: stok defteri ile gümrük statü denetimi aynı transaction'da çalışır, `Core.Contracts`'a koruma arayüzü ve mimari test tablosu değişikliği gerekmez. Karar teyit edildi (4 Kasım 2026). Alternatif ve reddedilme gerekçesi bölüm 6'dadır.

**K2 — Taraflar Core'dadır.** İthalatçı/malik ve gümrük müşaviri yeni firma tablosu değildir; Core `Company`/`Party` kaydıdır, bir kez girilir. ADR-0029 K9'daki ilişki rolü kataloğuna `ithalatçı/malik` ve `gümrük müşaviri` rolleri eklenir. Depo modülü yalnızca firma kimliğine (`CompanyId`) başvurur.

**K3 — Malik ve faturalanan taraf ayrıdır.** Her parti `OwnerCompanyId` (mal kimin) ve `BillToCompanyId` (ardiye/fatura kime; varsayılan malik) taşır. Aynı ürün farklı malik ve gümrük statüsüyle ayrı bakiyededir (M3.1).

**K4 — Vekâlet.** `PowerOfAttorney`: ithalatçı, müşavir, geçerlilik tarihleri, kapsam, belge, durum. Müşavir yalnızca geçerli vekâleti olduğu ithalatçıların kayıtlarını görür ve yazar; bu kapsam sunucuda sorgu seviyesinde uygulanır, arayüzde gizleme yetki sayılmaz.

**K5 — Müşavir dış erişimi yalnızca ön bildirim ve belge ile sınırlıdır.** Müşavir `ASN` (ön bildirim) taslağı oluşturur/gönderir ve belge yükler (`warehouse.customs.asn.submit`, `warehouse.customs.documents.upload`). Stok hareketi yaratamaz; ASN'yi antrepo personeli kabul eder. Giriş **kanalı** dinamiktir: her ASN bir `Source` taşır (elle, Excel, kullanıcı, API, portal) ve tesis ayarı hangi kanalların açık olduğunu belirler; bütün kanallar aynı `asn.submit` iznine ve vekâlet kapsamına bağlıdır. İlk sürümde açık kanallar: (a) kısıtlı rollü kullanıcı (müşavir tenant'ta "Müşavir" rolüyle üyedir; kimlik platformda global olduğundan aynı kişi birden çok antrepoda üye olabilir) ve (b) içeri alma (e-posta/Excel ile gelen ASN'yi antrepo personeli kaydeder). Dış API anahtarı (Ç11) ve portal (Ç19.8) henüz yoktur; gelince aynı sözleşmeye bağlanır. Kanal önceliği pilot (Makaroğlu) akışına göre netleşir (doğrulanmadı).

**K6 — Beyanname.** `CustomsDeclaration` (tescil no, tarih, gümrük idaresi, rejim kodu, ithalatçı, müşavir, durum, kaynak, dış referans) ve `CustomsDeclarationLine` (satır no, GTİP, tanım, kap cinsi, kap adedi, brüt/net kg, menşe, miktar+birim, bakiye, fatura değeri). `UNIQUE(TenantId, RegistrationNo)`. Yasal gerçek satırdadır: bakiye, kısmi çıkış ve mutabakat beyanname satırı üzerinden yürür; stok kartındaki GTİP yalnızca varsayılandır. Rejim kodu (7100 vb.) katalogdan (lookup) gelir, koda sabitlenmez.

**K7 — Tescil numarası doğrulaması rejim profilindedir.** Biçim ve uzunluk kuralı (düzenli ifade + uzunluk) koda yazılmaz; `RegimeProfile` içinde (K16) tanımlanır. Başlangıçta ihlal **uyarıdır**; uzman doğrulayınca profil sürümünde engele çevrilir. Tenant daha sıkı yapabilir (uyarıyı engele çevirir), gevşetemez (K8, ADR-0029). Elle giriş ve entegrasyon aynı servisten geçer.

**K8 — Entegrasyon sözleşmesi.** Gümrük müşavirliği yazılımlarından (Evrim, Solon vb.) beyanname alımı `ICustomsDeclarationSource` benzeri bir sözleşmeyle ve gelen veri adaptörüyle yapılır (mevcut `IAcceptedQuoteSource` kalıbı). Adaptör aynı doğrulama ve kayıt boru hattından geçer; yinelenen dış referans ikinci kayıt üretmez. Sağlayıcıların gerçekten uygun bir API sunup sunmadığı doğrulanmamıştır; ilk sürümde yalnızca elle giriş vardır.

**K9 — GTİP kataloğu global, sürümlüdür.** Bakanlık verisi tenant'a ait olmadığı için `platform` şemasında, `TenantId`'siz, yürürlük tarihli ve sürümlü tutulur (ADR-0029 K3 kalıbı); platform yöneticisi Excel/XML ile toplu yükler. Bakanlıktan canlı çekme ilk aşamada yoktur (kamuya açık servis doğrulanmadı). Kod **metin** saklanır (baştaki sıfırlar kaybolmasın); ilk 6 hane HS kodudur; 12–14 hane desteklenir. Katalogda bulunmayan kod uyarı üretir, engellemez. Stok kartındaki GTİP varsayılan ve düzenlenebilir bir önerisidir.

**K10 — Kap cinsi ve menşe.** Kap cinsi (palet, koli…) ADR-0016 lookup'ıdır; menşe ülke kodu lookup'ıdır. Net kilo brüt kilodan büyük olamaz.

**K11 — Lokasyon seviyeleri tanımlıdır, sabit sütun değildir.** Lokasyon, kendine bağlı bir ağaçtır (`ParentId`, `Path`). Seviyeler tesis bazında `LocationLevelDefinition` ile tanımlanır: sıra, anahtar, görünen ad, uzunluk/desen, ayraç (örn. `ANT-A-02-04-C` için Tesis, Koridor, Raf, Kat, Göz; başka tenant üç seviye ya da altı seviye kurabilir). `Code` seviye değerlerinden üretilir; `UNIQUE(TenantId, FacilityId, Code)`. Kapasite ve kısıtlar düğümde tutulur ve alt düğümlere kalıtılır. Seviye tanımı kullanımdaki tesiste yalnızca ekleme ve yeniden adlandırma yapar; kod kalıbını bozan değişiklik etki raporu üretir. **Alan türü** (K18) sunucuda hareket sırasında denetlenir: gümrük denetimindeki mal serbest alana yerleştirilemez. Tesise özgü ülke özellikleri (antrepo tipi vb.) ADR-0029 K6 uyarınca tesis kaydında değer olarak tutulur.

**K12 — Serbest/gümrüklü aynı depoda.** Yol haritası M3.8 gereği hukuki doğrulama olmadan etkinleştirilmez: rejim profilindeki (K16) "birlikte bulunabilir" izni ve tesis bayrağı, varsayılan kapalı, `Doğrulanmadı` etiketiyle.

**K13 — Stok hareket defteri.** `StockMovement` yalnızca eklenir: hareket türü (kod **kategoriye** bakar: giriş, çıkış, transfer, rezervasyon, serbest bırakma, düzeltme; tenant bu kategorilere bağlı kendi alt türlerini ve neden kodlarını tanımlar, K18), parti, kaynak/hedef lokasyon, beyanname satırı, miktar ve kilo, `OccurredAt` (UTC), `UserId`, `MembershipId` (bileşik FK), `CorrelationId`, neden kodu, belge referansı, **`IdempotencyKey`** (`UNIQUE(TenantId, Key)`; barkod/terminal tekrarı çift hareket üretmez). Düzeltme güncelleme değil ters kayıttır (`ReversesMovementId`). Bakiye defterden türetilir; eşzamanlı çıkış/rezervasyon `rowversion` ile korunur ve eksi stok politikasını aşamaz; bakiyeyi doğrudan değiştiren uç nokta yoktur. **Denetim kaydı (karar, 4 Kasım 2026):** hareketler hareket defteri servisinden yazılır; servis aynı transaction'da `IAuditWriter` ve outbox'ı çağırır. `StockMovement` `AuditedEntities`'e girer (`warehouse.stockmovement`). Tek işlemler (elle çıkış, terminal okuması, düzeltme) hareket başına bir `Created` girdisi üretir. Toplu işlemlerde (kabul, Excel içe aktarma, toplu transfer) denetim ayrıntısı bir ayardır: varsayılan **özet** (tek satır; `RecordId` toplu işlem/kabul kaydıdır, `Changes` düz alanlardır: hareket adedi, toplam miktar ve kilo, hedef lokasyon, neden; iç içe liste yazılmaz, çünkü denetim ekranı `{alan: {old, new}}` biçimini bekler ve hassas alan gizleme alan adına bakar; `CorrelationId` özeti defterdeki hareketlere bağlar); isteyen tenant "hareket başına" seçer. Hareket düzeyinde iz her durumda defterdedir. Statü geçişi, `StockHold`, beyanname, ASN kabulü, vekâlet, profil seçimi, ayar ve lokasyon seviyesi değişiklikleri de denetime yazılır. Defter iş gerçeğidir, denetim kaydı "kim, hangi yolla" sorusunu cevaplar; biri ötekinin yerine geçmez.

**K14 — Gümrük statüsü durum makinesidir; makine rejim profilinden gelir.** Statüler (antrepoda, serbest dolaşıma girdi, transit vb.), izinli geçişler ve her geçişin gerektirdiği belge türü koda yazılmaz; `RegimeProfile` (K16) tanımlar. Kod yalnızca statünün **kategorisine** bakar (örn. `under_customs`, `released`, `in_transit`). Geçiş belge gerektirir (ör. çıkış beyannamesi) ve `CustomsStatusTransition` olarak kim/ne zaman/neden/belge ile kaydedilir. Statü sıradan alan düzenlemesiyle, alan kuralıyla veya otomasyon aksiyonuyla değişmez, yalnızca geçiş servisinden değişir (M3.7). Karantina ve gümrük engeli ayrı `StockHold` kaydıdır; engelli mal sevk edilemez (M3.4).

**K15 — Yetki ve kapsam.** İzinler `warehouse.*` ve `warehouse.customs.*` önekiyle; fatura değeri gibi alanlar alan seviyesi yetkiyle korunur. İş ortağı/müşteri portalı kullanıcısı başka maliklerin malını göremez (M3.9). Her varlık için `ICompanyShared`/`ICompanyScoped` seçimi ADR-0008'e göre uygulama diliminde yapılır.

**K16 — Rejim profili (sürümlü paket).** Ülke/rejim/antrepo tipine göre değişen her şey `RegimeProfile` içinde tanımlıdır: rejim kodu, tescil no kuralı (K7), statüler ve izinli geçişler (K14), geçiş başına zorunlu belge türü, süre kuralı (gün, uzatma, uyarı eşiği), teminat gerekliliği, serbest/gümrüklü alan birlikteliği izni (K12), zorunlu beyanname alanları. Profil `platform` şemasında global ve sürümlüdür (taslak → uzman doğrulaması → yayın; ADR-0029 K8 paket kalıbı); tesis bir veya birden çok profil seçer. Profilin üç modu vardır (karar, 4 Kasım 2026: dinamik): (a) **platform paketi**: yalnızca platform yayınlar, yasal çekirdek budur; (b) **türetilmiş profil**: tenant bir paketi temel alır ve yalnızca sıkılaştırır; (c) **özel profil**: tenant statüleri, geçişleri ve belgeleri sıfırdan yazar; "Doğrulanmamış (tenant tanımlı)" etiketi taşır, resmi rapor taslakları kapalıdır, K17 sabitleri aynen geçerlidir ve yayın için en az bir `under_customs` ve bir `released` kategorili statü gerekir. Türetilmiş profilde tenant uyarlaması yalnızca **sıkılaştırabilir** (uyarıyı engele çevirir, ek zorunlu alan, daha kısa uyarı eşiği); profildeki yasal kuralı kaldıramaz. Yeni rejim veya ülke kod değişikliği olmadan yeni profil sürümüyle gelir. Profil yayını mevcut kayıtları değiştirmez; kayıt, işlendiği sürüme bağlı kalır.

**K17 — Sabit kalanlar (gerekçeli).** Şunlar yapılandırılamaz, çünkü ihlalleri veri bütünlüğünü veya yasal izi bozar: tenant izolasyonu; hareket defterinin yalnızca eklenmesi ve idempotency; bakiyenin defterden türetilmesi; statünün yalnızca geçiş servisinden ve belgeyle değişmesi; sunucu tarafı yetki ve kapsam denetimi; yasal çekirdek alanların (GTİP, brüt/net kilo, menşe, kap cinsi, miktar, tescil no) varlığı ve gizlenememesi; net ≤ brüt değişmezi; gümrük denetimindeki malın serbest alana yerleştirilememesi. Bunlar dışında kalan her seçim tenant'a göre değişebilir ve kodda sabit yazılmaz.

**K18 — Kod kategoriye bakar, ad ve değer tenant'ındır.** Mevcut seçim listesi kalıbı (`ChoiceListDefinition`: kategori + düzenlenebilir değer) kullanılır. Kodun anladığı sabit **kategoriler** küçük tutulur; tenant bu kategorilere bağlı kendi adlarını, sırasını ve etkinliğini yönetir. Mevcut mekanizma bir kayıt türünün tek alanına bağlıdır (`ChoiceListDefinition`: `EntityKey` + `Field`); bu yüzden hareket, engel, ASN ve kabul satırı boru hattında kayıt türü olarak tanımlanır. İlk sürüm listeleri (yeni kategori eklemek kod değişikliğidir):

| Liste | Bağlandığı alan | Kodun anladığı kategoriler |
|---|---|---|
| Hareket türü | hareketin türü | giriş, çıkış, transfer, rezervasyon, serbest bırakma, düzeltme |
| Hareket nedeni | hareketin nedeni | kategorisiz |
| Alan türü | alanın türü | gümrük denetimli, serbest |
| Engel nedeni | `StockHold` nedeni | karantina, gümrük engeli |
| ASN durumu | ASN durumu | taslak, gönderildi, kabul edildi, reddedildi |
| Kabul farkı nedeni | kabul satırı | eksik, fazla, hasar |

Gümrük statüsü seçim listesi değildir, rejim profilinden gelir (K14). Kap cinsi ve menşe lookup'tır (K10). Kategoriye bağlı en az bir etkin değer her zaman kalır.

**K19 — Mevcut dinamik altyapı yeniden kullanılır; ikinci mekanizma yazılmaz.**

| Konu | Mekanizma | Tenant ne yapar |
|---|---|---|
| Stok kartı, parti, beyanname, ASN, kabul ek alanları (sıcaklık, SKT, seri vb.) | Özel alan (`CustomizableEntities`) | Alan ekler; yasal çekirdek alanları kaldıramaz |
| Form yerleşimi | `IModule.Forms` / `LayoutForm` (ADR-0021) | Sekme, bölüm, zorunluluk düzenler |
| Koşullu zorunlu/gizli/doğrula (örn. kap cinsi "palet" ise palet no zorunlu) | Alan kuralı katmanı (ADR-0029 K7) | Kural yazar; K17'deki sabitleri aşamaz |
| Politikalar | `IModule.Settings` (`PerCompany` olabilir) | Aşağıdaki liste |
| Hassas alanlar (fatura değeri, tarife) | `SensitiveFields` | Rolle görünürlük |
| Uyarı ve iş akışı (süre dolumuna kala, belge eksik) | Kural/otomasyon (ADR-0027) | Kural yazar |
| Raporlar ve resmi rapor taslakları | `ModuleReporting` | Veri kümesi seçer |
| İlişki alanları (firma, müşavir, ürün) | Lookup (ADR-0016) | Aranabilirlik |

Ayarlar (başlangıç listesi): lokasyon kodu ayracı ve biçimi, FIFO/FEFO politikası, eksi stok politikası (varsayılan kapalı), tescil no ihlalinin uyarı/engel düzeyi, süre uyarı eşikleri (gün), kör sayım, kabul tolerans yüzdesi, otomatik yerleştirme önerisi, müşavir ASN'sinin onay gerektirip gerektirmediği (varsayılan gerektirir), vekâlet zorunluluğu.

**K20 — Dinamiklik yasal güvenliği aşmaz ve performansı bozmaz.** Alan kuralı ve özel alan değerlendirmesi sunucuda tek yoldan çalışır (UI, API, import ve entegrasyon aynı değerlendirici); gizli alan değeri silinmez. Kural koşul derinliği sınırlıdır ve ilişki sorgusu içermez; hareket yazımı yolunda N+1 sorgu testi zorunludur. Etkin profil ve ayar bilgisi sürüm numarasıyla önbelleğe alınır, yayın/değişiklikte sürüm artar.

## 3. Dilimler

1. Bu ADR'nin kabulü ve uzman doğrulama listesi (bölüm 9).
2. Tesis, alan türü, lokasyon seviye tanımı ve ağaç, kod üretimi; modül ayarları ve seçim listesi iskeleti (K18, K19).
3. İlişki rolleri, vekâlet, stok kartı + özel alan/form tanımı, lookup'lar (kap cinsi, menşe), GTİP kataloğu ve Excel yükleme.
4. Rejim profili paketi (K16: sürümlü, platform tarafı) ve profil seçimi; beyanname başlık/satır ve tescil no kuralı.
5. ASN, kabul (eksik/fazla/hasar), yerleştirme, hareket defteri ve bakiye.
6. Statü makinesi (profilden), kısmi çıkış, beyanname mutabakatı.
7. Müşavir dış erişimi (Ç11/Ç19.8'e bağlı).
8. Entegrasyon adaptörü, süre uyarıları, teminat, resmi rapor taslakları.
9. Ardiye ücreti (M3.6).

Her dilim dört testle teslim edilir (mutlu yol, doğrulama, tenant izolasyonu, yetki); yeni tenant tablosu `TenantIsolationSql.AddTable` ile RLS'e eklenir.

## 4. Riskler

- Stok kartı depo modülünde başlar; ortak ürün tanımı Core'a taşınınca (ADR-0029 açık soru 2) kart/ürün eşlemesi göç gerektirir.
- Hareket defterinde bakiye güncellemesi ile eşzamanlılık hataya açıktır: yük ve yarış testi zorunludur.
- Tescil no ve süre kuralları yanlış sabitlenirse yasal hata üretir; bu yüzden yapılandırma ve uyarı-önce yaklaşımı.
- Dış API kimliği (Ç11/Ç19.8) gecikirse müşavir erişimi kısıtlı kullanıcıyla kalır.
- Dinamiklik hata yüzeyini büyütür: profil sürümü, tenant uyarlaması ve alan kuralı çakışabilir; çakışma yayını engellemeli ve raporlanmalıdır (ADR-0029 K8).
- Seviye tanımı değişirse mevcut lokasyon kodları bozulabilir; etki raporu ve yalnızca ekleme/yeniden adlandırma kuralı gerekir.
- Çok esneklik testi zorlaştırır: her profil ve sabit değişmez (K17) için sözleşme testi gerekir.
- Tek paylaşımlı veritabanında hareket tablosu büyür; dizin ve bölümleme ihtiyacı ölçülmelidir (doğrulanmadı).
- Denetim tablosu (`core.AuditLog`) tüm tenant'lar için tek tablodur; hareket başına denetim satırı onu hızla büyütür. Toplu işlemde varsayılan özet satırdır (K13). Dizin, toplu yazım ve saklama/arşiv politikası ölçülmedi (doğrulanmadı).

## 5. Seçenek: SQL Server ledger tablosu

Hareket defteri için SQL Server'ın append-only *ledger table* özelliği kurcalamaya karşı kanıt verebilir. EF Core, RLS ve migration zinciriyle uyumu **bilinmiyor**; karar öncesi kısa bir deneme gerekir. Başarısız olursa `INSTEAD OF UPDATE/DELETE` tetikleyicisi ve ilgili DB izinleriyle append-only zorlanır.

**Zamanlama (öneri):** ADR kabulünden sonra, hareket defteri diliminden (dilim 5) önce bir günlük deneme. ADR yalnızca "append-only veritabanı seviyesinde zorlanır" der; mekanizma denemeye bağlıdır. Bakılacaklar: EF Core, Row-Level Security ve migration zinciriyle uyum; bakiye tablosu güncellenebilir olmalıdır, ledger'a konamaz, ayrı tutulur; ledger tablosunu değiştiren migration kısıtları "geriye uyumlu migration" kuralıyla çakışabilir. Varsayılan plan tetikleyici ve DB izni; ledger ek kanıt katmanıdır.

## 6. Reddedilen seçenekler

- **İki ayrı proje (`Warehouse` + `Customs`):** bağımsızlık artar ama koruma arayüzü `Core.Contracts`'a girmek zorunda kalır ve stok denetimi ile statü denetimi aynı transaction'da kolay yapılamaz. İlk teslimde maliyeti faydasından yüksek; ikinci bir gümrük tüketicisi çıkarsa yeniden değerlendirilir.
- **Beyannameyi CRM'e koymak:** modüller arası tablo erişimi yasağı ve sektör mantığının Core/CRM'e sızması.
- **GTİP'i tenant tablosu yapmak:** her tenant'a kopya ve sürüm sapması.
- **Bakiye sütununu doğrudan güncellemek:** hareket izi ve yarış güvenliği kaybolur.
- **Tescil no biçimini koda sabitlemek:** doğrulanmamış hukuki kural; rejim profilinde (K16).
- **Sabit sütunlu lokasyon (Koridor/Raf/Kat/Göz) ve sabit statü listesi:** her tenant aynı yapıya zorlanır; çok sektörlü ürün için kabul edilemez (K11, K14).
- **Tenant'a yasal kuralı gevşetme yetkisi:** profildeki yasal kural yalnızca sıkılaştırılabilir (K16).
- **Serbest biçimli hareket türü:** kod anlamı bilmezse bakiye ve kontrol yapılamaz; kategori + tenant adı kullanılır (K18).

## 7. Açık sorular

1. ~~Tek modül mü, iki ayrı proje mi?~~ Karar: tek modül (4 Kasım 2026).
2. ~~Rejim profilini kim yayınlar?~~ Karar: dinamik; platform paketi, türetilmiş ve özel profil (K16).
3. ~~Seçim listesi kategorileri.~~ Karar: altı liste (K18).
4. ~~Müşavir kimliği.~~ Karar (taslak): kanal modeli; ilk sürümde kısıtlı kullanıcı + içeri alma, API/portal sonra (K5).
5. ~~Stok kartı ↔ Core ürün.~~ Öneri: kart Warehouse'ta başlar, boş bırakılabilir `CoreProductId` taşır. GTİP, kap, parti ve SKT takibi kartta kalır (sektör mantığı Core'a sızmaz). Core'a yalnızca ad, kod, birim ve kategoriyi içeren ortak ürün, ikinci bir modül (Siparişler, CRM teklif kalemi) aynı ürüne ihtiyaç duyunca çıkar; eşleme kod + birimle yapılır. `Core.Contracts`'ta ürün tanımı bulunamadı (yalnızca `UnitConversion`, `AcceptedQuote`); Siparişler modülünün kalem yapısı incelenmedi.
6. ~~Ledger denemesi zamanı.~~ Öneri: ADR'den sonra, dilim 5'ten önce (bölüm 5).

## 8. Test beklentileri (özet)

Tenant izolasyonu: A tenant'ı B'nin beyanname/parti/hareketini göremez. Yetki: yetkisiz kullanıcı statü geçişi yapamaz; müşavir vekâlet dışı ithalatçının verisini göremez ve stok hareketi yaratamaz. Doğrulama: net>brüt, yinelenen tescil no, alan türü ihlali reddedilir. Eşzamanlılık: iki eşzamanlı çıkış bakiyeyi eksiye düşüremez. Idempotency: aynı anahtarla tekrar çift hareket üretmez. Denetim: her hareket (veya toplu işlem özeti) aynı transaction'da denetim girdisi üretir; özet satırında hassas alan sızmaz; işlem geri alınırsa denetim girdisi de kalmaz. Dinamiklik: iki tenant farklı lokasyon seviyesi, hareket türü adı ve politikayla çalışır ve birbirinin ayarını görmez; tenant uyarlaması profildeki yasal kuralı gevşetemez; yasal çekirdek alan form düzeninden gizlenemez; profil yayını mevcut kaydın statü geçişini geriye dönük değiştirmez; kapalı `warehouse.customs` uzantılı tenant'ta gümrük alanları zorunlu değildir.

## 9. Doğrulanmadı

- Tescil numarası biçimi (örnek `26340000EX...`) ve uzunluğu.
- Antrepo tiplerinin süre, teminat ve alan kuralları; serbest/gümrüklü alanın aynı depoda olup olamayacağı.
- Evrim, Solon vb. yazılımların gerçek API'leri.
- Ticaret Bakanlığı'nın GTİP için makine okunur servisi.
- Pilot (Makaroğlu) akışı: ASN'yi kimin açtığı, kısmi çıkış, ardiye hesabı.
- Denetim kaydı yapısı okundu (`core.AuditLog`: kullanıcı, kayıt türü ve kimliği, işlem, alan farkı JSON, kaynak, `CorrelationId`; RLS ile değiştirilemez), hareket izi için uygun görünüyor; yük altında, dizin ve toplu yazım açısından ölçülmedi.
- **Yapılacak (uzmanla teyit):** gümrük denetiminin toplu işlemlerde hareket düzeyinde ayrı denetim satırı isteyip istemediği. Teyide kadar varsayılan özet satırdır ve tenant "hareket başına"yı seçebilir (K13).
- Kanal önceliği (müşavir ASN'yi kim, hangi yolla açıyor): pilot akışıyla teyit gerekir.
- Mevzuat ve entegrasyon maddeleri için hiçbir doğrulama yapılmadı; yukarıdaki liste uzman ve pilot onayı bekliyor.

## 10. Uygulama durumu

- **Dilim 2a (modül iskeleti, 5 Kasım 2026):** `Modulix.Modules.Warehouse` kaydedildi (anahtar `warehouse`; izinler `warehouse.facilities.*` ve `warehouse.locations.*`; "Depo sorumlusu" rol önerisi). Varlık, ayar, seçim listesi ve form yok; bunlar tesis ve lokasyon varlıklarıyla (dilim 2b) gelir, çünkü seçim listeleri bir kayıt türünün alanına bağlıdır ve kullanılmayan ayar eklenmez. Yerelde çalışan testler: mimari testler (56), Depo birim testleri (4), `ModulesAndRolesTests` (9). Tam entegrasyon seti çalıştırılmadı.
- **Dilim 2b-1 (tesis, 5 Kasım 2026):** `warehouse.Facilities` tablosu (kod `UNIQUE(TenantId, Code)`, ad, açıklama, ülke kodu, gümrük uzantısı bayrağı, etkin bayrağı; özel alan destekli), RLS politikası, `warehouse` şeması ve GRANT (init.sql + migration), `FacilityRecordStore` (genel kayıt uçları `/api/records/warehouse.facility` üzerinden), denetim kaydı ve özel alan bildirimi. `ICompanyShared` seçildi: şirket kapsamlı (`ICompanyScoped`) kayıt bugün hiçbir modülde yok ve tesisin işletici şirkete bağlanması ayrı bir karardır (aşağıdaki açık nokta). Arayüz (liste ve sekmeli form, ADR-0021 metadata düzeni) yok; ayrı bir adımdır.
- **Dilim 2b-2 (alan, 5 Kasım 2026):** `warehouse.Zones` (tesis içinde benzersiz kod, ad, tür, etkin bayrağı; tesis bileşik yabancı anahtarla bağlı, alanı olan tesis silinemez). Alan türü seçim listesi `warehouse.zone-kind`: kod kategoriye bakar (`controlled`, `free`), tenant türleri adlandırır (K18). Kurallar: gümrük denetimli alan yalnızca gümrük uzantısı açık tesiste tanımlanır; serbest ve gümrük denetimli alanlar aynı tesiste ancak tesisteki `AllowMixedZones` açıksa bir arada bulunur (varsayılan kapalı, K12; rejim profili gelince onunla birlikte değerlendirilir); mevcut alanlarla çelişen tesis ayarı kaydedilemez. Yeni izinler `warehouse.zones.read/write`.
- **Dilim 2b-3 (lokasyon seviyesi, 5 Kasım 2026):** `warehouse.LocationLevels` (ADR'deki `LocationLevelDefinition`). Her tesis kendi seviyelerini tanımlar: sıra (1–10, tesis içinde benzersiz), teknik anahtar (küçük harf, tesis içinde benzersiz), görünen ad, izinli karakter kümesi (`alphanumeric`, `letters`, `digits`; serbest düzenli ifade bilinçli olarak yok, K20 performans ve ReDoS riski), uzunluk aralığı (1–20) ve ayraç (`-._/:` karakterlerinden en çok üç, boş olabilir). Seviye tanımı olan tesis silinemez. Yeni izinler `warehouse.locationlevels.read/write`. "Kullanımdaki tesiste yalnızca ekleme ve yeniden adlandırma" denetimi 2b-4'te eklendi (aşağıda).
- **Dilim 2b-4 (lokasyon ağacı, 5 Kasım 2026):** `warehouse.Locations`. Düğümün seviyesi derinliğinden gelir (n. derinlik = tesisin sıraya göre n. seviye tanımı); değeri o seviyenin karakter kümesine ve uzunluk aralığına uyar ve büyük harfe çevrilir. Kod sunucuda üst kod + seviyenin ayracı + değerden üretilir (`ANT-A-02-04-C`), `UNIQUE(TenantId, FacilityId, Code)`; kök düğümde üst yok. Kökten kendine kimlik yolu (`Path`) alt ağaç sorguları için saklanır. Tesis, üst düğüm, değer ve kod oluşturulduktan sonra değişmez (alt düğüm kodları buna bağlı; düğüm taşıma sonraki bir adımdır). Alan (`ZoneId`, isteğe bağlı) üst düğümden kalıtılır; farklı alan verilemez, başka tesisin alanı verilemez, alt düğümü olan düğümün alanı değişmez. Silme: alt düğümü olan düğüm, lokasyonu olan alan ve tesis silinemez. **Seviye tanımı denetimi:** lokasyonu olan tesiste yeni seviye yalnızca mevcutların sonuna eklenir, mevcut seviyede yalnızca ad değişir, lokasyonların kullandığı seviye silinemez. Kapasite ve kısıtlar (K11) ve gümrük denetimli malın serbest alana yerleştirilememesi (K17) stok dilimine bırakıldı; kapasite için gerçek kullanım bilinmeden alan eklenmedi.
- **Dilim 2c-1 (arayüz: tesis, 5 Kasım 2026):** "Depo ve Antrepo" uygulaması (`AppCatalog`, "Operasyon" panel grubu; pasif "Antrepo" kartı kalktı) ve **Tesisler** ekranı (`/depo/tesisler`): ViewSelector (tümü, etkin, pasif, gümrük uzantılı), arama, ColumnPicker, Pager ve birincil "Yeni tesis" düğmesiyle liste; düzenleme `FacilityEditor` ile sekmeli ve metadata'dan çizilir (`WarehouseForms`: Genel bilgiler, Gümrük ve alanlar; ADR-0021, firma Form tasarımı ekranından özelleştirilebilir; özel alanlar desteklenir). Kayıt genel uçlardan (`RecordResponse`/`RecordWriteRequest`); doğrulama hataları alanın altında ve sekme rozetinde. İstemci anahtarları `Modulix.Shared.Warehouse.WarehouseAccess` içinde, sunucuyla aynılığı birim testle denetlenir. Yerelde tarayıcıda uçtan uca denendi (oluşturma, doğrulama hatası, açık/koyu tema belirteçleri, 375 px'te yatay taşma yok); ekran görüntüsü alınamadı (tarayıcı paneli görünür değildi), bu yüzden görsel inceleme (`visual-qa-checklist.md`) yapılmadı.
- **Dilim 2c-2 (arayüz: alan, seviye, lokasyon, 5 Kasım 2026):** üç ekran daha, aynı kalıpla. **Alanlar** (`/depo/alanlar`): ViewSelector (tümü, etkin, pasif), tesis ve alan türü süzgeçleri `FilterToggle` arkasında, tür rozeti kategoriye göre (gümrük denetimli dikkat tonu, serbest marka tonu), `ZoneEditor` metadata'dan çizilir (`WarehouseForms`). **Lokasyon seviyeleri** (`/depo/lokasyon-seviyeleri`): ayar niteliğinde olduğundan `FormTabs` ile tek bölümlü kayıt formu; lokasyonu olan tesiste sunucu kuralları (yalnızca ad, sona ekleme) ekranda not olarak görünür. **Lokasyonlar** (`/depo/lokasyonlar`): kök/etkin/pasif görünümleri, tesis ve alan süzgeçleri, satırda "Alt lokasyon ekle" (tesis, üst lokasyon ve alan önceden dolar), `LocationEditor` metadata'dan çizilir; kod sunucuda üretilir. Tesis, alan ve üst lokasyon seçimi `LookupPicker` ile (ADR-0016, `WarehouseLookupDefinitions`); alan ve üst lokasyon seçilen tesise bağlıdır. Depo menüsü dört ekranı izinlerine göre gösterir. Not: önceki ekranda kullanılan tanımsız rozet sınıfları (`status-success`, `status-muted`) mevcut tasarım sınıflarıyla (`status-customer`, `status-inactive`) değiştirildi.
- **Dilim 3a (vekâlet, 6 Kasım 2026):** ilişki rolleri `ithalatci-malik` ve `gumruk-musaviri` platform kataloğunda zaten vardı (ADR-0029 K9, `RelationshipRoles` migration'ı); yeni rol tablosu eklenmedi. `warehouse.PowersOfAttorney` (K4): ithalatçı ve müşavir Core firmasıdır (`core.party`, bileşik yabancı anahtar, vekâleti olan firma silinemez ve birleştirmede kullanım sayılır), geçerlilik tarihleri (bitiş boşsa süresiz), kapsam ve belge referansı (serbest metin; belge dosyası yükleme sonraki adım), etkin bayrağı (iptal kaydı silmez). Kurallar: ithalatçı "İthalatçı / malik", müşavir "Gümrük müşaviri" rolüne sahip olmalı (oluştururken denetlenir), ikisi aynı firma olamaz, bitiş başlangıçtan önce olamaz, taraflar sonradan değişmez. Yeni izinler `warehouse.attorneys.read/write`; depo modülü firmayı yalnızca vekâlet okuma iznine bağlı okur (`SharedRecordAccess`). Genel altyapıya küçük ekleme: `LookupDefinition.FixedFilterName/FixedFilterValue` (sabit süzgeç) ve firma deposunda `relationshipRole` süzgeci; ithalatçı ve müşavir seçim kutuları yalnızca ilgili roldeki firmaları getirir. Vekâletin müşavir kapsamına dönüşmesi (müşavir yalnızca geçerli vekâleti olan ithalatçıların kaydını görür) ASN diliminde yapılır; bu kayıt tek başına erişim vermez. Arayüz 3b'dedir.
- **Dilim 3b (arayüz: vekâlet, 6 Kasım 2026):** **Vekâletler** ekranı (`/depo/vekaletler`): ViewSelector (tümü, bugün geçerli, iptal edilmiş), arama (kapsam ve belge), ithalatçı ve müşavir süzgeçleri `FilterToggle` arkasında (rol süzgeçli seçim kutusu), durum rozeti (geçerli, başlamadı, süresi doldu, iptal edildi); `PowerOfAttorneyEditor` sekmeli ve metadata'dan çizilir (`WarehouseForms`: Genel bilgiler, Kapsam ve belge). İthalatçı ve müşavir adları liste için seçim kutusunun çözümleme ucundan alınır. Yerelde tarayıcıda uçtan uca denendi (rol süzgeçli seçim, doğrulama hatası ve sekme rozeti, oluşturma, liste; 375 px'te yatay taşma yok); ekran görüntüsü alınamadı (tarayıcı paneli görünür değildi), görsel inceleme yapılmadı.
- **Dilim 3c (stok kartı, 6 Kasım 2026):** `warehouse.StockItems` (K19, açık soru 5): tenant içinde benzersiz stok kodu (harf, rakam ve `-_./`; büyük/küçük harf farkı yok sayılır), ad, birim (Y9b listesi `warehouse.stock-unit`, kategorisiz; hazır değerler Adet, Kilogram, Koli, Palet; tenant yönetir), varsayılan GTİP, açıklama, etkin bayrağı; özel alan destekli (sıcaklık, SKT vb. K19). GTİP metin saklanır (baştaki sıfırlar kaybolmaz), ayraçlar (nokta, boşluk) atılır, yalnızca rakam ve 6-14 hane denetlenir (`GtipRules`); bu yalnızca yapısal denetimdir, hangi uzunluğun hangi tarifede geçerli olduğu ve kodun katalogda bulunması doğrulanmadı ve katalog dilimine (3d) bırakıldı (katalogda yok = uyarı, engel değil). **Bilinçli olarak eklenmeyenler:** `CoreProductId` (Core'da ortak ürün tanımı olmadığı için kullanılmayan sütun olurdu; ürün gelince geriye uyumlu bir sütunla eklenir), parti/SKT takibi bayrakları ve kap bilgileri (parti ve kabul dilimlerinde, kullanıldıkları yerde). Yeni izinler `warehouse.stockitems.read/write`. Arayüz sonraki PR'dadır.
- Sıradaki: Stok kartları ekranı, GTİP kataloğu ve Excel yükleme.
- **Karar (5 Kasım 2026):** tesis ve ona bağlı depo kayıtları tenant genelinde ortak kalır (`ICompanyShared`). Şirket kapsamına (`ICompanyScoped`: tesis antrepo izni olan işletici şirkete bağlı, başka şirketin kullanıcısı göremez) geçmek gerekirse `CompanyId` ekleyen bir migration ve şirket kapsam alanı gerekir; çoklu şirketli bir müşteri bunu isterse yeniden değerlendirilir.
