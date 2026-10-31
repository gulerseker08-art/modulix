# ADR-0021: Sözleşme yönetimi (EPIC 7) — analiz ve ilk dilim

Durum: Kısmen uygulandı (ilk dilim: modül iskeleti, veri modeli, kayıt depoları, temel ekranlar). Tarih: 2026-10-31.

## Mevcut durum (analiz anında, kodla kanıtlı)

Tarama: `src/**/*.cs`, `src/Modulix.Web/Pages` ve `docs` içinde `Sözleşme|Sozlesme|Contract`. Sonuç: sözleşme varlığı, tablosu, uç noktası
ve ekranı **yoktur**. "Contract" yalnızca `Modulix.Core.Contracts` projesinin adında geçer (modül sözleşmeleri, iş sözleşmesi değil).
Yol haritasında (M1.5i, M1.6) sipariş/sözleşme köprüsü "hedef modül yok" diye açıkça bırakılmıştır.

| İşlev | Mevcut durum | Kod kanıtı | Eksiklik/risk | Yapılacak geliştirme | Öncelik | Kabul kriteri |
|---|---|---|---|---|---|---|
| Sözleşme entity/tablo, API, ekran | Yok | `grep` sonucu boş; `README` "Yapılmadı: sipariş/sözleşme oluşturma" | Modül yok | Yeni `Modulix.Modules.Contracts` modülü (şema `contracts`) | P0 | Tablolar RLS'li, `TableInventoryTests` geçer |
| Kabul edilen tekliften devir | Kısmen var | `Core.Contracts/Sales/AcceptedQuote.cs`, `GET /api/crm/quotes/{id}/handover` | Tüketen modül yok; firma/kişi CRM kimliğiyle verilir (ortak kayıt kimliği yok) | `AcceptedQuote`'a `PartyId`/`PersonId` eklenir; tekliften sözleşme oluşturma (dilim 3) | P0 | Teklif/katalog değişince sözleşme değişmez |
| İç onay | Kısmen var, teklife özel | `Crm/Quotes/QuoteApprovalRules.cs`, `QuoteApprovalRule/Delegation` tabloları | Genel onay motoru yok (Ç6 yapılmadı) | Saf değerlendirici genelleştirilir, sözleşme kuralları modülde (dilim 5) | P0 | Onay gerektiren sözleşme onaysız imzaya gidemez |
| İmza | Yok | — | Sağlayıcı yok | İmzacı modeli, manuel imza doğrulama, `ISignatureProvider` + simülasyon, idempotent webhook (dilim 6) | P0 | Kısmi imza tamamlanmış sayılmaz |
| Belge/PDF | Kısmen var | `Api/Crm/QuotePdf.cs` (QuestPDF), `QuoteDocument` (SHA-256, şablon sürümü, snapshot) | Şablon kavramı yok | Şablon sürümleri, denetimli değişkenler, PDF + özet (dilim 4) | P0 | Belge sürüm/şablon/veri anlık görüntüsüyle kayıtlı |
| Dosya depolama ve tarama | Kısmen var | `QuoteDocument.Content` (varbinary) | Zararlı dosya tarama mekanizması yok | Aynı desen; tür/boyut doğrulama; tarama genişleme noktası | P1 | Yetkisiz kullanıcı belge indiremez |
| Zamanlanmış işler | Var (desen) | `Api/Crm/QuoteExpiryWorker.cs` (`sp_getapplock`, işaret satırları) | Sözleşme işi yok | `ContractScheduleWorker` (dilim 8) | P0 | Tekrar turu mükerrer görev üretmez |
| Bildirim ve aktivite | Var | `IEmailQueue`, `INotificationRecipients`, `IActivityLinkTarget` | Uygulama içi bildirim merkezi yok | E-posta + aktivite | P1 | Alıcı yetkisiyle içerik |
| Lookup | Var | `Core/Lookups/ILookups.cs`, ADR-0016 | — | Tüm N→1 alanlar lookup | P0 | Erişilemeyen kayıt sızmaz |
| Kayıt kapsamı, hassas/özel alan, audit, outbox, eşzamanlılık | Var | Kayıt boru hattı; `Version` eşzamanlılık belirteci | — | Aynı desen | P0 | Dört test türü |
| Tenant saat dilimi | Var | `core.regional.time-zone`, `RegionalTime` | Tarih aritmetiği yok | Saf dönem hesabı (dilim 2) | P0 | Ay sonu / artık yıl testleri |
| Kayıtlı görünümler | Yok | — | — | Önce sabit görünümler (dilim 9) | P2 | Sunucu süzgeci |
| Platform yöneticisi erişimi | Var (varsayılan kapalı) | Platform uçları `PlatformGate`; tenant uçları üyelik ister (`RequireTenantFilter`) | — | Değişiklik yok | — | Platform yöneticisi üyeliği olmayan tenant'ın sözleşmesini okuyamaz |
| Sipariş, başlangıç, destek/SLA, faturalama | Yok | `Modulix.Modules.Logistics` yalnızca izin tanımı | Entegrasyon hedefi yok | Yalnızca olay ve okuma sözleşmesi; "Engelli" raporlanır | P2 | Hedef modül yokken akış kırılmaz |

