# ADR-0027: Ortak kural ve iş akışı altyapısı

- **Durum:** Taslak, 3 Kasım 2026 (Dilim 0 incelemesi ve Dilim 1 sözleşmeleri bu belgeyle birlikte teslim edilir)
- **Karar:** Kural, otomasyon, onay, puanlama ve zamanlanmış takip için ücretli AI/LLM gerektirmeyen, tipli ve sürümlü tek bir Core altyapısı kurulur. Modüller yeteneklerini (olay, alan, ilişki, aksiyon) `IModule.Automation` ile beyan eder; Core hiçbir sektör modülünü tanımaz. Aksiyon kaydı, ileride AI ajanlarının da çağırabileceği tek tip araç sözleşmesidir; ayrı bir "AI araç" sistemi kurulmaz.
- **İlgili:** ADR-0002 ve ADR-0020 (tek paylaşımlı veritabanı), ADR-0007 (olay altyapısı), ADR-0008 (şirket kapsamı), ADR-0016 (lookup), ADR-0021 (form düzeni), ADR-0026 (departman); yol haritası Ç6b (otomasyon), Ç6a (onay), Ç2 (arka plan işleri), Ç5a (uygulama içi bildirim)

## Problem

Satış, destek, sözleşme ve sipariş modülleri bugün kendi dar kurallarını kendi zamanlayıcılarında çalıştırıyor. Kullanıcı "7 gündür aktivitesi olmayan fırsatın sorumlusuna takip görevi aç" gibi bir kuralı kendisi tanımlayamıyor; her yeni kural kod değişikliği ve dağıtım demek. Hedef: kuralı yetkili kullanıcının tanımlaması, yayın öncesi etkisini yan etkisiz görmesi, çalışırken yetkisini ve tenant sınırını aşmaması, her kararın nedeninin açıklanabilmesi.

Sınırlar (görevden): ücretli AI servisi, yerel model, GPU, embedding, vektör veritabanı ve LLM çağrısı **yoktur**; `Modulix.AI` projesine dokunulmaz. Kural çıktıları "AI tahmini" olarak sunulmaz; şablondan üretilen metin "AI üretimi" diye etiketlenmez. Üretime dağıtım, gerçek müşteriye mesaj ve gerçek veride toplu otomasyon kapsam dışıdır; yeni kurallar taslak/pasif teslim edilir.

## Dilim 0: mevcut durum (kanıtlarıyla)

### Veritabanı modeli: çelişki ve karar

| Kaynak | Ne diyor |
| --- | --- |
| ADR-0009 (satır 3) | "Geri alındı, 30 Ekim 2026 (ADR-0020)" |
| ADR-0020, ADR-0002, CLAUDE.md, README satır 41, `docs/tenant-isletim.md` satır 9 | Tek paylaşımlı `Modulix` veritabanı |
| Kod: `ModulixDbContext` | Tek bağlam; tenant tabloları `TenantId` taşır (query filter + RLS); `ITenantConnectionResolver` kodda **yok** (yalnızca ADR metinlerinde geçer) |
| ADR-0017 (madde 1) ve ADR-0019 (satır 10) | Hâlâ "tenant başına ayrı veritabanı (ADR-0009) zaten mimaridir" der: **eskimiş metin** |

Karar: ADR-0020 geçerlidir, kod bunu doğrular. ADR-0017 ve ADR-0019'daki ADR-0009 atıfları tarihsel olarak okunur; bu görevde bu iki ADR'ye dokunulmadı (kapsam dışı), işaret burada bırakıldı. Bu ADR'nin tüm tabloları `core` şemasında, `TenantId` taşıyan, RLS'li tablolardır. Tenant başına DB'ye dönüş yapılmaz.

### Boşluk analizi

