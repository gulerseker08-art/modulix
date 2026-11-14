# Modulix mimari değerlendirmesi (14 Kasım 2026)

Bütün projenin salt okunur incelemesi: modül sınırları, veri ve tenant izolasyonu, güvenlik ve API, istemci, test, CI ve işletim. Kod değiştirilmedi, derleme ve test çalıştırılmadı. Bulgular dosya ve satır kanıtına dayanır; kanıtı olmayanlar "doğrulanmadı" diye işaretlidir. Satır numaraları `main` @ `cfc11f7b` içindir.

> **Durum (15 Kasım 2026):** Dalga 1 main'de (PR #377: A1, A3, A7, A10, C1, D3; C2 yalnızca Report-Only). Dalga 2'nin ilk kalemi A2 (JSON log, log kapsamı, OpenTelemetry) PR #383'te. Yapay zekâ kuralları [ADR-0047](adr/0047-yapay-zeka-ajan-kurallari.md) main'de.

## Özet hüküm

Modulix'in **çekirdek mimarisi sağlam ve korunmalı**. Tenant oturumdan belirleniyor ve her istekte doğrulanıyor. RLS bağlamı salt okunur ve kapalı varsayılanla kuruluyor. Kayıt boru hattında izin, kapsam, hassas alan, denetim ve outbox aynı transaction'da işleniyor. Modül sınırları proje başvuru testleriyle sabit. ~2.600 test var, bunların ~1.285'i gerçek SQL Server'la koşan entegrasyon testi.

Zayıflık mimarinin özünde değil, üç başka alanda:

1. **Üretime ve çok örneğe hazırlık:** Data Protection anahtarları kalıcı değil, log ve izleme yok, CD yok, dosya deposu yerel disk, hız sınırları bellekte.
2. **Sınırların kenarları:** modül iş akışları Api projesinde duruyor ve mimari test Api'yi taramıyor. Gümrük kodu Core'da. Herkese açık uç iskeleti kopyalanmış.
3. **Ölçekte bakım:** istemcide bütün modüller tek derlemede (lazy loading yok), ~50 liste ekranı aynı kodu kopyalıyor, birkaç dosya 900–1.100 satır.

| Alan | Değerlendirme |
|---|---|
| Tenant izolasyonu ve veri | Güçlü |
| Yetkilendirme ve kayıt boru hattı | Güçlü |
| Modül sınırları | İyi, kenarları açık (Api, Customs) |
| Güvenlik sertleştirme | İyi, birkaç orta bulgu |
| İstemci mimarisi | Tutarlı, ölçek sorunu var |
| Test | Geniş, arayüz ve uçtan uca test yok |
| İşletim ve gözlemlenebilirlik | **En zayıf alan** |

## Korunması gerekenler

- **Tenant bağlamı:** `TenantSessionContextInterceptor` her bağlantı açılışında `sp_set_session_context ... @read_only = 1` çalıştırıyor. Tenant yoksa hiçbir satır görünmüyor (kapalı varsayılan). Tenant ve şirket oturumdan alınıp her istekte üyeliğe karşı yeniden doğrulanıyor (`Api/Tenancy/Tenancy.cs:56-124`).
- **Kayıt boru hattı:** tenant, lisans ve izin sırasıyla denetleniyor (`Core/Records/RecordPipeline.cs:586-610`). Kapsam sorguya uygulanıyor. Gizli alanlar yanıttan çıkarılıyor; bu alanlarla süzme, sıralama ve yazma reddediliyor. Denetim kaydı ve outbox aynı `SaveChanges` içinde. Api uç noktalarında boru hattını atlayan yazma bulunmadı.
- **Kimlik:** sunucu tarafı oturum tablosu sayesinde oturum anında iptal edilebiliyor. MFA platformda zorunlu, firma isterse üyelerine de zorunlu kılabiliyor. Hesap bilgisi sızıntısına karşı eşit süreli yanıt veriliyor. Dosya yüklemede dosya imzası (magic byte) ve uzantı izin listesi var.
- **Mimari testler:** proje başvuru tablosu, Core.Contracts ad alanı izin listesi, RLS ve tablo envanteri, modül veri erişimi.
- **Çok örnekli işçi güvenliği:** işçiler tenant başına `sp_getapplock` kullanıyor; otomasyonda kira (`LeaseUntil`), e-postada koşullu UPDATE var.
- **Metadata yaklaşımı:** `IModule` izin, form, seçim listesi, onay ve içe aktarma tanımlarını tek yerden veriyor. Kodda sektöre özel `if` görülmedi.
- **İstemci disiplini:** tek `ApiClient` kapısı var (`ApiResult` + ProblemDetails). .razor içinde sabit hex renk sıfır. Ortak bileşenler yaygın kullanılıyor: `LoadingState` 196 dosyada, `AuthGate` 151, `ViewTabs` 53. Erişilebilirlik ve mobil kart listesi var.

