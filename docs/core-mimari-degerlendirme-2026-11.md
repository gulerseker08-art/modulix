# Core mimari değerlendirmesi: çok sektörlü SaaS hedefi (14 Kasım 2026)

Soru: Modulix birden fazla sektöre modül barındıracaksa Core'da hatalı ya da eksik ne var, ne değişmeli, ne eklenmeli?

Salt okunur inceleme. `main` @ `6a3e51ac` (PR #377 sonrası) üzerinde, `docs/adr/` altındaki 46 kararın durum satırları ve kod taranarak yapıldı. Kod değiştirilmedi, derleme ve test çalıştırılmadı. Bu belge tek başına hiçbir şeyi uygulamaz; her madde kendi ADR'siyle ya da diliminde karara bağlanır. Genel (işletim, güvenlik, istemci, test) bulgular 14 Kasım 2026 tarihli genel mimari raporundadır (`main`'de değil, aşağıda "ADR'lerde bulunan tutarsızlıklar" bölümüne bakın); bu belge yalnızca Core'un çok sektörlü hedefe uygunluğunu ele alır.

## Özet hüküm

Core'un temeli doğru kurulmuş. Çok sektörlü hedef için üç yapısal sorun var:

1. **Sektör kodu Core'a sızmış** (gümrük, hazır listelerdeki sektör sözlüğü).
2. **Ortak iş verisi CRM'de sıkışmış** (döviz kuru, katalog) ve bazı ortak kavramlar modüllerde ayrı ayrı yazılmış (çalışma takvimi, belge numarası).
3. **Her sektör modülünün ihtiyaç duyacağı birkaç ortak servis hiç yok** (entegrasyon katmanı, belge çıktısı, iş zamanlayıcısı, dış kullanıcı kimliği).

## Sağlam olan, korunmalı

- **Tenant izolasyonu:** EF süzgeci + RLS. Modül tablosu `ITenantEntity` uygulamıyorsa ya da şeması modül anahtarı değilse model kurulurken hata verir (`ModulixDbContext.ApplyModuleModels`).
- **Proje sınırları:** `Modulix.Infrastructure` hiçbir modüle başvurmuyor; modül tabloları modelin içine modül kaydından keşfedilerek giriyor.
- **Metadata yaklaşımı:** `IModule` izin, form, lookup, içe aktarma, otomasyon, onay ve rapor beyanlarını tek yerden veriyor. Kodda sektör adına bakan koşul bulunmadı.
- **Platform tarafı:** plan, abonelik, sınır ve sınır istisnası (`Core/Platform/Plans.cs`), lisans süresi ve salt okunur mod (ADR-0028) var.

## Hatalı olan, değiştirilmesi gereken

| # | Sorun | Kanıt | Öneri |
|---|---|---|---|
| H1 | **Gümrük kodu Core'da.** "Core sektöre özel iş mantığı içermez" kuralı proje başvurularında korunuyor ama içerikte delinmiş. | `Core/Customs` (GTİP kataloğu, GTİP içe aktarma, rejim profili), `Core.Contracts/Customs`, `Infrastructure/Customs`, `ModulixDbContext`'te `GtipCatalog*` ve `RegimeProfile*` | ADR-0044 karar 3 bunu "sonraki ADR'ye" bıraktı; ADR-0046 §6 (platform başvuru tabloları) taslak. Karara bağlanıp Warehouse'a taşınmalı. |
| H2 | **Core'un hazır listelerinde sektör sözlüğü.** | `Core/Modules/CoreModule.cs:385-421`: araç muayenesi, kasko, takograf kalibrasyonu, SRC belgesi, antrepo yer türü | Belge ve yer türleri sahibi modülün `ChoiceLists` tanımına ya da sektör başlangıç paketine (`Core/Sectors/StarterPackages.cs`) taşınmalı; Core yalnızca listenin kendisini tanımlamalı. |
| H3 | **Modül dikey bütün değil.** Yeni bir sektör modülü kendi projesinin yanında Api, Shared, Web ve Infrastructure'a (migration) da dokunmak zorunda. | `Program.cs`'te elle 12 `AddHostedService` ve 50 `Map…Endpoints`; `Modulix.Api` altında modül klasörleri (Crm 17 dosya, 4'ü doğrudan `ModulixDbContext` kullanıyor; Contracts 8/2, Marketing 7/1, Support 3/2, Orders 2/1). `IModule`'da uç nokta ve arka plan işi beyanı yok. | ADR-0046 kabul edilip Dilim 0 yeni modül diliminden **önce** yapılmalı; her yeni modül bu borcu büyütüyor. |
| H4 | **Döviz kuru ve katalog CRM'de.** "Müşteri herhangi bir modülle başlayabilir" ilkesiyle çelişiyor: CRM'siz bir Lojistik ya da İK müşterisinin kuru olmaz. | Kur CRM'de (`Crm/Domain/PricingEntities.cs`, `Crm/Records/PricingRecordStores.cs`); Orders kuru tekliften kopyalıyor (`Orders/Lifecycle/OrderFromQuote.cs`). Core'da kur tablosu yok; yalnızca saf hesap var (`Core.Contracts/Pricing`). | Para birimi, kur, vergi oranı ve ölçü birimi Core ana verisi olmalı (Firma, Kişi, Adres gibi: bir kez girilir, bütün modüller kullanır). Masraf (ADR-0038 dilim 8), bordro ve faturalanabilir kalem de kur isteyecek. |
| H5 | **Çalışma takvimi iki modülde ayrı yazılmış.** | `Support/Sla/WorkCalendar.cs` (mesai) ve `Hr/Records/LeaveCalendar.cs` (tatil); Core'da yok | Tek Core takvimi (mesai saatleri + resmî tatil, tenant başına yapılandırılabilir). SLA, izin günü hesabı, onay eskalasyonu ve teslim süresi aynı takvimi kullanmalı. |
| H6 | **Belge numarası her modülde ayrı üretiliyor.** | Dört ayrı üretici: `QuoteRevisionRules.NewNumber` (teklif), `JobHistory.NewNumber` (operasyon işi), `TicketNumbers.New` (destek), `TripRecordStores.NextNumberAsync` ("SF-yıl-", sefer). Core'da numaralama servisi yok. | Tek Core numaralama servisi: tenant (gerekirse şirket) başına seri, önek ve yıl sıfırlaması firma ayarından; eşzamanlılıkta çift numara üretmeyen tek uygulama. Fatura ve irsaliye gibi kesintisiz seri isteyen belgeler gelmeden yapılmalı. |
| H7 | **Modüller arası bağımlılık beyan edilmiyor.** | `IModule`'da bağımlılık üyesi yok. `RequiredModules` yalnızca otomasyon aksiyonu düzeyinde var (`AutomationCatalog.cs:98`). Orders, `Crm.Contracts`'a başvuruyor (`AcceptedQuote`). | "Orders açık, CRM kapalı" gibi durumların kuralı `IModule`'da beyan edilmeli (zorunlu / isteğe bağlı bağımlılık) ve lisans atanırken denetlenmeli. |

## Eksik olan, eklenmesi gereken

| # | Eksik | Kanıt | Neden gerekli |
|---|---|---|---|
| E1 | **Dış entegrasyon katmanı** | API anahtarı ve API sürümü yok. Webhook yalnızca sözleşme imzasında (`Api/Contracts/ContractSignatureEndpoints.cs`) ve pazarlama gönderiminde. | e-Fatura/e-İrsaliye, U-ETDS, gümrük sistemleri, araç takip, muhasebe paketleri. Her sektör modülü kendi entegrasyonunu sıfırdan yazarsa kimlik bilgisi saklama, yeniden deneme ve denetim kaydı her seferinde yeniden çözülür. Gereken: bağlayıcı sözleşmesi, kimlik bilgisi kasası, giden webhook, sürümlü dış API. |
| E2 | **Belge şablonu ve çıktı servisi** | Ortak servis yok; PDF üretimi `Modulix.Api` içindeki modül klasörlerinde. | Teklif, sözleşme, irsaliye, CMR, bordro: hepsi firma antetli, şablonlu çıktı ister. |
| E3 | **Tek iş zamanlayıcısı** | Ortak zamanlayıcı yok; her modül kendi `BackgroundService`'ini yazıyor. | ADR-0046 §3'te öneri var. Tenant başına kilit, lisans kontrolü ve hata yönetimi bugün işçiden işçiye kopyalanıyor. |
| E4 | **Dış kullanıcı / portal kimliği** | Portal ya da dış kullanıcı kavramı yok; herkese açık uçlar tek seferlik bağlantılarla çalışıyor. | Sevkiyat takibi, antrepo stok sorgusu, destek portalı, bayi erişimi. Yetki modeline (User – Membership) dış taraf eklenmeden her modül kendi bağlantı düzenini kurar. |
| E5 | **Silme, arşiv ve saklama politikası** | `envanter.md` B3'ten beri açık; denetim kaydı için saklama kararı yok. | KVKK ve sektör mevzuatı (gümrük, İK) belge saklama süresi ister; hukuk kararını bekliyor. |
| E6 | **Yapay zekâ çekirdeği** | `Modulix.AI` projesinde kod yok; ADR-0047 "öneri". | Ürün "yapay zekâ destekli" diye tanımlanıyor; ilk dilim ADR-0047 onayını bekliyor. |

## ADR'lerde bulunan tutarsızlıklar

- **ADR-0037 dosyası yok**, ama beş ADR (0028, 0038, 0039, 0042, 0044) ona atıf yapıyor.
- **0043 numarası iki ADR'de** kullanılmış (firma sektör kataloğu, sevkiyatta ülke); 0041 de "numara geçicidir" diyor.
- **Durum satırları kodun gerisinde:** 0027, 0028, 0029, 0032, 0033, 0034 "Taslak" yazıyor, ama kodları `main`'de ve `CLAUDE.md` kuralları bunlara dayanıyor.
- **ADR-0046 "Taslak, karar bekliyor"** yazıyor; kabul edildiyse belgeye işlenmemiş.
- **Önceki genel mimari raporu `main`'de değil:** `docs/mimari-degerlendirme-2026-11.md` yalnızca `gseker/modulix-crm-architecture-476e6e` dalında (commit `96d777ef`); PR #369 başka bir taban dala birleşmişti.

## Önerilen sıra

1. **ADR-0046 kabul + Dilim 0** (H3), ardından Customs ve hazır listelerin taşınması (H1, H2). Yeni modül dilimlerinden önce.
2. **Yeni ADR: ortak iş ana verisi** — para birimi/kur, vergi, birim, çalışma takvimi, belge numaralama (H4, H5, H6). Geçiş geriye uyumlu olmalı: CRM kur tablosu Core'a taşınırken mevcut kayıtlar korunur.
3. **Yeni ADR: entegrasyon katmanı** (E1) ve modül bağımlılık beyanı (H7).
4. **ADR temizliği:** 0037, çift 0043, durum satırları.
5. E2, E3 (ADR-0046 içinde), E4: ilk ihtiyaç duyan modül dilimiyle birlikte, ama modülün içinde değil Core'da.

## Doğrulanmayanlar ve sınırlar

- Vergi oranının ve ürün/hizmet kataloğunun CRM dışındaki kullanımları tek tek izlenmedi; H4'teki kanıt döviz kuru için kesin, vergi ve katalog için ADR-0018'e ve dosya sayımına dayanıyor.
- Sipariş (`SalesOrder.Number`) ve sözleşme numarasının hangi üreticiden geldiğine bakılmadı; H6'daki dört üretici doğrulananlardır.
- Modül bağımlılığının lisans atama ekranında ayrıca denetlenip denetlenmediği incelenmedi (H7).
- `IModuleData.Set<T>()` tablonun sahibini denetlemiyor bulgusu önceki genel rapordan (B4) alındı, yeniden doğrulanmadı.
- Sınır ölçümü (kullanım sayacı) ve plan sınırlarının modüllerde nasıl uygulandığı incelenmedi.