| Alan | Durum | Kanıt | Karar |
| --- | --- | --- | --- |
| Modül kaydı, lisans/modül erişimi | **Mevcut** | `IModule` (`Core.Contracts/Modules/IModule.cs`), `ModuleCatalog` (açılışta doğrular), `ICurrentAccess.IsModuleEnabledAsync`, `.RequireModule` | Genişlet: `IModule.Automation` |
| Tenant bağlamı, query filter + RLS | **Mevcut** | `ITenantEntity`, `ModulixDbContext.ApplyTenantFilter`, `TenantIsolationSql.AddTable`, `TenantSessionContextInterceptor`, `TableInventoryTests` | Yeniden kullan |
| Rol, departman, kapsam, alan yetkisi | **Mevcut** | `ICurrentAccess`, `RecordScope`, `IModule.SensitiveFields`, `ModuleCatalog.HiddenFields`, ADR-0026 | Yeniden kullan |
| Dinamik alan, form, lookup, seçim listesi | **Mevcut** | `CustomFieldType` (7 tip), `ICustomFieldService`, `ChoiceListDefinition`, `LookupDefinition`, `FormDefinition` | Alan kataloğu bunlardan beslenir |
| Koşul modeli örneği | **Kısmen** | `SegmentFields.cs` (tip→operatör tablosu), `SegmentConditions.cs` (AND/OR ağacı, `MaxDepth=4`); hedefi "kimlik kümesi sorgusu"dur, kayıt/olay değerlendirmesi yapmaz | Aynı kalıp, ayrı değerlendirici (segment motoruna dokunulmaz) |
| Olay, outbox, teslim, retry, dead-letter | **Mevcut** | ADR-0007, `EventDispatcher`, `EventDeliveries`, `IEventSubscriber`, `EventEnvelope` (correlation/causation/depth) | Yürütme bu hatta bağlanır (Dilim 2) |
| Arka plan işleyicileri | **Mevcut, dağınık** | `SupportJobsWorker`, `ContractJobsWorker`, `OrderJobsWorker`, `QuoteExpiryWorker`, `EmailDispatchWorker`, `EventDispatchWorker`; tenant başına `sp_getapplock`. Genel iş çerçevesi (Ç2) **yok**; Marketing/Logistics işçisi yok | Yeni çerçeve kurulmaz; kural taraması aynı kalıpla tek işçi (Dilim 2) |
| SLA | **Mevcut** | `SlaScheduler`, `SlaEngine`, duraklatma, yeniden açılma, öncelik değişimi, `SlaEscalation` tekilliği | Dokunulmaz; kural motoru SLA'yı yeniden hesaplamaz |
| Çalışma takvimi, tatil | **Kısmen (Support içinde)** | `Support/Sla/WorkCalendar.cs` (`CalendarSpec`, `CalendarHoliday`, `AddWorkingTime`, DST). Platform çapı `IWorkCalendar` **yok**; Sözleşme `ContractPolicySettings.TimeZoneId` ile ayrı | Dilim 4'e ertelendi (aşağıda gerekçe) |
| Onay | **Kısmen, modüle özgü** | `ApprovalFlow` (saf, Core.Contracts), CRM `QuoteApprovalRule/Delegation`, Contracts `ContractApprovalEngine` (dört göz, kendini onaylama engeli, vekâlet, hedef süre). Süre aşımı yalnızca hatırlatma; **eskalasyon yok**; genel onay tanımı/örneği (Ç6a) **yok** | Mevcut onayı yeniden yazma; kural aksiyonu "mevcut onay sürecine gönder" olarak çağırır; genel örnek Dilim 4 |
| Görev/aktivite | **Mevcut** | `IFollowUpActivities` (`FollowUpRequest`), CRM uygulaması `CrmFollowUpActivities`, host `HostFollowUpActivities` | "Görev oluştur" aksiyonu bunu çağırır (Dilim 3) |
| Uygulama içi bildirim | **Eksik** | Tablo/servis yok (`InAppNotification` vb. arandı); yalnızca `IEmailQueue`, `INotificationRecipients` | Dilim 3/4: en küçük bildirim kutusu (Ç5a ile birleşik) |
| Şablon motoru | **Kısmen** | Yalnızca sözleşme: `ContractTemplateEngine` (beyaz liste, tek geçiş). E-posta/bildirim/görev şablonu motoru yok | Dilim 4: ortak değişken doğrulayıcı; sözleşme motoru taşınmaz |
| Puanlama | **Eksik** | Lead/fırsat/müşteri skoru yok; yalnızca sözleşme `RiskLevel` | Dilim 4 |
| Genel kural/otomasyon motoru | **Eksik** | Dar kural yapıları var (Support yönlendirme, SLA eskalasyon, onay kuralları, `LeadFirstContactSubscriber`) | Bu ADR |
| Mükerrer kontrol | **Mevcut, uç nokta katmanında** | `DuplicateEndpoints`, `RecordMerge`, `SharedReferenceMerger`; Support: `ITicketDuplicates` | Kural aksiyonu çağırır; **engelleme** kayıt doğrulamasında kalır (Bölüm "Bütünlük") |
| Segment | **Mevcut** | `ISegmentService`, `ISegmentEngine`, `ISegmentFieldSource` | "Hareketsiz müşteri" kuralı segment servisine kriter sunar |
| Satış devri | **Mevcut** | `IOrderHandovers`, `IAcceptedQuoteSource`, `OrderScheduler` | Yetkili ve idempotent servis çağrılır; mükerrer sipariş oluşturulmaz |
| Sözleşme yenileme | **Mevcut** | `ContractScheduler` (`MaxCatchUpPeriods=24`), `IContractRenewals` | Taşınmaz (aşağıda) |
| Teklif süresi | **Mevcut** | `QuoteExpiry` + hatırlatma işareti `ExpiryReminderCreated` | Taşınmaz; kural şablonu "mevcut özellik" olarak gösterilir |
| Denetim | **Mevcut** | `IAuditWriter`/`AuditEntry`, `AuditLogEntry`, `ExecutionSource` (`Api`, `BackgroundJob`, `Import`, `AiTool`) | Yönetim işlemleri `core.automation-rule` olarak yazılır. Otomasyon etkisi `BackgroundJob` + `Subscription` ile yazılır |
| Blazor menü, form, tema | **Mevcut** | `AppCatalog`, `AppNavigation`, `LayoutForm`, `docs/design-system` | Dilim 5 |
| Test ve CI | **Mevcut** | Mimari, Core, modül, entegrasyon projeleri; `ci.yml`; `ef-migration.yml` | Yeni test sınıfları aynı projelere |
| Lojistik modülü | **İskelet** | `LogisticsModule.cs`: 6 izin, 1 rol şablonu; varlık/depo/uç nokta yok | Yalnızca yetenek beyanı (Dilim 1) |