## Bulgular (öncelik sırasıyla)

Önem düzeyleri: **Y** = Yüksek, **O** = Orta, **D** = Düşük.

### A. Üretim ve işletim

| # | Bulgu | Önem | Kanıt | Öneri |
|---|---|---|---|---|
| A1 | Data Protection anahtarları kalıcı değil | Y | `src` içinde `AddDataProtection` yok. Kimlik Identity çerezi (`Api/Auth/Auth.cs:28`); davet ve sıfırlama belirteçleri de buna bağlı. | Anahtarlar bir platform tablosunda ya da paylaşımlı depoda tutulsun, sertifikayla korunsun. Yapılmazsa her yeniden başlatmada bütün oturumlar ve e-postadaki bağlantılar düşer; birden fazla pod çalışamaz. |
| A2 | Yapılandırılmış log, metrik ve izleme yok | Y | Serilog, OpenTelemetry ve hata izleme paketi yok. TenantId/CorrelationId log kapsamı yok. Yol haritası Ç14.5 bunu kabul ediyor. | OpenTelemetry (log, trace, metrik). TenantId ve TraceId log kapsamına girsin; kuyruk yaşı ve işçi turları metrik olarak yayımlansın. Denetim kaydındaki `CorrelationId` buna hazır bir temel. |
| A3 | Kimliği doğrulanmış API'de genel hız sınırı yok | O→Y | `Api/Auth/Hardening.cs:51-58`: yalnızca adlandırılmış ilkeler var, `GlobalLimiter` yok. | Kullanıcı ve tenant bölümlemeli genel sınır; dışa aktarma ve rapor uçlarına daha sıkı ilke. Tek paylaşımlı veritabanında bir tenant'ın yükü bütün tenant'ları etkiler. |
| A4 | Durum tek düğüme bağlı | O | Yalnızca `LocalBlobStorage` var (`PersistenceSetup.cs:187`). İçe aktarma hata raporu `IMemoryCache`'te (ADR-0033). Hız sınırları bellekte. | S3 veya Azure Blob uyumlu depo adaptörü; paylaşımlı önbellek ya da veritabanı tabanlı sınır. |
| A5 | Sürekli dağıtım (CD) ve imaj taraması yok | O | `.github/workflows/docker-image.yml` yalnızca elle tetikleniyor (`workflow_dispatch`). | main sonrası imaj üret → tara (Trivy) → staging'e `migrate` → uygulama. |
| A6 | Migration geriye uyumluluğu sınanmıyor | O | CI migration'ları yalnızca boş veritabanına uyguluyor. | Bir önceki sürümün entegrasyon testlerini yeni şemaya karşı koşan bir job. Tek veritabanında uyumsuz bir migration bütün tenant'ları birden durdurur. |
| A7 | `/health` tek uç; liveness ve readiness ayrımı yok | O | `Program.cs:152-154`. E-posta kuyruğu kontrol edilmiyor; Dockerfile'da `HEALTHCHECK` yok. | `/health/live` ve `/health/ready` ayrılsın; kuyruk yaşı kontrolleri eklensin. |
| A8 | Yedek ve geri yükleme yalnızca belgede | O | `docs/tenant-isletim.md:119-127`; geri yükleme provası yok. | Veritabanı ve blob yedeği birlikte alınsın; düzenli geri yükleme provası yapılsın. |
| A9 | Denetim kaydı için saklama ya da bölümleme kararı yok | O | Otomasyon (`AutomationRetention`), iletişim ve oturumlar için temizlik işi var; `AuditLog` için yok (git grep). | Bilinçli bir karar yazılsın: süresiz saklama mı, tarihe göre bölümleme/arşiv mi? Tablo en hızlı büyüyen tablo olacak. |
| A10 | İşlenmeyen hata yanıtı ve hata kodları tutarsız | D | `AddProblemDetails` var (`Program.cs:68`), `UseExceptionHandler` ve `UseStatusCodePages` yok. Alan hataları bir yerde 422, başka yerlerde 400. | İkisi eklensin; alan hataları için tek kod (422) seçilsin. |

