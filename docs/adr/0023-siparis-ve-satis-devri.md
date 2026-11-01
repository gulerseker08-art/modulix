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

**Dilim 3 — Satış devri (1 Kasım 2026): uygulandı ve doğrulandı.** Sipariş başına tek yapılandırılmış devir kaydı (`orders.handover`: kapsam, kapsam dışı, çıktılar, taahhütler, lokasyon, ilgili kişi, hedef tarihler, öncelik, belgeler, ön koşullar, ödeme/sözleşme koşulları, riskler, özel talepler, bağımlılıklar, sorumlu operasyon kullanıcısı/ekibi, tamamlanma ve müşteri kabul kriterleri), varsayılan sekiz maddelik kontrol listesi (`orders.handover-item`; varsayılan maddeler silinmez ve metni değişmez, kullanıcı kendi maddesini ekleyebilir) ve devir geçmişi. Sunucu her kayıtta zorunlu bilgi ve kontrol listesini denetler: eksik varsa devir Hazırlanıyor, yoksa Hazır olur; gönderim yalnızca Hazır devir ve onaylanmış siparişle yapılır. Akış: gönder (`orders.handovers.send`) → operasyon kabul eder ya da gerekçeyle bilgi ister (`orders.handovers.accept`) → satış tamamlayıp (neyin tamamlandığını yazarak) yeniden gönderir; tur sayacı, gönderen, kabul eden, tarihler, bilgi talepleri ve yeniden gönderimler geçmişte tutulur. Gönderilen devir karara bağlanana kadar, kabul edilen devir ise tamamen kilitlidir (sonraki değişiklikler değişiklik talebiyledir). Siparişin devir durumu devir kaydının aynasıdır ve aynı işlemde güncellenir. Takip: gönderimde operasyon sorumlusuna, bilgi isteğinde satış sorumlusuna aktivite açılır (aktivite modülü yoksa atlanır); kabul bekleme süresi (`orders.handover.wait-hours`, varsayılan 48) aşılırsa zamanlayıcı (`orders.scheduler` sistem kimliği, tenant başına `sp_getapplock`) satış sorumlusuna tur başına bir kez takip açar; çalıştırmalar tekrarlansa da kayıt üzerindeki tur işareti ikinci takibi engeller. Migration `OrderHandover`; devir ekranı (`/siparisler/{id}/devir`).

Dilim 3 doğrulaması: 19 yeni birim testi (hazırlık denetimi, düzenlenebilirlik, geçişler), 5 yeni entegrasyon testi (kontrol listesi ve hazır olma, eksik/onaysız gönderim ve yetki, gönder-bilgi iste-yeniden gönder-kabul akışı, kilitler, geçmiş ve takip aktiviteleri, kontrol listesi kuralları, tenant izolasyonu ve yetki, gecikme takibinin tekrarsızlığı). Kabul edilen devirden operasyon işi oluşturma dilim 4'tedir; bu dilimde kabul yalnızca devir ve sipariş durumunu günceller.

**Dilim 4 — Operasyon işi (1 Kasım 2026): uygulandı ve doğrulandı.** Operasyon işi siparişe ve ilgili kalemlerine bağlı gerçek kayıttır (`orders.operation-job`; kalem bağları, görevler, bağımlılıklar ve geçmiş ayrı tablolar). İşler devir operasyon tarafından kabul edilirken **aynı işlemde** ve bir kez üretilir; strateji `orders.jobs.strategy` ayarıyla yönetilir: `PerKind` (ürün kalemleri tek Teslimat, hizmet/paket/serbest kalemler tek Hizmet başlangıcı işi; varsayılan), `Single` (tek iş) ya da `PerLine` (kalem başına). `orders.jobs.service-after-delivery` açıksa hizmet işi teslimat işi tamamlanmadan başlayamaz. Mükerrer iş engeli: aynı sipariş ve üretim grubu için açık iş filtreli benzersiz indeksle tektir; bir kalem aynı anda yalnızca bir açık işte bulunur. Üretim ve kabul tek işlemde olduğundan ayrıca outbox/yeniden deneme altyapısı gerekmez: hata halinde kabul de geri alınır, hata kullanıcıya görünür ve yeniden denenebilir, tekrar çalıştırma mükerrer iş üretmez. Görevler ürün/hizmet türü şablonundan gelir (`OperationTemplates`; teslimat ve hizmet için beşer madde, karma işte ikisi); şablon görevinin başlığı değişmez ve silinmez, kullanıcı kendi görevini ekler. Atanacak sorumlu/ekip yoksa iş kaybolmaz, "Atama bekliyor" durumunda kuyrukta görünür. Geçişler sunucuda doğrulanır: atama (`orders.jobs.assign`), başlatma (atanmış, bağımlı işler tamamlanmış, sipariş onaylı), tamamlama (zorunlu görevler bitmiş), iptal (gerekçeli) ve bağımlılık ekleme/kaldırma (kendine ve döngüye bağ yok, aynı sipariş; iptal edilen öncül işi beklemeyi sürdürür, bağımlılık kaldırılarak çözülür) — hepsi `orders.jobs.manage`. Sipariş iptalinde açık işler aynı kaydetmede iptal edilir, tamamlanan işler korunur. Elle iş açma yalnızca devir kabul edildikten sonra ve onaylı siparişte mümkündür. Zamanlayıcı: atama bekleyen iş (`orders.jobs.unassigned-hours`, varsayılan 24) için devri kabul eden kullanıcıya, planlanan bitişi geçmiş açık iş için sorumluya iş başına bir kez takip aktivitesi açar. Migration `OperationJobs`; ekranlar: operasyon iş kuyruğu (`/siparisler/isler`: benim, atanmamış, planlanan, devam eden, geciken görünümleri), iş detayı (`/siparisler/isler/{id}`) ve sipariş detayında operasyon işleri paneli.

