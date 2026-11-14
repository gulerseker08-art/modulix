# ADR-0046: Modül dikey dilimleri (uç noktalar ve arka plan işleri modüle taşınır)

- **Durum:** Taslak, 14 Kasım 2026. Karar bekliyor; açık sorular yanıtlanmadan kodlanmaz.
- **Karar (öneri):** Bir modülün HTTP uç noktaları ve arka plan işleri `Modulix.Api` yerine modülün kendi projesinde durur. Host (`Modulix.Api`) modülleri tanımaz; uç noktaları ve işleri modül kaydından (`IModule` ve yanındaki arayüzler) keşfeder. Arka plan işleri modül başına ayrı `BackgroundService` yerine tek bir zamanlayıcıdan çalışır. DTO'lar ve Blazor ekranları bu ADR'de yerinde kalır; onlar için yalnızca yön belirlenir.

> Numara notu: 14 Kasım 2026'da `main`'de en yüksek numara 0045'ti ve 0046 adını taşıyan açık PR yoktu. Birleştirmeden önce numara yeniden kontrol edilmeli (0043 iki ADR'de kullanılmış durumda).

## Bağlam

ADR-0044 modüller arası sözleşmeleri `Core.Contracts`'tan çıkardı; modül bağımlılıkları artık `AllowedReferences` tablosunda okunuyor. Ama bir modülün kodu hâlâ dört projeye dağılmış durumda:

| Parça | Bugün nerede | Ölçü (14 Kasım 2026) |
|---|---|---|
| Entity, kayıt depoları, iş kuralları | `Modulix.Modules.<Ad>` | modül projesi |
| HTTP uç noktaları | `Modulix.Api/<Ad>/` | Api toplam ~15.400 satır; Crm 17, Sözleşme 8, Marketing 7 dosya |
| Arka plan işleri | `Modulix.Api/<Ad>/*Worker.cs` | 11 `BackgroundService`, 5'i modüle özel |
| DTO'lar | `Modulix.Shared/<Ad>/` | Crm 15, Platform 7, Sözleşme 6 dosya |
| Ekranlar | `Modulix.Web/Pages/<Ad>/` | Crm 37, İK 22, Antrepo 12 sayfa |

Sonuçları:

- **Host bütün modülleri tanıyor.** `Program.cs` içinde 62 uç nokta eşleme ve iş kaydı satırı var. Yeni modül eklemek host'u değiştirmek demek; "modül tek `IModule` uygulamasıyla kaydolur" kuralı yalnızca metadata için geçerli.
- **Modül sınırı Api'de delinebiliyor.** `Modulix.Api` her projeye başvurabildiği için uç nokta dosyaları başka modülün koduna ve altyapıya doğrudan uzanabiliyor. Bugün: `Api/Contracts` `Modulix.Modules.Crm`'e, `Api/Support` ve `Api/Contracts` `Modulix.Api.Crm`'e başvuruyor; 22 Api dosyası `ModulixDbContext`'i doğrudan kullanıyor. Mimari testler bunu görmüyor, çünkü test proje başvurularına bakıyor ve Api'nin her şeye başvurması serbest.
- **Arka plan işleri kopyalanarak çoğalıyor.** Modüle özel beş iş (`QuoteExpiry`, `ContractJobs`, `OrderJobs`, `SupportJobs`, `MarketingSend`) aynı kalıbı tekrarlıyor: `WorkerEnabled` ve `PollIntervalSeconds` seçenekleri, tenant'ları gezen döngü, kapsam (scope) açma, lisans kontrolü, hata yakalama. Her yeni modül bir kopya daha ekliyor; lisans ve tenant durumu kuralları (ADR-0028) her kopyada ayrı ayrı doğru yazılmak zorunda.
- **Customs taşınamıyor.** ADR-0044 karar 3: GTİP ve rejim profili sözleşmeleri `Core.Contracts`'ta, uygulaması `Core` ve `Infrastructure`'da, uç noktaları `Api/Customs`'ta. Modüller yalnızca tenant tablolarına erişebildiği için (`IModuleData`) bu kod Warehouse modülüne taşınamıyor.

## Seçenekler