### Mevcut zamanlayıcıların kural motoruna taşınması

Karar: **taşınmaz**. Sözleşme yenileme, teklif süresi, SLA ihlali/eskalasyonu ve sipariş devri kabul gecikmesi modüllerin kendi durum makinesine bağlıdır (K14: her modül kendi durum makinesinin sahibi), tekil kayıtlarla (`SlaEscalation`, `QuoteMarkerKinds`, dönem anahtarları) çift etkiyi zaten önler ve testlidir. Kural motoruna taşımak risk ekler, kullanıcıya yeni bir şey vermez. Bu özelliklerin kural şablonu karşılığı **"mevcut özellik"** olarak listelenir, ayarına bağlanır; kural motoru aynı olaya ikinci bir etki üretmez. Çift tetikleme yaratabilecek şablonlar (fırsat takibi dışında) yalnızca ilgili modül özelliği kapalıysa etkinleştirilebilir.

### Çalışma takvimi kararı

Takvim, SLA'nın tek tüketicisidir ve Support içinde çalışır. İlk on iki şablonun hiçbiri çalışma günü hesabı istemez ("7 gün", "3 gün kala", "90 gün" takvim günüdür, tenant saat diliminde gün sınırıyla: `ITenantClock`). Bu yüzden Dilim 1-3'te yeni takvim altyapısı kurulmaz. Dilim 4'te, kural/akış beklemeleri "iş günü" isteyince `WorkCalendar` saf hesabı Support'tan `Core.Contracts`'a taşınarak ortaklaştırılır (SLA davranışı değişmez, testleri taşınır). Gerekçe: iki ayrı takvim mantığı yazmamak ama tüketicisi olmayan altyapıyı da şimdi kurmamak.

## Core–modül sınırı

- Core; kural tanımı ve sürümü, koşul modeli ve değerlendirici, aksiyon sözleşmesi, yetki/denetim kuralları ve (sonraki dilimlerde) çalıştırma, akış, onay örneği, puan ve şablon doğrulamasını içerir. Sektöre özel hiçbir koşul veya alan adı içermez.
- Modül `IModule.Automation` ile beyan eder: **olaylar** (`AutomationEventDefinition`), **alanlar** (`AutomationFieldDefinition`: tip, seçenek, lookup, okuma izni), **ilişkiler** (`AutomationRelationDefinition`: sınırlı exists/count), **aksiyonlar** (`AutomationActionRegistration`: tanım + işleyici sınıfı). Beyan açılışta `ModuleCatalog` tarafından doğrulanır (anahtar öneki, tekillik, tip→operatör uyumu, izinlerin ve modüllerin varlığı); hatalı beyan uygulamayı açtırmaz.
- Core hiçbir modül projesine bağımlı değildir; `ProjectReferenceRulesTests.AllowedReferences` değişmez (yeni proje eklenmedi). Mimari test, Logistics'in CRM olmadan aynı sözleşmeyle yetenek beyan ettiğini ve Core'un modül projelerine başvurmadığını denetler.
- Aksiyonlar mevcut uygulama servislerinden ve kayıt boru hattından (`IRecordPipeline`, `IFollowUpActivities`, ...) geçer; işleyici `DbContext`'e doğrudan yazmaz (`EventSubscriberRulesTests` kalıbı aksiyon işleyicilerine de uygulanır).

## Aksiyon (araç) sözleşmesi

Tek tip sözleşme; kural motoru, manuel çalıştırma ve ileride AI ajanları aynı kaydı kullanır. AI kodu **yazılmaz**.

```csharp
public sealed record AutomationActionDefinition(
    string Key,                                    // modül önekli, değişmez: "crm.follow-up.create"
    string DisplayName, string Description,        // kullanıcıya gösterilir
    IReadOnlyList<AutomationParameter> Parameters, // parametre şeması (ad, tip, zorunlu, seçenek, lookup)
    IReadOnlyList<string> RequiredPermissions,     // çağıran kimliğin sahip olması gereken izinler
    IReadOnlyList<string> RequiredModules,         // tenant için açık olması gereken modüller (lisans)
    AutomationSideEffect SideEffect,               // Read | Draft | Write
    bool SupportsDryRun,
    AutomationIdempotency Idempotency);            // anahtar nasıl türetilir

public interface IAutomationAction
{
    Task<AutomationActionResult> ExecuteAsync(
        AutomationActionRequest request,           // aksiyon, parametreler, hedef kayıt, IdempotencyKey, DryRun
        AutomationCallContext context,             // çağıran kimlik: dışarıda, sözleşmenin parçası değil
        CancellationToken cancellationToken);
}
```