### B. Modül sınırları ve kod yapısı

| # | Bulgu | Önem | Kanıt | Öneri |
|---|---|---|---|---|
| B1 | Modül iş akışları Api projesinde ve mimari testin dışında | Y | Api altında 9 modül klasöründe 44 dosya var: işçiler, PDF üretimi, herkese açık portallar. 26 dosyada 74 doğrudan `ModulixDbContext` kullanımı (ör. `OrderJobsWorker.cs:67`, `ImportRollback.cs:125`). `ModuleDataAccessRulesTests` yalnızca `Modules.*` ve `AI`'ı tarıyor. | `IModule`'a uç nokta eşleme ve hosted service kaydı eklensin; işçi ve PDF kodu modüle taşınsın. Api'deki modül klasörleri için mimari test yazılsın. |
| B2 | Gümrük (Customs) Core ve Infrastructure içinde | Y | `Core/Customs/*`, `Infrastructure/Customs/GtipCatalogService.cs`, `ModulixDbContext.cs:204-206`. Testte geçici istisna (`CoreContractsNamespaceRulesTests.cs:43`). | ADR-0044'teki açık soru 3 karara bağlansın, sonra Customs Warehouse'a ya da ayrı bir modüle taşınsın. |
| B3 | İşçi ve herkese açık uç iskeleti kopyalanmış; sahte "neden" olayı | O | `OrderJobsWorker` ile `SupportJobsWorker` arasında yalnızca 54 satır fark var. Sistem kimliği kurmak için `RecordDeleted` + `Import` içeren sahte bir `EventEnvelope` 10 dosyada tekrarlanıyor. Herkese açık uçlarda tenant çözümü tutarsız: bazı uçlar yalnızca kısa adla (slug), bazıları Guid ile de çözüyor. | Ortak `TenantJobRunner<T>`, `PublicTenantScope.OpenAsync(...)` ve `EventHandlingScope.BeginSystem(...)`. Ayrı bir `ExecutionSource.PublicForm`. |
| B4 | `IModuleData.Set<T>()` tablonun sahibini denetlemiyor | O | `Core.Contracts/Persistence/IModuleData.cs:17`, `Infrastructure/Persistence/ModuleData.cs:11-12`. `Core.Contracts` EF'e bağımlı. | Çalışma zamanında şema = modül anahtarı denetimi, ya da modül başına tipli `IModuleData<TModule>`. |
| B5 | Dizgeyle kurulan gizli modüller arası bağımlılık | D | `Support/Lifecycle/TicketLinks.cs:15-17` (`"orders.order"`), Marketing'de `"crm.lead"` dört yerde elle yazılmış. | Varlık anahtarları sahibi olan modülün `.Contracts` projesindeki sabitlerden alınsın. |
| B6 | Büyük sınıflar | O | `CrmModule.cs` 1.128 satır, `ApprovalService.cs` 923, `UserAdministration.cs` 866, `ForecastWorkflow.cs` 857, `RoleService.cs` 775. | `CrmModule` tanım dosyalarına bölünsün (Forms, Permissions, ChoiceLists); servisler komut başına sınıflara ayrılsın. |
| B7 | Ad çakışması: `Modulix.Modules.Contracts` | D | Bu proje bir iş modülü (sözleşme yönetimi, `ModuleKey = "contracts"`). ADR-0044'teki `.Contracts` sözleşme projeleriyle karışıyor; `Modules.Contracts.Tests` de öyle. | `Modulix.Modules.Agreements` olarak yeniden adlandırılsın (modül anahtarı değişmeden). |
| B8 | Core seed verisinde sektör sözlüğü | D | `Core/Modules/CoreModule.cs:352-398`: takograf, kasko, antrepo yer türü. | Modül `ChoiceLists` tanımlarına ya da sektör başlangıç paketlerine taşınsın. |
| B9 | Elle kompozisyon | D | `Program.cs`'te 12 `AddHostedService` ve 39 `Map*Endpoints` elle yazılmış. | B1 ile birlikte modül döngüsüne geçilsin. |