Dilim 4 doğrulaması: 9 yeni birim testi (üretim planı ve stratejiler, şablonlar, geçiş tablosu, atama durumu, bağımlılık ve döngü kuralı), 8 yeni entegrasyon testi (devir kabulünde bir kez üretim, strateji ve bağımlılık, başlat-görev-tamamla ve yetki, atama bekleyen iş ve atama yetkisi, bağımlılık kuralları, sipariş iptalinde işler, tenant izolasyonu ve yetki, atanmamış/geciken takibinin tekrarsızlığı). Not: 'ürün' kalemi (teslimat işi) testlerde kalem türü doğrudan veritabanında ayarlanarak denendi; katalogdan ürün satırı üretimi mevcut teklif testlerinin kapsamındadır. Teslim edilen miktar ve hizmet başlangıcı kayıtları dilim 5'tedir; iş tamamlamak şu an gerçekleşme durumunu değiştirmez.

**Dilim 5 — Teslimat ve hizmet başlangıcı (1 Kasım 2026): uygulandı ve doğrulandı.** Ürün kalemleri çoklu ve kısmi teslimatla (`orders.delivery` + teslimat satırları), hizmet/paket/serbest kalemler kalem başına başlangıç kaydıyla (`orders.service-start`) ayrı izlenir; karma sipariş ikisini birlikte taşır. Teslimat planı kalan miktarı (etkin miktar - teslim edilen) ve diğer açık teslimatlarda planlı miktarı aşamaz; fazla teslimat sunucuda reddedilir ve veritabanında `CHECK` ile de korunur (siparişin teslimat ve hizmet yazmaları işlem kapsamlı kilitle `ITransactionLock`, `sp_getapplock` sıraya konur; kilidi bekleyen istek güncel miktarı okuyup yeniden doğrular, böylece iki eşzamanlı teslim kaydından yalnızca biri geçer, diğeri doğrulama hatası alır; sürüm belirteci ve `CHECK` son savunmadır; kilit ilk uygulamada eksikti ve eşzamanlı testte kilitlenme (deadlock) hatası verdiği için eklendi). Teslim kaydı teslim alan kişi, gerçekleşen tarih (gelecekte olamaz) ve gerçekleşen miktarı ister; teslim edilen miktar teslimat satırına ve sipariş kalemine aynı kaydetmede yazılır, eksik teslimat kalan miktar olarak açık kalır. Başarısız teslimat gerekçeyle işaretlenir, kayıt silinmez; yeniden planlama gerekçe ve yeni tarih ister, yeni teslimat öncekine bağlanır ve her başarısız teslimat yalnızca bir kez yeniden planlanır. Planlı teslimat gerekçeyle iptal edilir; sipariş iptalinde planlı teslimatlar iptal edilir, teslim edilmiş kayıtlar korunur. Teslim kanıtı (`DeliveryEvidence`; genel dosya deposu olmadığından küçük bir tabloda): yalnızca PDF, PNG, JPEG; en çok 2 MB; teslimat başına 10 dosya; ad yol içeremez, içerik başlığı bildirilen türle uyuşmalıdır, SHA-256 saklanır; indirme okuma yetkisi ve veri kapsamından geçer, `Cache-Control: no-store` ve `nosniff` ile `attachment` olarak verilir, kanıt adı ve içeriği günlüğe yazılmaz. Hizmet başlangıcı eksik ön koşulla kaydedilemez: sipariş onaylı, satış devri kabul edilmiş, hizmet işinin bağımlı olduğu işler tamamlanmış ve hizmet şablonunun üç ön koşul görevi (`OperationTemplates.ServicePrerequisites`) bitmiş olmalıdır; engeller ekranda listelenir. Başlangıç tamamlanma sayılmaz: başlat → müşteri onayı (onaylayan kişi zorunlu) → tamamla ayrı eylemlerdir; süreli (tekrarlayan) hizmette başlamış ama tamamlanmamış hizmet "devam eden yükümlülük" olarak görünür kalır; planlanan başlangıcı geçen hizmet "başlangıç gecikti" işaretlenir ve gecikme gerekçesi kaydedilebilir. Siparişin gerçekleşme durumu (`FulfillmentStatus`) kalem teslimatları ve hizmet kayıtlarından türetilir (`FulfillmentCalculator`) ve her birincil işlemle aynı işlemde yazılır: Planlandı (planlı teslimat ya da hizmet), Kısmen (herhangi bir teslim ya da başlamış hizmet), Tamamlandı (tüm ürünler teslim, tüm hizmetler tamamlanmış). Böylece dilim 2'deki normal kapatma artık mümkündür. Yetki `orders.fulfillment.record` (operasyon rolü şablonuna eklendi); okuma `orders.orders.read` ile. Migration `OrderFulfillment` (RLS dahil). Ekran: `/siparisler/{id}/teslimat` (kalem tablosu, teslimat planı/teslim/başarısız/yeniden planla/iptal, kanıt yükleme ve indirme, hizmet başlangıcı eylemleri ve engeller) ve sipariş detayında bağlantı.

