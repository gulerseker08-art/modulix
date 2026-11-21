# ADR-0053: Birleşik müşteri zaman çizelgesi

- **Durum:** Dilim 1 (kaynak sözleşmesi, CRM kaynakları, uç nokta, firma kartında sekme) 17 Kasım 2026'da teslim edildi (PR #468).
  Dilim 2a (Sipariş kaynağı) ve 2b (Sözleşme kaynağı) 17 Kasım 2026'da, 2c (Destek kaynağı) 21 Kasım 2026'da teslim edildi. Diğer dilimler öneridir.
- **Kaynak:** [CRM eksik süreç epic listesi](../crm-eksik-surecler-epicleri.md) E9.

## Bağlam

Firma kartında aktiviteler, fırsatlar, teklifler, siparişler, destek talepleri ve kampanyalar ayrı sekme ve listelerde duruyor. "Bu müşteriyle en
son ne oldu" sorusunun cevabı için kullanıcı sekmeleri tek tek geziyor. Rakip CRM'ler bütün geçmişi tek akışta gösteriyor.

Sınırlar:

- Modüller birbirinin tablosunu okumaz (ADR-0044); CRM, Sipariş ya da Destek tablosuna sorgu atamaz.
- Yetki sunucuda ve sorgu seviyesinde uygulanır: her satır kullanıcının o modüldeki izni ve veri kapsamıyla süzülmelidir.
- Lisansı kapalı modülün satırı gelmemelidir; erişim olmayan veri "kayıt yok" gibi gösterilmemelidir (ADR-0010 ilkesi).

## Karar

### Kaynak sözleşmesi

Kart CRM'e aittir. `Modulix.Modules.Crm.Contracts/Timeline` altında `ICustomerTimelineSource` tanımlanır; kendi kayıtlarını çizelgede göstermek
isteyen her modül bu sözleşmeyi uygular ve DI'a kaydeder. CRM kayıtlı kaynakları birleştirir, hiçbirinin tablosuna dokunmaz.

- Sorgu: CRM firma kimliği, ortak taraf (`PartyId`) kimliği, imleç (`Before`) ve satır sayısı (`Take`). Kaynak kendi kayıtlarını hangisine
  bağlıyorsa onu kullanır.
- Satır: kayıt türü (`EntityKey`), kimlik, an (`OccurredAt`), başlık, durum ve tür kodu, sorumlu, kısa tanıtıcı (belge numarası).
- Kaynak çağıranın kimliğiyle ve kendi kurallarıyla çalışır. Okuyamıyorsa (izin yok, modül kapalı) `null` döner; boş dilim "okunabilir ama
  kayıt yok" demektir. Yanıttaki `sources` listesi okunabilen kaynakları sayar.

### CRM kaynakları (dilim 1)

Üç kaynak kayıt boru hattından okur; izin, veri kapsamı, lisans ve özel aktivite maskesi orada uygulanır:

| Kaynak | Kayıt | Satırın anı |
|---|---|---|
| `crm.activities` | Firmaya, kişilerine ve fırsatlarına bağlı aktiviteler | Kapanış, yoksa başlangıç, yoksa son tarih, yoksa oluşturulma |
| `crm.opportunities` | Firmanın fırsatları | Oluşturulma |
| `crm.quotes` | Firmanın teklifleri (her revizyon ayrı satır) | Oluşturulma |

Boru hattı oluşturulma anını dışarı vermez. Bu an, boru hattının görünür bulduğu kayıt kimlikleriyle modülün kendi tablosundan okunur; kayıt
türlerine yeni alan eklenmez (alan listesi özelleştirme, içe aktarma ve otomasyon kataloglarına yansıdığı için). Aktivite deposuna
`sort=-occurred` ve `occurredBefore`, teklif deposuna `createdBefore` süzgeci eklendi.

### Uç nokta ve sayfalama

`GET /api/crm/companies/{id}/timeline?before=&take=` (`crm.companies.read`). Firma çağıranın kapsamında değilse 404. `take` 1–100 arasıdır
(varsayılan 20). Sayfalama imleçlidir: her kaynak imleçten önceki en yeni `take` satırını verir, uç birleştirip en yeni `take` satırı döndürür;
daha eski satır varsa `nextBefore` son satırın anıdır. İmleç sınırı hariçtir: aynı ana (aynı zaman damgasına) denk gelen iki satır sayfa
sınırına düşerse ikincisi atlanabilir; pratikte zaman damgası çözünürlüğü bunu önler, kabul edilen sınırdır.