1. **Olduğu gibi bırak.** Maliyeti yok. Host her modülle büyür; Api'deki sınır ihlalleri testle yakalanamaz.
2. **Yalnızca kural ekle.** Kod yerinde kalır, mimari teste "`Api/<Ad>/` yalnızca kendi modülüne başvurur" kuralı yazılır. Ucuz ve ihlalleri durdurur; ama host modülleri tanımaya devam eder ve işlerdeki tekrar çözülmez. Ara adım olarak değerli (Dilim 0).
3. **Uç noktalar ve işler modül projesine (önerilen).** Modül projesi ASP.NET'e başvurur, uç noktalarını kendisi eşler; host yalnızca döngüyle çağırır.
4. **Modül başına ayrı uç nokta projesi** (`Modulix.Modules.<Ad>.Api`). Modülün iş kodu ASP.NET'ten bağımsız kalır. Karşılığında proje sayısı iki katına yakın artar ve uç noktalar modülün `internal` tiplerine erişemez (`InternalsVisibleTo` ya da gereksiz `public` gerekir). Bu ölçekte kazancı maliyetini karşılamıyor.
5. **Tam dikey dilim** (DTO ve ekranlar da modülde). En temiz sonuç, en büyük iş: Blazor WebAssembly'de modül başına Razor sınıf kitaplığı, ortak bileşen kitaplığı ve yönlendirme değişikliği gerekir. Bu ADR'nin kapsamı dışında bırakıldı (aşağıda "Yön").

## Karar

### 1. Modülün ASP.NET'e erişimi: `Modulix.Core.Hosting`

`Modulix.Core.Contracts` ASP.NET'e başvurmaz ve başvurmayacak (sözleşme projeleri ve testler hafif kalır). Uç nokta yazmak için gereken her şey yeni bir projeye konur:

- `Modulix.Core.Hosting`: `Microsoft.AspNetCore.App` çerçeve başvurusu + `Core.Contracts`. İçeriği bugün `Modulix.Api/Access` ve `Modulix.Api/Records` altında duran yardımcılar (yaklaşık 600 satır): `.RequirePermission(...)`, `.RequireModule(...)`, kayıt boru hattı uç nokta kalıpları.
- Modüller `Core.Contracts`, `Core.Hosting`, `Modulix.Shared` ve modül sözleşme projelerine başvurabilir. `Core` ve `Infrastructure` yasak kalır.
- Sözleşme projeleri (`Modulix.Modules.<Ad>.Contracts`) `Core.Hosting`'e başvuramaz.

### 2. Uç noktaların keşfi

`Core.Hosting`'de yeni arayüz; modül sınıfı `IModule` ile birlikte uygular:

```csharp
public interface IModuleEndpoints
{
    /// <summary>Modülün uç noktalarını eşler. Grup, modül lisans kontrolüyle (RequireModule) host tarafından sarılmış gelir.</summary>
    void MapEndpoints(IEndpointRouteBuilder routes);
}
```

- Host bütün modülleri gezer, `IModuleEndpoints` uygulayanlar için `/api/<modül anahtarı>` grubunu açar, gruba `RequireModule(<anahtar>)` uygular ve `MapEndpoints`'i çağırır. Böylece lisans ve salt okunur kuralı (ADR-0028) modülün unutabileceği bir şey olmaktan çıkar.
- İzin kontrolü (`RequirePermission`) uç nokta başına modülde kalır. Bugün korumasız uç noktayı yakalayan bir mimari test yok; Dilim 1'de eklenir (modül projesindeki her `Map*` çağrısı `RequirePermission` ile bitmeli ya da gerekçeli istisna listesinde olmalı).
- Ortak kayıt uç noktaları (`MapRecordEndpoints`: modülün `OwnedRecords` tanımından üretilen genel uçlar) host'ta kalır. Filo modülü bugün yalnızca bunları kullanıyor ve Api'de kendi klasörü yok; hedef durumun örneği odur.
- Yollar değişmez. Bugün `/api/<anahtar>` dışında yol kullanan modül uç noktası varsa taşıma diliminde listelenir ve yerinde bırakılır; bu ADR URL değiştirmez.

