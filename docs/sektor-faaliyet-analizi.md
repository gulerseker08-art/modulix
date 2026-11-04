# Sektör, faaliyet ve modül profili — mevcut durum analizi ve uygulama planı

Tarih: 3 Kasım 2026. Durum: **analiz; kabul edilmedi.** Karar önerileri `docs/adr/0029-sektor-profili-genisletme-ve-lojistik-kurulum.md` taslağındadır. Bu belge kod okumasına dayanır; hiçbir test çalıştırılmadı.

Kanıt notu: Satırlar ilgili dosya/sembolle verilir. Bazı satır numaraları ve Blazor tarafı (`LayoutForm`, `Web`) baştan sona okunmadı (bölüm 10). Görülmeyen özellik var sayılmamıştır.

## 1. Mevcut durum ve eksik analizi

| Bileşen | Mevcut durum | Kod kanıtı | Eksik/risk | Önerilen değişiklik | Bağımlılık | Öncelik |
|---|---|---|---|---|---|---|
| Tenant çözümleme/izolasyon | Tenant yalnızca oturumdan gelir, her istekte doğrulanır; EF query filter + SQL RLS (FILTER + BLOCK) | `Api/Tenancy/Tenancy.cs:54-70`, `TenantIsolationSql.cs:16-66` | Şirket kapsamı DB'de değil uygulamada | Yeni tenant tabloları aynı kalıpla (`ITenantEntity` + `ICompanyShared`, `AddTable`) | — | Yüksek |
| Platform yöneticisi sınırı | `/api/platform` ayrı filtre; üyeliksiz tenant bağlamı kurulmaz | `PlatformEndpoints.cs:17-28`, `Tenancy.cs:60-66` | Sınırı zorlayan ayrı test bulunamadı; `PlatformAdministration` tenant bağlamı açabilir | Kapsama kuralı + test: katalog/paket uçları tenant iş verisi döndürmez | — | Yüksek |
| Modül kaydı/lisans | `IModule`, `ModuleCatalog` açılışta doğrular; `platform.ModuleEntitlements` yalnızca açık/kapalı | `IModule.cs:16-90`, `ModuleCatalog.cs:30-80` | Bu belgenin ilk sürümü `main`'den eskiydi: `main`'de ADR-0028 (platform lisans, taslak) ve `PlatformLicenseFoundation` migration'ı var; plan/süre/kaynak/limit orada ele alınıyor. Sektör→modül açma yalnızca profil uygulamasıyla | Faaliyet↔yetenek↔modül eşlemesi katalogda; modül açma etkin hak çözümleyicisi (ADR-0028 lisans) üzerinden, çakışma yok | ADR-0028 lisans | Orta |
| Rol/yetki/kapsam | User–Membership–Company–BusinessUnit–Team–Role; rol başına tek kapsam | `Role.cs:15,59`, ADR-0028 (taslak) | Kayıt türüne göre kapsam yok | Bu işte değiştirme; sürücü/maliyet gizleme ayrı iş | ADR-0028 | Düşük |
| Ayar deposu | `TenantSetting` + `IModule.Settings` tanımı, modül önekli | `TenantSetting.cs:15-33`, `ModuleCatalog.cs:437-460` | Çoklu seçim için uygun değil | Seçimler ayrı tablo; faaliyet ayarları ayar deposunda | — | Orta |
| Audit/outbox | `AuditLogEntry` RLS altında; outbox platformda | `RecordInfrastructure.cs:18,73,154-173` | Platform yöneticisi değişiklikleri `core.tenant` kalıbıyla yazılır | Katalog/paket/seçim değişiklikleri denetime | — | Yüksek |
| Önbellek/işler | Merkezi tenant/ayar önbelleği bulunamadı; üyelik kapsamı önbellekli; çok sayıda worker | `docs/tenant-isletim.md:92-97` | Profil önbelleği henüz yok | Etkin profil sürüm numarasıyla anahtarlanır, yayında artar | Paket sürümü | Orta |
| Ortak Firma/Kişi | `Party`/`Person` Core.Contracts, `core` şeması; rol alanı yok (rol modül uzantısından) | `SharedRecords.cs:62,98` | Müşteri/tedarikçi/taşıyıcı etiketi ve rol bazlı sorgu yok | İlişki rolü kataloğu (bölüm 8) | Katalog | Yüksek |
| Adres/tesis | `Location`, `Address`, `ContactPoint` CRM şemasında | `CrmEntities.cs:505,534,616` | CRM kapalıyken yok; ülke/tesis tipi özelliği yok | Temel tesis kimliği Core'a; ayrıntı özellikleri tesis özelliği olarak (bölüm 4) | Sahiplik kararı | Orta |
| Ürün/hizmet kataloğu | `Product`, `MeasureUnit`, fiyat listesi CRM şemasında | `CatalogEntities.cs`, `PricingEntities.cs` | CRM kapalıyken yok; minimum ücret yok | Ortak hizmet tanımı sahipliği ayrı karar; bu işte yalnızca tanım+bağ | ADR | Orta |
| Lead/fırsat/teklif | CRM; `QuoteLine` türleri Product/Service/Package/Free, birim, grup | `QuoteEntities.cs:10,125` | Tahmini/gerçekleşen ayrımı ve minimum ücret yok; kabul edilmiş teklifte bağımsız snapshot yok | Hizmet kapsamı satırı + ücret türü; kabul anında snapshot | Hizmet tanımı | Orta |
| Sözleşme/sipariş | Ayrı modüller; teklife `IAcceptedQuoteSource` ile bağlı | `AcceptedQuote.cs:106` | — | Dokunma | — | — |
| Özel alanlar | 7 tip, kayıt türü başına 30 alan; yalnız `CustomizableEntities` beyan edenlerde | `CustomField.cs`, `RecordCustomFields.cs` | Lookup tipi yok; koşullu zorunluluk yok; yalnız Choice süzülür | Mevcut alanlar sektör paketiyle eklenir; yeni tip bu işte yok | — | Orta |
| Form düzeni | `IModule.Forms`, `TenantFormLayout`, sunucu zorunluluk denetimi | `FormDefinition.cs`, `FormLayoutService.cs` | Koşullu göster/gizle/zorunlu yok; yalnız CRM ve Sözleşme formları var | Alan kuralı katmanı (bölüm 6) | Alan kuralı | Yüksek |
| Seçim listesi | Kategori düz, tek seviye, kodda sabit; lookup `ParentId` var | `ChoiceListRules.cs`, `LookupDefinition.cs` | İki kademeli/yönetilebilir ağaç için yetersiz | Faaliyet ağacı liste değil, ayrı tablo | — | Yüksek |
| Kural altyapısı | ADR-0027 Dilim 0+1: sözleşme, doğrulayıcı, depo, önizleme; **yürütme yok**; göster/gizle/zorunlu aksiyonu yok | `AutomationEndpoints.cs`, ADR-0027 | Kritik doğrulama bilerek otomasyona bırakılmaz | Koşul modelini (`ConditionNode`, evaluator) yeniden kullanan ayrı **alan kuralı** katmanı | ADR-0027 | Yüksek |
| Lead nitelendirme | `ILeadQualificationRule`, kodda sabit | `LeadWorkflow.cs`, `CrmModule.cs:1087` | Tenant yapılandıramaz | Alan kuralı "doğrula" aksiyonuyla kapsanır | Alan kuralı | Orta |
| Sektör profili | Tek `ProfileKey`; yalnızca ekler (alan, liste değeri, modül) | `SectorProfile.cs`, `Tenant.cs:47-60` | Çoklu seçim, sürüm, form/kural/rol yok | Seçim tablosu + paket sürümü; eski profil "genel" olarak kalır | Katalog | Yüksek |
| Seed/migration | Tek zincir `migrate`; `DemoDataSeeder` yalnız Development; paket sürümü kavramı yok | `Program.cs:41-49` | Paket yayını için mekanizma yok | `platform.ProfilePackage` sürümlü, atomik yayın | — | Yüksek |
| Yönetim ekranları | Sektör profili, özel alanlar, listeler, form tasarımı, ayarlar, roller | `Pages/Admin/*`, `AppCatalog.cs` | Katalog/paket/seçim ekranları yok | Bölüm 7 | Servisler | Yüksek |
| Mimari testler | Proje referansı, modül kaydı, veri erişimi, otomasyon tarafsızlığı | `tests/Modulix.ArchitectureTests`, `TableInventoryTests.cs` | Yeni proje → kural tablosu; yeni tablo → RLS | Yeni proje eklemeden `Modulix.Core` içinde | — | — |
| Logistics | İskelet: izin, 1 rol şablonu, otomasyon beyanı; kayıt deposu ve form yok | `LogisticsModule.cs:49-78` | Katalogda "lojistik" olması modülün hazır olduğu anlamına gelmemeli | Katalog düğümü `ModuleReadiness` taşır | — | Orta |

