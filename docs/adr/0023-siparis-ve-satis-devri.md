# ADR-0023 — Sipariş ve satış devri (EPIC 8)

Durum: Kabul edildi (1 Kasım 2026). Uygulama durumu en sondaki bölümde; yalnızca kodda ve testte doğrulananlar "yapıldı" sayılır.

Veritabanı modeli ADR-0002 ve ADR-0020'ye uyar: tek paylaşımlı veritabanı, her sipariş tablosu `TenantId` taşır, EF query filter ve SQL Server Row-Level Security ile korunur. Tenant istemciden değil oturumdan çözülür. Bu epic tenant başına veritabanı varsaymaz.

## 1. Mevcut durum analizi (kod kanıtlarıyla)

Etiketler: **Var** (kodda ve testte doğrulandı), **Kısmen var**, **Yok**, **Doğrulanamadı**.

| Özellik | Mevcut durum | Kod/dosya kanıtı | Eksik veya risk | Uygulanacak çözüm | Bağımlılık |
|---|---|---|---|---|---|
| Teklif ve revizyonlar | Var | `Crm/Domain/QuoteEntities.cs` (`Quote.FamilyId`, `RevisionNo`, `QuoteLine.StableKey`), `Quotes/QuoteLifecycle.cs`, `QuoteTests`, `QuoteRuleTests` | Yok | Sipariş kabul edilmiş revizyondan okunur | Yok |
| Müşteri kabulü ve kanıtı | Kısmen var | Portal kabulü `QuoteDecision` (imzalayan, e-posta, belge SHA-256, ifade, maskeli adres); manuel kabul `QuoteWorkflow.RecordAcceptedAsync` yalnızca not ve `QuoteEvent` bırakır | Manuel kabulde belge/kanıt yok; `AcceptedQuote` yalnızca `AcceptedBy` ve `DocumentSha256` verir, kabul yöntemi, kabul eden kullanıcı, geçerlilik tarihi ve kabul gerekçesi yok | `AcceptedQuote` sözleşmesine kabul yöntemi (portal/manuel), kabul eden kullanıcı kimliği, geçerlilik, sorumlu ve kalem ayrıntıları eklenir (geriye uyumlu, `init` özellikleri) | Yok |
| İç onay ile müşteri kabulünün ayrımı | Var | `Quote.ApprovalStatus` ayrı alan; `QuoteApprovalRules`, `QuoteMultiStepApprovalTests` | Yok | Siparişin kendi onayı da ayrı alan | Yok |
| Dönüşümün geçerlilik ilkesi | Kısmen var | `QuoteRules.Can`, `QuoteSharingService` kabulde `ValidUntil` denetler; kabul sonrası durum `Accepted` kalır (`QuotePostAcceptTests`) | Siparişte "hangi tarihe göre" açık değildi | Geçerlilik **kabul anında** denetlenir; dönüşümde yeniden denetlenmez. Sipariş kabul tarihini ve geçerliliği snapshot olarak taşır | Yok |
| Sipariş ve kalemleri | **Yok** | `grep` sonucu: sipariş entity'si yok; yol haritası M1.6 ve M6.1 "yapılmadı" der | Hedef modül yok | Yeni `Modulix.Modules.Orders` (şema `orders`): `SalesOrder`, `SalesOrderLine`, snapshot | Core.Contracts |
| Sipariş–teklif tekilliği | **Yok** | — | Mükerrer sipariş riski | Kalem tüketim defteri (`QuoteLineConsumption`), `CHECK (Converted <= Accepted)`, eşzamanlılık belirteci, benzersiz anahtarlar | Sipariş tabloları |
| Sözleşme bağlantıları | Kısmen var | Sözleşme `SourceQuoteId` taşır (`ContractEntities.cs`); `ContractFromQuote` kalem tüketimi | Siparişten sözleşmeye/sözleşmeden siparişe bağ yok; modüller birbirinin tablosuna erişemez | Sipariş, sözleşmeyi kimlik+numara referansı olarak taşır (doğrulama yok, belgelenir); sözleşmeden okuma `Core.Contracts` arayüzüyle sonraki iş | Sipariş modülü |
| Operasyon işleri ve devir | **Yok** | `Modulix.Modules.Logistics` yalnızca izin ve rol şablonu içerir (`LogisticsModule.cs`); entity, tablo, uç nokta yok | "Mevcut operasyon modülü" aslında boş iskelet | Operasyon işi, sipariş modülünde sınırlı fakat gerçek bir yapı olarak kurulur (`OperationJob`); lojistik modülü genişletilmez (sevkiyat/taşıyıcı ayrı epic) | Devir kaydı |
| Teslimat ve hizmet başlangıcı | **Yok** | — | — | `Fulfillment` kayıtları: kalem bazlı teslimat, hizmet başlangıcı, ayrı izleme | Operasyon işi |
| Müşteri kazanımı sonrası onboarding | **Yok** | `grep onboarding`: yalnızca bir yorum/metin eşleşmesi; modül, tablo, şablon yok | Sahiplik tanımsız | Onboarding bu epic'te **kurulmaz**; sahiplik kuralı: sipariş devrinden doğan operasyon görevleri operasyon işine aittir, müşteriye yönelik eğitim/belge toplama ileride onboarding modülünün işidir | Yok |
| Aktivite altyapısı | Var | `CRM Activities`, `IFollowUpActivities` (`Core.Contracts/Activities`), `IActivityLinkTarget`, `ContractLinkTarget` | Sipariş bağlantı hedefi yok | `OrderLinkTarget`, devir/gecikme takip aktiviteleri işaret satırıyla bir kez | Sipariş modülü |
| Belge/dosya | **Yok** (genel depo) | Teklif ve sözleşme PDF'i veritabanında `byte[]` (`QuoteDocument`, sözleşme belgeleri); genel dosya eki altyapısı yok (yol haritası Ç4) | Teslim kanıtı için dosya yükleme yok | Teslim kanıtı: belge **özeti (SHA-256) ve adı** kaydedilir, içerik sipariş modülünde küçük bir kanıt tablosuna konur (boyut sınırı, tenant'a bağlı); genel dosya deposu Ç4'e bırakılır ve bu sınırlama belgelenir | Teslimat |
| Bildirim | Kısmen var | `IEmailQueue`, `AssignmentEmailSubscriber`; uygulama içi bildirim merkezi yok | Uygulama içi bildirim yok | Aktivite + e-posta kuyruğu kullanılır; yeni bildirim kanalı kurulmaz | Yok |
| Raporlama | Kısmen var | `QuoteReport`, `ContractDashboard` örnek | Sipariş raporu yok | `OrderDashboard`: para birimi bazında ayrı toplam | Sipariş modülü |
| Katalog, paket, fiyat listesi | Var | `CatalogEntities.cs` (`Product.Kind` Product/Service, `ChargeType`, `DeliveryMode`, `ServiceDurationMinutes`), `PricingEntities.cs`, `QuoteLine.ParentStableKey/Role` | `AcceptedQuoteLine` ürün türünü, paket üst anahtarını, indirimi taşımaz | Sözleşmeyi genişlet: kalem türü (ürün/hizmet), üst kalem, liste/indirim bilgisi | Yok |
| Rol/ekip/birim/yetki | Var | `Core/Teams/Team.cs`, `Core/Organization/Company.cs` (`ICompanyShared/Scoped`, ADR-0008), `RecordScope`, `SensitiveFields`, `IModule.RoleTemplates` | Operasyon ekibi seçimi için lookup tanımı yok | Takım lookup'ı (`orders.operation-job.teamId`), sipariş sorumlusu kullanıcı lookup'ı | Yok |
| Olay/outbox/iş altyapısı | Var | `EventDispatcher`, `ContractJobsWorker` (tenant başına `sp_getapplock`), `QuoteExpiryWorker` | — | Devir kabulü ve iş üretimi **tek işlemdedir** (atomik); asenkron gerekmez. Zamanlanan takipler için sipariş işçisi aynı kalıpla | Sipariş modülü |