- **Çağıran kimlik bağlamdır**: `AutomationCallContext(Kind: User | Automation | Agent, UserId, RuleVersionId?, CorrelationId, CausationId, Depth)`. `Agent` ileride AI içindir; bugün kullanılmaz ve hiçbir çağrı yolu `Agent` üretmez.
- **Yetki**: çağıran kimliğin izinleri ve tenant'ın modül/lisans durumu hem yayın anında hem çalışma anında denetlenir. Kuralı yayınlayan, sahip olmadığı izni gerektiren aksiyonu yayınlayamaz (yetki yükseltme yok).
- **Yan etki sınıfı**: `Read` yazmaz; `Draft` taslak üretir ama dış etki ve kesinleşme yoktur; `Write` kayıt değiştirir veya dış etki üretir. Dry-run `Write` aksiyonu çalıştırmaz, parametreleri çözüp planı gösterir.
- **Idempotency**: motor anahtarı türetir (`tenant + tetikleyici/olay kimliği + kural sürümü + hedef kayıt + adım`); aksiyon bunu hedef sisteme geçirir. `CallerSupplied` modu, anahtarı çağıranın verdiği (manuel/AI) aksiyonlar içindir.
- Parametre değeri sabit veya kayıttan bağlamadır (`{"$field": "crm.opportunity.ownerId"}`). Serbest SQL, C#, betik veya çalıştırılabilir ifade **yoktur**; metin şablonu değişkenleri Dilim 4'te ortak doğrulayıcıyla gelir.

## Koşul modeli

Tipli ve doğrulanan ağaç; JSON olarak `AutomationRuleVersions` içinde saklanır (polimorfik `$type`: `group`, `leaf`, `relation`).

- **Grup**: `and`/`or`, iç içe; derinlik en çok 4, yaprak en çok 50 (segment motoruyla aynı mertebe).
- **Yaprak**: `Field`, `Source` (`New` varsayılan | `Old`), `Operator`, `Values` (metin), `Currency` (para için). Alan tipine göre izinli operatörler: `AutomationOperators.For(type)`.

| Tip | Operatörler |
| --- | --- |
| Text | eq, ne, contains, startsWith, in, notIn, isEmpty, isNotEmpty |
| Number | eq, ne, gt, gte, lt, lte, between, isEmpty, isNotEmpty |
| Money | gt, gte, lt, lte, between, eq, ne (para birimi zorunlu; farklı para birimi **karşılaştırılmaz**, toplanmaz, dönüştürülmez) |
| Boolean | eq |
| Date, DateTime | before, after, between, withinNextDays, withinLastDays, olderThanDays, isEmpty, isNotEmpty |
| Choice | eq, ne, in, notIn, isEmpty, isNotEmpty (değer: kalıcı seçenek anahtarı) |
| Lookup | eq, ne, in, notIn, isEmpty, isNotEmpty (değer: **kalıcı kimlik** `Guid`, görünen metin değil) |
| herhangi | changed, changedTo, changedFrom (yalnızca değişiklik bilgisi olan tetikleyicilerde) |

Açık davranışlar:

- **Null**: karşılaştırma operatörleri (`eq`, `gt`, `contains`, `in`...) null değerde **yanlıştır**; `ne` ve `notIn` null değerde **doğrudur** ("durumu kapalı olmayan"); null için `isEmpty`/`isNotEmpty` kullanılır. Açıklama metni bunu yazar.
- **Eski/yeni**: `Source=Old` yalnızca olayın farkı (`{alan:{old,new}}`) varsa değerlenir; fark yoksa yaprak yanlıştır ve nedeni "değişiklik bilgisi yok" olarak açıklanır.
- **Tarih**: `Date` tenant takvim günüdür (`ITenantClock`, saat dilimi ve yaz saati dahil); `DateTime` UTC anıdır. `withinNextDays(N)` bugün dahil N gün; `olderThanDays(N)` bugünden en az N gün önce. Hassasiyet tipi tanımdan gelir, kullanıcı seçmez.
- **Hassas alan**: alanın `ReadPermission`'ı vardır; değerlendiren kimlik bu izne sahip değilse yaprak yanlış sayılır ve açıklama değeri göstermez. Yayınlayan kullanıcı okuyamadığı alana kural yazamaz.
- **İlişki**: `relation` düğümü yalnızca modülün beyan ettiği ilişkiyi (`exists`, `notExists`, `count`) kullanır; `where` yalnızca hedef türün alanlarına yaprak/grup koyar, ikinci seviye ilişki **yoktur**; toplama (`aggregate`) ilk sürümde `count` ile sınırlıdır. Çözümleyici (`IAutomationRelationResolver`) tek sorguda sayar; kayıt başına N+1 yoktur.
- **Değerlendirme çıktısı** her zaman açıklamadır: düğüm ağacında geçti/kaldı, okunabilir metin, (görmeye yetkiliyse) gerçek değer.

## Tetikleyici modeli