Dilim 5 doğrulaması: 37 yeni birim testi (planlama ve teslim miktar kuralları, gerçekleşme hesabı, geçiş tabloları, başlangıç ön koşulları, kanıt dosyası kuralları), 8 yeni entegrasyon testi (kısmi teslimat ve kalan miktar, fazla teslimatın engellenmesi, eşzamanlı teslim, başarısız teslimat ve gerekçeli yeniden planlama, kanıt türü/boyut/içerik sınırları ve indirme yetkisi, hizmet başlangıcı ön koşulları ve başlangıç-tamamlanma ayrımı ve normal kapatma, bağımlı iş engeli, sipariş iptalinde teslimatlar, tenant izolasyonu ve yetki); tarayıcıda plan ve kısmi teslim akışı denendi. Not: ürün kalemi testlerde kalem türü veritabanında ayarlanarak denendi. Teslimat için lojistik talep taslağı, stok düşümü, fatura/tahsilat ve onboarding/destek bağlantısı **yoktur**; sözleşme numarası yalnızca bilgi olarak gösterilir.

**Dilim 6 — Özet göstergeler ve birleşik izleme (1 Kasım 2026): uygulandı ve doğrulandı.** `GET /api/orders/summary` yalnızca **sayı** döndürür (tutar toplanmaz, gelir ya da tahsilat sayılmaz): devir bekleyen (onaylı, devri kabul edilmemiş), onay bekleyen, kısmen gerçekleşen, geciken sipariş; atanmamış ve geciken iş; açık, geciken teslimat; geciken hizmet başlangıcı; ilk gönderimden kabule ortalama devir kabul süresi (son kabul edilen en çok 200 devir, örnek sayısıyla). Her sayı kayıt boru hattının listesinden gelir; bu yüzden kullanıcının izni ve veri kapsamı dışındaki kayıtlar sayıya girmez ve gösterge listeyle aynı süzgeci kullanır. Ekran: `/siparisler/ozet` (kutular ilgili filtrelenmiş listeyi açar: `?gorunum=handover|partial|overdue`, `?durum=PendingApproval`, operasyon işlerinde `?gorunum=unassigned|overdue`; geciken teslimat ve hizmet başlangıçları ilk 10 kayıtla listelenir), menüde "Pano". Birleşik izleme: sipariş detayında teslimat/hizmet özeti (teslim edilen/etkin adet, açık ve geciken teslimat, hizmet durumu sayıları) ve siparişe bağlı aktiviteler; firma kartında ve fırsat ekranında ilişkili siparişler (`RelatedOrders`; durum, devir, gerçekleşme ve tutar, her biri kendi para biriminde).

