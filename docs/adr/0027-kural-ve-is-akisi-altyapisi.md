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
- Parametre değeri sabit veya kayıttan bağlamadır (`{"$field": "crm.opportunity.ownerId"}`). Serbest SQL, C#, betik veya çalıştırılabilir ifade **yoktur**; metin şablonu değişkenleri Dilim 4d'de geldi (aşağıda).

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
| 2b-1 | Tarama tabanlı tetikleyiciler (`Scheduled`, `DateRelative`, `Inactivity`), `AutomationScanCursors`, `IAutomationRecordSource` | Uygulandı (aşağıda "Dilim 2b-1") |
| 2b-2 | Çalışma/adım saklama temizliği | Uygulandı (aşağıda "Dilim 2b-2") |
| 2b-3 | İş olayı tetikleyicisi (`DomainEvent`); Ç0.4 iş olayı yayınlama sözleşmesine bağlı | Sonra |
| 3a | CRM yetenek beyanı ve aksiyonu (takip aktivitesi), kayıt kaynağı, aktivite ilişkisi, `IFollowUpActivities` idempotency | Uygulandı (aşağıda "Dilim 3a") |
| 3b | İlk şablonlar (pasif, kodda beyan, kullanınca taslak kural) | Uygulandı (aşağıda "Dilim 3b"); demo Dilim 5 ile |
| 4a | Genel onay: süreç tanımı, talep, Onay merkezi (Ç6a asgari) | Uygulandı (aşağıda "Dilim 4a") |
| 4b | Uygulama içi bildirim kutusu (Ç5a), onay süre aşımı eskalasyonu, genel vekâlet | Uygulandı (aşağıda "Dilim 4b") |
| 4c | Kayda bağlı onay (modül beyanı, kural aksiyonu "onaya gönder"), kalıcı akış | Sonra |
| 4d | Takvim ortaklaştırma, puanlama, şablon motoru | **Metin değişkenleri uygulandı** (aşağıda "Dilim 4d"); takvim ortaklaştırma ve puanlama sonra |
| 5 | Blazor ekranları (5a liste, şablon, ayrıntı; 5b çalışma geçmişi; 5c düzenleyici) | 5a, 5b ve 5c (ilk sürüm) uygulandı (aşağıda "Dilim 5a", "5b", "5c"); 5d kayıt seçici, kayıt üzerinde deneme ve elle çalıştırma uygulandı ("Dilim 5d") |
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

### Dilim 2a (4 Kasım 2026)

Kurallar artık olay yolundan çalışır (ayrıntı için README "Otomasyon kuralları → Yürütme"). Yapılanlar:

- **Olay yolu:** `core.automation` aboneliği (`IEventSubscriber`, `Ordered=false`, izinsiz). Katalog aboneliğin `EntityKeys` değerini modüllerin kural için beyan ettiği varlıklara daraltır; hiçbir modül beyan etmediyse abonelik kaldırılır. Abone yalnızca olay farkından tetikleyiciyi eşler (`AutomationTriggerMatcher`) ve `Pending` bir `AutomationRun` yazar; değerlendirme ve yürütme `AutomationRunner`'dadır.
- **Veri:** `core.AutomationRuns`, `core.AutomationRunSteps` (migration `AutomationRuns`, RLS). `UNIQUE(TenantId, DedupeKey)`; `DedupeKey = SHA256(tenant|olay|sürüm|varlık|kayıt)`. Çalışmalar kayıt değeri saklamaz; parametreler çalışma anında `AutomationParameterResolver` ile yeniden çözülür (önizleme ile aynı kod).
- **Yürütme:** tenant başına `sp_getapplock`, çalışma bazında kira (koşullu UPDATE, 3 dk), adımlar sırayla ve adım başına `SHA256(DedupeKey|adım)` idempotency anahtarıyla; yeniden deneme 1/5/15/60 dk, en çok 5 deneme. Beklenmeyen istisnada çalışma `Failed` olur, yalnızca istisna türü yazılır. Zincir koruması: derinlik > 0 ise aynı kural + kayıt + korelasyon ikinci kez çalışmaz.
- **Kuralın kimliği:** `EventHandlingScope.Begin(zarf, "core.automation", GrantedPermissions)`. İzinler tanımdan türetilir (varlık okuma, kullanılan hassas alan okumaları, aksiyonların gerekli izinleri); kapsam Tümü. Kural, kendi tanımının gerektirdiğinden fazlasını göremez ve değiştiremez.
- **Acil durdurma:** platform `Automation:Enabled`, firma ayarı `core.automation.enabled` (varsayılan Evet), kural kapatma. Bir tenant'ın yürütmesi diğerlerini durdurmaz.
- **Limitler:** firma başına etkin kural (varsayılan 200, `AutomationOptions.MaxActiveRulesPerTenant`), olay başına 50 aday kural, tur başına tenant başına 25 çalışma.
- **Çalışan tetikleyiciler:** `RecordCreated`, `FieldChanged`, `StatusTransition`, `Manual`. `Scheduled`, `DateRelative`, `Inactivity` ve `DomainEvent` yayın/etkinleştirmede 422 ("henüz çalışmıyor") ile reddedilir (`AutomationTriggerMatcher.IsExecutable`); Dilim 2b işidir.
- **Elle çalıştırma:** yalnızca etkin `ManualTrigger` kurallar; çağıran kaydı kendi izniyle okuyabilmeli; sürüm + kayıt + tenant günü başına bir elle çalışma (ikincisi 409 + `existingRunId`); `core.automation-rule` denetim kaydında "run" alanı.
- **Geçmiş API'si:** çalışma listesi/ayrıntısı ve elle çalıştırma uç noktaları (`/api/automation`), `core.automation-logs.read` izniyle.
- **Test dikişi:** `IAutomationRecordReader` (varsayılan `PipelineAutomationRecordReader`, kayıt boru hattı üzerinden); entegrasyon testleri `FakeRecordReader` ile değiştirir. `AutomationWorker` (BackgroundService) test fabrikasında kapalıdır (`Automation:WorkerEnabled=false`).

Tasarımdan ayrılan / netleşen noktalar:

- Dilim planındaki 2 iki parçaya bölündü: 2a (olay yolu, bu bölüm) ve 2b (tarama tabanlı tetikleyiciler).
- Çalışmalar kayıt değeri saklamadığı için geçmişte "o gün hangi değer vardı" görünmez; yalnızca hangi sürümün hangi kayıtta ne yaptığı ve sonucu vardır.

### Test sonuçları (Dilim 2a)