### Arayüz

Firma detayına "Zaman çizelgesi" sekmesi eklendi (`CustomerTimeline` bileşeni). Mevcut `activity-timeline` sınıfları kullanılır, yeni CSS ya da
token yoktur. Fırsat ve teklif satırı kendi sayfasına bağlanır; "Daha eski kayıtları göster" düğmesi sonraki sayfayı ekler.

## Dilimler

1. **Kaynak sözleşmesi + CRM kaynakları + uç nokta + sekme** (bu teslim).
2. Sipariş, Sözleşme, Destek ve Pazarlama kaynakları (her modül kendi PR'ında; taraf kimliğiyle).
   - **2a Sipariş (17 Kasım 2026):** `OrderTimelineSource` (`orders.orders`), Sipariş modülünde. Siparişler ortak taraf kaydına (`partyId`)
     bağlıdır; satırın anı oluşturulma anıdır, tanıtıcı sipariş numarasıdır. Sipariş deposuna `createdBefore` süzgeci eklendi (varsayılan
     sıra zaten en yeniden eskiyedir). Modül kapalıysa ya da okuma izni yoksa kaynak `sources` listesinde yoktur.
   - **Görünen ad kararı:** sözleşme değişmedi; durum ve tür kodlarının görünen adını arayüz verir (`OrderLabels` gibi modül etiket
     yardımcılarıyla, okurun dilinde). Kaynak görünen ad üretmez: çeviri altyapısı (ADR-0034) arayüzdedir. Arayüzün henüz tanımadığı
     kayıt türü genel adla ("Kayıt") ve bağlantısız çizilir; satır kaybolmaz.
   - **2b Sözleşme (17 Kasım 2026):** `ContractTimelineSource` (`contracts.contracts`), Sözleşme modülünde. Siparişle aynı kalıp: taraf
     kimliğiyle süzülür, satırın anı oluşturulma anıdır, tanıtıcı sözleşme numarasıdır; durum olarak yürürlük durumu (`effectiveStatus`)
     verilir. Sözleşme deposuna `createdBefore` süzgeci eklendi.
   - **2c Destek (21 Kasım 2026):** `TicketTimelineSource` (`support.tickets`), Destek modülünde. Aynı kalıp: talepler ortak taraf
     kimliğiyle süzülür, satırın anı açılış anıdır, başlık konu, tanıtıcı talep numarasıdır; durum talep durumudur. Destek deposuna
     `createdBefore` süzgeci eklendi. Destek modülü CRM sözleşme projesine başvurur (mimari kural tablosu güncellendi) ve bunu
     `IModule.Dependencies` ile isteğe bağlı bağımlılık olarak beyan eder (ADR-0051); CRM kapalıyken ya da Destek yokken çalışma
     zamanı davranışı değişmez. Arayüzde talep satırı ikon, durum rozeti ve talep sayfası bağlantısıyla çizilir.
   - Kalan: Pazarlama.
3. Kaynak türüne göre süzgeç ve özet şeridi (son temas, açık iş sayısı).
4. Durum değişikliği satırları (fırsat kazanıldı/kaybedildi, teklif kabul edildi) ve müşteri adayı geçmişi (dönüşen aday).

## Yapılmadı / sınırlar

- Pazarlama satırları yok (Sipariş dilim 2a, Sözleşme dilim 2b, Destek dilim 2c ile geldi); firma kartındaki ilişkili sipariş, destek ve kampanya
  listeleri yerinde duruyor.
- Bir kayıt tek satırdır: fırsatın kazanılması ya da teklifin kabulü ayrı satır üretmez (durum rozeti güncel durumu gösterir).
- Sipariş, sözleşme ve destek talebi satırları (dilim 2a, 2b, 2c) tarayıcıda gözle doğrulanmadı.
- Ekran 17 Kasım 2026'da açık ve koyu temada, 1440 px ve 390 px genişlikte gözle doğrulandı. Yeni sekmeyle firma detayının sekme çubuğu
  1440 px'te taşıyordu; "Fırsatlar ve müşteri adayları" sekmesinin adı "Satış" olarak kısaltıldı (kişi detayında da aynı etiket).