### Bulguların sonucu

- Epic'in çekirdeği (sipariş, devir, operasyon işi, teslimat) kodda **yoktur**; yol haritası da bunu "yapılmadı" olarak işaretler.
- Eksiksiz teklif→sözleşme devri örnek alınır: `IAcceptedQuoteSource` + kalem tüketimi + kayıt boru hattı + atomik işlem.
- Finans, stok, muhasebe ve destek modülleri yoktur; bunlar varmış gibi gösterilmez. Sipariş tutarı "sipariş tutarı"dır; tahsilat, fatura ya da gelir değildir.

## 2. Tasarım kararları

### 2.1 Modül sınırı

Yeni `Modulix.Modules.Orders` (anahtar `orders`, şema `orders`). Yalnızca `Modulix.Core.Contracts`'a başvurur; CRM'e ve sözleşmeye başvurmaz. Teklifi `IAcceptedQuoteSource` ile okur. Operasyon işi, devir ve teslimat bu modüldedir; lojistik modülü sevkiyat/taşıyıcı için kalır.

### 2.2 Üç ayrı durum alanı

| Alan | Değerler |
|---|---|
| Ticari durum (`Status`) | `Draft` taslak, `PendingApproval` onay bekliyor, `Approved` onaylandı, `Cancelled` iptal edildi, `Closed` kapatıldı |
| Devir durumu (`HandoverStatus`) | `NotStarted` başlamadı, `Preparing` hazırlanıyor, `Ready` devre hazır, `Sent` operasyona gönderildi, `InfoRequested` bilgi bekleniyor, `Accepted` kabul edildi |
| Gerçekleşme (`FulfillmentStatus`) | `NotStarted`, `Planned`, `Partial`, `Completed` — kalem ve hizmet kayıtlarından **türetilir**, elle yazılmaz |