## 2. Veri sahipliği matrisi

| Varlık | Sahip bileşen | Tüketen modüller | Düzenleme yetkisi | Entegrasyon biçimi |
|---|---|---|---|---|
| Party/Person kimliği | Core (`core`) | CRM, Destek, Pazarlama, Sözleşme, Sipariş | Modül izinleriyle (ortak kayıt izni) | `Core.Contracts` kayıt arayüzü |
| Firma ilişki rolü (müşteri, tedarikçi, taşıyıcı…) | Core (yeni, bölüm 8) | Tüm modüller | Yönetici; modül kendi uzantısını açar | Rol kataloğu + Party'ye bağ |
| Adres | Bugün CRM; hedef Core | CRM, Lojistik, Depo | Modül izni | Servis arayüzü (taşıma ayrı iş) |
| Temel tesis kimliği | Core (hedef) | CRM, Depo, Lojistik | Yönetici | Tesis kimliği; ayrıntı modülde |
| Tesis ülke/tür özellikleri (ör. antrepo sınıfı) | Katalogda tanım (Core), değer tesis kaydında | Depo/Antrepo, CRM görüntüler | Yönetici | Özellik değeri tesise bağlı |
| Sektör/faaliyet katalogu | Core (`platform`, global) | Tüm modüller | Platform yöneticisi | Okuma servisi |
| Tenant faaliyet seçimi | Core (`core`, tenant tablosu) | Tüm modüller | Tenant yöneticisi (platform atayabilir, `ITenantScopeFactory` ile) | Etkin profil servisi |
| Hizmet/ürün tanımı | **Açık karar** (bugün CRM) | CRM, Depo, Lojistik | Satış/ürün yöneticisi | Ortak tanım + modül uzantısı |
| Lead, fırsat, aşama, aktivite, kazanma/kaybetme | CRM | Raporlar, Pazarlama | CRM izinleri | CRM API/olay |
| Fırsat hizmet kapsamı | CRM | Teklif | Satış | CRM içi |
| Teklif | CRM (ayrı karar: ortak ticari süreç) | Sipariş, Sözleşme | Satış | `IAcceptedQuoteSource` |
| Sözleşme, sipariş | Kendi modülleri | — | Kendi izinleri | Mevcut |
| Stok, raf, antrepo hareketi | Depo/Antrepo (yok) | — | Depo | Ayrı epic |
| Sevkiyat, sefer | Taşıma/Lojistik | — | Operasyon | Ayrı epic |
| Araç bakımı | Filo (yok) | — | Filo | Ayrı epic |