`AutomationTrigger`: `RecordCreated`, `FieldChanged(alanlar)`, `StatusTransition(alan, from, to)`, `Scheduled(Tarama)` (günlük, tenant saatinde, artımlı), `DateRelative(alan, ±gün)`, `Inactivity(ilişki, gün)`, `DomainEvent(olay anahtarı)`, `Manual`. Modül yalnızca `AutomationEventDefinition` ile bildirdiği olay ve alanlara tetikleyici koyabilir; geçersiz birleşim yayını engeller. Dilim 1 yalnızca modeli ve doğrulamayı kurar; tetikleyicinin çalışması Dilim 2'dir.

## Veri modeli

Tümü `core` şemasında, `ITenantEntity + ICompanyShared`, migration'da `TenantIsolationSql.AddTable` (kural tenant geneldir; şirket başına kural ihtiyacı doğarsa `ICompanyScoped` ayrı ADR'dir).

**Dilim 1**

| Tablo | Amaç |
| --- | --- |
| `core.AutomationRules` | Kural başlığı: ad, açıklama, `ModuleKey`, `EntityKey`, öncelik, `StopProcessing`, etkinlik tarihleri, `Status` (`Draft`, `Active`, `Inactive`), `PublishedVersionId`, eşzamanlılık sürümü |
| `core.AutomationRuleVersions` | Sürümler. `State`: `Draft` (düzenlenebilir, kural başına en çok bir) ya da `Published` (**değişmez**). `Trigger`, `Condition`, `Actions` JSON; `DefinitionHash`; yayınlayan ve zaman |

- Yayınlanmış sürüm değişmez: uygulama katmanı değiştirmeyi reddeder; yayından sonra düzenleme yeni taslak sürüm açar. Çalışan örnek (Dilim 2/4) başlatıldığı sürüme bağlı kalır.
- Benzersizlik tenant ile birlikte: `UNIQUE(TenantId, RuleId, VersionNo)`; kural başına tek taslak için filtreli benzersiz indeks.
- Silme: yayınlanmış sürümü olan kural silinmez (geçmiş bağlıdır); pasifleştirilir/arşivlenir.

**Sonraki dilimler (şimdi oluşturulmaz)**: `AutomationRuns`/`AutomationRunSteps` (tekilleştirme anahtarı, kira, deneme, sonuç, maskeli hata), `AutomationScanCursors` (artımlı tarama), `WorkflowDefinitions/Instances/Waits`, `ApprovalRequests/Steps`, `ScoreModels/Criteria/Results`, `AutomationTemplates` (metadata şablonları), tenant sınırları.

## Çalıştırma ve tekilleştirme tasarımı (Dilim 2)