### C. Güvenlik

| # | Bulgu | Önem | Kanıt | Öneri |
|---|---|---|---|---|
| C1 | Uç nokta koruma testi kör noktalı | O | `IntegrationTests/EndpointProtectionTests.cs:121,170-187`: rolsüz üye için yalnızca 2xx yanıtlar hata sayılıyor; yol parametresi rastgele, gövde boş. | 403 dışındaki her yanıt "korumasız" sayılsın, ya da `RequirePermission` metadata'sı uç düzeyinde doğrulansın. Bugün izin denetimi unutulmuş bir yazma ucu 404 ya da 422 döndüğü sürece test geçer. |
| C2 | CSP betik kaynağını kısıtlamıyor | O | `Hardening.cs:88`. | `script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'`. |
| C3 | Kilitleme hesabın var olduğunu ele veriyor ve hedefli kilitlemeye açık | D | `Auth.cs:162-178`: var olan hesap için 423, olmayan için 401. | Her durumda genel 401; kilit yerine kademeli gecikme. |
| C4 | Destek erişiminin salt okunurluğu yalnızca rol izinlerine bağlı | D | `SupportAccessService.cs:170-190`. | Destek üyeliğinde okuma dışı HTTP yöntemleri middleware'de sert biçimde reddedilsin. |
| C5 | Dış sözleşmeler sürümsüz | D | `/api/...` yollarında sürüm yok; herkese açık uçlar (lead formu, imza webhook'u, teklif bağlantısı) dışarıya açık. | En az `/api/public/v1`. |
| C6 | Küçük notlar | D | HSTS `includeSubDomains` yok; chunked yükleme yalnızca Kestrel'in 30 MB varsayılanıyla sınırlı. | — |

### D. İstemci (Blazor WASM)

| # | Bulgu | Önem | Kanıt | Öneri |
|---|---|---|---|---|
| D1 | Bütün modüller tek derlemede, lazy loading yok | Y | `Modulix.Web.csproj` içinde `BlazorWebAssemblyLazyLoad` yok; `App.razor`'da `OnNavigateAsync` yok. Yalnızca Lojistik lisanslı müşteri de CRM, İK ve Platform ekranlarının kodunu indiriyor. | Modül başına Razor sınıf kütüphanesi + `LazyAssemblyLoader`. Önce yayın boyutu ölçülsün (doğrulanmadı). |
| D2 | Liste ekranı kalıbı kopyalanmış | O | `ChooseViewAsync` 50 dosyada, `SortAsync` 52'de, `new PersonalGridViews` 47'de. | Tek `GridPage<TDto>` taban bileşeni. Kişisel görünüm dilimi bundan önce yapılırsa yaklaşık 50 dosyaya dokunur. |
| D3 | Oturum düşünce girişe yönlendirme yok | O | `ApiClient.cs:78` 401'i yalnızca iletiye çeviriyor. | Ortak bir işleyici (`DelegatingHandler`) `SessionState`'i sıfırlasın, `AuthGate` yönlendirsin. |
| D4 | Çeviri (ADR-0034) yarım | O | Admin 31/31, İK 22/22, Platform 15/15, Lojistik 8/8 dosyada `Texts.` hiç kullanılmıyor; bileşenlerin 68/181'i de öyle. | Kalan dilimler bu sayılarla planlansın. |
| D5 | Büyük ekranlar | O | `ReportBuilder` 1.243 satır, `ContractDetail` 1.155, `OpportunityDetail` 1.119. `Components/` 181 dosyalı düz bir klasör. | Alt bileşenlere bölünsün; `Components/` modüllere göre klasörlensin. |
| D6 | CSS tek dosya; tanımsız değişken | D | `app.css` 7.232 satır. `5481-5482` ve `5521`'de tanımsız `--surface`/`--border` var; koyu temada sabit renge düşüyor. `4333` ve `4338`'de token dışı renk. | Dosya bölümlere ayrılsın; `--mx-*` tokenları kullanılsın. |
| D7 | Shared'a sunum etiketleri sızmış | D | `Shared/Imports/ImportContracts.cs:58-82`, `AccessContracts.cs:90-143`. | Etiket ve rozet eşlemeleri `Modulix.Web`'e taşınsın. |
| D8 | Kalan "Yükleniyor…" düz metinleri | D | `Logistics/Places.razor:45`, `Platform/Industries.razor:53`, `Admin/SupportAccess.razor:60` ve diğerleri. | `LoadingState` kullanılsın. |

### E. Test

| # | Bulgu | Önem | Kanıt | Öneri |
|---|---|---|---|---|
| E1 | Arayüz ve uçtan uca test yok | O | `tests/Modulix.E2ETests` boş (yalnızca csproj); bUnit ve Playwright yok. | Kritik akışlar için bUnit + birkaç Playwright testi (giriş, firma, fırsat, teklif), ya da boş proje kaldırılsın. |
| E2 | Modül birim testleri ince | D | Fleet 11, Warehouse 21, İK 25 test; ağırlık entegrasyon testlerinde. Entegrasyon seti tek job'da ~52 dk. | Kural ve hesaplama mantığı birim teste çekilsin; entegrasyon süresi kontrol altında tutulsun. |

## Ürün ve süreç düzeyinde öneriler

Önceki CRM değerlendirmesinden geçerli olanlar:

- **Kapsamı odakla.** CRM, Lojistik, Filo, İK ve Antrepo paralel ilerliyor; A1–A3 gibi üretim önkoşulları ise açık. Pilot öncesinde yeni modül dilimleri yavaşlatılıp "üretime hazırlık" dalgası araya alınmalı.
- **CRM'in eksikleri:** çoklu satış süreci ve aşama bilgisi (ADR-0017), e-posta/takvim eşitlemesi (ADR-0015), fırsat düzeni kuralları (altyapı var, kural yazılmadı), kazanıldıktan sonra operasyona devir.
- **Düzeltme:** ilk değerlendirmede "rıza altyapısı yok" demiştim, bu yanlıştı. `ConsentEvent` ve iletişim tercihleri ADR-0025 ile var, gerçek gönderim sağlayıcısı da bilerek kapalı. Eksik olan İYS entegrasyonu ve KVKK metinleri için hukuki karar.
- **Yapay zekâ:** kurallar ADR-0047 taslağında. İlk dilim A2 (izleme) ve A3 (hız sınırı) olmadan üretime alınmamalı.

## Önerilen yol haritası

**Dalga 1, üretime hazırlık (2–3 hafta):** A1 Data Protection, A3 genel hız sınırı, A10 hata işleyici, C1 uç nokta koruma testi, C2 CSP, A7 health ayrımı, D3 401 yönlendirmesi. Hepsi küçük, bağımsız ve riski yüksek kalemler.

**Dalga 2, işletim (3–4 hafta):** A2 OpenTelemetry, A4 nesne deposu, A5 CD + imaj taraması, A6 migration uyumluluk job'u, A8 yedek provası, A9 denetim saklama kararı.

**Dalga 3, sınırlar ve bakım (sürekli, modül dilimleriyle birlikte):** B1 modül uçları ve işçilerinin `IModule`'a taşınması + mimari test, B3 ortak iskeletler, D2 `GridPage` (kişisel görünüm diliminden önce), D1 lazy loading, B2 Customs taşıma, B7 yeniden adlandırma, E1 uçtan uca test.

## Doğrulanmayanlar ve sınırlar

- Veri katmanı incelemesi kısmen yapıldı. Kontrol edilenler: RLS bağlam kurulumu, ham SQL kullanımı (yalnızca `ModuleData` ve `EventDispatcher`), eşzamanlılık belirteçleri (modül tablolarında `Version` var), temizlik işleri. İndeks stratejisi ve sorgu planları ayrıntılı incelenmedi; tenant tablolarındaki bileşik indekslerin `TenantId` ile başlayıp başlamadığı tek tek denetlenmedi.
- Gerçek WASM yayın boyutu, CI süreleri ve üretim yapılandırması (anahtarların dışarıdan bir birime bağlanıp bağlanmadığı) ölçülmedi.
- Modüle özel pano ve rapor uçlarının hassas tutar alanlarını toplamlarda gizleyip gizlemediği yalnızca `CompanySummaryEndpoint` ve `ReportColumnResolver` için doğrulandı.