## Kararlar

- **Ayrı modül.** Sözleşme yönetimi sektörden bağımsızdır (hizmet, satış, çerçeve, bakım, abonelik...). CRM'in içine konmaz;
  `Modulix.Modules.Contracts` yalnızca `Modulix.Core.Contracts`'a başvurur (mimari test). Modül anahtarı `contracts`, şema `contracts`,
  izinler `contracts.*`.
- **Tek veritabanı, RLS.** Görev metnindeki "tenant başına ayrı veritabanı" varsayımı ADR-0020 ile geçersizdir; bu modül tek
  `Modulix` veritabanında `TenantId` + EF filtresi + Row-Level Security ile çalışır. Bağlantı seçimi ve tenant başına migration yoktur.
- **Müşteri ortak kayıttır.** Sözleşmenin müşterisi ve kişileri CRM firması değil, ortak taraf/kişi kaydıdır (`core.party`,
  `core.person`; ADR-0006). Böylece CRM kapalıyken de sözleşme açılır ve modül CRM tablosuna bağlanmaz. Kabul edilen teklif devri
  (`IAcceptedQuoteSource`) üçüncü dilimde ortak kayıt kimliklerini de taşıyacak şekilde genişletilir.
- **Dört ayrı durum alanı.** Hazırlık (`Draft/InReview/ReadyToPublish`), onay (`NotRequired/Pending/Approved/Rejected/Withdrawn`),
  imza (`NotSent/Pending/PartiallySigned/Completed/Rejected/Expired/Cancelled`) ve yürürlük
  (`NotInForce/AwaitingStart/Active/Suspended/Expired/Terminated/Cancelled`) tek alana sıkıştırılmaz. Geçişler yalnızca eylem uç
  noktalarıyla yapılır (ikinci dilim); genel `PUT` bu alanları yazamaz (sistem alanı).
- **Sürüm modeli.** Başlık ve kalemler güncel taslağı taşır; onaya/imzaya gönderilen içerik `ContractVersions` tablosuna değişmez
  anlık görüntü (JSON + SHA-256) olarak dondurulur (ikinci dilim). İmzalı sürüm yerinde düzenlenmez: yeni revizyon ya da ek
  protokol açılır; yürürlükteki sürüm işaretçisi (`EffectiveVersionNo`) yeni sürüm imzalanana kadar değişmez.