Dilim 6 doğrulaması: 3 yeni birim testi (ortalama hesabı), 3 yeni entegrasyon testi (sayıların doğruluğu ve listeyle aynı süzgeç, ortalama devir kabul süresi, tutar alanı yokluğu, izin / veri kapsamı / tenant sınırı, müşteri ve fırsat üzerinden siparişlere erişim, siparişe bağlı devir aktiviteleri); tarayıcıda pano ve süzgeç bağlantıları denendi.

## 5. EPIC 8 kapanış durumu

**Epic bütünüyle tamamlanmış sayılmaz.** Süreç omurgası (tekliften sipariş → yaşam döngüsü → satış devri → operasyon işi → teslimat/hizmet başlangıcı → izleme) uygulanmış ve doğrulanmıştır; aşağıdaki maddeler yapılmamış ya da kısmendir ve gerçek engel olarak kayıtlıdır.

### Kabul senaryoları (15) ve kanıtları

| # | Senaryo | Doğrulama |
| --- | --- | --- |
| 1 | Doğru kalem ve tutarlarla sipariş | `Accepted_quote_becomes_an_order_with_the_right_lines_totals_acceptance_and_trace` |
| 2 | Uygun olmayan teklif reddi | `Drafts_rejected_cancelled_and_unknown_quotes_cannot_become_orders`, `Order_can_be_created_after_the_quote_validity_because_acceptance_was_already_checked` |
| 3 | Çift tıklama, tekrar, eşzamanlı istek | `Repeated_and_concurrent_requests_produce_a_single_order` |
| 4 | Kısmi dönüşümde kabul edilen miktar aşılmaz | `Partial_conversion_tracks_remaining_quantity_and_never_exceeds_the_accepted_quantity` |
| 5 | Snapshot değişmez | `Later_customer_changes_do_not_change_the_order_snapshot_and_commercial_fields_stay_editable_only_in_draft` |
| 6 | Eksik bilgiyle devir gönderilemez | `Incomplete_or_unapproved_handover_cannot_be_sent_and_only_senders_can_send` |
| 7 | Bilgi iste / yeniden gönder | `Handover_flows_send_info_request_resubmit_and_accept_with_history_locks_and_follow_ups` |
| 8 | Kabul işleri yalnızca bir kez üretir | `Accepting_a_handover_creates_the_delivery_and_service_jobs_once_with_lines_tasks_and_planning_from_the_handover`, `Job_creation_follows_the_configured_strategy_and_a_dependency_blocks_the_service_job_until_delivery_is_complete` |
| 9 | Kısmi teslimat, kalan miktar, fazla teslimat engeli | `Partial_deliveries_track_remaining_quantity_and_block_over_delivery`, `Concurrent_deliveries_cannot_deliver_more_than_the_ordered_quantity` |
| 10 | Eksik ön koşulla hizmet başlangıcı kaydedilemez | `Service_start_is_blocked_until_prerequisites_are_done_and_starting_is_not_completing`, `Service_start_waits_for_the_dependent_delivery_job_when_services_follow_delivery` |
| 11 | Başlangıç tamamlanma sayılmaz | `Service_start_is_blocked_until_prerequisites_are_done_and_starting_is_not_completing` |
| 12 | İptal/değişiklik gerçekleşmiş kayıtları silmez | `Approved_order_can_be_cancelled_and_cancellation_withdraws_a_pending_change_request`, `Cancelling_the_order_cancels_planned_deliveries_but_keeps_delivered_records`, `Cancelling_the_order_cancels_open_jobs_and_keeps_completed_ones` |
| 13 | Yetkisiz kullanıcı işlem yapamaz, tutarları göremez | `Other_tenants_and_unauthorized_users_cannot_create_read_or_see_amounts`, `..._cannot_decide_see_amounts_or_reach_change_requests`, `Fulfillment_requires_an_accepted_handover_the_record_permission_and_tenant_scope`, `Summary_is_limited_by_permission_data_scope_and_tenant` |
| 14 | Tenantlar arası erişim yok | yukarıdaki yetki testleri (kayıt, kanıt dosyası, iş) ve `Delivery_evidence_is_limited_by_type_content_and_size_and_download_follows_permissions_and_tenant`. **Kısmen:** zamanlayıcının tenant'lar arası izolasyonu ayrıca bir senaryoyla sınanmadı; her çalıştırma tek tenant bağlamında ve kendi kilidiyle koşar |
| 15 | Arka plan hatasından sonra yeniden deneme mükerrer kayıt üretmez | `A_handover_waiting_too_long_gets_one_follow_up_per_round_and_the_job_can_be_repeated_safely`, `Unassigned_and_overdue_jobs_get_one_follow_up_each_and_the_scheduler_can_be_repeated_safely`. İş üretimi kabulle aynı işlemde yapıldığından ayrı yeniden deneme altyapısı yoktur: hata halinde kabul geri alınır ve yeniden denemek mükerrer iş üretmez (benzersiz filtreli indeks) |

