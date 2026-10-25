# ADR-0007: Olay altyapısı

- **Durum:** Taslak, 25 Ekim 2026
- **Karar:** Outbox olayları iki adımda işlenir: tek bir dağıtıcı her olayı ilgili abonelere "teslim" satırı olarak dağıtır, teslimler abone başına ayrı ayrı, olayın tenant'ı bağlamında, en az bir kez ve aynı kaydın olayları sırayla işlenir. Abonelik `IModule` üzerinden tek satırla bildirilir, abone sözleşmesi `Modulix.Core.Contracts`'tadır

## Problem

Yol haritasındaki Ç1 (Olay altyapısı ve güvenilir teslim): modüller arası her akış (fırsat kazanıldı → sevkiyat taslağı, sevkiyat teslim edildi → fatura taslağı), bildirim (Ç5), otomasyon (Ç6) ve webhook (Ç11a.4) olaylara dayanır. K5 gereği modül başka modülün tablosuna yazamaz; tepkiyi olay ve sözleşmeyle verir.

Bugünkü durum:

- Kayıt boru hattı (`RecordPipeline`) her oluşturma, güncelleme ve silmede `IDomainEventOutbox.Add(new DomainEvent(TenantId, EventType, EntityKey, RecordId, Payload))` çağırır. Olay, kaydın kendisi ve denetim kaydıyla aynı `SaveChanges` içinde yazılır; kayıt geri alınırsa olay da yazılmaz (Ç1.1'in ana şartı zaten sağlanıyor).
- Olay türleri `DomainEventTypes`: `RecordCreated`, `RecordUpdated`, `RecordDeleted`, `StatusChanged` (sonuncusu hiçbir yerde üretilmiyor). Payload, değişen alanların `{ alan: { old, new } }` farkıdır (özel alanlar düzleştirilmiş; oluşturmada `old`, silmede `new` boş).
- Tablo `platform.OutboxMessages`: `Id` (UUIDv7), `TenantId`, `EventType`, `EntityKey`, `RecordId`, `Payload`, `OccurredAt`, `ProcessedAt`, `AttemptCount`, `LastError`; indeks (`ProcessedAt`, `OccurredAt`). Platform şemasındadır, RLS dışındadır; işleyici her olayı o tenant'ın bağlamında işlemelidir (ADR-0002).
- Olayı okuyan bir işleyici **yok** (kodda "E12" diye anılıyor). Zarfta Ç1.1'in istediği sürüm, sıra numarası, işlemi yapan kişi, kaynak ve correlation/causation bilgisi yok; denetim kaydında olan kişi ve correlation olaya yazılmıyor.
- Benzer kalıp: e-posta kuyruğu (`platform.EmailMessages`, `EmailDispatcher`, `EmailDispatchWorker`). Her 10 saniyede zamanı gelmiş iletileri seçer, her birini koşullu `UPDATE` ile kiralar (`NextAttemptAt` 5 dakika ileri, `AttemptCount + 1`; etkilenen satır 1 ise ileti bu örneğindir), 1 dk / 5 dk / 15 dk / 1 sa arayla yeniden dener, sınırda `Failed` olur. Birden fazla uygulama örneğinde her ileti tek örnekçe alınır.
- Arka plandan tenant verisine erişim kalıbı: `ITenantScopeFactory.Create(new TenantContext(tenantId, userId, membershipId))` yeni bir DI kapsamı ve bağlantı açar, RLS oturum değeri bağlantı açılırken yazılır. Platform yönetimi bunu `UserId = Guid.Empty` ile zaten kullanıyor.
- Kayıt sürümü (`Version`) her değişiklikte bir artan, eşzamanlılık belirteci olan bir sayıdır. Aynı kaydın iki değişikliği birbirini beklemek zorundadır (409), yani bir kaydın sürümleri yazılış sırasını verir.

Yol haritasındaki K3 kararı, modüller arası her yeni sözleşme için ADR ister.

## Seçenekler

### Teslim modeli

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Olay başına tek durum (`ProcessedAt`, `AttemptCount` bugünkü gibi) | Tablo hazır; basit | Bir abonenin hatası olayı bütün aboneler için yeniden çalıştırır; başarılı abone ikinci kez çalışır. Ç1.3'ün kabul ölçütü ("yalnızca başarısız abone yeniden çalışır") sağlanamaz |
| Dağıtım + abone başına teslim satırı (seçildi) | Hata ve yeniden deneme abone başına; `(tenant, abone, olay)` tekilliği inbox görevi görür; teslim ve iş etkisi aynı işlemde yazılabilir | İkinci tablo, iki adımlı işleyici; olay başına abone sayısı kadar satır |
| Boru hattı yazarken teslim satırlarını da yazar (dağıtım adımı yok) | Tek adım, gecikme az | Boru hattı aboneleri bilmek zorunda; her kayıt yazımı abone sayısı kadar ek satır; modül o an kapalıysa sonradan açılınca karar verilemez |
| Dış ileti aracısı (RabbitMQ, Azure Service Bus) | Ölçeklenir, hazır yeniden deneme | Yeni altyapı ve işletim yükü (K11: modüler monolit); outbox yine gerekir; tenant bağlamı ve RLS kuralları aracıda yok |

### Çok örnekte tek işleme

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Koşullu `UPDATE` ile kira + kira belirteci (seçildi, teslimlerde) | E-posta kuyruğuyla aynı kalıp; kilit satır başına; çöken örneğin işi kira bitince başkasına geçer | Kira süresinden uzun süren abone için kira uzatma (heartbeat) gerekir |
| `UPDLOCK, READPAST` ile seçip işlem boyunca tutmak | Tek sorgu | Abone çalışırken veritabanı işlemi ve kilit açık kalır; uzun işlemde engelleme |
| Uygulama kilidi (`sp_getapplock`), tek işleyici (seçildi, yalnızca dağıtımda) | Sıra kendiliğinden korunur; çok basit | Paralellik yok; dağıtım kısa ve ucuz olduğu için sorun değil, teslimde kullanılamaz |

## Karar

### Olay zarfı

`platform.OutboxMessages` genişletilir; boru hattı alanları kendisi doldurur, çağıranlar değişmez.

| Alan | Anlam |
| --- | --- |
| `Id` | Olay kimliği (UUIDv7). Aboneler idempotentlik için bunu kullanır |
| `TenantId` | Olayı üreten isteğin oturumdan doğrulanmış tenant'ı; işleyici bağlamı bundan kurar, istemciden gelen bir değer değildir |
| `EventType`, `SchemaVersion` | Ör. `RecordUpdated`, `1` |
| `EntityKey`, `RecordId` | Kayıt (aggregate) türü ve kimliği |
| `Sequence` | Kaydın değişiklikten sonraki sürümü (silmede son sürüm + 1). Kayıt dışı olaylarda boştur (sırasız) |
| `UserId`, `Source` | İşlemi yapan kişi ve kaynak (`ExecutionSource`: Api, BackgroundJob, Import, AiTool); denetim kaydıyla aynı |
| `CorrelationId` | İsteğin izi (denetim kaydındaki `TraceId`); zincir boyunca taşınır |
| `CausationId`, `Depth` | Olay bir abonenin yaptığı değişiklikten doğduysa tetikleyen olayın kimliği ve zincir derinliği (ilk olay 0) |
| `Payload` | Değişen alanların `{ alan: { old, new } }` farkı (bugünkü biçim, sürüm 1) |
| `OccurredAt`, `DispatchedAt` | Yazılma ve abonelere dağıtılma zamanı (`ProcessedAt` yeniden adlandırılır) |

- `StatusChanged` kaldırılır: durum değişikliği `RecordUpdated` içindeki alan farkından anlaşılır. Anlamı olan iş olayları (fırsat kazanıldı, sevkiyat teslim edildi) Ç0.4'ün açık iş komutlarıyla modül olayı olarak gelir; tür adı modül anahtarıyla başlar (`crm.opportunity.won`). Modülün olay yayınlama ve olay türü bildirme sözleşmesi o adımda eklenir; zarf ve işleyici değişmez.
- `DomainEventTypes` ve zarf (`EventEnvelope`) `Modulix.Core.Contracts`'a taşınır; aboneler bunları kullanır.
- Kayıt silme bugün kaynağı sabit `Api` yazıyor; silme de kaynağı komuttan almalı (abonenin yaptığı silme "Arka plan işi" görünmeli).

**Sürümleme:** Sürüm olay türü başınadır. Alan eklemek sürüm artırmaz; aboneler tanımadıkları alanı yok sayar. Bir alanın anlamı veya biçimi değişirse sürüm artar, üretici bir geçiş süresince iki sürümü de yazar ya da aboneler iki sürümü de okur; eski sürümü okuyan abone kalmayınca eski sürüm kaldırılır. Abone desteklediği en yüksek sürümü bildirir; daha yeni sürümlü olay ona teslim edilmez, "başarısız" olur (yayından önce yakalanması için katalog açılışta denetler).

### Abonelik bildirimi ve abone sözleşmesi

Modül, diğer bildirimler gibi tek satırla abone olur (ADR-0004, 0005, 0006 kalıbı):

```csharp
// IModule
IReadOnlyList<EventSubscription> EventSubscriptions => [];

// CrmModule (örnek)
public IReadOnlyList<EventSubscription> EventSubscriptions =>
[
    EventSubscription.For<PartyRenamedSubscriber>(
        "crm.party-renamed", [DomainEventTypes.RecordUpdated], ["core.party"], ["crm.companies.write"]),
];
```

```csharp
// Modulix.Core.Contracts.Events
public sealed record EventSubscription(
    string Key,                           // modül anahtarıyla başlar, değişmez; teslim satırları buna bağlıdır
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> EntityKeys,     // boş: bütün kayıt türleri
    IReadOnlyList<string> Permissions,    // abone çalışırken sahip olduğu izinler (yalnızca kendi modülünün)
    Type Subscriber)
{
    public bool Ordered { get; init; } = true;
    public int MaxSchemaVersion { get; init; } = 1;
}

public interface IEventSubscriber
{
    Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
```

- Katalog açılışta denetler: anahtar modül anahtarıyla başlar ve tekildir, olay türü ve kayıt türü bilinir, izinler modülün kendi izinleridir. Abone sınıfını modül `ConfigureServices`'te scoped kaydeder; abone yalnızca Contracts'taki servisleri kullanır.
- Modül başka modülün kayıt türüne abone olabilir (asıl amaç budur), ama tepkisini yalnızca kendi kayıtlarında veya ortak kayıtta (ADR-0006) gösterir.
- **Abonenin kimliği:** abone olayı tetikleyen kişi adına değil, "sistem" adına çalışır. Bağlam `TenantContext(tenantId, Guid.Empty, Guid.Empty)` ile kurulur; erişim servisi (`ICurrentAccess`) bu kapsamda yalnızca aboneliğin bildirdiği izinleri verir, kayıt kapsamı tenant geneli, modülü tenant için kapalıysa hiçbiri. Böylece abonenin yetkisi kodda görünür ve dar kalır (Ç6b.2'nin servis kimliği ilkesi).
- **Abonenin servisleri:** kayıt boru hattı (`IRecordService`), e-posta kuyruğu (`IEmailQueue`), ileride bildirim (Ç5a). Kayıt değişikliği boru hattından geçer (izin, doğrulama, hassas alan, audit, outbox); kaynak `ExecutionSource.BackgroundJob`, denetim kaydında "Arka plan işi" ve abonelik anahtarı görünür. Abone `DbContext`'e doğrudan yazmaz.
- Abonenin yaptığı değişiklik yeni olay üretir; `CausationId` tetikleyen olaydır, `CorrelationId` aynı kalır, `Depth` bir artar. Derinlik 5'i aşan olay yazılır ama dağıtılmaz ("derinlik sınırı" olarak işaretlenir, uyarı loglanır); abonelerin birbirini sonsuza kadar tetiklemesi böyle kesilir.

### İşleyici

İşleyici iki adımdan oluşur; mantık Infrastructure'da (`EventDispatcher`), döngü Api'de (`EventDispatchWorker`, `EmailDispatchWorker` gibi `BackgroundService`).

**1. Dağıtım (fan-out).** `DispatchedAt` boş olayları `OccurredAt, Id` sırasıyla en fazla 200'er okur. Her olay için eşleşen abonelikleri bulur; aboneliğin modülü olayın tenant'ı için açıksa bir teslim satırı ekler, sonra olayı dağıtılmış işaretler. Adım `sp_getapplock` (işlem sahipli, beklemesiz) ile tek örnekte çalışır; kilidi alamayan örnek bu turda dağıtım yapmaz. Dağıtım kısa ve yalnızca platform tablolarına yazdığı için tenant bağlamı gerekmez. Aboneliği olmayan olay da dağıtılmış işaretlenir.

**2. Teslim.** Yeni tablo `platform.EventDeliveries` (RLS dışında, platform şeması):

| Alan | Anlam |
| --- | --- |
| `Id`, `EventId`, `TenantId`, `Subscription` | `(TenantId, Subscription, EventId)` tekildir: aynı olay aynı aboneye ikinci kez satır açamaz |
| `EntityKey`, `RecordId`, `Sequence` | Sıra denetimi için olaydan kopyalanır |
| `Status` | `Pending`, `Succeeded`, `Failed`, `Skipped` |
| `AttemptCount`, `NextAttemptAt`, `LeaseToken`, `LastError`, `CompletedAt` | Kira ve yeniden deneme |

- Zamanı gelmiş `Pending` teslimler seçilir, her biri e-posta kuyruğundaki gibi koşullu `UPDATE` ile kiralanır: `NextAttemptAt` = şimdi + 2 dk, `AttemptCount + 1`, yeni `LeaseToken`. Etkilenen satır 1 değilse teslim başka örneğindir.
- Teslim için `ITenantScopeFactory` ile olayın tenant'ında yeni kapsam açılır (yukarıdaki sistem kimliğiyle). Tenant kapatılmışsa (`TenantStatus.Closed`) teslim yapılmaz, `Skipped` olur. Aboneliğin modülü o arada kapatılmışsa da `Skipped` (gerekçesiyle).
- Abone kapsamın `DbContext`'inde açılmış bir veritabanı işlemi içinde çalışır; abone başarıyla dönerse teslim aynı işlemde `Succeeded` yapılır ve işlem onaylanır. Abonenin boru hattından yaptığı değişiklik, kuyruğa yazdığı e-posta ve teslimin "başarılı" işareti birlikte yazılır ya da hiçbiri (Ç1.3: "mümkünse iş etkisiyle aynı işlem").
- Teslimi bitiren güncelleme `LeaseToken` eşleşmesini şart koşar: kirası dolmuş ve iş başka örneğe geçmişken geç biten eski örnek sonucu yazamaz, işlemi geri alınır.
- **Heartbeat:** abone çalışırken kira her 30 saniyede uzatılır (ayrı bağlantıyla, `LeaseToken` şartlı). Aboneye 5 dakikalık zaman aşımı verilir; uzun süren iş abone değil, aboneden kuyruğa atılan bir arka plan işi (Ç2) olmalıdır. Örnek çökerse heartbeat durur, en geç 2 dakika sonra teslim yeniden alınır.
- **Adillik:** bir turda tek tenant'tan en fazla 20 teslim alınır, kalan kapasite diğer tenant'lara gider. Bir tenant'ın birikmesi diğerlerini bekletmez.
- **Periyot:** her 5 saniyede bir tur; tur dolu biterse beklemeden devam eder (e-posta işleyicisiyle aynı). Ayarlar `Events` bölümünde: `WorkerEnabled`, `PollIntervalSeconds`, `BatchSize`. Testlerde ve tek seferlik komutlarda işleyici kapatılabilir.

### Teslim garantisi

- **En az bir kez.** Teslim satırı tekrarlanmaz, ama abone birden fazla kez çalışabilir: iş etkisi aynı veritabanı işleminde değilse (dış servis, SMTP) veya işlem onaylanırken bağlantı koparsa. Her abone bunu varsayar ve idempotent yazılır.
- Abone veritabanına yazıyorsa iş etkisi ve teslim işareti aynı işlemde olduğu için çift etki oluşmaz. Dışarıya etki eden abone (webhook, ileride SMS) olay kimliği + abonelik anahtarını dış sisteme idempotentlik anahtarı olarak verir (Ç1.5).
- Elle yeniden deneme de aynı teslimi yeniden çalıştırır, yeni teslim açmaz; idempotent abone ikinci etki üretmez (Ç1.5 kabulü: "replay yeni iş etkisi oluşturmaz").

### Sıra

- Global sıra veya tenant sırası vaat edilmez. Aynı kaydın (`TenantId, EntityKey, RecordId`) olayları bir aboneye `Sequence` sırasıyla teslim edilir: bir teslim, aynı abone ve aynı kayıt için daha küçük sıra numaralı, `Succeeded` veya `Skipped` olmamış bir teslim varken alınmaz.
- Dağıtım tek örnekte ve yazılış sırasıyla yapıldığı için daha küçük sıra numaralı olayın teslim satırı her zaman önce oluşur.
- Sırası önemli olmayan abone `Ordered = false` bildirir (ör. bildirim e-postası); onun teslimleri birbirini beklemez.
- **Zehirli olay:** sıralı bir abonenin bir teslimi `Failed` olursa o abonenin aynı kayda ait sonraki teslimleri bekler (karantina yalnızca o kayıt ve o abone için). Diğer kayıtlar, diğer aboneler ve diğer tenant'lar etkilenmez (Ç1.4 kabulü). Platform ekranında beklettiği teslim sayısı gösterilir; yönetici olayı yeniden dener veya atlar.

### Hata ve yeniden deneme

- Abone istisna fırlatırsa teslim 1 dk, 5 dk, 15 dk, 1 sa, 4 sa arayla (her biri ±%20 rastgele sapmayla) yeniden denenir; 6. başarısız denemeden sonra `Failed` (dead-letter) olur. Abone kalıcı hatayı `PermanentEventFailureException` ile bildirirse doğrudan `Failed` olur (ör. kayıt artık yok, olay sürümü desteklenmiyor).
- Hata abone başınadır: bir aboneliğin başarısızlığı aynı olayın diğer aboneliklerini etkilemez, başarılı aboneler yeniden çalışmaz (Ç1.3 kabulü).
- `LastError` istisna türü ve iletisidir (2000 karakterle sınırlı). Log ve hata kaydına olay kimliği, tenant, kayıt türü ve kimliği yazılır; payload ve alan değerleri yazılmaz. Abone istisna iletisine kişisel veri koymaz (e-posta işleyicisindeki alıcı adresi kuralı).
- **Platform ekranı (Olaylar):** platform yöneticisi bekleyen, başarısız ve bekletilen teslimleri tenant, abonelik, olay türü ve tarihe göre süzer. Satırda olay türü, kayıt türü ve kimliği, abonelik, deneme sayısı, son hata görünür; payload gösterilmez. İşlemler: **Yeniden dene** (deneme sayacı sıfırlanır, hemen zamanlanır) ve **Atla** (`Skipped`; sıralı abonenin bekleyen teslimlerini serbest bırakır). İkisi de tek tek veya süzülmüş liste için toplu yapılır ve o tenant'ın denetim kaydına yazılır (`core.event-delivery`, işlemi yapan platform yöneticisi, olay kimliği ve abonelik; Y4'teki platform işlemleri gibi).
- Gözlemleme (Ç14.5): en eski dağıtılmamış olayın ve en eski bekleyen teslimin yaşı, başarısız teslim sayısı metrik olarak yayınlanır; `/health` dağıtılmamış olay 15 dakikayı geçerse "degraded" döner.

### Saklama ve temizlik

- `Succeeded` ve `Skipped` teslimler ile bütün teslimleri bitmiş olaylar **7 gün** sonra silinir. `Failed` teslimler ve olayları **30 gün** saklanır, sonra silinir (silme sayısı loglanır). Dağıtılmamış veya bekleyen olay silinmez.
- Kalıcı geçmiş denetim kaydıdır; outbox bir arşiv değildir. Kısa saklama, platform şemasında RLS dışında duran kişisel verinin süresini de kısaltır (Ç13a.4).
- Temizliği Ç2'ye kadar aynı işleyici saatte bir, 1000'erlik parçalarla yapar.

### Hassas alanlar

Payload, kaydın bütün değişen alanlarını içerir; hassas alanlar da (Y8b, ör. fırsat tutarı, ileride maaş) dahildir. Outbox'ta tam fark saklanır (denetim kaydı da öyle saklar); filtre teslim anında uygulanır:

- **Uygulama içi abone:** zarftaki farktan, aboneliğin bildirdiği izinlerle görülemeyen hassas alanlar çıkarılır; çıkarılan alanların yalnızca adları `HiddenFields` olarak verilir ("tutar değişti ama değeri sizin için gizli"). Değere ihtiyaç duyan abone, ilgili hassas alan iznini bildirmek zorundadır; bu bildirim kodda görünür ve incelenir.
- **İnsana giden içerik:** abone e-posta veya bildirim metnine alan değeri koyacaksa alıcının o anki yetkisine göre koyar (alıcı kaydı okuyabiliyor mu, alanı görebiliyor mu); aboneliğin izni alıcının izni değildir (Ç5a: "kayda erişimi kalmayan kullanıcı hassas metin görmez").
- **Webhook (Ç11a.4) ile dışarı çıkış:** varsayılan yük asgaridir: olay kimliği, türü, sürümü, zamanı, kayıt türü ve kimliği, değişen alanların adları; değer içermez. Değer gönderimi webhook aboneliğinde açıkça seçilen alanlarla (allowlist) olur; hassas alan yalnızca servis kimliğinin (Ç11a.1) o alanı görme izni varsa seçilebilir. Alıcı ayrıntıyı API'den kendi yetkisiyle çeker.

### İlk gerçek abone: atama e-postası

Ç5a'nın ön adımı ve Ç1'in ilk kullanımı:

- **Abonelik:** `core.assignment-email`, çekirdek modülde (`CoreModule`). Olaylar `RecordCreated`, `RecordUpdated`; kayıt türleri, modüllerin `OwnedRecords` ile bildirdiği türler (CRM'de firma, fırsat, aktivite). Yeni modül ek iş yapmadan katılır. `Ordered = false`.
- **Davranış:** farkta `ownerId` değiştiyse ve yeni sorumlu işlemi yapan kişi değilse, yeni sorumluya "Size bir kayıt atandı" e-postası kuyruğa yazılır (`IEmailQueue.Enqueue`, aynı işlemde; kullanıcı girdisi `WebUtility.HtmlEncode` ile). İçerik: kayıt türü, kaydın adı (`AuditedEntity.TitleFields`), atayan kişi, kayda bağlantı. Hassas alan konmaz.
- Alıcı gönderim anında hâlâ etkin üye değilse veya kaydı okuyamıyorsa e-posta yazılmaz, teslim başarılı sayılır.
- Teslim ve e-posta aynı işlemde yazıldığı için olay tekrar teslim edilse de ikinci e-posta oluşmaz.

## Ç2 ile ilişkisi

- İşleyici şimdilik kendi döngüsünde çalışır (`EventDispatchWorker`), e-posta işleyicisi gibi. Ç2 (arka plan işleri) geldiğinde ikisi ortak çerçeveye taşınır (Ç2.5): gözlemleme, kira ve heartbeat ortaklaşır, sürekli okuma döngüsü cron tetikleyicisine dönüştürülmez. Temizlik Ç2'de zamanlanmış iş olur.
- Tablolar, kira kuralları ve abone sözleşmesi taşımada değişmez.
- Uzun süren tepkiler (toplu hesaplama, rapor) abonede yapılmaz; abone Ç2'de bir iş kuyruğa yazar.

## Geçiş

1. Zarf: `OutboxMessages`'a yeni sütunlar (`SchemaVersion`, `Sequence`, `UserId`, `Source`, `CorrelationId`, `CausationId`, `Depth`), `ProcessedAt` → `DispatchedAt`; `DomainEventTypes` ve `EventEnvelope` Contracts'a; boru hattı yeni alanları doldurur; silme kaynağı komuttan alır. Mevcut satırlar `SchemaVersion = 1`, `Sequence` boş (sırasız) kalır.
2. `EventDeliveries` tablosu, `EventSubscription`, `IEventSubscriber`, `IModule.EventSubscriptions`, katalog denetimi ve mimari test.
3. `EventDispatcher` + `EventDispatchWorker`, sistem kimliğiyle `ICurrentAccess`, hassas alan süzgeci.
4. Atama e-postası aboneliği.
5. Platform ekranı (Olaylar), yeniden dene / atla, denetim kaydı, metrik ve health.
6. README'ye "Olaylar" bölümü.

Mevcut birikmiş olaylar ilk dağıtımda abonelere dağıtılmaz: geçişte o ana kadar yazılmış olaylar dağıtılmış işaretlenir (yoksa açılışta eski atamalar için e-posta gider).

### Kabul testleri

- İşlem geri alınınca olay yazılmaz (Ç1.1).
- İki işleyici örneği aynı teslimi bir kez başarılı yazar; kirası dolan örneğin geç sonucu yazılmaz, teslim kaybolmaz (Ç1.2).
- Bir abone başarısız, diğeri başarılı: yalnızca başarısız olan yeniden çalışır; yeniden denemede ikinci e-posta oluşmaz (Ç1.3).
- Aynı kaydın iki olayı sırayla teslim edilir; ilki `Failed` iken ikincisi bekler, başka kaydın olayı beklemez; bir tenant'ın başarısız olayları başka tenant'ın teslimini durdurmaz (Ç1.4).
- Abone tenant A'nın olayında tenant B'nin verisini göremez (RLS); bağlamsız sorgu satır döndürmez.
- Aboneliğin izni olmayan hassas alan zarfta yoktur; platform ekranı ve loglar payload içermez (Ç1.5).

## Sonuçlar

- Bir modül başka modülün değişikliğine tepki vermek için tek satır bildirim ve bir abone sınıfı yazar; diğer modülü bilmez (K5).
- Olay başına abone sayısı kadar teslim satırı oluşur; 7 günlük saklama ile tablo boyu sınırlı kalır. Kayıt yazma yolu değişmez (dağıtım sonradan yapılır).
- Olay ile tepki arasında birkaç saniyelik gecikme vardır (periyot 5 sn). Anlık tutarlılık gereken iş aynı işlemde, olayla değil komutla yapılır.
- Abone olay anındaki farkı alır; kaydın güncel hâline ihtiyaç duyarsa boru hattından okur ve arada değişmiş olabileceğini bilir.
- Abone sistem kimliğiyle çalıştığı için yetkisi kişiye değil bildirime bağlıdır; aboneliğin izinleri kod incelemesinde görünür.
- Platform şemasında, RLS dışında tenant verisi (fark) bulunur; erişim yalnızca işleyici ve payload göstermeyen platform ekranıyladır, saklama kısadır.

## Açık noktalar

Her biri için önerilen cevap yazıldı; kabulde karara bağlanacak.

1. **Abonenin kimliği:** sistem kimliği (aboneliğin bildirdiği izinler) mi, olayı tetikleyen kişinin adına mı? *Öneri:* sistem kimliği. Kişi adına çalışmak, kişinin yetkisi o arada değişince veya kişi devre dışı kalınca belirsizleşir; otomasyonda (Ç6) da servis kimliği isteniyor. Denetim kaydında "Arka plan işi · abonelik" ve tetikleyen olayın kişisi (`CausationId` üzerinden) görünür.
2. **Sıralı varsayılan:** abonelik varsayılan olarak sıralı mı olsun? *Öneri:* evet; modüller arası akışlarda sıra hatası sessiz veri bozar, sıra gerekmediğini bilen abone `Ordered = false` der. Bedeli: sıralı abonede zehirli olay o kaydın sonraki olaylarını elle müdahaleye kadar bekletir.
3. **Yeniden deneme ve sınır:** 1 dk, 5 dk, 15 dk, 1 sa, 4 sa; 6. denemede `Failed` (toplam ~5,5 saat). *Öneri:* bu değerler; gece boyunca kesilen bir dış servis sabah elle yeniden denenir.
4. **Saklama süreleri:** başarılı 7 gün, başarısız 30 gün. *Öneri:* bu değerler; KVKK envanterinde (Ç13a.4) outbox "kısa süreli teknik kayıt" olarak geçer.
5. **Tenant yöneticisi ekranı:** başarısız teslimleri yalnızca platform yöneticisi mi görsün, tenant yöneticisi de kendi tenant'ında görüp yeniden deneyebilsin mi? *Öneri:* ilk aşamada yalnızca platform yöneticisi; Ç11a webhook'ları gelince tenant yöneticisine kendi webhook teslimleri için ekran açılır.
6. **Kayıt devrinde atama e-postası:** ADR-0004'teki toplu devir yüzlerce `ownerId` değişikliği üretir. *Öneri:* devir işlemi olaylara `Reason = "transfer"` yazar, atama aboneliği bunları atlar ve devir yeni sorumluya tek özet e-postası gönderir.
7. **Mevcut olaylar:** geçişte o ana kadar birikmiş olaylar dağıtılmadan işaretlensin mi? *Öneri:* evet (yukarıda); aksi hâlde ilk açılışta geçmiş atamalar için e-posta gider.
8. **Derinlik sınırı:** 5. *Öneri:* bu değer; Ç6 otomasyonlarıyla birlikte gözden geçirilir.
9. **İşleyicinin ayrı süreçte çalışması (K11):** işleyici API ile aynı süreçte mi, ayrı bir worker sürecinde mi? *Öneri:* şimdilik API süreci içinde (`Events:WorkerEnabled` ile kapatılabilir); yük arttığında aynı kod ayrı süreçte çalıştırılır, kira kuralları buna zaten uygundur.