### 2.3 Ticari durum geçişleri

| Kaynak | Hedef | Yetkili rol (izin) | Ön koşul | Yan etki |
|---|---|---|---|---|
| (yok) | Taslak | `orders.orders.create` | Kabul edilmiş teklif, kalem tüketimi uygun | Sipariş, kalem snapshot'ı, tüketim defteri, tarihçe, audit |
| Taslak | Onay bekliyor | `orders.orders.write` | En az bir kalem, müşteri bağlı | Tarihçe |
| Taslak/Onay bekliyor | Onaylandı | `orders.orders.approve` | Gönderen ile onaylayan ayrı (ayar ile izin verilebilir) | Ticari alanlar kilitlenir, tarihçe |
| Onay bekliyor | Taslak | `orders.orders.write` | Gerekçe | Tarihçe |
| Taslak/Onay bekliyor/Onaylandı | İptal edildi | `orders.orders.cancel` | Gerekçe; gerçekleşmiş teslimat/başlamış hizmet varsa yalnızca açık kalan kısım iptal edilir ve gerçekleşen kayıtlar korunur | Kalem tüketimi serbest kalır (teslim edilmemiş miktar için), açık operasyon işleri iptal edilir, tarihçe |
| Onaylandı | Kapatıldı | `orders.orders.close` | Kalan yükümlülük yok (tüm kalemler teslim/başlamış ve açık iş yok); varsa `orders.orders.force-close` ve gerekçe | Tarihçe, audit |
| Kapatıldı/İptal edildi | — | — | Son durum | — |

Geçişler sunucuda doğrulanır (`OrderTransitions.Can`); arayüz aynı kuralı eylem listesinden okur. İptal iadeyle karıştırılmaz: iptal siparişin açık kısmını sonlandırır; iade/fatura/tahsilat finans modülünün konusudur ve bu epic'te yoktur.

### 2.4 Dönüşüm kuralları