### 3. Tek iş zamanlayıcısı

`Core.Contracts`'a (ASP.NET gerekmez) iş tanımı eklenir; `IModule.Jobs` ile bildirilir:

```csharp
public sealed record ModuleJobDefinition(
    string Key,                 // "orders.jobs" — modül anahtarıyla başlar
    TimeSpan DefaultInterval,
    Type Runner,                // IModuleJob uygulaması, modülde
    bool RequiresWritableModule = true);

public interface IModuleJob
{
    /// <summary>Tek tenant için çalışır; kapsam, tenant bağlamı ve lisans kontrolü zamanlayıcıdan gelir.</summary>
    Task RunAsync(ModuleJobContext context, CancellationToken cancellationToken);
}
```

- Host'ta tek `ModuleJobsWorker` bulunur: işleri aralığına göre sıraya koyar, erişime açık tenant'ları gezer (`TenantStatusRules.Operational`), her tenant için kapsam açıp tenant bağlamını kurar, modülü `IModuleLicense.AllowsAsync`/`IsWritableAsync` ile denetler, hatayı tenant ve iş bazında yalıtır ve günlüğe yazar.
- Aralık ve aç/kapa ayarı tek yapılandırma kalıbına iner: `Jobs:<anahtar>:Enabled`, `Jobs:<anahtar>:IntervalSeconds`. Eski bölüm adları (`OrderJobs:WorkerEnabled` gibi) bir sürüm boyunca okunmaya devam eder (on-prem yapılandırmaları kırılmasın).
- Entegrasyon testleri bugünkü gibi zamanlayıcıyı kapatıp işi doğrudan çağırır.
- Platform işleri (`EventDispatch`, `EmailDispatch`, `Automation`, `ApprovalEscalation`, `ReportSchedule`, `CommunicationCleanup`) bu ADR'de yerinde kalır. Tenant başına çalışan ve aynı kalıba uyanlar sonradan aynı zamanlayıcıya alınabilir; olay ve e-posta dağıtımı gibi sürekli kuyruk tüketen işler ayrı kalır.
- Hazır kitaplık (Hangfire, Quartz) kullanılmaz: ihtiyaç "aralıkla, tenant başına, tek örnekte çalıştır"dan ibaret; ek tablo, pano ve bağımlılık on-prem tek imaj hedefine yük getirir. Çok örnekli çalışma gerekirse (aynı işin iki kopyada birden koşmaması) ayrı karar alınır.

### 4. Taşımanın ön koşulu: uç nokta yalnızca modül servislerini çağırır

Bir uç nokta dosyası modüle taşınabilmek için `Core` iç koduna, `Infrastructure`'a ve başka modülün ana projesine başvurmamalıdır. Bugün başvuranlar taşınmadan önce düzeltilir:

- `ModulixDbContext` kullanan uç nokta kodu modülün kayıt depolarına ya da `IModuleData`'ya alınır.
- Başka modülün koduna uzanan uç nokta (ör. `Api/Contracts` → `Modulix.Modules.Crm`) o modülün sözleşme projesindeki bir arayüze çevrilir (ADR-0044).
- `Core` iç tiplerine ihtiyaç gerçekse (`Core.Events`, `Core.Platform`, `Core.Settings`) karşılığı `Core.Contracts`'a arayüz olarak çıkarılır; çıkarılamıyorsa o uç nokta host'ta kalır ve gerekçesiyle listelenir.

Bu, taşımanın asıl işidir ve modül başına ayrı ayrı yapılır. Küçük modüller (Antrepo, Lojistik, İK: uç noktaları bugün yalnızca kendi modülüne ve `Shared`'a başvuruyor) neredeyse hazırdır; Crm, Sözleşme, Marketing, Sipariş ve Destek için önce temizlik gerekir.

### 5. DTO'lar ve ekranlar: yön (bu ADR'de taşınmaz)

- **DTO'lar** `Modulix.Shared`'da kalır; modüller `Shared`'a başvurabilir hâle gelir (bugün uç noktalar Api'de olduğu için gerekmiyordu). `Shared` bağımlılıksızdır ve WebAssembly'ye derlenir; bu değişmez. Modül başına DTO projesi, ekranlar taşınırken birlikte değerlendirilir.
- **Ekranlar** `Modulix.Web`'de kalır. Modül başına Razor sınıf kitaplığı ayrı bir ADR konusudur ve şu sorular yanıtlanmadan açılmaz: ortak bileşenlerin (`LayoutForm`, `ViewTabs`, `RecordTable`) ayrı kitaplığa çıkarılması, yönlendirmede ek derlemeler, kapalı modülün ekran kodunun indirilmemesi (lazy loading) isteniyor mu.