Fiyatlandırma/teklif/sözleşme/sipariş "ortak kullanılıyor" diye Core'a taşınmaz; mevcut `IAcceptedQuoteSource` sözleşme sınırı korunur.

## 3. Sektör/faaliyet modeli

Sekiz kavram ayrı tutulur; tek seçenek listesinde birleştirilmez.

| Kavram | Temsil | Tablo/yer |
|---|---|---|
| Sektör, faaliyet grubu, alt faaliyet | Aynı ağaç, `Kind` ile (kodda seviye sınırı yok) | `platform.ActivityNodes` |
| Hizmet özelliği | Ayrı katalog; düğümlere çoktan çoğa bağlanır (tekrar tanım yok) | `platform.ServiceFeatures`, `ActivityNodeFeatures` |
| Modül | Mevcut `IModule` | kodda |
| Yetenek | Ayrı katalog + bağımlılık grafiği (ağaçtan ayrı) | `platform.Capabilities`, `CapabilityDependencies` |
| Düğüm↔yetenek | Hangi faaliyet hangi yeteneği gerektirir/önerir | `ActivityCapabilities` |
| Tenant seçimi | Açıkça seçilen düğümler + özellik değerleri | `core.TenantActivitySelections`, `TenantActivityFeatureValues` |
| Modül profili | Paket (sürümlü) → alan/form/kural/şablon | `platform.ProfilePackages` (sonraki dilim) |