| Komut | Sonuç |
| --- | --- |
| `dotnet build` | 0 hata |
| `dotnet test tests/Modulix.ArchitectureTests` | 52 başarılı |
| `dotnet test tests/Modulix.Core.Tests` | 539 başarılı |
| Crm / Contracts / Logistics / Orders / Support / Marketing birim testleri | başarılı (258 / 146 / 4 / 109 / 145 / 133) |
| Otomasyon entegrasyon testleri (`AutomationRunTests`, `AutomationRuleTests`; mutlu yol, doğrulama, tenant izolasyonu, yetki) ve `SettingsTests` | başarılı |
| Tam entegrasyon paketi (yerel) | Beş test düştü, otomasyon/ayar/yönetim testlerinde hata yok. `QuotePostAcceptTests` (2), `QuoteOpportunityTests`, `EventTests.Self_assignment_and_unassignment_send_no_email` tam pakette düşer, tek tek koşunca geçer (9/9); Dilim 1'de de aynı kararsızlık gözlendi. `TableInventoryTests` yerel veritabanındaki, bu dalda kodu olmayan `platform.ActivityNodes`, `Capabilities`, `ActivityNodeCapabilities`, `CapabilityDependencies` tablolarından düşer (başka bir dalın migration'ından kalma olduğu varsayımı; migration geçmişiyle doğrulanmadı) |

CI (GitHub Actions) bu dilim için çalışmadı: hesap faturalandırma/harcama limiti nedeniyle iş başlamadı. Migration'ın temiz veritabanında uygulanması ve tam paketin CI'da koşması merge öncesi doğrulanmalıdır.

### Kalan işler (Dilim 2a sonrası)