### 6. Platform başvuru tabloları (Customs için)

Öneri, açık soru 3 yanıtlanınca kesinleşir: modül, tenant'sız ve tenant'lar için salt okunur **başvuru tablosu** sahibi olabilir (GTİP kataloğu, rejim profili gibi). Kurallar:

- Entity yeni bir işaret arayüzü uygular (`IPlatformReferenceEntity`); `TenantId` taşımaz, RLS dışındadır, tablo `platform` şemasında kalır (şema taşınmaz, migration gerekmez).
- Modül bu tablolara `IModuleData`'dan ayrı, yalnızca okuma veren bir arayüzle erişir. Yazma yalnızca platform uçlarından (`/api/platform`, `PlatformAccessRules`) yapılır.
- `TableInventoryTests` yeni sınıfı tanır: her tablo ya tenant tablosu, ya platform tablosu, ya da bir modülün başvuru tablosudur.
- Tenant verisi dışa aktarma ve silme (`tenant-export`, `tenant-delete`) bu tablolara dokunmaz; tenant'a ait veri içermezler.

Bu kabul edilirse Customs sözleşmeleri `Modulix.Modules.Warehouse.Contracts`'a (ya da ayrı Gümrük modülüne, ADR-0032), uygulaması ve uç noktaları modüle taşınır; ADR-0044'teki son `PendingMoves` istisnası kapanır.

## Açık sorular (karar verilmeden kodlanmaz)

1. **`Core.Hosting` adı ve kapsamı.** Ayrı proje mi, yoksa `Core.Contracts` ASP.NET'e mi başvursun? Öneri ayrı proje; `Core.Contracts`'a çerçeve başvurusu eklemek sözleşme projelerini ve birim testlerini de ASP.NET'e bağlar.
2. **Modül → `Modulix.Shared` başvurusu.** DTO'lar `Shared`'da kalırken modüllerin ona başvurması kabul mü? Alternatif: uç nokta DTO'ları modülde tanımlanır ve `Shared`'dakilerle eşlenir (çift tanım, bu ölçekte gereksiz).
3. **Platform başvuru tabloları.** Bölüm 6'daki model kabul mü? Gümrük ayrı modül mü olacak, Warehouse'ta mı kalacak?
4. **Platform ve çekirdek uç noktaları.** `Api/Platform`, `Auth`, `Users`, `Customization`, `Imports` gibi modüle ait olmayan klasörler host'ta mı kalsın, `Core`'un yanında ayrı bir projeye mi çıksın? Öneri: bu ADR'de host'ta kalır.
5. **Çok örnekli çalışma.** Tek zamanlayıcı bugünkü gibi "tek API örneği" varsayar. Birden çok örnek planlanıyorsa iş kilidi (veritabanında kira/lease) bu ADR'ye eklenmeli.
6. **Api'nin Crm'e özel yardımcıları.** `Api/Support` ve `Api/Contracts` `Modulix.Api.Crm`'e başvuruyor. Neyin kullanıldığı Dilim 0'da çıkarılır; ortak yardımcıysa `Core.Hosting`'e, Crm davranışıysa Crm sözleşmesine gider.

## Dilimler

Her dilim ayrı PR'dır; taşıma dilimleri davranış ve URL değiştirmez.

