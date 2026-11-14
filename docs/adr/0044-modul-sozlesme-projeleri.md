# ADR-0044: Modül sözleşme projeleri (modüller arası sözleşmeler Core'dan çıkar)

- **Durum:** Kabul edildi ve uygulandı, 14 Kasım 2026. Dilim 1 (Fleet), 2 (Crm), 3 (Marketing), 4 (Pricing ad alanı) ve 6 (ad alanı izin listesi testi) teslim edildi. Açık sorular 14 Kasım 2026'da karara bağlandı (aşağıda): sözleşme modülünün yeniden adlandırılması ertelendi, `Customs` sonraki ADR'ye devredildi.
- **Karar:** Bir modülün başka modüllere açtığı arayüzler ve veri tipleri `Modulix.Core.Contracts` yerine o modülün kendi sözleşme projesinde (`Modulix.Modules.<Ad>.Contracts`) durur. Bir modül başka bir modülün yalnızca sözleşme projesine başvurabilir; bu bağımlılık mimari test tablosunda açıkça yazılır. `Modulix.Core.Contracts` yalnızca bütün modüllerin ortak kullandığı çekirdek sözleşmeleri tutar ve içine modüle özel alan adı eklenmesi testle engellenir.

> Numara notu: 0043 numarası açık dallarda iki ayrı ADR tarafından kullanılıyor (`0043-firma-sektor-katalogu`, `0043-sevkiyatta-ulke-ve-uluslararasi-tasima`). Bu taslak 0044 aldı; birleştirmeden önce numara yeniden kontrol edilmeli.

## Problem

Mimari kural: modüller yalnızca `Modulix.Core.Contracts`'a başvurur (`ProjectReferenceRulesTests.AllowedReferences`). Kural modüllerin birbirinin tablolarına ve iç tiplerine erişmesini engelliyor, bu doğru. Ama iki modülün konuşması gerektiğinde sözleşmenin konabileceği tek yer `Core.Contracts` oluyor. Bugün orada modüle özel şu alanlar var:

| `Core.Contracts` alanı | İçerik | Kavramın sahibi | Kullanan |
|---|---|---|---|
| `Fleet` | `IVehicleDirectory`, `VehicleSummary` | Fleet | Logistics |
| `Sales` | `IAcceptedQuoteSource`, `AcceptedQuote` | Crm | Orders, Contracts |
| `Activities` | `IFollowUpActivities` | Crm | Orders, Contracts, Marketing |
| `Marketing` | `ICampaignLeads`, `IRecipientResolver`, `ISegmentFieldSource`, `IMessageProvider`, `IAddressLookup` | Marketing | Crm (3 dosya), Api |
| `Customs` | GTİP kataloğu, rejim profili | Antrepo/gümrük | Warehouse, Core, Infrastructure, Api |
| `Pricing` | `Money`, `QuoteCalculator`, `LineCalculation`, `PriceSelection`, `UnitConversion` | Ortak (karar 2) | Crm, Contracts |

Sonuçları:

- **Çekirdek modülleri tanıyor.** "Core sektöre özel iş mantığı içermez" kuralı proje başvurularında korunuyor ama içerikte delinmiş durumda. `Customs` bunun en ileri hâli: yalnızca sözleşme değil, uygulama da `Modulix.Core` ve `Modulix.Infrastructure` içinde.
- **Bağımlılıklar görünmüyor.** Logistics'in Fleet'e, Orders'ın Crm'e bağımlı olduğu hiçbir proje dosyasında ya da test tablosunda yazmıyor; ancak `using` satırları okunarak anlaşılıyor. Bir modülü tek başına satmak ("müşteri herhangi bir modülle başlayabilir") için hangi modülün hangisine ihtiyaç duyduğunu bilmek gerekir.
- **Her yeni modül `Core.Contracts`'ı büyütüyor.** İK, Rent-a-car ve sonraki modüller aynı kalıbı izlerse çekirdek sözleşme projesi bütün modüllerin kesişim noktası olur; her modül değişikliği çekirdeğe dokunur ve paralel çalışan dallar aynı projede çakışır.

## Seçenekler

1. **Olduğu gibi bırak.** Maliyeti yok. Sorun her modülle büyür.
2. **`Core.Contracts` içinde klasör kuralı.** Modüle özel sözleşmeler `Core.Contracts/Modules/<Ad>/` altına toplanır. Düzen sağlar ama bağımlılıklar yine görünmez ve çekirdek yine modülleri tanır.
3. **Modül başına sözleşme projesi (önerilen).** Bağımlılık proje başvurusu olarak görünür ve testle denetlenir. Maliyeti: yeni projeler, ad alanı değişiklikleri.
4. **Yalnızca olaylarla iletişim.** Modüller hiç senkron arayüz açmaz, her şey outbox olaylarıyla (ADR-0007, ADR-0039) akar. En gevşek bağ, ama "kabul edilmiş teklifi oku" ya da "araç listesini getir" gibi anlık sorgular için her modülde veri kopyası gerektirir. Bu ölçekte gereksiz karmaşıklık.

