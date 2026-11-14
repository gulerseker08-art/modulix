# ADR-0044: Modül sözleşme projeleri (modüller arası sözleşmeler Core'dan çıkar)

- **Durum:** Uygulanıyor, 13 Kasım 2026. Dilim 1 (Fleet), Dilim 2 (Crm: `Sales`, `Activities`) ve Dilim 3 (Marketing) teslim edildi; açık sorular 1-4 hâlâ karar bekliyor ve ilgili dilimlerden (4, 5) önce yanıtlanmalı.
- **Karar (öneri):** Bir modülün başka modüllere açtığı arayüzler ve veri tipleri `Modulix.Core.Contracts` yerine o modülün kendi sözleşme projesinde (`Modulix.Modules.<Ad>.Contracts`) durur. Bir modül başka bir modülün yalnızca sözleşme projesine başvurabilir; bu bağımlılık mimari test tablosunda açıkça yazılır. `Modulix.Core.Contracts` yalnızca bütün modüllerin ortak kullandığı çekirdek sözleşmeleri tutar ve içine modüle özel alan adı eklenmesi testle engellenir.

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
| `Pricing` | `Money`, `QuoteCalculator`, `LineCalculation`, `PriceSelection`, `UnitConversion` | Belirsiz (aşağıda) | Sayılmadı |

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

`Authorization`, `Records`, `Tenancy`, `Persistence`, `Events`, `Modules`, `Lookups`, `Customization`, `Files`, `Notifications`, `Automation`, `Approvals`, `Reporting`, `Sectors`, `SharedRecords` gibi her modülün kullandığı altyapı sözleşmeleri kalır. Mimari teste bir izin listesi eklenir: `Core.Contracts` altındaki üst düzey ad alanları bu listeyle sabitlenir, listede olmayan yeni ad alanı testi kırar.

### Açık sorular (karar verilmeden kodlanmaz)

1. **Sözleşme modülünün adı.** `Modulix.Modules.Contracts` (satış sözleşmeleri) bu kuralla `Modulix.Modules.Contracts.Contracts` projesine yol açar. Önce modülün proje adı değiştirilmeli (ör. `Modulix.Modules.Agreements`); modül anahtarı, tablo şeması ve izin anahtarları değişmez, yalnızca proje ve ad alanı değişir.
2. **`Pricing`.** `Money` ve birim dönüşümü birden çok modülün ihtiyacı olabilecek genel tipler; `QuoteCalculator` ise teklif alanına ait görünüyor. Kullananlar çıkarılıp ikiye ayrılmalı: genel olan Core'da kalır, teklif hesabı Crm sözleşmesine gider.
3. **`Customs` uygulaması.** Sözleşmeler Warehouse sözleşme projesine taşınabilir. Uygulama ise `platform` şemasındaki tenant'sız tabloları kullanıyor, modüller bugün yalnızca `ITenantEntity` tablolarına erişebiliyor (`IModuleData`). Uygulamayı modüle taşımak için modüllerin platform tablosu sahibi olabilmesi gerekir; bu ayrı bir karar. Gümrük ayrı bir modül mü olmalı sorusu da burada yanıtlanmalı (ADR-0032).
4. **Kalıcı tip adları.** Taşınan tiplerden herhangi biri outbox yükünde, denetim kaydında ya da ayarlarda .NET tip adıyla saklanıyorsa ad alanı değişikliği eski kayıtları okunamaz yapar. Her dilimde taşımadan önce kontrol edilir.

### Dilimler

Her dilim yalnızca taşıma yapar, davranış değiştirmez; ayrı PR'dır.

1. **Fleet.** `IVehicleDirectory` → `Modulix.Modules.Fleet.Contracts`. En küçük örnek (1 dosya, 1 kullanan modül); kural tablosu ve yeni mimari test bu dilimde eklenir.
2. **Crm.** `Sales` ve `Activities` → `Modulix.Modules.Crm.Contracts`.
3. **Marketing.** `Marketing` alanı → `Modulix.Modules.Marketing.Contracts`.
4. **Sözleşme modülünün adı** (açık soru 1) ve **Pricing ayrımı** (açık soru 2).
5. **Customs** (açık soru 3 karara bağlandıktan sonra).
6. **Kapanış.** `Core.Contracts` ad alanı izin listesi testi; `CLAUDE.md` ve README'deki "modüller yalnızca `Core.Contracts`'a başvurur" cümlesinin güncellenmesi.

## Kabul ölçütleri

- `Core.Contracts` altında modüle özel ad alanı kalmaz (Customs ve Pricing kendi dilimlerine kadar istisna olarak listede durur).
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