- **Finansal alanlar hassastır.** Sözleşme bedeli, kalem fiyatları ve toplamlar `contracts.financials.read` iznine bağlıdır (Y8b):
  izni olmayan sözleşmeyi görür, tutarları görmez ve yazamaz. Hesap yalnızca sunucuda (`QuoteCalculator`, Core.Contracts/Pricing);
  farklı para birimleri kur olmadan toplanmaz (sözleşme tek para birimlidir).
- **Kayıt deposu araç takımı.** CRM'deki kayıt deposu kalıbı (`RecordField`, `Field`, `FilterValue`, depo tabanı) bu modülde
  sadeleştirilmiş kopyayla kuruldu. Ortak bir "modül kayıt deposu araç takımı"nın Core.Contracts'a taşınması ayrı bir iş kalemidir
  (yol haritası Ç0.2); bu epic kapsamında CRM yeniden düzenlenmedi.

## Dilim planı

1. **İskelet (bu dilim):** modül projesi, referans kuralı, `IModule`, izinler, `Contracts`/`ContractParties`/`ContractLines`/
   `ContractEvents` tabloları + RLS, kayıt depoları, CRUD uçları, tarihçe ucu, liste ve detay ekranı, testler.
2. Durum modelleri ve kurallar: saf `ContractRules`, dönem aritmetiği (ay sonu, artık yıl, tenant saat dilimi), eylem uçları,
   `ContractVersions` dondurma ve karşılaştırma.
3. Tekliften sözleşme: `IAcceptedQuoteSource` tüketimi, kalem sabitleme, mükerrer engeli; firma kartı ve fırsat sekmesi;
   `IActivityLinkTarget`.
4. Şablonlar ve belge üretimi (sürüm, değişkenler, PDF, özet).
5. İç onay (genel değerlendirici, kurallar, adımlar, vekâlet, iş listesi).
6. İmza (imzacılar, manuel doğrulama, sağlayıcı arayüzü + simülasyon, webhook).
7. Taahhütler (dönemsel gerçekleşme, ihlal → aktivite).
8. Yenileme, fesih, ek protokol, zamanlanmış iş.
9. Ekran tamamlama (sihirbaz, dashboard, takvim, görünümler, dışa aktarma, ayarlar, erişilebilirlik).
10. Dokümantasyon ve teslim raporu.

## Uygulama durumu (birinci dilim: iskelet)

**Uygulandı ve testle doğrulandı** (birim `ContractRulesTests` 10; entegrasyon `ContractTests` 4: mutlu yol, doğrulama, tenant izolasyonu,
yetki/kapsam; `TableInventoryTests`, `EndpointProtectionTests`, `ModulesAndRolesTests`, `LookupTests`, `SharedRecordTests` yeni modülle geçer):