- Dilim 2b: tarama tabanlı tetikleyiciler, `AutomationScanCursors`, `IAutomationRecordSource`, iş olayı tetikleyicisi, çalışma saklama temizliği.
- (Dilim 3a ile kapandı: CRM aksiyon, kayıt kaynağı ve ilişki beyan eder; gerçek veritabanı testleri CI'da doğrulanır.)
- `IFollowUpActivities` idempotency anahtarı Dilim 3a ile eklendi; özel alan olay farkı biçiminin (`custom.<anahtar>`) gerçek outbox yüküyle doğrulanması hâlâ açıktır.

### Dilim 2b-1 (4 Kasım 2026)

Tarama tabanlı tetikleyiciler çalışır (ayrıntı için README "Otomasyon kuralları → Tarama tetikleyicileri"). Yapılanlar:

- **Sözleşme** (`Modulix.Core.Contracts/Automation/AutomationRecordSource.cs`): `IAutomationRecordSource.ScanAsync(AutomationScanQuery)` yalnızca kayıt kimlikleri döndürür (kimliğe göre artan, anahtar tabanlı sayfa); `AutomationEntityDefinition.RecordSource` modülün kaynak tipini bildirir (catalog açılışta tipi doğrular). Core modül tablosu okumaz; koşul, alan yetkisi ve aksiyon yine kuralın kimliğiyle kayıt boru hattından geçer.
- **Tetikleyiciler:** `Scheduled` (tenant saatinde "SS:dd" sonrası, tüm kayıtlar), `DateRelative` (tarih alanı bugün − OffsetDays gününe düşenler; tenant takvim günüyle), `Inactivity` (ilişkide, tenant gününün başlangıcından `Days` gün öncesinden beri hareketi olmayanlar). Kaynağı olmayan kayıt türünde tarama tetikleyicisi yayınlanamaz.
- **Veri:** `core.AutomationScanCursors` (migration `AutomationScanCursors`, RLS): (kural sürümü, tenant günü) başına tek imleç, `UNIQUE(TenantId, RuleVersionId, PeriodKey)`; son kayıt kimliği, kuyruğa alınan sayı, tamamlandı. `AutomationRunOrigin.Scan` (metin olarak saklandığı için ek şema değişikliği gerekmez).
- **Tekilleştirme:** dönem anahtarı tenant takvim günüdür (`scan:yyyy-MM-dd`): aynı gün ikinci tarama aynı kayıtta ikinci çalışma açmaz, ertesi gün açar (tasarım bölümündeki "dönem anahtarı" kararı).
- **Yürütme:** `AutomationScanner`, `AutomationRunner.RunTenantAsync` içinde tenant kilidi ve acil durdurma kontrollerinden sonra, sıradaki çalışmalardan önce çalışır. Sayfa, imleç ve çalışmalar tek `SaveChanges` ile yazılır. Koruma: tur başına `ScanRecordsPerTenantPerRound` (500) kayıt, bekleyen çalışma `ScanBacklogLimit` (1000) olunca duraklama, sayfa `ScanPageSize` (200); kapalı modül ve etkinlik tarihi dışındaki kural atlanır; bozuk kaynak yalnızca kendi kuralını o turda atlatır (loga yalnızca hata türü).

Tasarımdan ayrılan / netleşen noktalar:

- **Artımlı tarama yapılmadı:** ilk tasarım "artımlı" diyordu; günlük tarama tam taramadır çünkü zamana bağlı koşullar ("X gündür bekliyor") kayıt değişmese de doğru olabilir. Maliyet: `Scheduled` kuralı koşulu sağlamayan kayıtlar için de her gün `NotMatched` çalışma satırı yazar; saklama temizliği (2b-2) ve kural yazarının koşulu `DateRelative`/`Inactivity` ile daraltması bunu sınırlar.
- **Kaçırılan günler geriye dönük taranmaz** (işçi bir gün kapalıysa o günün taraması yapılmamış olur).
- Zincirleme: her tarama çalışması yeni bir iz (correlation) ve derinlik 0 ile başlar.

### Test sonuçları (Dilim 2b-1)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Core.Tests --filter Automation` | başarılı (tarama planı, imleç, kaynak beyanı ve doğrulama testleri dahil) |
| `dotnet test tests/Modulix.IntegrationTests --filter AutomationScanTests` (SQL Server) | 10 başarılı (mutlu yol, ikinci tarama/ertesi gün, tarihe bağlı, hareketsizlik, sayfalama ve imleç, bekleyen sınırı, tenant izolasyonu, acil durdurma, bozuk kaynak, yayın izni) |
| Otomasyon, ayarlar ve yönetim entegrasyon testleri (`Automation`, `SettingsTests`, `AdministrationTests`) | 102 başarılı, 1 başarısız: `PlatformAdministrationTests.Every_platform_write_endpoint_has_an_explicit_role_rule` tek başına koşunca `Assert.NotEmpty(endpoints)` satırında düşer (bu dilim platform uçlarına dokunmaz; ana dalda doğrulanmadı) |
| `dotnet test tests/Modulix.ArchitectureTests` | 52 başarılı |
| `dotnet test tests/Modulix.Core.Tests` | 640 başarılı |

Çıkan ders: üç otomasyon test sınıfı süreç genelindeki `AutomationProbe` durumunu (davranış, gecikme) paylaşır; sınıflar paralel koşunca yeniden deneme testleri kararsız düşüyordu. Üçü tek bir sıralı xUnit koleksiyonuna alındı (`AutomationTestCollection`). `TableInventoryTests` yerel veritabanında başka dalların tabloları (`platform.LicensePolicies` vb.) yüzünden düşer; CI temiz veritabanında doğrulanmalı.


### Dilim 2b-2 (5 Kasım 2026)

Planlanan iki işten **saklama temizliği** yapıldı; **iş olayı tetikleyicisi (`DomainEvent`) yapılmadı** ve bilerek Ç0.4'e bağlı bırakıldı.

- **Saklama temizliği** (`AutomationRetention`, `AutomationRunner.CleanTenantAsync`, `AutomationWorker`): bitmiş çalışmalar ve adımları saklama süresinden sonra silinir. Başarılı, iptal ve koşulu sağlanmayan (`NotMatched`) çalışmalar `Automation:RunRetentionDays` (90), başarısız çalışmalar `Automation:FailedRunRetentionDays` (180) gün sonra (tamamlanma zamanına göre). Günlük taramanın yazdığı `NotMatched` satırları da böylece sınırlanır. Bitmemiş (bekleyen, çalışan) çalışmaya hiç dokunulmaz. Eski tarama imleçleri (`UpdatedAt`) aynı süreyle silinir. Önce adımlar, sonra çalışmalar silinir (yabancı anahtar `Restrict`); 500'lük gruplar, tur başına en çok 40 grup, sınır dolarsa sonraki tur devam eder; yarım kalan grup bir sonraki turda tamamlanır.
- **Çalışma biçimi:** işçi `Automation:RetentionIntervalMinutes` (360) aralığıyla her tenant için temizliği, turla **aynı tenant kilidi** altında çalıştırır. Platform acil durdurma anahtarı ve firma otomasyon ayarı temizliği durdurmaz (kapalı otomasyonun eski kayıtları da süresi dolunca silinir). Denetim kaydı (audit) etkilenmez.
- **Veri:** migration `AutomationRunRetentionIndex` yalnızca `IX_AutomationRuns_TenantId_CompletedAt` indeksini ekler (geriye uyumlu, tablo/RLS değişikliği yok).
- **Sınırlama:** temizlik, işçinin turla gezdiği tenant'lar için çalışır (kural yeteneği beyan eden bir modülü açık olanlar); modülü kapatılmış tenant'ın eski çalışmaları modül yeniden açılana kadar kalır.

**`DomainEvent` neden yapılmadı:** iş olayı yayınlama ve olay türü bildirme sözleşmesi (ADR-0007, Ç0.4) yok. Bugün yalnızca beş kayıt olayı üretiliyor, katalog abonelikte `DomainEventTypes.SchemaVersions` dışındaki türü reddediyor ve hiçbir modül iş olayı yayınlamıyor (`crm.opportunity.won` bile `RecordUpdated` durum geçişidir). Yayıncısı olmayan bir tetikleyiciyi açmak, "sessizce hiç tetiklenmeyen kural bırakılmaz" ilkesini bozardı; bu yüzden `DomainEventTrigger` ile yayın 422 vermeye devam eder (taslak saklanabilir). Ç0.4 yayınlama sözleşmesi geldiğinde Dilim 2b-3 şunlarla tamamlanır: aboneliğin olay türlerinin katalogda `AutomationEventDefinition`'lardan genişletilmesi, `AutomationTriggerMatcher`'a iş olayı eşleşmesi, `IsExecutable`'a eklenmesi.

### Test sonuçları (Dilim 2b-2)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.IntegrationTests --filter "AutomationRetentionTests\|AutomationScanTests"` (SQL Server) | 15 başarılı. Yeni 5 test: süresi dolan çalışma ve adımın silinmesi (taze olan kalır, tekrar çalıştırma güvenli), başarısız çalışmanın daha uzun saklanması ve bitmemiş çalışmanın silinmemesi, eski imleçlerin silinmesi, tenant izolasyonu, acil durdurma kapalıyken de temizlik ve bilinmeyen tenant |

Temizliğin API yüzü yoktur (yalnızca arka plan işi); bu yüzden dört test kuralındaki "yetki" ayağı bu dilimde uygulanamaz, yerine "yalnızca bitmiş ve süresi dolmuş satır silinir" sınanır.

### Dilim 3a: CRM yetenek beyanı (5 Kasım 2026)

Dilim 3'ün ilk parçası: CRM kural altyapısına gerçek bir modül olarak katıldı. **Pasif başlangıç şablonları ve demo 3b'ye bırakıldı** (şablon altyapısı yok; şablon motoru Dilim 4'tür, burada yalnızca metadata şablonları olurdu ve ayrı bir tasarım kararı ister).

- **Beyan** (`Modulix.Modules.Crm/Automation/CrmAutomation.cs`): kayıt türleri `crm.lead` ve `crm.opportunity` (okuma izinleri kural yazma izni gibi davranır), dört olay (oluşturuldu / güncellendi), alanlar, iki "Aktiviteler" ilişkisi, kayıt kaynağı ve `crm.follow-up.create` aksiyonu. `IModule.Automation` ile kaydolur; katalog açılışta doğrular.
- **Hassas alanlar:** `amount` ve `probability` hem `SensitiveFields` hem `AutomationFieldDefinition.ReadPermission` olarak `crm.opportunity-amounts.read` taşır (Dilim 2 notunun karşılığı); birim/entegrasyon testi iki beyanın uyumunu denetler.
- **Kayıt kaynağı** (`CrmAutomationRecordSource`, `Records/` altında: mimari kural `IModuleData`'yı yalnızca `Records` klasörüne izin verir): yalnızca kayıt kimliği döndürür; zamanlanmış ve hareketsizlik taramaları açık kayıtlarla sınırlıdır (kapanmış kayıtlar için günlük `NotMatched` satırı yazmamak için), tarihe bağlı tarama durumdan bağımsızdır. Tarih sütunlarının tek kaynağı `CrmAutomation.*DateColumns` tablosudur; beyanla ayrışmaz (birim test).
- **İlişki çözümleyici** (`CrmActivityRelationResolver`): doğrudan sütun ve `ActivityLink` ile bağlı aktiviteleri tek sorguyla sayar, özel aktiviteleri saymaz. `Where` (bugün yalnızca aktivite durumu) durum kümesine çevrilir: VE kesişim, VEYA birleşim.
- **İdempotency (Dilim 2 notunun kapanışı):** `FollowUpRequest.IdempotencyKey` (isteğe bağlı, geriye uyumlu) ve `FollowUpResult.AlreadyExisted`. Yeni tablo ya da migration **gerekmedi**: aktivitede zaten `(TenantId, ExternalSource, ExternalId)` filtreli benzersiz indeksi vardı; anahtar `externalSource = "automation"` ile yazılır. Sistem alanları iç çağrı için iş akışı kapısı açılarak yazılır. Eşzamanlı iki çağrıdan kaybeden, kazananın aktivitesini bulur.

Tasarımdan ayrılan / netleşen noktalar:

- **Atanan kullanıcı parametresine alan bağlanamaz** (`AllowFieldBinding = false`): doğrulayıcı lookup alanını parametreyle aynı lookup anahtarında ister; müşteri adayı ve fırsatın sorumlu alanları farklı anahtarlara sahip. Varsayılan zaten kaydın sorumlusudur (işleyici bunu kayıt boru hattından okur), bu yüzden bağlamaya gerek kalmadı. Core doğrulayıcısı değiştirilmedi.
- **Gün sayısı sınırı yayında değil çalışmada denetlenir:** parametre şemasında aralık yok; `dueInDays` 0-365 dışındaysa adım kalıcı hata verir ve neden yazılır. Şemaya aralık eklemek Core değişikliğidir, sonraya bırakıldı.
- **Doğrulama dersi:** katalog beyanı açılışta doğruladığı için hatalı bir beyan uygulamayı hiç açtırmaz. İlk denemede lookup parametresi lookup anahtarsız beyan edildiği için bütün entegrasyon testleri `InvalidOperationException` ile düştü. Bu yüzden gerçek katalogla beyanı kuran bir test eklendi (`CrmAutomationDeclarationTests`, veritabanı gerektirmez).

### Test sonuçları (Dilim 3a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Modules.Crm.Tests` | 280 başarılı (aksiyon doğrulaması, dry-run, tarih sütunu / beyan tutarlılığı dahil) |
| `dotnet test tests/Modulix.IntegrationTests --filter CrmAutomationDeclarationTests` | 3 başarılı (gerçek katalog, hassas alan izinleri, aksiyon tanımı) |
| `dotnet test tests/Modulix.ArchitectureTests` | 52 başarılı |
| `dotnet test tests/Modulix.IntegrationTests --filter CrmAutomationTests` (SQL Server) | **Yerelde doğrulanamadı.** Yerel paylaşımlı veritabanında `{}` içerikli yayınlanmış kural paketi sürümleri var; kayıt oluşturan **her** test (mevcut `LeadTests` dahil) `FormatException: Kural listesi çözülemedi` ile düşüyor. Testler (hareketsizlik, tarihe kala, ilişki koşulu, tekrar teslimde tek aktivite, doğrulama, tenant izolasyonu, yetki) CI'da temiz veritabanında koşar |

### Dilim 3b: Başlangıç şablonları (5 Kasım 2026)

3a'da "ayrı bir tasarım kararı ister" diye bırakılan şablon altyapısı karara bağlandı: **şablon kodda beyan edilir, tabloda saklanmaz.**

- **Karar:** `AutomationCapabilities.Templates` (`AutomationTemplateDefinition`: anahtar, ad, açıklama, kayıt türü, tanım). Şablon tenant'tan bağımsız sabittir (tek imaj, müşteriye özel kod yok); tenant'a ait olan yalnızca onu **kullanınca** oluşan kuraldır. Bu yüzden yeni tablo, migration ya da RLS politikası yoktur. Bu dilimde kararlaştırılan: planlanan `AutomationTemplates` tablosu (firmanın kendi şablonlarını kaydetmesi) **şimdilik yoktur**; "kuralı şablon olarak kaydet" ihtiyacı doğarsa ayrı bir dilimdir ve bu beyan yolunu bozmaz.
- **Pasiflik güvencesi yapıdadır:** "kullan" yalnızca `IAutomationRuleService.CreateAsync` çağırır, yani **taslak** kural açar. Taslağı hiçbir yol çalıştırmaz (tarayıcı ve olay aboneliği yalnızca yayınlanmış, açık kuralları işler). Yayın ve açma ayrı izinle (`core.automation.publish`) firmanın kararıdır; şablonun kendi başına bir şey açan yolu yoktur. Denetim kaydı kural oluşturma yolundan gelir.
- **Katalog denetimi açılışta (yapı):** anahtar modül önekli ve tekil, ad ve açıklama dolu, kayıt türü modülün, en az bir aksiyon, adım kimlikleri tekil, aksiyon kayıtlı. Alan/koşul/izin ayrıntısı **kullanan kimliğin yetkisiyle** `AutomationDefinitionValidator` ile doğrulanır; aksiyon izni olmayan için şablon "kullanılamıyor" görünür ve taslağı açılmaz (yetki yükseltme yok).
- **Uç noktalar:** `GET /api/automation/templates` (`core.automation.read`; modülü bu firmada kapalı şablonlar listede yoktur, kullanılamayanlar `issues` ile gelir) ve `POST /api/automation/templates/{key}/use` (`core.automation.manage`; bilinmeyen / modülü kapalı şablon 404, kullanılamayan 400, başarıda 201 + taslak kural).
- **CRM şablonları** (`CrmAutomationTemplates`, hepsi yalnızca takip aktivitesi açar; mesaj göndermez, kayıt değiştirmez): hareketsiz müşteri adayı (7 gün), hareketsiz fırsat (14 gün), kapanış tarihine 3 gün kalan açık fırsat, satın alma tarihine 7 gün kalan açık müşteri adayı.
- **Demo** bu dilimde yoktur: gerçek veride toplu otomasyon kapsam dışıdır, şablon taslak teslim edilir. Demo ancak ekran (Dilim 5) ile birlikte anlamlıdır.

### Test sonuçları (Dilim 3b)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Core.Tests --filter Automation` | 138 başarılı (yeni: şablon katalog denetimi, anahtar/ad/açıklama/kayıt türü/aksiyon/adım kimliği/tekillik) |
| `dotnet test tests/Modulix.Modules.Crm.Tests --filter CrmAutomationTemplateTests` | 4 başarılı (şablonlar beyanla tutarlı, yalnızca takip aktivitesi, JSON gidiş-dönüş) |
| `dotnet test tests/Modulix.ArchitectureTests` | 65 başarılı |
| `dotnet test tests/Modulix.IntegrationTests --filter "CrmAutomationDeclarationTests|CrmAutomationTemplateTests"` (SQL Server) | 9 başarılı. Yeni 4: taslak açılır, çalışmaz, yayınlanınca çalışır; dört şablon gerçek yayın doğrulamasından geçer; bilinmeyen ve modülü kapalı şablon 404; tenant izolasyonu; okuma/yönetim izni ve aksiyon izni olmayanın şablonu kullanamaması |
| `dotnet test tests/Modulix.IntegrationTests --filter CrmAutomationTests` (SQL Server) | 8 başarılı (3a testleri bozulmadı) |

### Dilim 5a: Kural listesi, şablonlar, kural ayrıntısı (5 Kasım 2026)

Dilim 5 (Blazor ekranları) üç parçaya bölündü; ilk parça salt okuma ağırlıklı ve yaşam döngüsü işlemlerini kapsar:

| Parça | Kapsam | Durum |
| --- | --- | --- |
| 5a | Kural listesi, şablon seçimi, kural ayrıntısı (yayınla / aç-kapat / taslağı at) | Uygulandı |
| 5b | Çalışma geçmişi ve elle müdahale (yeniden dene, iptal; elle çalıştırma ertelendi) | Uygulandı ("Dilim 5b") |
| 5c | Kural düzenleyici (tetikleyici, koşul, aksiyon parametreleri, önizleme) | İlk sürüm uygulandı ("Dilim 5c") |

- **Ekranlar** (Yönetim > Otomasyon, `core.automation.read`): `/yonetim/otomasyon` (liste: durum görünümü, arama, sayfalama; yeni kural yalnızca şablondan), `/yonetim/otomasyon/sablonlar` ("Kullan" taslak açar, `core.automation.manage`), `/yonetim/otomasyon/{id}` (taslak ve yayınlanmış sürüm, sunucunun ürettiği okunabilir özet, doğrulama sorunları; "Yayınla (kapalı kalsın)", "Yayınla ve aç", "Aç / Kapat", "Taslağı at"; yayın ve açma `core.automation.publish`).
- **Tasarım:** yeni CSS, token ya da bileşen yok; mevcut `PageHeader`, `ViewSelector`, `record-table`, `FormSection`, `Pager`, onay şeridi (`alert confirm`) kullanıldı. Durum rozeti: Açık yeşil, Kapalı nötr, Taslak bilgi tonu. Bu ekranlar kayıt formu değil yönetim ekranı olduğu için sekmeli form kuralının dışındadır.
- **Güvenlik tutumu:** "Yayınla ve aç" onay şeridi ister (kural açılınca gerçek kayıtlarda işlem yapar); doğrulama sorunu olan taslakta yayın düğmeleri kapalıdır; sürüm çakışmasında (`ExpectedVersion`) güncel hal yeniden yüklenir. Düğmeleri gizlemek yetki değildir; her işlem sunucuda yeniden denetlenir.
- **Doğrulanamayan:** ekranlar bu oturumda **tarayıcıda gözle kontrol edilemedi** (Browser paneli gizli kaldığı için Blazor sayfası çizilmedi; açık/koyu tema ve telefon düzeni bakılmadı). API uçları gerçek uygulamada oturum açılarak doğrulandı (şablon listesi ve boş kural listesi dönüyor). Birleştirmeden önce açık ve koyu temada bir göz atılmalı.

### Test sonuçları (Dilim 5a)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Web.Tests` | 23 başarılı (yeni 4: menü girdisi okuma iznine bağlı, durum adları ve rozet tonları, davranış metni, kayıt türü adı) |
| `dotnet test tests/Modulix.ArchitectureTests` | 65 başarılı |
| `dotnet build src/Modulix.Web` / Release `src/Modulix.Api` | 0 hata |

Ekranların kullandığı uçların dört kural testi (mutlu yol, hata, tenant izolasyonu, yetki) Dilim 1 ve 3b'de entegrasyon testleriyle kapsanmıştır; bu dilim yeni sunucu davranışı eklemez.

### Dilim 5b: Çalışma geçmişi, yeniden deneme, iptal (6 Kasım 2026)

- **Ekranlar:** `/yonetim/otomasyon/calismalar` (liste: durum görünümü, `?kural=` ile tek kurala süzme, sayfalama; `core.automation-logs.read`) ve `/yonetim/otomasyon/calismalar/{id}` (çalışma bilgisi, adım tablosu: sıra, adım, işlem adı, durum, deneme, sonuç özeti). Girişler kural listesindeki "Çalışma geçmişi" ve kural ayrıntısındaki "Çalışmaları" bağlantılarıdır.
- **Elle müdahale:** "Yeniden dene" yalnızca `Failed` çalışmada, "İptal et" yalnızca `Pending`/`Running` çalışmada görünür (`AutomationLabels.CanRetry/CanCancel`; geçersiz durumu sunucu zaten reddeder). İptal onay şeridi ister: tamamlanmış adımların etkisi geri alınmaz. İkisi `core.automation.run` ister; düğmeleri gizlemek yetki değildir.
- **Ertelenen:** "Kural için elle çalıştır" yapılmadı; bir kayıt seçmeyi gerektirir (kayıt kimliği yazdırmak kullanışsız ve hataya açık) ve ortak bir kayıt seçici henüz yok. Kural düzenleyiciyle (5c) birlikte ele alınacak.
- **Gizlilik:** ekranlar yalnızca sunucunun döndürdüğü durum, özet ve kimlikleri gösterir; çalışma kaydı kayıt değeri ya da parametre taşımaz.
- **Tasarım:** yeni CSS, token ya da bileşen yok (`PageHeader`, `ViewSelector`, `record-table`, `FormSection`, `Pager`, `chip active`, onay şeridi). Durum tonları: Başarılı yeşil, Başarısız kırmızı, Çalışıyor bilgi, İptal çizgili nötr, Bekliyor ve Koşul sağlanmadı nötr.
- **Sınırlama:** sol menü girdisi `core.automation.read` ister; yalnızca `core.automation-logs.read` olan kullanıcı menüden değil doğrudan adresten girer.
- **Doğrulanamayan:** 5a gibi, ekranlar bu oturumda tarayıcıda gözle kontrol edilmedi (açık/koyu tema, telefon).

### Test sonuçları (Dilim 5b)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Web.Tests` | Yeni 4: çalışma durumu adları ve tonları, yeniden deneme/iptal yalnızca uygun durumda, kaynak ve adım durumu adları, etiketsiz durum kalmaması |

Uçların dört kural testi Dilim 2'de entegrasyon testleriyle kapsanmıştır; bu dilim yeni sunucu davranışı eklemez.

### Dilim 4a: Genel onay ve Onay merkezi (7 Kasım 2026)

Ç6a'nın asgari hali; lojistik planındaki (ADR-0038 taslağı) masraf onayının ön koşuludur. Mevcut teklif ve sözleşme onayları değişmedi.

- **Veri:** `core.ApprovalDefinitions` (adımlar JSON, `Revision`), `core.ApprovalRequests`, `core.ApprovalRequestSteps`; üçü de `ICompanyShared`, RLS altında. ADR'deki `ApprovalRequests/Steps` adları korundu; ayrı sürüm tablosu kurulmadı, çünkü talep açılırken adımlar satır olarak kopyalanır (Ç6a.2 anlık görüntüsü budur).
- **Tanım:** firma kendi süreçlerini tanımlar. Adımda kişi ya da rol (rolün doğrudan üyelerinden biri yeter; departmandan gelen rol sayılmaz), tutar eşiği, süre. Aynı sıra numarası paralel çalışır. Eşikler sürecin para birimindedir, kur çevrilmez.
- **Talep:** başlık ve tutar değişmez; değişecekse geri çekilip yeniden açılır (Ç6a.2 "tutar değişirse onay geçersizleşir" böyle sağlanır). Bütün adımlar eşik altında kalırsa talep kendiliğinden onaylanır.
- **`ApprovalFlow` kullanılmadı:** o hesap rol onaycıyı ifade edemez ve onayları olay listesinden türetir; burada adım satırı durumu taşır.
- **Dört göz:** talep sahibi kendi talebine karar veremez; açılışta her adımda talep sahibinden başka etkin onaycı aranır, yoksa talep açılmaz.
- **Eşzamanlılık:** `Version` belirteci; aynı adıma iki karar gelirse biri 409 alır.
- **Sahipsiz adım (Ç6a.1):** onaycısı ayrılan etkin adım panoda görünür, `core.approvals.manage` onaycıyı değiştirir. Destek üyeliği etkin onaycı sayılmaz (ADR-0028).
- **İzinler:** `core.approvals.request`, `.decide`, `.manage`. Karar için izin yetmez, kişi etkin adımın onaycısı olmalıdır. `core.automation.approve` kural kaynaklı onaylar (4c) için ayrılmış kalır. Yeni izinler mevcut rollere otomatik verilmez; yönetici rollere ekler.
- **Görünürlük:** talep sahibi, onaycı ve karar verenler görür; `manage` hepsini görür. Sorgu seviyesinde uygulanır; görünmeyen talep 404 döner.
- **Bildirim:** sıradaki onaycılara ve sonuçta talep sahibine e-posta, kayıtla aynı işlemde (`IEmailQueue`).
- **Arayüz:** `/onaylar` (pano bandı, grafikler, sekmeli liste, çekmecede adım izi ve karar), `/yonetim/onay-surecleri`. Yeni token `--mx-gradient-hero` ve `hero-band`, `step-track` sınıfları tasarım sistemine işlendi.

**Bilerek yapılmadı:** süre aşımında eskalasyon (geciken talep yalnızca görünür), genel vekâlet, uygulama içi bildirim, kayda bağlı onay ve onay sonucunun hedef modül komutuna girdi olması (Ç6a.3), olay yayını (Ç0.4 bekler).

### Dilim 5c: Kural düzenleyici, ilk sürüm (7 Kasım 2026)

- **Ekranlar** (`core.automation.manage`): `/yonetim/otomasyon/yeni` ("Sıfırdan kural") ve `/yonetim/otomasyon/{id}/duzenle` ("Düzenle"). Bölümler: Genel (ad, açıklama, öncelik, "başka kural çalışmasın"), Ne zaman (tetikleyici türü ve parametreleri), Hangi koşulda (düz koşul listesi, hepsi / herhangi biri), Ne yapar (sıralı aksiyon adımları; yukarı/aşağı), Doğrulama.
- **Sunucu tek doğruluk kaynağıdır:** düzenleyici alan, operatör ve izin kurallarını yeniden yazmaz. Seçenekler katalogdan gelir (alanın operatörleri, seçim seçenekleri, aksiyon parametre tipleri); "Doğrula" `POST /api/automation/preview` ile tanımı yan etkisiz denetler. Kayıt `POST /rules` / `PUT /rules/{id}` ile **yalnızca taslak** yazar, eksik taslak kaydedilebilir (tam doğrulama yayında). Yayınlanmış kuralı düzenlemek yeni taslak açar; sürüm çakışmasında kullanıcıya iletilir.
- **Düzenleyicinin gösteremediği parçalar silinmez:** iç içe grup ve ilişki koşulu, iş olayı tetikleyicisi ve `{"$field": …}` alan bağlamalı parametre ham haliyle korunur (koşul için "kaldırıp yeniden kur", bağlama için "sabit değere çevir" bilinçli eylemi vardır). Tek yaprak koşul kayıtta tek öğeli grup olarak yazılır (anlamı aynı).
- **Web tarafı:** Web yalnızca `Modulix.Shared`'e başvurduğu için tanım `JsonNode` ile okunur ve yazılır (`AutomationEditorModel.cs`). Biçimin sunucunun tipli modeline çözüldüğü `AutomationEditorJsonShapeTests` (Core.Tests) ile, düzenleyicinin tam bu metni ürettiği `AutomationEditorModelTests` (Web.Tests) ile birlikte sabitlenir.
- **Bu sürümde yok (sonraki parçalar):** kayıt üzerinde deneme (kayıt seçici gerekir; elle çalıştırma ile birlikte), iç içe grup ve ilişki koşulu kurma, parametreye kayıt alanı bağlama, geçerlilik tarihleri (var olan değer korunur, değiştirilemez), lookup parametreleri için seçici (kimlik elle girilir), iş olayı tetikleyicisi (Ç0.4 bekler). Tarama tetikleyicilerini desteklemeyen kayıt türünde seçim engellenmez; sunucu "Doğrula"da bildirir.
- **Doğrulanamayan:** ekranlar tarayıcıda gözle kontrol edilmedi (açık/koyu tema, telefon); gerçek API ile uçtan uca kayıt denenmedi, biçim uyumu yukarıdaki iki testle sabitlendi.

### Test sonuçları (Dilim 5c)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Web.Tests` | Yeni 12 (`AutomationEditorModelTests`): gidiş-dönüş, tek yaprak, düz OR grubu ve eski değer, iç içe/ilişki koşulunun korunması, iş olayı tetikleyicisinin korunması, tetikleyici parametreleri, aksiyon parametre tipleri, bağlama ve bilinmeyen parametrenin korunması, adım kimliği, operatör adları, tetikleyici adları, sıfırdan kurulan kuralın sunucu biçimi |
| `dotnet test tests/Modulix.Core.Tests --filter AutomationEditorJsonShapeTests` | Yeni 7: düzenleyicinin ürettiği biçim ve her tetikleyici türü tipli modele çözülür |

Uçların dört kural testi (mutlu yol, hata, tenant izolasyonu, yetki) Dilim 1'de entegrasyon testleriyle kapsanmıştır; bu dilim yeni sunucu davranışı eklemez.

### Dilim 4b: Bildirim kutusu, süre aşımı, vekâlet (7 Kasım 2026)

- **Bildirim kutusu (Ç5a):** `core.Notifications`, `INotificationInbox` (Core.Contracts; kayıtla aynı işlemde yazar), `/api/notifications`, üst çubukta `NotificationBell`. `UNIQUE(TenantId, UserId, DedupeKey)` aynı olayın ikinci bildirimini engeller. Metin kaydın ayrıntısını taşımaz (süreç adı var, talep başlığı ve tutarı yok); bağlantı açılınca yetki denetlenir, böylece erişimi kalkan kişi hassas metin görmez. İzin gerekmez, herkes yalnızca kendi bildirimini görür.
- **Süre aşımı:** adımda "Süre aşılınca" kişi ya da rol seçilir. `ApprovalEscalationWorker` 5 dakikada bir `DueAt` geçen etkin adımları işler (`Approvals:WorkerEnabled`). Eskalasyon onaycısı devralmaz, ek onaycı olur; ilk onaycı karar vermeye devam eder. Hedef yoksa onaycılara yalnızca hatırlatma gider. `EscalatedAt` adımı bir kez işletir. Denetimde `core.approval-escalation` kaynağıyla görünür. Süre takvim saatidir; iş günü hesabı 4d ile gelir.
- **Vekâlet:** `core.ApprovalDelegations`; kişi kendi vekâletini açar ve geri alır, yönetici başkası adına da yapabilir. Tarihler çakışamaz, en çok 365 gün, zincirlenmez. Vekil kişiye atanmış adımlara karar verir, karar `OnBehalfOf` ile yazılır. Role atanmış adımlar vekâlete girmez. Dört göz vekâletle aşılamaz: talep sahibi vekil olsa da kendi talebine karar veremez, talep sahibinin vekili de onun adına karar veremez. CRM teklif vekâleti (`QuoteDelegation`) ayrı kalır.

**Bilerek yapılmadı:** bildirim saklama temizliği, modüllerin kutuya bağlanması (yalnızca onay yazıyor), kişisel bildirim tercihi (Ç5b), iş günü takvimi.

### Dilim 4d: Metin değişkenleri, ortak şablon doğrulayıcı (7 Kasım 2026)

Dilim 4'ün "şablon motoru" kalemi, onay merkezinden (4a) bağımsız ilk parça olarak uygulandı: kural aksiyonunun metin parametresine kayıt değişkeni.

- **Biçim:** `{{alan.anahtarı}}` (`AutomationTextTemplate`, Core). İfade, işlev, koşul, iç içe şablon ve kaçış yoktur; geçersiz ya da kapanmamış `{{ }}` yayında hata.
- **Beyaz liste (yayın):** değişken kayıt türünün beyan edilmiş alanı olmalı, yayınlayan alanı görebilmeli (`ReadPermission`), alan kayıt seçimi ya da tarih-saat olmamalı (metne anlamlı çevrilemez). Parametre `AllowFieldBinding = false` ise değişken kabul etmez. Hepsi `AutomationDefinitionValidator.ValidateParameters` içinde, mevcut doğrulama sorunlarıyla aynı biçimde (422 + `issues`).
- **Çalışma ve önizleme aynı kodu kullanır** (`AutomationParameterResolver.Resolve`, artık aksiyon tanımını da alır): seçim etiketi, Türkçe sayı ve para, Evet/Hayır, `gg.AA.yyyy`; boş ya da görülemeyen değer boş metin + not (değer notta asla görünmez); parametrenin `MaxLength`'ini aşan metin kısaltılır ve not düşer, böylece uzun bir müşteri adı adımı düşürmez; zorunlu metin boş kalırsa mevcut davranışla adım atlanır.
- **Enjeksiyon yok:** değer tek geçişte yerleştirilir, değerin içindeki `{{…}}` tekrar yorumlanmaz. `Render(..., encodeHtml: true)` yerleştirilen değerleri `WebUtility.HtmlEncode` ile kodlar; bugün HTML'e giren bir aksiyon olmadığı için yalnızca motor seviyesinde vardır ve testlidir (CLAUDE.md: HTML'e giren kullanıcı girdisi kodlanır).
- **Yetki yükseltme yok:** `AutomationTriggerMatcher` kuralın kimliğinin sahip olması gereken izinleri hesaplarken değişkendeki alanların `ReadPermission`'ını da ekler; yayından sonra kimlik izni kaybederse alan boş kalır ve notta "görülemiyor" yazar.
- **CRM beyanı:** takip aktivitesinin `subject` ve `description` parametreleri kayıt verisine açıldı (`AllowFieldBinding = false` kaldırıldı); müşteri adayı `subject` ve fırsat `title` metin alanları beyan edildi (koşulda `Contains`/`StartsWith` için de kullanılabilir). Dört hazır şablonun konu ve açıklaması da değişkenli: `Müşteri adayıyla yeniden iletişime geçin: {{crm.lead.subject}}`, `Fırsatı gözden geçirin: {{crm.opportunity.title}}`, kapanış ve satın alma tarihi hatırlatmalarında tarih de metne girer (`{{crm.opportunity.expectedCloseDate}}` → `07.11.2026`). Şablonlar yalnızca izin gerektirmeyen alanları kullanır (CRM birim testi denetler), böylece şablonu kullanan herkes yayınlayabilir. Bu değişiklik yalnızca bundan sonra "Kullan" denen şablonları etkiler; açılmış taslak ve yayınlı kurallar olduğu gibi kalır.
- **Arayüz:** editörde metin parametresinin altında "Kayıt değişkeni ekle" seçicisi (`AutomationParameterDto.AllowFieldBinding` ile yalnızca uygun parametrede); sunucu API'sine tek alan eklendi (geriye uyumlu, varsayılan `true`).
- **Bilinen sınır:** daha önce yayınlanmış bir kuralda sabit metin olarak `{{x}}` yazıldıysa artık değişken sayılır (bu davranış yeni; böyle bir kural bilinmiyor, yayınlı kural yeniden yayında doğrulanır). Başka modüller kendi aksiyonlarında `AllowFieldBinding`'i açarak aynı motoru kullanır.

### Test sonuçları (Dilim 4d)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Core.Tests --filter "AutomationTextTemplateTests"` | Yeni testler: değişken ayrıştırma, hatalı biçimler, alan uygunluğu ve izin, tip bazlı biçimleme, boş/görülemeyen değer ve notta sızıntı yok, enjeksiyon yok, HTML kodlama, çözümleyici (doldurma, bağlanamayan parametre, kısaltma, boş konu), yayın doğrulaması (kabul, ret, yetkisiz alan, bağlanamayan parametre) |
| `dotnet test tests/Modulix.IntegrationTests --filter CrmAutomationTests` (SQL Server) | Yeni 4: mutlu yol (konu `Takip: Sessiz aday` olarak açılır), hata (bilinmeyen, kapanmayan, kayıt seçimi, başka kayıt türünün alanı), tenant izolasyonu (her firma kendi kaydından doldurur), yetki (tutar izni olmayan tutar değişkeni yayınlayamaz) |

### Dilim 5d: Kayıt seçici, kayıt üzerinde deneme, elle çalıştırma (7 Kasım 2026)

Dilim 5b'de ertelenen "elle çalıştırma" ve 5c'de eksik kalan "kayıt üzerinde deneme" için ortak bir kayıt seçici kuruldu.

- **`RecordPicker` (Web bileşeni):** bir kayıt türünden tek kayıt seçer. Arama, sayfalama, izin ve veri kapsamı **sunucudadır**: genel kayıt API'si (`GET /api/records/{tür}?search=&page=&pageSize=`) ve kayıt boru hattı kullanılır; kullanıcının göremediği kayıt listede çıkmaz, yeni uç nokta yoktur. ADR-0016 lookup'ından farkı: bir alanın referansı değil kaydın kendisi seçilir; lookup tanımı (kaynak alan, firma ayarı, yönetim ekranı) gerekmez, böylece "Alan ve lookup ayarları" ekranı kirlenmez. Görünüm ve klavye (ok tuşları, Enter, Esc, `aria-activedescendant`) mevcut `lookup-*` sınıflarıyla; yeni CSS yok. Eski aramanın yanıtı yeni aramayı ezmez (sıra numarası), yazarken 300 ms bekler.
- **Etiket alanı:** kayıt türü beyanına `AutomationEntityDefinition.LabelField` eklendi (CRM: müşteri adayı `subject`, fırsat `title`). Beyan edilmiş bir **metin** alanı olmalı (açılışta doğrulanır); katalog (`AutomationEntityDto.LabelField`, geriye uyumlu, varsayılan `null`) alanı, kullanıcı onu okuyabiliyorsa verir (alan yetkisi atlanmaz). Etiket yoksa ya da bu kayıtta boşsa seçici kısa kimliği (`Kayıt 3f9a1c2e`) gösterir.
- **Kayıt üzerinde deneme:** kural düzenleyicinin "Doğrulama" bölümünde isteğe bağlı kayıt seçilir; `POST /api/automation/preview` kayıtla çağrılır. Gösterilenler: koşulların sağlanıp sağlanmadığı, koşul ağacının neden açıklaması, kural çalışsaydı yapacakları (parametreler çözülmüş: `Takip: Anka Lojistik`). Hiçbir şey çalıştırılmaz.
- **Elle çalıştırma:** kural ayrıntısında, **açık** ve tetikleyicisi **elle** olan kuralda ("Elle çalıştır", `core.automation.run`) kayıt seçilir, onay şeridinden sonra `POST /rules/{id}/run` çağrılır ve çalışma ayrıntısına gidilir. Kural elle değilse, kapalıysa ya da kayıt okunamıyorsa sunucu reddeder (düğmenin gizlenmesi yetki değildir); aynı kayıt için bugün zaten çalıştırıldıysa 409 iletisi gösterilir.
- **Bilinen sınırlar:** seçici yalnızca etiket alanı ve kısa kimlikle tanıtır (ikincil bilgi, ör. durum, yok); kayıt türüne özel arama alanı kayıt deposunun `search` davranışına bağlıdır; tek kayıt seçilir (çoklu seçim, toplu çalıştırma yok); ekran tarayıcıda gözle kontrol edilmedi.

### Test sonuçları (Dilim 5d)

| Komut | Sonuç |
| --- | --- |
| `dotnet test tests/Modulix.Core.Tests --filter AutomationLabelFieldTests` | Yeni 4: metin alanı etiket olur, etiketsiz tür, geçersiz etiket (bilinmeyen/metin olmayan/başka türün alanı) açılışta reddedilir, okunamayan etiket alanı katalogda verilmez |
| `dotnet test tests/Modulix.Modules.Crm.Tests --filter CrmAutomationTemplateTests` | Yeni: her kural yazılabilen CRM türü beyan edilmiş, izin gerektirmeyen bir metin alanını etiket olarak adlandırır |
| `dotnet test tests/Modulix.Web.Tests` | Yeni 4 (`RecordPickerLabelsTests`): etiket alanı metni, eksik/boş/metin olmayan alanda kısa kimlik, etiketsiz tür, eski katalog yanıtı |
| `dotnet test tests/Modulix.IntegrationTests --filter CrmAutomationTests` (SQL Server) | Yeni 3: mutlu yol (katalog etiket alanı, aramayla bulma, daraltma), tenant izolasyonu (başka firmanın kaydı çıkmaz), yetki (müşteri adayı okuma izni olmayan türü ve kayıtları göremez, liste 403) |

Elle çalıştırmanın kendi dört kural testi (mutlu yol, hata, tenant izolasyonu, yetki) Dilim 2'de entegrasyon testleriyle kapsanmıştır; bu dilim yeni sunucu davranışı eklemez (yalnızca katalogda etiket alanı).