Düğüm alanları: `Id`, `Code` (sabit, benzersiz), `ParentId`, `Kind`, `Name`, `Description`, `SortOrder`, `IsActive`, `ModuleReadiness`, `Version`. Ad değişimi referansı bozmaz (referans `Id`/`Code`). Silme yok, pasife alma var.

Kurallar:
- Döngü ve kendine bağlanma, taşıma sırasında üst zincir taranarak reddedilir.
- Düğüm taşıma: önce etki raporu (alt düğüm sayısı, tenant seçimi, paket bağı) üretilir; seçimli düğüm başka sektöre taşınmaz, yalnız pasifleştirilir ve yenisi açılır.
- Üst başlık seçmek alt faaliyetleri etkinleştirmez. Etkin üst bağlam seçimden **türetilir** (saklanmaz), açıkça seçimle karışmaz.
- Karşılıklı dışlama: özellik seçeneği grubu `ExclusionGroup` ile.
- Eksik bağımlılık: seçim önizlemesi, yetenek grafiğinde karşılanmayan yetenekleri listeler; yayın/etkinleştirme engeli değil uyarı veya engel (kural alanı).
- Tenant, katalogu değiştirmez. Tenant uyarlaması paket katmanında (bölüm 5).

## 4. Lojistik başlangıç paketi (öneri; doğrulanmadı)

Taşımacılık (karayolu, denizyolu, havayolu, demiryolu), Depolama (gümrüklü antrepo, serbest depolama), Dağıtım ve son kilometre, Taşıma organizasyonu, Transit aktarma, Diğer. Özellikler: komple/parsiyel, yurt içi/uluslararası, sıcaklık kontrollü.

- Bu sınıflandırma ürün önerisidir; Makaroğlu'nun gerçek işleyişiyle doğrulanmadı.
- "Antrepo sınıfı/tipi" tesis özelliğidir ve ülkeye bağlıdır: firma türüne veya tenant genelindeki tek alana dönüştürülmez.
- Mevzuat zorunlulukları pazarlama metninden türetilmez; doğrulanmamış hukuki kural zorunlu üretim kuralı yapılmaz (paket kurallarında `Doğrulanmadı` etiketi).
- Katalogdaki faaliyet, operasyon modülünün hazır olduğunu söylemez (`ModuleReadiness`).

## 5. Profil birleşim kuralları

Katmanlar (alttan üste): ürün standardı → sektör paketi → tenant uyarlaması → (ileride) şirket/şube/tesis kapsamı → kullanıcı görünüm tercihi.

- Ortak alan kod bazında bir kez gösterilir. Aynı kod farklı anlamla gelirse **çakışma raporu**, yayın engeli.
- Tamamlayıcı bölümler birleşir; çelişen zorunluluk/varsayılan tespit edilir ve raporlanır.
- Tenant uyarlaması güvenlik kuralını (kod zorunluluğu, yetki) kaldıramaz; kullanıcı tercihi zorunluluğu/erişimi aşamaz.
- Açıklama: her etkin alan/kural için kaynak paket + kural kodu üretilir ("neden görünüyor/zorunlu?").
- Girdi seti: etkin modüller, tenant seçimi, özellik değerleri, firma hizmet profili, fırsat/teklif hizmet kapsamı, süreç aşaması, kullanıcı yetkisi.