| Konu | Karar / davranış |
|---|---|
| Proje | `src/Modulix.Modules.Contracts` (yalnızca Core.Contracts'a başvurur; mimari test tablosunda), `tests/Modulix.Modules.Contracts.Tests`, CI adımı. API host modülü kaydeder ve `/api/contracts/...` uçlarını eşler. |
| Veri modeli | `contracts.Contracts` (numara `SZ-yyyyMMdd-XXXXXX` tenant içinde benzersiz; müşteri `PartyId` ve kişi `PersonId` ortak kayda bileşik FK; dört durum alanı; tarihler, yenileme, bildirim, gizlilik, sürüm işaretçileri, sunucu toplamları), `ContractParties` (rol, taraf/kişi), `ContractLines` (kopya kalem, kararlı anahtar, dönem, sunucu tutarları), `ContractEvents` (tarihçe). Migration `ContractManagement`: dört tablo RLS politikasında; `contracts` şeması için uygulama rolüne idempotent `GRANT` (init.sql'e de eklendi; eksikliği "INSERT permission denied" olarak ortaya çıktı ve test etti). |
| Kayıt depoları | CRM kalıbının sadeleştirilmiş kopyası (`RecordField`/`Field`, depo tabanı, `ContractsStoreContext`, iş akışı kapısı). Sistem alanları (durumlar, numara, toplamlar, sürüm) genel uçlardan yazılamaz. Taraf ve kalem görünürlüğü/kapsamı üst sözleşmeyi izler; kapsam dışı sözleşmeye kalem eklemek "Sözleşme bulunamadı" verir. |
| Kurallar | `ContractRules`: içerik düzenleme (taslak, onay beklemiyor, imzaya gitmemiş, yürürlükte değil), silme (imzalı/yürürlükteki silinmez), tarih/yenileme/bildirim doğrulaması, son bildirim tarihi, numara. Kişi müşteri tarafına bağlı olmalıdır (bağımlı lookup, sunucuda yeniden denetlenir). |
| Hesap | `QuoteCalculator` (Core.Contracts/Pricing) ile aynı yuvarlama; tek seferlik toplam başlıkta, dönemsel özet JSON'da; açıklama satırı tutar taşımaz; kabul örneği 2 × 1.000, %10, %20 → 1.800 / 360 / 2.160 testle doğrulandı. |
| Yetki | `contracts.contracts.read|write`, `contracts.financials.read` (hassas: bedel, fiyat, indirim, tutarlar; izinsiz yazma denemesi 403, boş gönderim serbest). Sözleşme yazma izni ortak taraf/kişi açabilir (CRM kapalı tenant için). Hazır roller "Sözleşme sorumlusu" (kendi), "Sözleşme izleyici". |
| Ortak kayıt | `SharedRecordAccess`/`SharedRecordReferences` bildirildi: taraf/kişi sözleşmede kullanılıyorsa silinemez; kullanım sayımı `partyId`/`personId` süzgeçleriyle. |
| Ekranlar | `/sozlesmeler` (hızlı görünümler, süzgeçler, kritik tarih, lookup ile çözülen müşteri adı), `/sozlesmeler/yeni|{id}` (sekmeler: genel, taraflar, kalemler, tarihçe + işlem geçmişi; salt okunur uyarısı; kaydedilmemiş değişiklik kilidi; 409 uyarısı). Uygulama menüsünde "Sözleşmeler". |
| Çekirdek düzeltmesi | `RecordPipeline.OwnerOutOfScope` sorumlu alanı olmayan alt kayıt türünde `ArgumentNullException` (500) üretiyordu; genel alan adıyla 422 döner. `ResourceEndpoints` (tipli CRUD eşleyici) CRM'den bağımsız hale getirildi; CRM kendi kopyasını korur. |

## Uygulama durumu (ikinci dilim: durum geçişleri, sürüm dondurma, karşılaştırma)

**Uygulandı ve testle doğrulandı** (birim `ContractLifecycleRulesTests` 16; entegrasyon `ContractLifecycleTests` 4: mutlu yol, doğrulama,
tenant izolasyonu, yetki/maske; `EndpointProtectionTests` yeni uçlarla geçer):

| Konu | Karar / davranış |
|---|---|
| Dönem aritmetiği | `ContractPeriod`: N aylık dönem takvim ayıyla hesaplanır (365 gün değil); gün taşarsa hedef bir sonraki ayın ilk gününe kayar, bitiş = hedef − 1 gün. Örnekler testte: 1 Kas 2026 + 12 ay → 31 Eki 2027; 1 Mar 2027 + 12 → 29 Şub 2028; 29 Şub 2024 + 12 → 28 Şub 2025; 31 Oca + 1 → 28 Şub. Başlangıç ve bitiş günü dahildir; sonraki dönem bitişin ertesi günü başlar. "Bugün" tenant saat dilimiyle hesaplanır (`core.regional.time-zone`). |
| Tarih değerlendirmesi | `ContractDates.StatusOn/Evaluate` (saf): imzalı sözleşme başlangıç gelmeden aktif olmaz (`AwaitingStart`), bitiş günü dahil aktiftir, ertesi gün `Expired`; belirsiz süreli dolmaz; askıdaki/feshedilen/iptal edilen değerlendirilmez; süresi dolan geri aktifleşmez. İmza tamamlanması (dilim 6) ve zamanlanmış iş (dilim 8) bunu kullanır. |
| Eylemler | `POST /api/contracts/contracts/{id}/{eylem}` (gövde: `version`, `reason`), `GET .../actions` (yetkili eylemler ve yapılamayanların nedeni). Eylemler: `send-to-review`, `mark-ready`, `reopen-draft`, `submit-for-approval`, `withdraw-approval`, `approve`, `reject-approval`, `cancel`, `revise`. Ön koşullar `ContractTransitions` (saf; arayüzle ortak): yayıma hazır için müşteri + başlangıç + (bitiş ya da belirsiz süreli) zorunlu; onay yalnızca yayıma hazır içerikte; onay beklerken taslağa dönülmez (önce geri çek); imzaya gitmiş/imzalı içerik taslağa dönmez, revizyon açılır; iptal yalnızca yürürlüğe girmemiş ve imzalanmamış sözleşmede; kapanmış sözleşmede işlem yok. Ret, iptal ve revizyon gerekçe ister. Her eylem kayıt sürümü ister (409). |
| Sürümler | `contracts.ContractVersions` (migration `ContractVersions`, RLS): onaya gönderimde güncel taslak başlık + taraflar + kalemler JSON anlık görüntüsü ve SHA-256 özetiyle dondurulur; aynı sürüm numarası yeniden dondurulmaz. Onay/ret/geri çekme sürüm durumuna da yazılır. Onaylı/reddedilmiş içeriği düzenlemek için taslağa dönülünce eski sürüm `Superseded`, `CurrentVersionNo` artar, onay geçersiz kalır (`ApprovalInvalidated`); imzalı sürüm `Signed` kalır, `EffectiveVersionNo` değişmez. `GET .../versions`, `.../versions/{no}` (finansal izni olmayana tutarlar maskeli), `.../versions/{no}/compare?with={no|0}` (0 = güncel taslak; alan, kalem (kararlı anahtar) ve taraf farkları; finansal alanlar izinsiz okuyucuya gösterilmez). |
| Yetki | Yeni izin `contracts.contracts.approve` (yazmadan ayrı); hazır rol "Sözleşme onaylayıcı". Ayar `contracts.approval.allow-self` (varsayılan hayır): onaya gönderen kendi gönderdiğini onaylayamaz (`IContractPolicyProvider`, host tenant ayarından okur). |
| Tarihçe | `ContractEvents.FromStatus/ToStatus` dört durumun özetini taşır ("hazırlık/onay/imza/yürürlük", 100 karakter). |
| Ekran | Detayda eylem çubuğu (gerekçe formu, yapılamayan işlemler ve nedenleri), "Sürümler" sekmesi (liste, özet, güncel taslakla / önceki sürümle karşılaştırma tabloları), tarihçe etiketleri. |

**Yapılmadı (sonraki dilimler):** imza ve yürürlüğe alma eylemleri (dilim 6; `ContractDates` hazır), geriye dönük tarih girişinin yetki/gerekçeyle
denetimi (imza/yürürlük adımında uygulanacak), onay kuralları/adımlar/vekâlet (dilim 5; bu dilimde tek adımlı onay), tekliften sözleşme (`AcceptedQuote`'a ortak kayıt
kimlikleri), firma/fırsat sekmesi, `IActivityLinkTarget`; şablon/PDF; iç onay; imza; taahhüt; yenileme/fesih/ek protokol ve zamanlanmış iş;
dashboard, kayıtlı görünümler, dışa aktarma; CRM kapalı tenant'ta arayüzden yeni müşteri açma (API var, sözleşme formunda hızlı oluşturma
yok); teslim raporu. Ekranlar tarayıcıda denetlendi (aşağıdaki not), klavye/ekran okuyucu denemesi yapılmadı.