- Varsayılan: teklifin **tamamı** tek siparişe dönüşür. Kısmi dönüşüm ayrı ve açık bir seçenektir (`mode = Partial`, kalem bazında miktar).
- Kalem tüketim defteri her teklif kalemi için kabul edilen miktar, dönüştürülen miktar ve eşzamanlılık belirteci tutar. `CHECK (ConvertedQuantity <= AcceptedQuantity)` ile fazla dönüşüm veritabanında da engellenir.
- Tekrar/çift tıklama: kalemler tükendiyse ikinci istek 409 döner ve yanıtta mevcut sipariş kimliği bulunur; arayüz o siparişe yönlendirir. Eşzamanlı istekte ikincisi eşzamanlılık belirteci ya da `CHECK` nedeniyle reddedilir.
- Geçerlilik **kabul anında** denetlenmiştir. Dönüşüm bu denetimi yinelemez. Reddedilmiş, iptal, süresi dolmuş (kabul edilmemiş) veya revizyonla yerine geçilmiş teklif `Accepted` olmadığı için kaynak tarafından zaten verilmez.
- İstisnai dönüşüm: bu epic'te **desteklenmez** (kabul edilmemiş teklif asla dönüştürülmez). Gerekirse ayrı karar gerekir.
- Snapshot: kalemler (kod, ad, miktar, birim, net birim fiyat, vergi, tutarlar), para birimi, ödeme/teslimat/ticari koşullar, müşteri ünvanı ve adresler siparişe kopyalanır. Sonradan değişen katalog, müşteri kartı veya teklif siparişi değiştirmez.

### 2.5 Sahiplik sınırları

| Süreç | Sahip |
|---|---|
| Teklif, kabul | CRM |
| Sipariş, devir, operasyon işi, teslimat, hizmet başlangıcı | Siparişler modülü |
| Sözleşme taahhütleri, yükümlülükler | Sözleşmeler modülü |
| Onboarding (müşteriye eğitim/belge toplama), destek, finans, stok | Bu epic'te yok; arayüz noktaları belgelenir, çalışıyormuş gibi gösterilmez |

### 2.6 Dilimler

1. **Dilim 1 — Çekirdek:** analiz, ADR, modül iskeleti, sipariş+kalem+tüketim defteri, tekliften dönüşüm (tam ve kısmi), liste/detay uç noktaları, izinler, RLS, testler, sipariş listesi ve oluşturma ekranı.
2. **Dilim 2 — Yaşam döngüsü:** geçiş tablosu, onay, iptal, kapatma, değişiklik talebi.
3. **Dilim 3 — Satış devri:** devir kaydı, kontrol listesi, gönder/kabul/bilgi iste/yeniden gönder, geçmiş, takip aktiviteleri.
4. **Dilim 4 — Operasyon işi:** iş üretimi (kabulde atomik), atama, görev, bağımlılık, kuyruk.
5. **Dilim 5 — Gerçekleşme:** kısmi teslimat, hizmet başlangıcı, kanıt, türetilmiş durum.
6. **Dilim 6 — Ekranlar ve göstergeler:** sipariş detayı birleşimi, özet göstergeler, doküman ve yol haritası.

## 3. Sınırlamalar (baştan bilinen)

- Genel dosya depolama (Ç4) yoktur; teslim kanıtı küçük, sınırlı bir tabloda tutulur.
- Sözleşme bağlantısı kimlik ve numara referansıdır; çapraz modül doğrulaması yoktur.
- Onboarding, destek, finans ve stok bağlantıları kurulmaz.
- Kur dönüşümü yoktur; farklı para birimleri ayrı gösterilir, toplanmaz.

## 4. Uygulama durumu

**Dilim 1 — Çekirdek (1 Kasım 2026): uygulandı ve doğrulandı.** Modül iskeleti (`Modulix.Modules.Orders`, şema `orders`), `SalesOrder`, `SalesOrderLine`, `SalesOrderEvent` ve `QuoteLineConsumption` tabloları (migration `OrderManagement`, RLS ve şema yetkisi dahil), `IAcceptedQuoteSource` sözleşmesinin kabul yöntemi/kabul eden/geçerlilik/kalem türü/adres alanlarıyla genişletilmesi (geriye uyumlu), tekliften tam ve kısmi dönüşüm, kalem tüketim defteri, liste/detay/önizleme/oluşturma uç noktaları, izinler ve rol şablonları, sipariş listesi, tekliften oluşturma ve sipariş detay ekranları.