Mevcut `SectorProfile` "yalnızca ekler" ilkesi korunur; yeni sürümlü paket onun üst kümesidir. Eski tenant'lar `general` benzeri varsayılanla aynı davranışı sürdürür.

## 6. Dinamik koşul matrisi (alan kuralı)

ADR-0027 otomasyondur ve **yürütmesi yoktur**; ayrıca kritik doğrulamayı bilerek otomasyona bırakmaz. Bu yüzden form/kayıt kuralları için ayrı bir **alan kuralı** katmanı önerilir, koşul modelini (`ConditionNode`, evaluator, `RecordFacts`) yeniden kullanarak; ikinci bir altyapı değil.

| Aksiyon | UI | API/Import | Not |
|---|---|---|---|
| Göster/gizle | Evet | Hayır (yalnız görünürlük) | Gizlenen alan değeri silinmez; sunucu yetkisi görünürlükten bağımsız |
| Salt okunur | Evet | Evet | |
| Koşullu zorunlu | Evet | Evet | Yaşam döngüsü aşamasına göre: taslak, teklif onayı, hizmet aktivasyonu |
| Lookup filtrele | Evet | Evet | Yetki filtreli |
| Varsayılan öner | Evet | Hayır | Kullanıcı değerini ezmez |
| Doğrula / uyar / engelle | Evet | Evet | Aynı sunucu kodu |
| Onay akışına yönlendir | Evet | Evet | Yalnız mevcut onay mekanizması varsa |

- Boş, "hayır" ve "uygulanamaz" ayrı değerlendirilir; operatörler tip uyumlu.
- Yayın öncesi: döngü, gizli+zorunlu, çelişen aksiyon, maliyet sınırı (koşul derinliği/yaprak sayısı üst sınırı, ilişki sorgusu yok) denetlenir.
- Serbest SQL/kod çalıştırılmaz. UI, API ve import aynı değerlendirici kodunu çağırır.
- Kural: kod, sürüm, kapsam, tetikleyici, AND/OR koşul, aksiyon, mesaj, öncelik, geçerlilik, taslak/yayın.

## 7. Yönetim ekranları

| # | Ekran | Yetki | Dilim |
|---|---|---|---|
| 1 | Sektör ve faaliyet ağacı | Platform | B |
| 2 | Hizmet özelliği/seçenek katalogları | Platform | B |
| 3 | Faaliyet–yetenek–modül eşleştirmesi | Platform | B |
| 4 | Sektör paketi ve sürümü | Platform | C |
| 5 | Tenant sektör/faaliyet çoklu seçim | Tenant yöneticisi | B |
| 6 | Faaliyete bağlı özellik ayarı | Tenant yöneticisi | B |
| 7 | Modül profili ve form bölümü eşlemesi | Platform | C |
| 8 | Kural editörü | Platform + tenant (sınırlı) | C |
| 9 | Örnek kayıtla profil önizleme | İkisi | C |
| 10 | Yayın etki analizi | Platform | G |
| 11 | Tenant uyarlamaları/paket farkı | Tenant yöneticisi | G |
| 12 | Değişiklik geçmişi | İkisi | G |

Ortak gereksinimler: arama, sayfalama/yavaş yükleme, klavye erişimi, boş/yükleniyor/hata durumları, eşzamanlı düzenleme için `Version` iyimser kilidi, tasarım sistemi (sekme, `record-table`, `ViewSelector`). Platform yöneticisi katalogu yönetir; tenant iş kayıtlarını görmez.

## 8. Firma kartı ve ilişki rolleri

Ayrı tutulacaklar: tenant sektörü, müşteri firma sektörü, ticari ilişki, lojistik rolü, aldığı/sağladığı hizmet, işlemdeki taraf rolü (gönderici/alıcı).