## Karar

### Proje ve başvuru kuralları

- Başka modüle bir şey açan modül bir `Modulix.Modules.<Ad>.Contracts` projesi alır. Hiçbir şey açmayan modül için proje açılmaz.
- Sözleşme projesi yalnızca `Modulix.Core.Contracts`'a başvurur. Başka bir modülün sözleşme projesine başvurmaz; böylece sözleşmeler arasında döngü oluşamaz.
- Sözleşme projesinde yalnızca arayüz, kayıt (record), enum ve sabit bulunur. Entity, `DbContext` erişimi ve iş mantığı bulunmaz.
- Modül, kendi sözleşme projesine ve `AllowedReferences` tablosunda adı yazılan başka modüllerin sözleşme projelerine başvurabilir. Başka bir modülün ana projesine başvuru yasak kalır.
- `Modulix.Api` (host) bütün sözleşme projelerine başvurabilir.

Örnek (`AllowedReferences`):

```csharp
["Modulix.Modules.Fleet.Contracts"] = [CoreContracts],
["Modulix.Modules.Fleet"]           = [CoreContracts, "Modulix.Modules.Fleet.Contracts"],
["Modulix.Modules.Logistics"]       = [CoreContracts, "Modulix.Modules.Fleet.Contracts"],
```

### Sözleşme hangi modülde durur

- **Sağlanan hizmet:** kavramın sahibi olan modülde. `IVehicleDirectory` Fleet'te, `IAcceptedQuoteSource` ve `IFollowUpActivities` Crm'de.
- **Genişleme noktası:** bir modülün "bana şunu sağlayan olursa kullanırım" dediği arayüz, onu tanımlayan modülde. Marketing'in alıcı çözümleme ve segment alanı arayüzleri Marketing'de kalır; Crm bunları uygulamak için `Modulix.Modules.Marketing.Contracts`'a başvurur. (Dilim 3'te doğrulandı: `ISegmentFieldSource`, `IRecipientResolver`, `IAddressLookup` ve `ICampaignLeads` arayüzlerini Crm uygular; `IMessageProvider` şimdilik Marketing içindeki sahte sağlayıcıyla uygulanır.)

### Modül yokken davranış

Değişmez. Bugünkü kalıp korunur: sağlayan modül tenant'ta kapalıysa ya da kurulu değilse çağıran taraf `Unavailable` sonucu alır (`FollowUpResult`, `CampaignLeadResult` örneklerindeki gibi). Sözleşme projesi her zaman imajın içindedir (tek imaj kuralı); bağımlılık derleme zamanındadır, çalışma zamanında modülün açık olması gerekmez.

### `Core.Contracts`'ta kalanlar

`Authorization`, `Records`, `Tenancy`, `Persistence`, `Events`, `Modules`, `Lookups`, `Customization`, `Files`, `Notifications`, `Automation`, `Approvals`, `Reporting`, `Sectors`, `SharedRecords` gibi her modülün kullandığı altyapı sözleşmeleri ve ortak hesap çekirdeği `Pricing` (karar 2) kalır. Mimari teste bir izin listesi eklenir: `Core.Contracts` altındaki üst düzey ad alanları bu listeyle sabitlenir, listede olmayan yeni ad alanı testi kırar.

### Açık soruların kararı (14 Kasım 2026)