0. **Envanter ve kural.** Api'deki her modül klasörü için başvuru envanteri (kendi modülü dışında neye uzanıyor). Mimari teste "`Api/<Ad>/` klasörü başka modülün ana projesine başvuramaz" kuralı, bugünkü ihlaller geçici istisna listesiyle. Kod taşınmaz.
1. **`Core.Hosting` + `IModuleEndpoints` + pilot.** Yardımcılar `Api/Access` ve `Api/Records`'tan taşınır; pilot olarak Antrepo (tek dosya, yalnızca kendi modülüne ve `Shared`'a başvuruyor). `AllowedReferences` ve mimari testler güncellenir.
2. **Küçük modüller.** Lojistik, İK. (Filo'nun modüle özel uç noktası yok; taşınacak bir şey çıkmaz.)
3. **İş zamanlayıcısı + pilot.** `ModuleJobDefinition`, `IModuleJob`, `ModuleJobsWorker`; pilot `OrderJobs`. Eski yapılandırma adları okunmaya devam eder.
4. **Kalan modül işleri.** `QuoteExpiry`, `ContractJobs`, `SupportJobs`, `MarketingSend`.
5. **Orta modüller.** Sipariş, Destek, Marketing: önce bölüm 4'teki temizlik, sonra taşıma (modül başına bir PR).
6. **Büyük modüller.** Sözleşme, Crm (Crm birden çok PR).
7. **Platform başvuru tabloları ve Customs** (açık soru 3'ten sonra).
8. **Kapanış.** `Program.cs`'te modüle özel satır kalmadığını denetleyen mimari test; `CLAUDE.md` ve README güncellemesi ("yeni modül için host değişmez").

## Kabul ölçütleri

- `Modulix.Api` hiçbir modülün adını içermez: modül kaydı (`ModulixModules.Create()`) dışında modüle özel `using`, uç nokta eşlemesi ya da iş kaydı yoktur (mimari testle denetlenir).
- Yeni bir modül eklemek `Modulix.Api`'de modül listesine bir satır eklemekten ibarettir.
- Modül projeleri `Core` ve `Infrastructure`'a başvurmaz (bugünkü kural değişmez); `Core.Hosting` ve `Shared` başvurusu tabloda yazılıdır.
- Her modül uç noktası host tarafından `RequireModule` ile sarılır; izin kontrolü olmayan modül uç noktası (yeni) mimari testi kırar.
- Modüle özel arka plan işleri tek zamanlayıcıdan çalışır; erişime kapalı tenant ve lisansı bitmiş modül için iş çalışmaz (entegrasyon testiyle).
- Her taşıma diliminde mevcut entegrasyon testleri değişmeden geçer (yalnızca `using` satırları değişebilir).

## Sonuçlar

**Olumlu**

- Modülün sunucu tarafı tek projede toplanır; sınır ihlali derleme hatası olur (modül başka modülün ana projesine ve `Infrastructure`'a başvuramaz).
- Yeni modül host'a dokunmadan eklenir; paralel dallarda `Program.cs` çakışmaları biter.
- Lisans, tenant durumu ve hata yalıtımı kuralları işlerde tek yerde uygulanır.
- Customs için yol açılır; ADR-0044'ün son istisnası kapanabilir.

**Olumsuz**

- Modül projeleri ASP.NET'e bağlanır; modül birim testleri de bu çerçeveyi yükler (derleme süresine etkisi ölçülmedi; Api Release derlemesi bugün 17-35 dakika sürüyor).
- Büyük modüllerde (Crm, Sözleşme) taşımadan önce temizlik gerekir; iş haftalara yayılır ve açık PR'larla çakışır. Dilimler küçük tutulmalı, taşıma PR'ları hızlı birleştirilmeli.
- `InternalsVisibleTo` ile test edilen iç tipler çoğalabilir; uç nokta kodu modüle girince modülün `internal` yüzeyi büyür.
- Tek zamanlayıcı tek hata noktasıdır; iş başına yalıtım ve günlükleme doğru yazılmazsa bir modülün işi diğerlerini geciktirir.

**Kapsam dışı**

- DTO'ların ve Blazor ekranlarının modüle taşınması (ayrı ADR).
- Modül başına `DbContext` ya da migration zinciri (tek `ModulixDbContext` ve tek zincir kalır).
- Platform işlerinin (`EventDispatch`, `EmailDispatch` vb.) yeniden tasarımı.
- URL ve API sözleşmesi değişiklikleri.
