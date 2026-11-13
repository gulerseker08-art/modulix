# ADR-0039: Modül iş olayları (Ç0.4 yayın sözleşmesi)

- **Durum:** Kabul edildi, 7 Kasım 2026 (Dilim 1 bu belgeyle birlikte teslim edilir)
- **Karar:** Modüller, iş anlamı olan olayları (fırsat kazanıldı, müşteri adayı dönüştü, sevkiyat teslim edildi) `IModule.BusinessEvents` ile **beyan eder** ve açık iş komutlarının içinden `IBusinessEventPublisher` ile **yayınlar**. İş olayı, kayıt olaylarıyla aynı outbox'a, aynı zarfla ve aynı veritabanı işleminde yazılır; mevcut dağıtıcı, abonelik kataloğu ve otomasyon aboneliği değişmeden bu olayları taşır. Otomasyonun `DomainEvent` tetikleyicisi bu olaylara bağlanır ve yayınlanabilir hâle gelir.

## Problem

ADR-0007 olay altyapısını kurdu (zarf, `EventDeliveries`, dağıtıcı, abonelik kataloğu, sistem kimliği, hassas alan süzgeci) ama **yalnızca kayıt olaylarını** taşıyor: `RecordCreated`, `RecordUpdated`, `RecordDeleted`, `RecordMerged`. ADR-0007 anlamı olan iş olaylarını Ç0.4'e bıraktı: "tür adı modül anahtarıyla başlar (`crm.opportunity.won`); modülün olay yayınlama ve olay türü bildirme sözleşmesi o adımda eklenir; zarf ve işleyici değişmez."

Bugün:

- Hiçbir modül iş olayı yayınlamıyor. Fırsatın kazanılması `RecordUpdated` içinde `status: Open → Won` farkı ve `Reason = "opportunity.won"` olarak görünüyor. Abone bunu anlamak için CRM'in alan adlarını ve durum değerlerini bilmek zorunda; bu K5'e (modüller birbirini bilmez) aykırı ve kırılgan.
- Abonelik kataloğu (`ModuleCatalog.ValidateSubscription`) yalnızca `DomainEventTypes.SchemaVersions`'taki türleri kabul ediyor; `crm.opportunity.won` türüne abone olunamıyor.
- Otomasyonun `DomainEventTrigger`'ı (ADR-0027 Dilim 2b-3) yayıncısı olmadığı için yayın ve etkinleştirmede 422 veriyor; `AutomationEventDefinition` modül önekli türü kabul ediyor ama hiçbir olay o türle gelmiyor.
- ADR-0038 dilim 6 (`ShipmentDelivered.v1`) ve ADR-0037 dilim 5 (teslim → faturalanabilir kalem) bu sözleşmeyi bekliyor.

Yol haritası Ç0.4 iş komutunu şöyle tanımlar: "ApproveQuote, DeliverShipment, PostInvoice gibi işlemler açık komuttur; genel alan güncellemesiyle taklit edilemez." CRM'de bu ayrım zaten var (`IOpportunityWorkflow.WinAsync`, `ILeadConversion.ConvertAsync`; genel kayıt ucu durumu yazamaz, iş akışı kapısı). Eksik olan, komutun sonucunu **adıyla** duyurmasıdır.

## Seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Aboneler `RecordUpdated` + `Reason` okumaya devam etsin | Kod yok | Abone başka modülün alan adlarını ve neden sabitlerini bilir (K5 ihlali); otomasyon tetikleyicisi için kullanıcıya "neden" kavramı gösterilemez; sürümlenemez |
| Ayrı tablo ve ayrı işleyici (`BusinessEvents`) | Zarf ayrı tasarlanabilir | İki işleyici, iki teslim tablosu, iki abonelik kataloğu; ADR-0007'nin sıra, kira, hassas alan, derinlik kuralları ikinci kez yazılır |
| **Aynı outbox, modül önekli tür + sürüm, beyan kataloğu, yayıncı servisi (seçildi)** | Zarf ve işleyici değişmez (ADR-0007'nin sözü); abonelik, hassas alan süzgeci, sıra, saklama, platform ekranı hazır; sözleşme tek yerde doğrulanır | Yayınlama disiplini kodla değil incelemeyle korunur (boru hattı dışı komut yayınlamayı unutabilir); payload sürümü modülün sorumluluğunda |

## Karar

### Olay türü ve sürüm

- Tür adı `{modül}.{kayıt}.{fiil}` biçimindedir, küçük harf: `crm.opportunity.won`, `crm.lead.converted`, `logistics.shipment.delivered`. Sürüm ayrı bir alandır (`SchemaVersion`, ADR-0007 zarfındaki sütun). Belgelerde `crm.opportunity.won.v1` yazımı "tür + sürüm" demektir; tür adının parçası değildir. ADR-0038'deki `ShipmentDelivered.v1` bu sözleşmede `logistics.shipment.delivered` sürüm 1'dir.
- Sürüm olay türü başınadır (ADR-0007 kuralı): alan eklemek sürüm artırmaz, bir alanın anlamı ya da biçimi değişirse sürüm artar; abone `MaxSchemaVersion` ile okuyabildiği sürümü bildirir, daha yeni sürüm ona teslim edilmez (kalıcı hata). Katalog, aboneliğin olayın güncel sürümünü okuyabildiğini açılışta denetler.

### Beyan: `IModule.BusinessEvents`

```csharp
// Modulix.Core.Contracts.Events
public sealed record BusinessEventDefinition(
    string EventType,                 // "crm.opportunity.won"; modül anahtarıyla başlar, değişmez
    int SchemaVersion,                // 1
    string EntityKey,                 // olayın kaydı: "crm.opportunity"
    string DisplayName,               // "Fırsat kazanıldı" (otomasyon editörü, platform ekranı)
    IReadOnlyList<string> Fields);    // payload'da bulunabilen alanlar; başka alan yazılamaz

// CrmModule
IReadOnlyList<BusinessEventDefinition> IModule.BusinessEvents =>
[
    new(Events.OpportunityWon, 1, Entities.Opportunity, "Fırsat kazanıldı",
        ["companyId", "ownerId", "stage", "actualCloseDate", "wonAmount", "wonCurrency"]),
    ...
];
```

- Katalog açılışta denetler: tür modül önekli ve tekil, biçim `modül.ad.ad`, sürüm ≥ 1, kayıt türü modülün ve denetim kaydı türleri arasında, alan adları tekil ve geçerli. Hatalı beyan uygulamayı açtırmaz (abonelik ve otomasyon beyanlarıyla aynı kalıp).
- `ModuleCatalog.EventSchemaVersions` kayıt olaylarıyla (`DomainEventTypes.SchemaVersions`) iş olaylarının birleşimidir; abonelik doğrulaması ve platform "Olaylar" ekranının tür listesi artık bu birleşimi kullanır. Abonelik, başka modülün iş olayına bağlanabilir (asıl amaç budur); bu yüzden olay türü denetimi, kayıt türü denetimi gibi **bütün modüller okunduktan sonra** yapılır.
- **Payload alanları kaydın alan adlarıdır** (aynı anlam, aynı biçim). Böylece ADR-0007'nin hassas alan süzgeci değişmeden çalışır: `wonAmount` fırsatın `crm.opportunity-amounts.read` iznine bağlı hassas alanıdır; izni bildirmeyen aboneye iş olayında da gönderilmez, adı `HiddenFields`'ta verilir. Kayıtta karşılığı olmayan türetilmiş alanlar (ör. `companyCreated`) beyan edilir ve hassas sayılmaz; hassas bir değer türetilmiş alana konmaz.
- Payload düzdür: `{ "alan": değer }`; kayıt olaylarındaki `{ alan: { old, new } }` farkı değildir. Olay "ne oldu"yu taşır; kaydın güncel hâline ihtiyaç duyan abone boru hattından kendi izniyle okur (ADR-0007 kuralı).

### Yayın: `IBusinessEventPublisher`

```csharp
public sealed record BusinessEvent(string EventType, Guid RecordId, JsonObject Payload)
{
    public long? RecordVersion { get; init; }  // zarfın Sequence'ı: kaydın komuttan sonraki sürümü
    public Guid? CompanyId { get; init; }      // işletici şirket (ADR-0008); ortak kayıtta boş
    public string? Reason { get; init; }
    public ExecutionSource Source { get; init; } = ExecutionSource.Api;
}

public interface IBusinessEventPublisher
{
    Task PublishAsync(BusinessEvent businessEvent, CancellationToken cancellationToken);
}
```

- Uygulama Core'dadır (`BusinessEventPublisher`): aktif tenant'ı oturumdan alır (istemciden gelen değer yok), beyanı katalogdan bulur, payload'daki her anahtarın beyan edilmiş olmasını ister, zarfı doldurur (`SchemaVersion` beyandan; `UserId` ve `Source` denetim kaydıyla aynı; `CorrelationId` isteğin izi; abonenin içinden yayınlanıyorsa `CausationId`, `Depth + 1`, kaynak arka plan işi — `EventHandlingScope`), `IDomainEventOutbox`'a yazar ve iş birimini kaydeder. Beyan dışı tür ya da alan, boş tenant, boş kayıt kimliği **programlama hatasıdır** ve istisnayla reddedilir; kullanıcıya dönen doğrulama hatası değildir.
- **Aynı işlem:** komut, durum değişikliğini ve yayını `IRecordPipeline.RunAtomicAsync` içinde yapar; ikisi birlikte yazılır ya da hiçbiri. Yayın, kayıt değişikliği başarıyla bittikten **sonra** çağrılır (doğrulamadan dönen komut olay bırakmaz). Komut zaten açık bir işlemde çalışıyorsa (olay abonesi) ona katılır.
- **Sıra:** `RecordVersion` verilirse olayın `Sequence`'ı kaydın komuttan sonraki sürümüdür; aynı komutun ürettiği `RecordUpdated` ile aynı sıra numarasını taşır. Sıralı abone için ikisi birbirini beklemez, ama kaydın sonraki değişikliği ikisinin de bitmesini bekler. Sürüm verilmezse olay sırasızdır (ADR-0007: kayıt dışı olay).
- **Idempotency:** yayının kendisi için ayrı anahtar yoktur; iş komutu zaten idempotenttir (kayıt sürümü şartı, "zaten kazanılmış" kuralı). Aboneler ADR-0007 gereği olay kimliğiyle idempotenttir; aynı olay yeniden teslim edilse ikinci etki üretmez.
- **Yetki:** yayıncı izin denetlemez; iş komutu kendi iznini (ör. `crm.opportunities.close`) kontrol etmiş ve kayıt boru hattından geçmiştir. Payload yalnızca komutun döndürdüğü, yani **çağıranın görebildiği** değerleri taşır: çağıranın hassas alan izni yoksa alan olayda da yoktur. Olay, olayı tetikleyenin görmediği bir değeri taşımaz; değere ihtiyaç duyan abone kaydı kendi izniyle okur.
- Yayıncı modülün içindeki iş komutundan çağrılır; olay abonesi de yayınlayabilir (zincir kuralları: derinlik sınırı 5, `CausationId`). Modül başka modülün olayını yayınlayamaz (katalog türü beyan eden modüle bağlar; çağıran modül denetimi Dilim 2'de mimari testle eklenir).

### Abonelik ve otomasyon

- Abonelik aynıdır: `EventSubscription.For<X>("billing.on-won", ["crm.opportunity.won"], ["crm.opportunity"], [...])`. Katalog türü ve kayıt türünü bütün modüller okunduktan sonra denetler.
- `AutomationEventDefinition.EventType` modül önekli bir türse katalog **aynı modülün beyan ettiği iş olayı** olmasını ve kayıt türünün uyuşmasını ister; beyansız olay otomasyona açılamaz ("sessizce hiç tetiklenmeyen kural bırakılmaz").
- `core.automation` aboneliğinin olay türleri katalogda `RecordCreated`, `RecordUpdated` + otomasyona beyan edilmiş iş olayı türleriyle doldurulur (kayıt türlerinin doldurulduğu gibi). Abone `DomainEventTrigger.EventKey`'i katalogdan türe çevirir ve olayın türü ile kayıt türünü eşler (`AutomationTriggerMatcher`); koşul ve aksiyonlar kuralın kimliğiyle her zamanki gibi çalışır. `IsExecutable` artık `DomainEventTrigger`'ı kabul eder; yayın 422 vermez. Editör tetikleyici listesine "İş olayı" eklenir; seçenekler kayıt türünün beyan edilmiş iş olaylarıdır.
- `DomainEventTrigger`'da eski/yeni değer koşulları yoktur (payload fark değildir); doğrulayıcı bunu zaten `ConditionOptions.Current` ile uygular.

### Dilim 1 kapsamı (bu PR)

- Sözleşme (`BusinessEventDefinition`, `BusinessEvent`, `IBusinessEventPublisher`, `IModule.BusinessEvents`), katalog (`EventSchemaVersions`, `FindBusinessEvent`, beyan ve abonelik doğrulaması, otomasyon beyanı denetimi, `core.automation` tür genişletmesi), Core yayıncısı ve DI kaydı.
- CRM olayları: `crm.opportunity.won`, `crm.opportunity.lost` (`WinAsync`/`LoseAsync`, `RunAtomicAsync` içinde), `crm.lead.converted` (`ConvertAsync`, zaten atomik). Otomasyon beyanına üç iş olayı eklendi. **13 Kasım 2026:** `crm.quote.accepted` eklendi: kullanıcının kaydettiği kabul (`RecordAcceptedAsync`) ve müşterinin sayfadan kabulü (`QuoteCustomerPortal.DecideAsync`) olayı durum değişikliğiyle aynı `RunAtomicAsync` içinde yayınlar; payload teklifin kayıt alanları + türetilmiş `acceptanceMethod` (`Manual`/`Portal`); ret, iptal ve süre dolumunun iş olayı yoktur (`QuoteAcceptedEvent`).
- Otomasyon: `DomainEventTrigger` eşleşmesi, `IsExecutable`, editörde "İş olayı" seçeneği.
- Platform "Olaylar" ekranı tür listesi iş olaylarını da gösterir.

### Sonraki dilimler

- **Dilim 2:** mimari test — `IBusinessEventPublisher.PublishAsync` yalnızca iş akışı/komut sınıflarından ve yalnızca kendi modülünün türüyle çağrılır; `Reason` sabitlerinin (`opportunity.won`) iş olayıyla çakışan kullanımı temizlenir (aboneler nedene değil türe bakar).
- **Dilim 3:** diğer CRM komutları (fırsat iptal/yeniden açma, teklif kabul, sözleşme imza, sipariş devri, destek SLA ihlali) ve Lojistik `logistics.shipment.delivered` (ADR-0038 dilim 6), ADR-0037 dilim 5 abonesi.
- **Dilim 4:** webhook (Ç11a.4) iş olaylarını dış sisteme taşır; tenant yöneticisine kendi webhook teslimleri ekranı.

## Kabul ölçütleri

- Fırsat kazanıldığında outbox'ta `crm.opportunity.won` sürüm 1, kaydın sürümüyle, kazanan kişinin kimliğiyle ve beyan edilmiş alanlarla **tek** olay bulunur; komut doğrulamadan dönerse olay yoktur.
- `DomainEventTrigger("crm.opportunity.won")` tetikleyicili kural yayınlanır (200), olay teslim edilince çalışma açılır ve aksiyon bir kez çalışır; kayıt olayı anahtarıyla (`crm.opportunity.updated`) ya da bilinmeyen anahtarla yayın 422'dir.
- Tenant A'nın olayı tenant B'nin kuralını tetiklemez; B'nin bağlamında A'nın olayı görünmez.
- Kapatma izni olmayan kullanıcı kazandıramaz (403) ve olay oluşmaz; tutar iznini bildirmeyen abone `wonAmount` değerini almaz, adı `HiddenFields`'tadır.
- Beyan dışı alanla ya da beyansız türle yayın istisnayla reddedilir; mimari testler geçer.

## Sonuçlar

- Bir modül başka modülün iş olayına, o modülün alan adlarını ve durum değerlerini bilmeden abone olur (K5). Olayın sürümü, anlamı ve alanları tek yerde (beyan) görünür; kod incelemesinde denetlenir.
- Outbox satır sayısı iş olayı başına bir artar (kayıt olayı zaten yazılıyor). Saklama ve temizlik aynıdır.
- Yayınlama disiplini: her açık iş komutu olayını yayınlamalıdır; unutulan komutun olayı yoktur. Dilim 2'deki mimari test ve kod incelemesi bunu korur. Boru hattı dışında durum değiştiren komut zaten yoktur (iş akışı kapısı).
- `Reason` alanı kalır (atama e-postası ve devir gibi davranış ipuçları için); iş anlamı artık türdedir.