1. **Sözleşme modülünün adı: ertelendi, ad kaydedildi.** `Modulix.Modules.Contracts` (satış sözleşmeleri) bugün başka modüle sözleşme açmıyor, bu yüzden sözleşme projesine ihtiyacı yok. Modül ilk sözleşmesini açacağı gün proje ve ad alanı `Modulix.Modules.Agreements` olur (sözleşme projesi `Modulix.Modules.Agreements.Contracts`); modül anahtarı (`contracts`), tablo şeması ve izin anahtarları değişmez. O güne kadar ad kalır; `RepositoryLayout.IsModuleContracts` dört parçalı ad kalıbıyla bu modülü sözleşme projelerinden ayırır. Not: modülün ad alanı migration anlık görüntülerinde entity adı olarak geçer; yeniden adlandırmada model anlık görüntüsünü güncelleyen boş bir migration gerekir.
2. **`Pricing`: Core'da kalır.** İçerik arayüz değil saf hesap kodudur (`Money`, birim dönüşümü, `DiscountEngine`, `PriceSelector`, `QuoteCalculator`); Crm ve sözleşme modülü kullanır, sipariş ve faturalanabilir kalem (ADR-0037) de aynı hesaba ihtiyaç duyabilir. Sözleşme projeleri iş mantığı taşımadığı için oraya konmaz; `Core.Contracts` içinde ortak hesap çekirdeği sayılır. Yalnızca ad alanı düzeltildi: `Modulix.Core.Pricing` → `Modulix.Core.Contracts.Pricing`. Sektöre ya da modüle özel bir kural (ör. belirli bir modülün fiyat politikası) buraya eklenmez.
3. **`Customs`: sonraki ADR'ye devredildi.** Arayüzleri (`IGtipCatalog`, `IRegimeProfileCatalog`) `Modulix.Infrastructure` `platform` şemasındaki tenant'sız tablolarla uygular. Yalnızca sözleşmeyi Warehouse sözleşme projesine taşımak `Infrastructure`'ın bir modül sözleşmesine başvurmasını gerektirir; bu bugünkü durumdan kötüdür. Sözleşme ve uygulama birlikte, uç noktaların ve platform tablolarının modüle taşınmasını ele alacak ADR'de taşınır; gümrüğün ayrı modül olup olmayacağı da orada yanıtlanır (ADR-0032). O zamana kadar `Modulix.Core.Contracts.Customs` izin listesi testinde tek geçici istisnadır (`PendingMoves`).
4. **Kalıcı tip adları: karar değil, taşıma kontrolü.** Taşınan bir tip outbox yükünde, denetim kaydında ya da ayarlarda .NET tip adıyla saklanıyorsa ad alanı değişikliği eski kayıtları okunamaz yapar. Dilim 1-4'te kontrol edildi, böyle bir kullanım bulunmadı. İleride yapılacak her ad alanı taşımasında aynı kontrol yapılır.

### Dilimler

Her dilim yalnızca taşıma yapar, davranış değiştirmez; ayrı PR'dır.

1. **Fleet.** `IVehicleDirectory` → `Modulix.Modules.Fleet.Contracts`. En küçük örnek (1 dosya, 1 kullanan modül); kural tablosu ve yeni mimari test bu dilimde eklenir.
2. **Crm.** `Sales` ve `Activities` → `Modulix.Modules.Crm.Contracts`.
3. **Marketing.** `Marketing` alanı → `Modulix.Modules.Marketing.Contracts`.
4. **Pricing ad alanı.** `Modulix.Core.Pricing` → `Modulix.Core.Contracts.Pricing` (karar 2). Sözleşme modülünün yeniden adlandırılması ertelendi (karar 1).
5. **Customs.** Bu ADR'de yapılmaz; sonraki ADR'ye devredildi (karar 3).
6. **Kapanış.** `Core.Contracts` ad alanı izin listesi testi (`CoreContractsNamespaceRulesTests`; Dilim 4 ve 5 beklenmeden eklendi, taşınmamış alanlar `PendingMoves` listesinde); `CLAUDE.md` ve README'deki "modüller yalnızca `Core.Contracts`'a başvurur" cümlesinin güncellenmesi.

## Kabul ölçütleri

- `Core.Contracts` altında modüle özel ad alanı kalmaz (tek istisna `Customs`; sonraki ADR'ye kadar `PendingMoves` listesinde durur. `Pricing` ortak hesap çekirdeği olarak kalır).
- Modüller arası her bağımlılık `AllowedReferences` tablosunda okunur.
- Bir modül başka bir modülün ana projesine başvurursa ya da sözleşme projesi `Core.Contracts` dışında bir projeye başvurursa mimari test kırılır.
- Sözleşme projelerinde entity ya da `DbContext` kullanımı varsa mimari test kırılır.
- Her dilimde mevcut birim ve entegrasyon testleri değişmeden geçer (yalnızca `using` satırları değişir).

## Sonuçlar

**Olumlu**

- Çekirdek modülleri tanımaz; "Core sektöre özel şey içermez" kuralı içerikte de geçerli olur.
- Modül bağımlılık haritası tek tabloda görünür; paketleme ve lisanslama kararları (hangi modül tek başına satılabilir) buna dayanabilir.
- Yeni modül çekirdeğe dokunmadan başka modüle hizmet açabilir; paralel dallarda `Core.Contracts` çakışmaları azalır.
- Uç noktaların ve ekranların modüle taşınması (sonraki adım) için ön koşul sağlanır.

**Olumsuz**

- Proje sayısı artar (bugün için en çok 4-5 yeni proje); derleme süresine etkisi ölçülmedi.
- Taşıma sırasında açık PR'larla çakışma olur; dilimler küçük tutulmalı ve açık PR yığınları birleştikten sonra yapılmalı.
- Genişleme noktalarında bağımlılık yönü ilk bakışta ters görünür (Crm → Marketing sözleşmesi); kural belgelenmezse yanlış yere sözleşme konur.

**Kapsam dışı**

- Uç noktaların, arka plan işlerinin, DTO'ların (`Modulix.Shared`) ve Blazor ekranlarının modüllere taşınması. Ayrı ADR.
- Modül başına `DbContext` ya da migration zinciri.