- **Bugün:** `Company.Industry` serbest metin, 100 karakter; müşteri firmanın sektörüdür, tenant'ın sektörüyle bağlantısı yok. Bu doğru ayrımdır ve korunur. Tenant sektörü yeni seçimden gelir.
- **Eksik:** Party üzerinde ilişki rolü yok. Öneri: `ilişki rolü` kataloğu (müşteri, tedarikçi, taşıyıcı, acente…) + Party'ye bağ, durum ve ticari koşul ayrı; bir firma aynı anda müşteri ve tedarikçi olabilir.
- Müşterinin sektörü ileride isteğe bağlı olarak aynı faaliyet ağacından seçilebilir (serbest metin geçişi geriye uyumlu, ayrı dilim E).
- "Antrepo hizmeti alan müşteri" işletmeci yapılmaz: hizmet ihtiyacı rolden ayrı bir kayıttır.

## 9. Migration ve test planı

Yeni tablolar:
- `platform` (global, `TenantId` yok): `ActivityNodes`, `ServiceFeatures`, `ActivityNodeFeatures`, `Capabilities`, `CapabilityDependencies`, `ActivityCapabilities`, sonra `ProfilePackages`/sürümler.
- `core` (tenant, `ITenantEntity` + `ICompanyShared`, RLS): `TenantActivitySelections`, `TenantActivityFeatureValues`.
- Her tenant tablosu için `TenantIsolationSql.AddTable`; `TableInventoryTests` ve `Model_has_no_changes_without_a_migration` yeşil kalmalı.
- Migration geriye uyumlu, yalnız ekleme; eski tenant'a seçim satırı yazılmaz (boş seçim = mevcut davranış). Seed tekrar çalışabilir (kod bazında "yoksa ekle"). Pilot verisi demo seed'e girmez.

Testler: her özellik için mutlu yol, doğrulama, tenant izolasyonu, yetki. Ek: döngü engeli, ad değişimi, pasife alma, eksik bağımlılık mesajı, seçimde üst başlığın alt faaliyeti etkinleştirmemesi, platform yöneticisinin tenant seçim verisini okuyamaması, eşzamanlı düzenleme, N+1 yok (lookup/kural).

Çalıştırma: `dotnet build` ve `tests/Modulix.ArchitectureTests` her adımda. Entegrasyon testleri SQL Server + Mailpit ister; Docker bu makinede var, denenmedi.

## 10. Doğrulanmayanlar

- Satır numaralarının bir kısmı ve Blazor tarafı (`LayoutForm`, `Web`, `CrmForms.cs` içeriği) okunmadı.
- Paket/etkin profil önbelleği için mevcut merkezi önbellek bulunamadı; kapsamlı aranmadı.
- Platform yöneticisinin iş verisini görememesini zorlayan testin yokluğu aramayla doğrulanmadı, sadece bulunamadı.
- Kapalı-CRM davranışını doğrulayan test aranmadı.
- Lojistik/antrepo sınıflandırmasının pilot işleyişiyle uyumu, antrepo mevzuat zorunlulukları doğrulanmadı.
- Hiçbir test çalıştırılmadı.

## 11. Uygulama dilimleri

| Dilim | İçerik | Durum |
|---|---|---|
| A | Core tanımları: katalog varlıkları, ağaç kuralları (döngü, taşıma etkisi), tenant seçimi, servis sınırı | Başlanacak |
| B | Katalog + tenant seçim yönetim ekranları | Planlı |
| C | Paket/profil birleşimi, alan kuralı katmanı | Planlı |
| D | Lojistik başlangıç paketi | Planlı |
| E | Firma ilişki rolü, hizmet ihtiyacı profili, müşteri sektörü bağı | Planlı |
| F | Lead/fırsat/teklif entegrasyonu (hizmet kapsamı, ücret türü, snapshot) | Planlı |
| G | Yayın etki analizi, geri alma, migration ve test kapanışı | Planlı |

Ayrı epicler (bu işte yapılmaz): TMS, WMS/stok, filo, gümrük/e-belge, sürücü saha ekranı ve offline (Ç16).