Doğrulama: 13 birim testi (kalem hesabı, kısmi dönüşüm, kuruş farkı, taslak düzenleme kuralı), 9 entegrasyon testi (doğru tutar ve kalemler, uygun olmayan teklifin reddi, çift tıklama ve eşzamanlı istek, kısmi dönüşümde kabul edilen miktarın aşılmaması, snapshot, genel uçtan oluşturma/silme engeli, tenant izolasyonu, yetki ve tutar gizleme, aktivite bağlantısı), mimari testler.

**Dilim 2 — Yaşam döngüsü ve değişiklik yönetimi (1 Kasım 2026): uygulandı ve doğrulandı.** Ticari durum geçiş tablosu sunucuda uygulanır (`OrderTransitions`; onaya gönderme, geri çekme, onay, gerekçeli geri gönderme, iptal, kapatma, zorla kapatma), dört göz ilkesi (ayarla gevşetilebilir), yetkiler (`orders.orders.approve|cancel|close|force-close`), iptalde teslim edilmemiş miktarın iptali ve teklif kalemi tüketim defterinden serbest bırakma (teslim edilen miktar ve kayıtları korunur; kalemin kopyalanmış tutarı değişmez, etkin tutar iptal miktarına göre hesaplanır), bekleyen talebin iptalde geri çekilmesi. Onaylanmış siparişin ticari alanları yerinde değişmez: gerekçeli değişiklik talebi (`orders.change-request`) başlık alanlarını (adres, lokasyon, tarihler, koşullar, notlar) ve kalem miktarı azaltmayı kapsar; artırma yoktur (ek kapsam yeni teklifle ayrı sipariştir), teslim edilen miktarın altına inilemez. Talep açılırken operasyon, teslimat, sözleşme ve tutar etkisi hesaplanıp gösterilir; talep yetkili ve talebi açandan farklı kişi onaylayınca tek işlemde uygulanır. Sipariş başına tek bekleyen talep veritabanında da (filtreli benzersiz indeks) korunur. Arayüz: durum işlemleri paneli (kuralı ve yapılamama nedenini sunucudan alır) ve değişiklik talepleri paneli. Migration `OrderLifecycle`.

Dilim 2 doğrulaması: 28 yeni birim testi (geçiş tablosu, iptal/azaltma ve kuruş hesabı, değişiklik kuralları, etki), 8 yeni entegrasyon testi (onay akışı ve dört göz, kilit, iptal ve tüketim defteri, kapatma ve zorla kapatma, talebin etkisi/uygulanması/reddi/geri çekilmesi, tenant izolasyonu, yetki ve tutar gizleme). Not: kapatma için gerçekleşmenin `Completed` olması gerekir; bunu üreten teslimat/başlangıç kayıtları dilim 5'tedir, o zamana kadar kapatma yalnızca zorla kapatma yetkisiyle yapılabilir. Teslim edilen miktar kayıtları dilim 5'e kadar oluşmadığından "kısmen teslim edilmiş siparişin iptali" kuralı birim testlerle doğrulanmıştır, uçtan uca henüz denenemez.

**Henüz yapılmadı (sonraki dilimler):** satış devri, kontrol listesi ve takip aktiviteleri (dilim 3); operasyon işi, atama, görev ve bağımlılık (dilim 4); kısmi teslimat ve hizmet başlangıcı (dilim 5); özet göstergeler ve birleşik sipariş izleme (dilim 6). İstisnai dönüşüm bu epic'te desteklenmez.

**Bilinen sınırlamalar (dilim 1):** manuel teklif kabulünde belge kanıtı yoktur (yalnızca not ve kullanıcı); sözleşme bağlantısı doğrulanmayan kimlik+numara referansıdır; PUT tam durum gönderir (platform kuralı), gönderilmeyen düzenlenebilir alan temizlenir.