- **Olay yolu**: çekirdek `core.automation` aboneliği (`IEventSubscriber`, `Ordered=false`) `RecordCreated/Updated` ve iş olaylarını alır; aday kuralları `(tenant, EntityKey, olay türü, değişen alan)` anahtarıyla **sürüm bazlı önbellekten** seçer; her aday için `AutomationRun` satırı yazar (aynı işlemde). Aksiyonlar ayrı adım satırlarıdır; uzun iş abonede yapılmaz.
- **Tekilleştirme anahtarı**: `DedupeKey = hash(tenant | olay kimliği ya da tarama dönem anahtarı | kural sürümü | hedef kayıt | aksiyon adımı)`, `UNIQUE(TenantId, DedupeKey)`. Olay yolunda olay kimliği, tarama yolunda **dönem anahtarı** (ör. tenant takvim günü) kullanılır: aynı gün ikinci tarama görev açmaz, ertesi gün meşru olarak açabilir. Kural "tekrar politikası" (her dönem / bir kez / bekleme süresi) dönem anahtarını belirler.
- **Çift işçi**: e-posta ve olay teslimindeki kalıp (koşullu `UPDATE` ile kira + `LeaseToken`, heartbeat); tarama için tenant başına `sp_getapplock`.
- **Döngü**: `EventEnvelope.CorrelationId/CausationId/Depth` taşınır; kural kaynaklı olay `Depth+1`; azami derinlik ADR-0007'deki 5 ile başlar, kural başına tekrar politikası ve aynı (kural, kayıt, correlation) için tek çalışma sınırı eklenir.
- **Retry**: ADR-0007 çizelgesi; başarısız adımlar kuyruğu; yetkili manuel tekrar aynı `DedupeKey`'i yeniden çalıştırır, ikinci etki üretmez. Harici kanalda sağlayıcı tekilleştirmesi yoksa "tam bir kez" vaat edilmez; belirsiz sonuç `Unknown` durumda görünür.
- **Kimlik**: otomasyon `TenantContext(tenantId, Guid.Empty, Guid.Empty)` + `automation` sistem kimliği ile çalışır; izinleri **kuralın yayınlandığı sürümde beyan edilen** (yayınlayanın izinlerinin altkümesi) kümedir. Çalışma anında izin, modül/lisans, kapsam, departman, alan güvenliği yeniden denetlenir. Kural kapanınca kuyruktaki adımlar `Cancelled`, yürüyen adım bir sonraki sınırda durur. Acil durdurma: tenant ayarı `core.automation.enabled` (Dilim 2) ve platform anahtarı.
- **Tenant limitleri**: tur başına tenant başına en çok N çalışma, tarama sayfası, zincir derinliği, süre (ADR-0007'deki adillik kalıbı).
- **Saklama**: çalışma ve adım kayıtları 90 gün, başarısız olanlar 180 gün (başlangıç önerisi; Dilim 6'da ölçümle gözden geçirilir); hata metni ve payload maskelenir, kişisel veri tutulmaz.

## Bütünlük: yalnızca asenkron olmayan engeller

Kritik mükerrerlik ve onaysız indirim gibi veri bütünlüğü engelleri otomasyona **bırakılmaz**: kayıt işleminin uygulama doğrulamasında (store `Validate` / mevcut onay kuralı) zaten çalışır ve çalışmaya devam eder. Kural motoru bunları yalnızca ek bilgi/takip için kullanır. Yeni şablonlar (Dilim 3) bu engelleri kural motoruna bağımlı yapmaz.

## Yetki ve denetim

İzinler (Core modülü, `core.automation.*`; anahtarlar mimari kuralı gereği modül anahtarıyla başlar):

| İzin | Anlam |
| --- | --- |
| `core.automation.read` | Kuralları, şablonları ve katalog beyanlarını görür |
| `core.automation.manage` | Taslak oluşturur, düzenler, siler (yayınlanmamış), önizler |
| `core.automation.publish` | Yayınlar, etkinleştirir/pasifleştirir |
| `core.automation.run` | Manuel çalıştırır (Dilim 2) |
| `core.automation.logs.read` | Çalışma geçmişini görür (Dilim 2) |
| `core.automation.approve` | Otomasyonun bekleyen onaylarını karara bağlar (Dilim 4) |

Her yönetim işlemi `core.automation-rule` kaydı olarak denetime yazılır (`ExecutionSource.Api`); otomasyon etkisi (Dilim 2) `BackgroundJob` + abonelik adıyla. Önizleme denetime yazılmaz, hiçbir veri değiştirmez.

## Zaman, puanlama, şablon (Dilim 4 tasarım notları)

- Tarihler UTC saklanır; gün kavramı `ITenantClock`. Kaçırılan tarama: sunucu kapalıyken geçen dönemler **en çok `MaxCatchUp` dönem** (başlangıç 3) geriye dönük, tek tek dönem anahtarıyla işlenir (`ContractScheduler.MaxCatchUpPeriods` örneği); bildirim tekrarı kural başına dönem anahtarıdır, günde bir.
- Puanlama: kriter + ağırlık + üst/alt sınır + sürüm; eksik veri kuralı kriter başına (atla/sıfır/uyarı); kullanıcı bileşenleri görür; "kazanma olasılığı" olarak sunulmaz. Ağırlıklı satış tahmini = tutar × tanımlı aşama olasılığı; para birimleri ayrı toplanır.
- Şablon: beyaz listeli değişken, bilinmeyen/yetkisiz değişken yayını engeller, eksik değer boş + uyarı, HTML'e giren değer `WebUtility.HtmlEncode` ile kodlanır, önizleme yan etkisizdir.

## Dilim planı

| Dilim | İçerik | Durum |
| --- | --- | --- |
| 0 | İnceleme, bu ADR | Bu oturum |
| 1 | `Core.Contracts` sözleşmeleri, koşul doğrulayıcı/değerlendirici, aksiyon sözleşmesi, `AutomationRules/Versions` + migration + RLS, kural servisi, yan etkisiz önizleme, `core.automation.*` izinleri, denetim, API, Logistics yetenek beyanı, testler | Bu oturum |
| 2a | Olay yolu (`core.automation` aboneliği), `AutomationRuns/Steps`, tekilleştirme, kira, retry, kuralın kimliği, acil durdurma, tenant limitleri, elle çalıştırma, çalışma geçmişi API'si | Uygulandı (aşağıda "Dilim 2a") |
| 2b | Tarama tabanlı tetikleyiciler (`Scheduled`, `DateRelative`, `Inactivity`), iş olayı tetikleyicisi, `AutomationScanCursors`, `IAutomationRecordSource`, saklama temizliği | Sonra |
| 3 | CRM yetenek beyanı ve aksiyonları, ilk şablonlar (pasif), demo | Sonra |
| 4 | Kalıcı akış ve onay örneği, takvim ortaklaştırma, puanlama, şablon motoru, uygulama içi bildirim kutusu | Sonra |
| 5 | Blazor ekranları | Sonra |
| 6 | Performans ölçümü, işletim belgesi, yol haritası | Sonra |

## Maliyet (harici ücret yok, iç maliyet var)

Harici kullanım ücreti yoktur ama maliyet sıfır değildir: kural değerlendirme CPU'su (olay başına aday kural sayısı kadar), `AutomationRuns` ve denetim satırları için veritabanı ve depolama büyümesi (saklama politikasıyla sınırlı), tarama sorgularının veritabanı yükü, ve kural/şablon bakım emeği. Bu yüzden: aday kural seçimi önbellekli, taramalar indeksli ve sayfalıdır; tenant limitleri paylaşılan veritabanını korur. Ölçülmemiş gecikme veya kapasite bu belgede **garanti olarak yazılmaz**; Dilim 6 ölçer.

## AI'a açık bırakılan noktalar (kod yazılmadan)

1. `AutomationCallContext.Kind = Agent`: aksiyon sözleşmesi çağıran kimliği dışarıdan alır; AI ajanı aynı kaydı kullanır, yetkisi kullanıcının yetkisiyle sınırlıdır (CLAUDE.md: AI yalnızca tenant ve kullanıcı yetkisi içinde, modül servisleri üzerinden).
2. `AutomationSideEffect` ve `SupportsDryRun`: AI aracı "önce planla, sonra onayla" akışını bu alanlardan kurar; `Write` aksiyonları insan onayına bağlanabilir.
3. `ExecutionSource.AiTool` zaten denetim kaydında vardır; AI çağrısı yapılırsa aynı denetim yolu kullanılır.
4. Kullanım defteri: çalışma/adım ölçümleri (kuyruk bekleme, süre, sonuç, tekilleştirilen iş, tenant) tek bir `AutomationUsage` ölçüm şemasına yazılır; ileride AI kullanım ölçümü (kota/fiyat) aynı deftere yazabilir. Bu görevde AI için doldurulmaz.
5. Şablondan üretilen metin ile (ileride) AI üretimi metni ayrı etiketlenir; bugün yalnızca "şablondan" vardır.

## Geri dönüş ve dağıtım

- Migration yalnızca iki yeni tablo ekler (geriye uyumlu, mevcut tabloya dokunmaz); eski uygulama sürümü yeni tabloları görmez ve çalışır. `dotnet ef database update` kullanılmaz; `dotnet run --project src/Modulix.Api -- migrate`.
- Geri dönüş: uygulama önceki sürüme alınır (tablolar zararsız kalır). Veri atılacaksa `Down` tabloları (RLS politikasından çıkarıp) kaldırır; önce `tenant-export` ile dışa aktarma alınır.
- Dilim 1'de yürütme yoktur: yayınlanmış/etkin kural hiçbir iş yapmaz. Bu, README'de açıkça yazılıdır.

## Uygulama durumu

### Dilim 0 (3 Kasım 2026)

Bu belge: boşluk analizi, Core–modül sınırı, aksiyon sözleşmesi, veri modeli, çalıştırma ve tekilleştirme tasarımı, dilim planı, AI'a açık noktalar. Kod değişikliği yok.

### Dilim 1 (3 Kasım 2026)

Yapılanlar (ayrıntı için README "Otomasyon kuralları"):

- **Sözleşmeler** (`Modulix.Core.Contracts/Automation`): `AutomationConditions.cs` (alan tipleri, operatörler, `ConditionNode` grup/yaprak/ilişki, tetikleyiciler, `AutomationDefinition`, `AutomationJson`), `AutomationCapabilities.cs` (varlık, olay, alan, ilişki, aksiyon beyanı; `AutomationActionDefinition`, `IAutomationAction`, `AutomationCallContext`, `IAutomationRelationResolver`), `AutomationFacts.cs` (`IAutomationFacts`, `ConditionExplanation`). `IModule.Automation` varsayılan boş beyanla eklendi; mevcut modüller değişmedi.
- **Katalog** (`ModuleCatalog.Automation.cs`): beyanları açılışta doğrular; `AutomationCatalogService` (`IAutomationCatalog`) tenant, modül/lisans, izin ve firmanın özel alanlarıyla kullanılabilir yetenekleri üretir.
- **Koşul** (`Modulix.Core/Automation`): `ConditionValidator` (tip/operatör/değer, derinlik, alan izni, ilişki sınırı), `ConditionEvaluator` (açıklamalı, yan etkisiz; null, para, tarih, eski/yeni davranışları), `ConditionText` (Türkçe okunabilir özet), `RecordFacts` (kayıt değerleri ve olay farkından olgular, alan yetkisi).
- **Tanım doğrulama** (`AutomationDefinitionValidator`): modül/kayıt türü, tetikleyici, koşul, aksiyon şeması, parametre bağlama, izin ve lisans; **önizleme** (`AutomationPreviewService`).
- **Veri** (`core.AutomationRules`, `core.AutomationRuleVersions`, migration `AutomationRules`, RLS): yayınlanmış sürüm değişmez, kural başına tek taslak (filtreli benzersiz indeks), `UNIQUE(TenantId, RuleId, VersionNo)`.
- **Yönetim**: `IAutomationRuleService` (`AutomationRuleService`, Infrastructure), `/api/automation` uç noktaları, `core.automation.*` izinleri, `core.automation-rule` denetim kaydı.
- **Lojistik** yetenek beyanı (belge durumu, kapasite kullanımı, sonraki bakım tarihi) ve `LogisticsAutomationCapabilityTests`; `AutomationNeutralityRulesTests` (Core hiçbir modülü adlandırmaz, AI/LLM bağımlılığı yoktur).

Tasarımdan ayrılan / netleşen noktalar:

- İzin anahtarları `automation.*` yerine **`core.automation.*`** (+ `core.automation-logs.read`): mimari kural izin anahtarının modül anahtarıyla başlamasını ister.
- Kural içeriği (ad, açıklama, öncelik, `StopProcessing`, etkinlik tarihleri) **sürümdedir**; başlıktaki `Name` listede göstermek için en son kaydedilen sürümün adıdır.
- Koşulda `Source`/`Changed*` yalnızca `FieldChanged` ve `StatusTransition` tetikleyicilerinde kullanılabilir (yalnızca güncelleme olayında fark vardır).
- Çalışma takvimi: Dilim 1-3'te kurulmadı (Dilim 0'daki gerekçe aynen geçerli; Dilim 4'te `WorkCalendar` ortaklaştırılır).
- Mevcut zamanlayıcıların (sözleşme, teklif, SLA, sipariş) kural motoruna taşınmaması kararı korunur.

### Test sonuçları (Dilim 1)

| Komut | Sonuç |
| --- | --- |
| `dotnet build` | 0 hata |
| `dotnet test tests/Modulix.ArchitectureTests` | 50 başarılı |
| `dotnet test tests/Modulix.Core.Tests` | 381 başarılı (91'i otomasyon) |
| `dotnet test tests/Modulix.Modules.Logistics.Tests` | 4 başarılı |
| Crm / Contracts / Orders / Support / Marketing birim testleri | başarılı (216 / 146 / 109 / 145 / 133) |
| `dotnet test tests/Modulix.IntegrationTests --filter AutomationRuleTests` (SQL Server + Mailpit) | 14 başarılı |
| Tam entegrasyon paketi (616 test) | 605 başarılı, 1 atlandı, 10 başarısız. İkisi bu dilimin yeni `core.automation.*` izinlerini sabit listede beklemeyen `AdministrationTests` idi; düzeltildi ve yeniden koşuldu (geçti). Kalan sekizi `EventTests`/`EmailTests`/`Quote*Tests` gibi olay-teslim testleridir; tek tek ve ayrı koşularda geçer, her koşuda farklı testler kırılır. Dilimden önceki `main` derlemesiyle (aynı veritabanında) `EventTests` ayrı koşularda 0, 2 ve 5 test kırdı: yerel veritabanında biriken ~9.800 bekleyen teslim satırı ve paylaşılan olay işleyicisi kaynaklı kararsızlık, bu dilimin değişikliğiyle ilgili değil. CI'da temiz veritabanıyla doğrulanmalı |

### Bilinen sınırlamalar ve kalan işler

- **Kurallar çalışmaz.** `Active` durumunun bugün hiçbir etkisi yoktur.
- **Gerçek uygulamada yayınlanabilir kural yok:** yayın en az bir aksiyon ister ve henüz hiçbir modül aksiyon beyan etmiyor (CRM, Dilim 3). Entegrasyon testleri aksiyonu yalnızca test sürecinde genişletilmiş katalogla sağlar.
- Aksiyon işleyicisi sözleşmesi tanımlı ama çağrılmıyor; `SupportsDryRun` işleyicilerinin önizlemede çağrılması ilk işleyiciyle (Dilim 3) eklenecek.
- `IFollowUpActivities` idempotency anahtarı almıyor; "görev oluştur" aksiyonu için Dilim 3'te eklenmesi gerekir (en az bir kez teslimde ikinci görevi önlemek için).
- Olay farkındaki özel alan anahtarlarının biçimi (ADR-0007: "düzleştirilmiş") `RecordFacts`'ın varsaydığı `custom.<anahtar>` yoluyla Dilim 2'de gerçek outbox yüküyle doğrulanmalı.
- İlişki çözümleyicisi (`IAutomationRelationResolver`) hiçbir modülde uygulanmadı; `Where` koşulunun sorguya çevrilmesi modülün işidir (Dilim 3'te CRM aktivite ilişkisiyle).
- Çoklu seçim özel alanı koşulda yok. Para alanında para birimi kayıttaki kardeş alandan okunur.
- Modüllerin hassas alanları `SensitiveFields` ile beyan ettiği izni `AutomationFieldDefinition.ReadPermission` olarak da vermesi gerekir (aksi hâlde alan koşulda herkese açık görünür); CRM beyanında (Dilim 3) test edilecek.
- Tenant başına kural sayısı sınırı yok (Dilim 2'de tenant limitleriyle gelir).
- Entegrasyon testlerinin bıraktığı kural satırları diğer tenant tabloları gibi veritabanında kalır (RLS ile görünmez).
- ADR-0017 ve ADR-0019'daki eskimiş "tenant başına veritabanı" atıfları düzeltilmedi.

### Sonraki dilim (2) için bağımlılıklar

`core.automation` olay aboneliği (ADR-0007 `IEventSubscriber`, `Ordered=false`), `AutomationRuns/Steps` ve `AutomationScanCursors` tabloları, `UNIQUE(TenantId, DedupeKey)`, kira/heartbeat kalıbı (`EventDispatcher`), tarama için `sp_getapplock` ve `ITenantClock`, `core.automation.enabled` ayarı (acil durdurma), tenant limitleri, sürüm bazlı kural önbelleği.