### Modüller arası entegrasyonların son durumu

| İlişki | Durum |
| --- | --- |
| Firma/kişi → siparişler | Var: firma kartında ve fırsat ekranında ilişkili siparişler. Sipariş detayından firma kartına doğrudan bağlantı yok (sipariş taraf kimliği taşır, firma kimliği değil) |
| Fırsat → teklif → sipariş | Var |
| Ürün/hizmet/fiyat → sipariş kalemi ve snapshot | Var (kalemler kopyalanır; teklifteki ürün kimliği saklanır) |
| Sözleşme | Kısmen: sözleşme numarası/kimliği referanstır, doğrulanmaz; kapsam ve başlangıç koşulları siparişe taşınmaz |
| Aktivite | Var: devir takibi, eksik bilgi, kabul/atama/iş gecikmesi; sipariş detayında listelenir |
| Onboarding | **Yok**: modül yok; mükerrer görev üretilmemesi için sahiplik kararı: sipariş sonrası operasyon görevleri bu epic'in sahipliğindedir, onboarding sonraki epic'tir |
| Destek | **Yok** |
| Raporlama | Kısmen: özet sayılar ve filtreli listeler; dışa aktarma ve tutar raporu yok |
| Finans, stok, muhasebe, lojistik talebi | **Yok** ve varmış gibi gösterilmez; sipariş tutarı tahsilat ya da gelir değildir |

### Kalan işler ve bilinen sınırlamalar

- İstisnai (geçerliliği dolmuş teklifin) dönüşümü ve özel yetkisi desteklenmez.
- Siparişe ekip ve business unit aktarımı yoktur (satış sorumlusu ve firma kapsamı vardır); operasyon ekibi serbest metindir (ekip lookup'ı yok).
- Operasyon işine belge eklenmez; genel dosya deposu (Ç4) olmadığından yalnızca teslim kanıtı küçük bir tabloda tutulur.
- Kur dönüşümü yoktur; farklı para birimleri ayrı gösterilir, toplanmaz.
- Arayüz metinleri yalnızca Türkçedir (TR/EN altyapısı ayrı iş kalemidir).
- Başlangıç ön koşulları hizmet şablonunun ilk üç görevinden okunur; müşteri başlangıç onayı serbest metin kişidir.
- Katalogdan ürün satırı üretimi testlerde kalem türü veritabanında ayarlanarak simüle edildi.
- Sipariş detayından firma kartına bağlantı, dışa aktarma ve zamanlayıcı tenant izolasyon senaryosu eksiktir.

### Kullanıcının deneyebileceği kısa uçtan uca senaryo

1. CRM'de teklifi sunun ve kabulü kaydedin; teklif ekranında "Sipariş oluştur" ile (`/siparisler/tekliften/{id}`) siparişi oluşturun.
2. Siparişi onaya gönderin; başka bir kullanıcı onaylasın.
3. `/siparisler/{id}/devir` ekranında devri hazırlayıp gönderin; operasyon kullanıcısı kabul etsin: iş kuyruğunda (`/siparisler/isler`) Teslimat ve Hizmet işleri oluşur.
4. `/siparisler/{id}/teslimat` ekranında teslimatı planlayıp kısmi teslim edin, kanıt ekleyin; hizmet için ön koşul görevlerini tamamlayıp başlangıcı kaydedin, müşteri onayını girin, hizmeti tamamlayın.
5. `/siparisler/ozet` panosundan sayıları ve sipariş detayından tüm süreci izleyin; tüm kalemler gerçekleşince siparişi kapatın.

**Bilinen sınırlamalar (dilim 1):** manuel teklif kabulünde belge kanıtı yoktur (yalnızca not ve kullanıcı); sözleşme bağlantısı doğrulanmayan kimlik+numara referansıdır; PUT tam durum gönderir (platform kuralı), gönderilmeyen düzenlenebilir alan temizlenir.
