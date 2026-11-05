# ADR-0030 — Kayıt türüne göre kayıt kapsamı ve platform görünürlük tavanı

Durum: **taslak** (3 Kasım 2026; açık kararlar 4 Kasım 2026'da cevaplandı, bölüm 7; dilim 1 uygulandı, dilim 2 bekliyor). Bu belge tek başına hiçbir şeyi uygulamaz. "Doğrulandı" yazılanlar kodda okunmuş, "doğrulanmadı" yazılanlar bölüm 9'da listelenmiştir.

Numara notu: `docs/adr` altında iki belge 0026, `0027` otomasyon, `0028` platform lisans ve tenant yönetimi, `0029` sektör profili genişletme numarasını taşır; bu belge bir sonraki boş numarayı (0030) kullanır. Taslak önce 0028, sonra 0029 olarak açılmış; numaralar başka belgelere verildiği için yeniden numaralandı.

## 1. Bağlam ve amaç

Pilot müşteri (Makaroğlu Lojistik) merkez ve bir şubeden oluşur. Müşteri havuzu ortaktır (şube merkezin firmalarını görür); fırsat ve aktivitelerin merkez ile şube arasında görünmesi ise **her tenant için farklı** olabilmelidir. Proje ilkesi: kural koda sabitlenmez, tenant başına yapılandırılır (müşteriye özel kod yok, tek imaj).

Bugün bir rolün **tek bir** kayıt kapsamı vardır ve bu kapsam rolün izin verdiği bütün sahipli kayıt türlerine (firma, fırsat, aktivite, lead, teklif, sözleşme, sipariş, talep…) aynı uygulanır. "Firmalar tümü, fırsatlar yalnızca departman" gibi bir kural ifade edilemez. Ayrıca kuralı kimin koyacağı belirsizdir: rol ekranını tenant yöneticisi düzenler, platform bir sınır koyamaz.

Karar verilmiş girdiler (kullanıcıyla konuşuldu):

- Görünürlük kuralı tenant başına dinamik olmalıdır.
- Kuralın **tavanını platform yöneticisi** belirler; tenant yöneticisi bu tavanın altında rol verir.
- Platform yöneticisi tenant verisini göremez (RLS); bu belge yalnızca **kuralı** ayarlar, kayıtları açmaz.

Kapsam dışı: alan seviyesi yetki (Y8b), kayıt bazlı paylaşım, koşullu/ifadeli görünürlük kuralları, kapsam dışı ortak kaydın "gizle/asgari kimlik" ayarı (ADR-0026 K8), sektör profilinin varsayılan tavan önermesi (gelecekte eklenebilir, bölüm 8).

## 2. Mevcut durum ve boşluk matrisi (kod kanıtıyla)

| Konu | Durum | Kanıt |
|---|---|---|
| Rolün kapsamı | Tek değer | `Role.Scope` (`Core/Authorization/Role.cs:59`), `RoleService` (satır 66, 92–120) |
| Kapsam değerleri | `All`, `Team`, `Own`, `Unit`, `UnitAndBelow`; ekip ile departman birbirini kapsamaz | `RecordScope.cs`, `RecordScopeRules.Covers` |
| Birleşim | Kişinin bütün rollerinin kapsamları sahip kümesi olarak **birleşir**; birleşim yalnızca genişletir | `MembershipDataScope.OwnersAsync` (`MembershipDataScope.cs:42`) |
| Kapsamı çözen servis | Kayıt türü almaz: `GetOwnersAsync(tenant, ct)`; sonuç üyelik başına önbelleklenir | `IDataScopeService` (`RecordPipelinePorts.cs:26`), `MembershipDataScope.cs:20–35` |
| Boru hattında kullanım | Liste ve tek kayıt kapsamı | `RecordPipeline.cs:69` ve `:473` |
| Modüllere açılan arayüz | `IRecordAccess.GetOwnersAsync(ct)` türsüz | `Core.Contracts/Records/RecordAccess.cs:13` |
| Türsüz çağıranlar | Modüllerde 20 çağrı (CRM, Destek, Sözleşme, Sipariş, Pazarlama), çekirdek ve altyapıda 5 (boru hattı 2, `RecordAccess`, `LookupService:207`, `Tenancy.cs:168`) | `GetOwnersAsync(` taraması |
| Sahipli tür tanımı | `IRecordStore.OwnerField` / `UsesOwnerScope`; modül `IModule.OwnedRecords` ile bildirir | `RecordContracts.cs:206–225`, `IModule.cs:33` |
| Rolü yazan kontrolü | Yönetici olmayan kişi kendi kapsamından geniş kapsam veremez | `EnsureActorMayGrantScopeAsync` (`RoleService.cs:556`) |
| Platformun sınır koyması | **Yok.** Platform tablosunda firma başına yalnızca modül hakları ve sektör profili var | `Tenant.cs:48`, `PlatformEndpoints.cs` |
| Test kapsamı | Kapsam kuralları ve entegrasyon | `RecordScopeRulesTests`, `DataScopeTests` |

Çıkarım: kapsam **tek noktada** hesaplanıyor (iyi), ama o noktaya **hangi kayıt türü için sorulduğu** iletilmiyor. Tür anahtarı iletilmeden türe göre kapsam kurulamaz.

## 3. Mimari kararlar

**K1 — Kapsam kayıt türü anahtarıyla çözülür.** `IDataScopeService.GetOwnersAsync(tenant, entityKey, ct)` ve `IRecordAccess.GetOwnersAsync(entityKey, ct)` tür anahtarı alır. Türsüz sürüm **kaldırılır**, eskisi korunmaz: modüller aynı depoda olduğu için 25 çağrı tek değişiklikte taşınır ve derleyici atlanan bir çağrıyı yakalar. Türsüz bir uyumluluk sürümü, bir rol türe özel daraltıldığında sessizce geniş sonuç döndürebileceği için güvenli değildir (fail-open).

**K2 — Rolde varsayılan kapsam kalır, türe özel istisna eklenir.** `Role.Scope` varsayılan olarak aynen sürer. Yeni tenant tablosu `core.RoleScopeOverrides(TenantId, RoleId, EntityKey, Scope)`: rolün belirli bir kayıt türündeki kapsamı. Etkin kapsam = tür için istisna varsa o, yoksa rolün varsayılanı. Tablo tenant verisidir (`ITenantEntity`, `ICompanyShared`, RLS, `TenantIsolationSql.AddTable`).

- Reddedilen: yalnızca ayrı roller kullanmak ("firma rolü", "fırsat rolü"). Kapsamlar birleştiği için bir rol diğerini daraltamaz; sadece genişletir. Yetersiz.
- Reddedilen: `Role.Scope`'u kaldırıp her şeyi tür bazlı yapmak. Mevcut roller ve rol şablonları için gereksiz geriye uyumsuzluk.

**K3 — Birleşim kuralı türe göre ayrı uygulanır.** ADR-0026 K5 aynen geçerlidir (kişinin etkin rollerinin, doğrudan ve departmandan gelenlerin, kapsamları birleşir; biri `All` ise tümü), ama her kayıt türü için o türün etkin kapsamlarıyla ayrı hesaplanır.

**K4 — Platform tavanı.** Platform tablosu `platform.TenantScopeLimits(TenantId, EntityKey, MaxScope, AppliesToAdministrators)` (RLS dışı, `ModuleEntitlements` gibi). Kayıt yoksa o tür için sınır yoktur. Tavan **tek değerdir** ve `RecordScopeRules.Covers(tavan, istenen)` ile doğrulanır; ekip ile departman birbirini kapsamadığı için tavan "Ekip" ise departman kapsamı verilemez ve tersi. Bu yön bilinçlidir (bölüm 7, açık karar 2).

**K5 — Tavan iki yerde uygulanır.**

1. **Yazarken:** rol varsayılanı veya türe özel istisna tavanı aşıyorsa reddedilir (409; yanıt hangi tür ve hangi tavan olduğunu söyler).
2. **Okurken (savunma derinliği):** etkin kapsam tavanı aşıyorsa tavana indirilir; tavanla karşılaştırılamayan kapsam `Own`'a düşer (güvenli varsayılan). Böylece platform tavanı sonradan daralttığında mevcut roller bir sonraki istekte kısıtlanır, rol kaydı beklenmez.

**K6 — Yönetici muafiyeti tenant ve tür başına yapılandırılır (karar 4 Kasım 2026).** Sabit "muaf" ya da "bağlı" kuralı yoktur (dinamik tasarım ilkesi). Her tavan kaydı `AppliesToAdministrators` taşır; **varsayılan `false`: Yönetici sistem rolü tavandan muaftır** ve "Yönetici her zaman tüm kayıtları görür" kuralı (Y8a) korunur. Platform yöneticisi bir tür için bunu `true` yaparsa tavan Yönetici rolünü de bağlar (ör. bir tenant'ın yöneticisinin başka birimin fırsatlarını görmemesi istenirse). Muafiyet kaydın kendisinde olduğu için tenant başına ve tür başına farklı olabilir.

- Yönetici bağlandığında kilitlenme riski vardır: tavan yöneticinin kendi kayıtlarını (`Own`) hiçbir zaman kısmaz, yani yönetici en kötü durumda yalnızca kendi kayıtlarına iner ve rol/kapsam ekranına erişimi sürer. Kurtarma yolu platformun tavanı gevşetmesidir (platform ekranı, denetim kaydıyla).
- `AppliesToAdministrators = true` yazılırken platform ekranı etkilenecek yönetici sayısını gösterir (bölüm 8, önizleme).
- Okuma tarafı (K5/2) yöneticiye yalnızca `AppliesToAdministrators = true` ise tavan uygular.

**K7 — Geriye uyum: boş tablo = bugünkü davranış.** İstisna ve tavan kaydı yoksa her tür için sonuç bugünkü `MembershipDataScope` sonucuyla aynıdır. Migration yalnızca iki boş tablo ekler; backfill gerekmez; mevcut `DataScopeTests` değişmeden geçmelidir.

**K8 — Önbellek.** İstek başına `(üyelik, kayıt türü)` anahtarıyla; rol yükleme (`LoadRolesAsync`) tür sayısından bağımsız bir kez yapılır. Olay aboneliğinde tenant geneli `null` davranışı değişmez (ADR-0007, ADR-0026 K10).

**K9 — Yetki ve denetim.**

- Tavanı yalnızca platform yöneticisi yazar (`PlatformGate`, mevcut `IsPlatformAdministrator`); yeni izin tanımlanmaz. Tenant yöneticisi tavanı **okur** (rol ekranı, neden seçenek kapalı) ama değiştiremez.
- Tavan değişikliği firmanın denetim kaydına `core.tenant` "Kayıt görünürlüğü" olarak, platform yöneticisi adına, önce/sonra değeriyle ve **etkilenen rol sayısıyla** yazılır (mevcut modül hakkı değişikliğiyle aynı desen).
- Rol istisnası değişikliği `core.role` kaydıdır.

**K10 — Arayüz.**

- **Platform → Firma detayı → "Kayıt görünürlüğü":** tür listesi tenant için açık modüllerin `OwnedRecords` tanımlarından gelir (koda sabit liste yok); her tür için tavan seçici, varsayılan "Sınır yok".
- **Tenant → Rol ekranı:** rolün varsayılan kapsamı ve "Kayıt türüne göre farklı kapsam" bölümü; tavanı aşan seçenekler devre dışı ve nedeni yazılı gösterilir. Bu yalnızca kolaylıktır; sunucu doğrulaması K5'tedir.

## 4. Etkilenen yerler

| Katman | Değişiklik |
|---|---|
| `Core.Contracts` | `IRecordAccess.GetOwnersAsync(entityKey, ct)`; `RecordScopeRules`'a tavan karşılaştırma ve indirgeme yardımcıları |
| `Core` | `IDataScopeService` imzası, `RoleScopeOverride` varlığı, `RoleService` doğrulaması |
| `Infrastructure` | `MembershipDataScope` (tür ve tavan), `RecordAccess`, `LookupService`, platform tavan servisi, migration |
| `Api` | Platform tavan uçları (`GET/PUT /api/platform/tenants/{id}/scope-limits`), rol uçlarında istisna alanı, `Tenancy.cs:168` |
| Modüller | 20 çağrı noktası tür anahtarı verir; hangi anahtarın kullanılacağı çağrı noktasında belirlenir |
| `Web` | İki ekran (K10) |
| Test | Bölüm 6; ayrıca `IRecordAccess` ve `IDataScopeService` sahte uygulamaları kullanan mevcut testler (`GetOwnersAsync`, `IDataScopeService` veya `RecordScope` 19 test dosyasında geçiyor; kaçının imzadan etkilendiği dilim 1'de belirlenir) yeni imzaya uyarlanır |

## 5. Geçiş ve dilimler

1. **Dilim 1 — Tür anahtarlı okuma (davranış değişmez).** K1'in imza değişikliği ve 25 çağrının taşınması; sonuç bugünküyle aynı. Mimari test: türsüz `GetOwnersAsync` çağrısı yok. En riskli mekanik adım önce yapılır ve tek başına incelenebilir.
2. **Dilim 2 — Veri modeli ve etkin kapsam.** K2–K8: iki tablo, migration, `MembershipDataScope`, `RoleService` doğrulaması, platform ve rol uçları, denetim. Ayrıca bölüm 7 kararları: `AppliesToAdministrators` alanı, `IRecordStore` kapsam türü anahtarı (alt kayıtlar), segment motorunda türe göre kapsam çözümleyicisi. Ekran yok.
3. **Dilim 3 — Ekranlar.** K10.
4. **Dilim 4 — Belgeler.** README (Yönetim ve Platform bölümleri), `docs/tenant-isletim.md` (tavan kaldırma/daraltma runbook'u).

Her dilim ayrı PR'dır; her biri bu belgenin bölüm 6'sındaki ilgili testlerle teslim edilir.

### Dilim 1 sonucu (uygulandı; davranış değişmedi)

`IDataScopeService.GetOwnersAsync(tenant, entityKey, ct)` ve `IRecordAccess.GetOwnersAsync(entityKey, ct)`; türsüz sürüm kaldırıldı. `MembershipDataScope` anahtarı doğrular (boşsa hata) ama sonucu hâlâ üyelik başına tek hesaptır. Mimari test (`RecordScopeRulesTests`) türsüz çağrıyı yasaklar. Çağrı noktalarında kullanılan tür:

| Çağrı noktası | Tür |
|---|---|
| Kayıt boru hattı (liste, tek kayıt) | `store.EntityKey` |
| CRM firma, kişi, fırsat, aktivite, teklif, lead erişim denetimleri | kendi türü (`crm.company`, `crm.contact`, `crm.opportunity`, `crm.activity`, `crm.quote`, `crm.lead`) |
| Lead kaydı, bağlanan firma ve kişi doğrulaması | firma için `crm.company`, kişi için `crm.contact` (iki ayrı küme) |
| Ortak kayıt görünürlüğü (`CrmSharedRecordVisibility`) | ortak türün karşılığı: firma `crm.company`, kişi `crm.contact` |
| Satış görünümü, tahmin iş akışı ve satış analitiği | `crm.opportunity` |
| Çakışma iletisindeki "yetkiliye başvurun" ipucu (ADR-0026 K9) | deponun kendi türü |
| Segment motoru | hedefin türü (firma `crm.company`, kişi `crm.contact`, lead `crm.lead`); açık karar 3 |
| Sözleşme, sipariş, destek bağlantı hedefleri ve alt kayıtları | `contracts.contract`, `orders.order`, `support.ticket` |
| Üye lookup'ı | lookup tanımının `SourceEntityKey`'i |
| `GET /api/tenant/members?assignable=true` | `entityKey` sorgu parametresi zorunlu (yoksa 400) |

Dilim 2'ye bırakılanlar (dilim 1'de sonuç değişmediği için güvenli, ama türe özel kapsam gelince yanlış sonuç verebilir):

- **Alt kayıt türleri.** Boru hattı alt kayıt deposu için kendi anahtarını sorar (ör. aktivite notu, fırsat iletişimcisi); ana kaydın kapsamını izlemesi gerekir. Dilim 2 alt türleri ana türe eşleyen bir kural (ör. `IRecordStore` üzerinde kapsam türü) ister.
- **Arayüzdeki atanabilir üye listesi** (`CrmLookups`) CRM genelinde tek listedir ve `crm.opportunity` ile sorulur; form türüne göre ayrı istenmelidir (dilim 3).

### Dilim 2a sonucu (uygulandı; roller henüz istisna yazamaz)

Kapsam artık kayıt türüne ve platform tavanına göre hesaplanır; tavanı platform yazar. Rol tarafı (istisna alanı, doğrulama, uçlar) dilim 2b'dedir (aşağıda).

- **Tablolar** (migration `RoleScopeOverridesAndLimits`, iki boş tablo, geriye uyumlu): `core.RoleScopeOverrides` (tenant verisi, RLS, `(TenantId, RoleId)` bileşik yabancı anahtar, rol silinince silinir, `(TenantId, RoleId, EntityKey)` tekil) ve `platform.TenantScopeLimits` (RLS dışı, `(TenantId, EntityKey)` tekil, `AppliesToAdministrators` varsayılan `false`). Dışa aktarma/silme tabloları katalogdan okuduğu için ikisini de kendiliğinden kapsar (testli).
- **Etkin kapsam** (`MembershipDataScope`): rolün tür istisnası varsa o, yoksa varsayılanı; kişinin rollerinin kapsamları türe göre ayrı birleştirilir; sonra tavana indirilir (`RecordScopeRules.Clamp`: tavan kapsamı kapsıyorsa aynen, kapsam tavanı kapsıyorsa tavan, karşılaştırılamıyorsa `Own`). Yönetici yalnızca `AppliesToAdministrators` ise bağlanır. Önbellek istek başına `(üyelik, tür)`; istisna yoksa ek sorgu yapılmaz, tavan sorgusu tenant başına bir kez.
- **Alt kayıtlar:** `IRecordStore.ScopeEntityKey`; 26 alt kayıt deposu sınıfı ana türünü bildirir (firma: kişi bağlantısı, ilişki, iletişim noktası, konum, adres; kişi: kişi iletişim noktası; fırsat: iletişimci, rakip; aktivite alt kayıtları; teklif satırı; sözleşme tarafı, satırı, taahhüt; sipariş alt kayıtları ve operasyon işleri; kampanya ve segment alt kayıtları).
- **Segment:** her koşul kendi türünün kapsamıyla (`ISegmentFieldSource.ScopeEntityKeys`, `SegmentEvaluationContext.OwnersFor`; CRM: firma, kişi, lead, fırsat). Kapsam bildirilmeyen tür sorulursa istisna fırlar, sessizce atlanmaz.
- **Platform ucu:** `GET`/`PUT /api/platform/tenants/{id}/scope-limits`; tür listesi kayıt depolarından (`ScopedRecordTypes`: sahip alanı olan, kapsamı kendi anahtarıyla çözülen depolar) ve firmada açık modüllerden gelir, koda sabit liste yoktur (`OwnedRecords` devir amaçlıdır ve kapsam türlerinin tamamını içermez; bu yüzden kullanılmadı). PUT tüm kümeyi değiştirir; geçersiz/kapalı/yinelenen tür ve geçersiz kapsam 400, firma yoksa 404. Denetim: firmanın `core.tenant` kaydı (alan `scopeLimits`, "Kayıt görünürlüğü") ve platform denetimi (`tenant.scope-limits-changed`, gerekçe, önce/sonra, etkilenen rol sayısı); aynı küme yeniden gönderilirse kayıt yazılmaz.
- **Bilinen sınır:** kişi kaydı firmaya bağlıyken görünürlüğünü bugün `crm.contact` kapsamının sahip kümesi, firmanın sorumlusuna ve kişinin sorumlusuna birlikte uygulanarak belirler. Firma ile kişi için farklı kapsam/tavan verilirse kişi, firmanın kapsamını değil kişi türünün kapsamını izler.
  **Karar (4.11.2026): ayrı kalır.** Kişi kendi türünün kapsamıyla görünür; firmanın kapsamını izlemesi, `crm.contact` tavanını firma kapsamı üzerinden delmeye yol açar ve "her tür kendi kapsamıyla" ilkesine (K1, segment kararı) aykırıdır. İhtiyaç çıkarsa firma bazında ayrı bir ayar olarak eklenir.

### Dilim 2b sonucu (uygulandı; rol uçları istisna yazar, ekran yok)

- **Rol sözleşmesi:** `SaveRoleRequest.ScopeOverrides` ve `RoleDto.ScopeOverrides` (kayıt türü → kapsam). İstek alanı verilmezse (`null`) mevcut istisnalar değişmez; boş küme hepsini kaldırır. Aynı alan `IRoleService.CreateAsync/UpdateAsync` ve `RoleSummary`'dedir.
- **Doğrulama** (`RoleService`): anahtar kapsamı ayarlanabilen bir türün olmalı (`ScopedRecordTypes.All()`: üst düzey, sahip alanlı depolar; alt kayıt türleri ve bilinmeyen anahtar 400). Modülün firmada açık olması aranmaz: sonradan kapanan türün istisnası geçerli kalır ve rol formu onu yeniden gönderebilir (ekran listesi açık modüllerden gelir). Geçersiz kapsam 400; sistem rolünde istisna 409.
- **Tavan (409):** rolün tavanı olan türdeki etkin kapsamı (istisna, yoksa varsayılan) tavanı `Covers` ile kapsamıyorsa reddedilir. Varsayılan kapsam da denetlenir: tavanlı türde istisnası olmayan geniş rol kaydedilemez. Güncellemede yalnızca bu istekte **değişen** etkin kapsamlar denetlenir; sonradan daralan tavan, rolün ilgisiz düzenlemelerini (ad, izin) kilitlemez, o rol okuma tarafında zaten tavana indirilir.
- **Yetki yükseltme:** her yeni ya da kaldırılan istisna için `EnsureActorMayGrantScopeAsync`; yönetici olmayan kişi kendi kapsamından geniş istisna veremez (403). Departmana rol atarken rolün istisnaları da denetlenir.
- **Denetim:** istisnalar `core.role` kaydının `scopeOverrides` alanında ("Kayıt türüne göre kapsam") önce/sonra görünür; rol silinirken son hâli kayda girer, tablo satırları rolle birlikte silinir.
- **Tavan okuma ucu (K9):** `GET /api/roles/scope-types` (`core.roles.manage`): kapsamı ayarlanabilen türler (firmada açık modüllerden) ve her türün platform tavanı (`maxScope`, yoksa `null`). Yalnızca kendi firmanın tavanlarını döner; yönetici muafiyet bayrağı gösterilmez. Rol ekranı bununla tavanı aşan seçenekleri kapalı gösterecek (dilim 3); sunucu doğrulaması bundan bağımsızdır.

### Dilim 3 sonucu (uygulandı; ekranlar)

- **Platform → Firma detayı → "Kayıt görünürlüğü"** (`TenantScopeLimitsSection`): tür listesi sunucudan gelir (`GET .../scope-limits`; koda sabit liste yok, `OwnedRecords` değil depolar), her tür için en geniş kapsam seçici (varsayılan "Sınır yok"), tavanı olan türde "Yöneticiyi de bağla". Gerekçe zorunlu (sunucu yalnızca isteğe bağlı sayar; ekran daha katıdır çünkü erişimi daraltır). Kayıttan sonra etkilenen rol sayısı gösterilir. Yalnızca değişiklik varken kaydedilebilir.
- **Rol ekranı → "Kapsam ve düzey" → "Kayıt türüne göre farklı kapsam":** tür başına "Varsayılan (...)" ya da bir kapsam; tavan, `GET /api/roles/scope-types` ile gelir. Tavanı aşan seçenek kapalı ve "(platform sınırını aşar)" yazılıdır; işlemi yapanın kapsamından geniş seçenek de kapalıdır. İstisnası olmayan türde varsayılan kapsam tavanı aşıyorsa satırda uyarı çıkar ve "Sınırı aşan türleri sınıra indir" düğmesi bu türlere tavan değerini istisna olarak yazar. Kaydetmede tüm istisna kümesi gönderilir (modülü kapalı türün istisnası da korunur).
- **Sunucu değişikliği:** modülü sonradan kapanan türün mevcut tavanı `PUT scope-limits` içinde geçerli kalır (ekran tüm kümeyi yeniden gönderdiği için aksi halde tavan sessizce silinirdi); kapalı modülün yeni türüne tavan konamaz (testli).
- **Doğrulama:** ekranlar yerelde (Release, demo veri) açık ve koyu temada elle denendi: tavan seçme, "Yöneticiyi de bağla", kaydetme, tavanı aşan seçeneklerin kapalı görünmesi, "sınıra indir". Ekran için otomatik arayüz testi yoktur; kurallar sunucu testleriyle korunur.
- **Bulgu (bu dilimde düzeltildi):** `RoleDetail`'deki `TabBar` `Changed` geri çağrısı almadığı için sekme değişince paneller yeniden çizilmiyordu (başlık değişiyor, içerik aynı kalıyordu). `Changed="_ => { }"` eklendi; diğer sayfalar (firma, kişi, fırsat, sözleşme detayı) zaten böyle kullanıyor. Aynı eksik `CreateTenantForm`, `ContractFromQuote`, `ContractTemplates`, `PriceLists` ve `Products` içindeki `TabBar` kullanımlarında da görünüyor; **doğrulanmadı** ve bu dilime alınmadı.

## 6. Test planı (dört test ilkesi)

- **Mutlu yol:** aynı rol firmada `All`, fırsatta `Unit` istisnasıyla kişiyi doğru kümelerle görür; tavan `Unit` iken `Unit` istisnası kaydedilir; liste, sayfalama ve toplam doğru kalır.
- **Hata/doğrulama:** tavanı aşan kapsam 409; geçersiz veya sahipsiz (`OwnerField`'ı olmayan) tür anahtarı 400; ekip tavanında departman kapsamı reddi; kapalı modülün türüne tavan konamaz.
- **Tenant izolasyonu:** A'nın tavanı ve istisnaları B'yi etkilemez; `RoleScopeOverrides` RLS ve EF filtresiyle korunur (`TenantIsolationSql.AddTable`, RLS testi); B'nin rolüne A'nın tür istisnası bağlanamaz.
- **Yetki:** tenant yöneticisi tavanı yazamaz (403); yönetici olmayan rol yöneticisi tavanın altında dahi kendi kapsamından geniş istisna veremez; yönetici muafiyeti (K6) her iki değerde (`AppliesToAdministrators` false ve true) testlidir; true iken Yönetici tavana bağlanır ve platform tavanı gevşetince eski görünürlüğüne döner.
- **Regresyon:** istisna ve tavan yokken `DataScopeTests` ve `RecordScopeRulesTests` değişmeden geçer; tavan sonradan daraltılınca mevcut rol bir sonraki istekte kısılır (K5/2).

Entegrasyon testleri SQL Server ister; veritabanı yoksa çalıştırılamadığı açıkça belirtilir.

## 7. Kararlar (4 Kasım 2026, kullanıcı cevapları)

1. **Yönetici muafiyeti (K6): dinamik.** Sabit kural yok; tenant ve tür başına `AppliesToAdministrators` (varsayılan muaf). Kullanıcı ifadesi: "dinamik kontrol olsun, her tenant için farklı kurallar olsun". Önerilen sabit "muaf" kuralı bu yüzden değiştirildi; muafiyet varsayılan kaldı.
2. **Tavanın biçimi (K4): tek değer + `Covers`.** Tür başına izinli kapsamlar kümesi gerekirse sonradan geriye dönük uyumlu bir migration ile eklenir. Bilinen sonuç: tavan "Ekip" iken departman kapsamı verilemez ve tersi.
3. **Segment ve rapor sorguları: her koşul kendi türünün kapsamıyla.** Satış görünümü ve tahmin yalnızca fırsatı süzer (sorun yok). Segment motoru tek sahip kümesi yerine türe göre küme veren bir çözümleyici alır (`SegmentEvaluationContext`, `Core.Contracts`; ör. "kazanılan fırsat" koşulu `crm.opportunity` kapsamıyla, firma koşulu `crm.company` kapsamıyla değerlendirilir). Hedef türle sınırlama reddedildi: mevcut segment özelliğini kısıtlar.
4. **Alt kayıt türleri (karar değil, tasarım):** `IRecordStore` üzerinde varsayılanı kendi `EntityKey`'i olan bir kapsam türü anahtarı; alt kayıt depoları (aktivite notu, fırsat iletişimcisi vb.) ana türünü bildirir. Boru hattı kapsamı bu anahtarla sorar.

Açık karar kalmadı. Dilim 2'nin başında yeniden doğrulanacak noktalar bölüm 9'dadır.

## 8. Sonraki işler (bu kararın dışında)

- Sektör profilinin varsayılan tavan önermesi (ör. "Lojistik" profili fırsatlarda `Unit`); önizleme ve onayla uygulanır, sessizce ezmez.
- Kapsam dışı ortak firma/kişi için "gizle" seçeneği (ADR-0026 K8, dilim 3).
- Tavanın platform ekranında "bu daralma kaç kullanıcıyı etkiler" önizlemesi (şu an yalnızca kayıttan sonra etkilenen rol sayısı gösterilir).
- `AppliesToAdministrators = true` yazılırken etkilenecek yönetici sayısı (K6): ekran bugün yalnızca uyarı metni gösterir, sayı vermez.

## 9. Doğrulanmadı

- ~~Modül çağrılarının tür anahtarları~~ dilim 1'de belirlendi (bölüm 5); segment motorunun çok türlü koşulları açık karar 3'te kalır.
- ~~`tenant-export` ve `tenant-delete` kapsamı~~ dilim 2a testiyle doğrulandı (katalogdan okunur).
- Web tarafındaki `SessionState` kapsam gösterimlerinin tür bazında değişmesi gerekip gerekmediği (şu an tek `Scope` görüntüsü gösteriliyor).
- Performans etkisi ölçülmedi; vaat edilmez. Beklenti: rol yükleme aynı kalır, yalnızca bir küçük istisna sorgusu eklenir.

## 10. Maliyet ve dağıtım

Harici ücret yoktur. İki yeni tablo ve her istekte tür başına küçük bir hesap eklenir. Migration geriye uyumludur (eski uygulama sürümü tabloları görmez); geri dönüş için önceki sürüme dönmek yeterlidir, `Down` tabloları RLS politikasından çıkarıp siler (veri atılacaksa önce `tenant-export`). Tek paylaşımlı veritabanı bütün tenant'ları etkilediği için migration kesintisiz olmalıdır (yalnızca tablo ekler).
