# ADR-0026 — Departman hiyerarşisi, departman bazlı rol ataması ve kayıt görünürlüğü (bayi yönetiminin erişim katmanı)

Durum: **kabul edildi** (2 Kasım 2026). Uygulama dilimlerle ilerler; her dilimin durumu bölüm 10'dadır. Yalnızca kodla ve testle doğrulananlar "yapıldı" yazılır; bu belge tek başına hiçbir şeyi uygulamaz.

## 1. Bağlam ve amaç

Bayi (kanal/iş ortağı) sürecini Dynamics CRM'deki gibi **departman ağacıyla** yönetmek istiyoruz: merkez kök departmandır, bayiler onun alt departmanlarıdır, bayi çalışanları ana uygulamada normal kullanıcıdır ve kayıt görünürlüğü kullanıcının departmanına göre belirlenir.

**Terim:** arayüzde ve belgelerde "departman" kullanılır; "birim" depoda ölçü birimi (`crm.units`, ADR-0008'deki "ürün, birim") anlamında zaten geçtiği için karışır. Kodda `CLAUDE.md`'deki `BusinessUnit` adı korunur. `Team` sınıfının açıklamasındaki "ekip ya da departman" ifadesi, ikisi karışmasın diye uygulama diliminde "ekip" olarak düzeltilir.

Bu belge **yalnızca erişim ve organizasyon katmanını** kapsar: departman ağacı, kullanıcı–departman üyeliği, rolün departmana atanması, departman bazlı kayıt kapsamı ve kullanıcı kartı. Bayinin **ticari** tarafı (iş ortağı profili, roller, satış kanalı, yönlendirme/inceleme, fırsat katkısı, komisyon) ayrı bir karardır (bölüm 9, ADR-0027 adayı). Departman ağacı bayiye özgü değildir; şube, depo, antrepo gibi yapılar da aynı yeteneği kullanır, bu yüzden Core'dadır ve sektöre özel mantık içermez.

Tenant başına ayrı veritabanı yoktur (ADR-0020); tek paylaşımlı `Modulix` veritabanı, `TenantId` + EF filtresi + RLS.

## 2. Mevcut durum ve boşluk matrisi (kod kanıtıyla)

| Gereksinim | Durum | Kanıt | Yapılacak |
|---|---|---|---|
| Departman hiyerarşisi (`BusinessUnit`) | **Yok** | `CLAUDE.md` yetki modelinde `BusinessUnit` ve "birim" kapsamı hedef olarak yazılı; kodda karşılık sınıf bulunamadı. `RecordScope` yalnızca `All=1, Team=2, Own=3` (`Core.Contracts/Authorization/RecordScope.cs`; aynı enum `Shared/Access/AccessContracts.cs:64`'te kopya) | Departman ağacı |
| Şube | Var, düz | `Branch` (`Core/Organization/Company.cs:56`): üst departmanı yok, "kapsam sınırı değil, idari birim" | Dokunulmaz |
| Ekip | Var, düz, çok üyeli | `Team` (`Core/Teams/Team.cs:9`, "ekip ya da departman"), `TeamMembers`; kişi birden çok ekipte olabilir | Dokunulmaz; departmanla karıştırılmaz (K1) |
| Rol ataması | Yalnızca kullanıcı (üyelik) | `MembershipRole(MembershipId, RoleId)` (`Core/Authorization/MembershipRole.cs`); `MembershipPermissions.GetAsync` (`Infrastructure/Authorization/MembershipPermissions.cs:28`) | Departman düzeyi atama |
| Kapsam hesabı | Rollerin **en küçük sayısı** | `ScopeOf` → `DefaultIfEmpty(Own).Min()` (`MembershipPermissions.cs:57`) | Sahip kümelerinin birleşimi (K5) |
| Kayıt görünürlüğü | Tek noktada, sahip kümesi | `MembershipDataScope.GetOwnersAsync` (`MembershipDataScope.cs:20`): `null`=tümü, `Team`=ekip arkadaşları, diğer=kendisi; olay aboneliğinde `null` | Departman kümeleri aynı noktaya eklenir |
| Ortak firma/kişi kısıtı | Var, aynı sahip kümesini izler | `CrmSharedRecordVisibility` (`Crm/Records/CrmSharedRecordVisibility.cs:28`, ADR-0010): kapsam dışı kayıt **asgari kimlik bilgisiyle** görünür | Kümeye kendiliğinden uyar; asgari kimlik ayarı (K8) |
| Kullanıcı kartında departman | **Yok** | `UserDto` (`Shared/Users/UserContracts.cs:19`) departman alanı taşımaz | Alan + ekran |
| Son yönetici koruması | Yalnızca doğrudan atama | `AdministratorGuard` (`MembershipRoles` üzerinden, satır 54 ve 74) | Sistem rolü departmana atanamaz (K6) |
| Rol silme/devir | Yalnızca doğrudan atama | `RoleService` (satır 127 ve 287) | Departman atamaları aynı işlemde taşınır |
| Dış kullanıcı kimliği | **Bulunamadı** | `Core` altında `External/Guest/IsExternal/MembershipType` araması boş; dış erişim yalnızca teklif bağlantısı ve anket portalı (`QuoteShareLink`, `SurveyPortal`) | Gerekmez: bayi çalışanları tenant kullanıcısıdır (K2) |
| Şirket süzgeci | Var, yalnızca uygulamada | ADR-0008 | Departman süzgeci aynı düzeyde (RLS tenant'ta kalır) |
| Üyelik tablosu | Platform şeması, RLS dışı | `CLAUDE.md` | Departman bilgisi tenant tablosunda tutulur (K3) |

## 3. Mimari kararlar

**K1 — Yeni `BusinessUnit` (Core); `Team` olduğu gibi kalır.** `Team` çok üyelidir (bir kişi birden çok ekipte), düzdür ve "ekibimin kayıtları" davranışı vardır. Kayıt görünürlüğünü belirleyen departman ise **tek departman, hiyerarşi, kayıt kapsamı** ister. `Team`'e hiyerarşi eklemek mevcut `Team` kapsamının anlamını bozar ve "tek birincil" kuralıyla çelişir. İkisi birlikte çalışır: kullanıcının departman kapsamı ile ekip kapsamı birleşir (K5).

**K2 — Bayi çalışanları tenant kullanıcısıdır.** Ayrı dış kimlik, bayi portalı ve genel paylaşım motoru bu karar kapsamında **yoktur**. Bayi izolasyonu departman kapsamıyla sağlanır. Sonuçları: bayi kullanıcıları tenant kullanıcı kotasına girer; bayi verisi tenant'a (merkeze) aittir.

**K3 — Kullanıcı–departman ilişkisi tenant tablosundadır** (`BusinessUnitMember`). Kullanıcının tenant'a özgü örgüt bilgisi RLS dışındaki `platform.Memberships`'e sütun olarak eklenmez. `MembershipId` taşıyan tenant tabloları gibi `platform.Memberships`'e bileşik yabancı anahtarla bağlanır.

**K4 — Tek departman kuralı.** Her üyelik **tam bir** departmana bağlıdır; departmansız üyelik yoktur. Mevcut üyelikler kök departmana (merkez) backfill edilir; davette departman verilmezse kök kullanılır. Çoklu departman üyeliği kayıt sahibinin departmanını belirsiz yaptığı için bu karar kapsamında yoktur.

**K5 — Kapsam, sahip kümelerinin birleşimidir; `Min` mantığı kalkar.** Kullanıcının etkin rollerinin (doğrudan + departmandan gelen) her kapsamı bir sahip kümesi üretir; sonuç bunların birleşimidir; herhangi biri `All` ise `null` (tümü). Yeni kapsamlar mevcut enum değerlerini değiştirmeden eklenir: `Unit = 4`, `UnitAndBelow = 5`. Sayısal sıra genişliği temsil etmez; hesap kümeyle yapılır. `Core.Contracts` ve `Shared` kopyaları birlikte güncellenir.

- `Unit`: aynı departmana bağlı **tüm üyeliklerin** (pasif/ayrılmış dahil) kullanıcı kimlikleri.
- `UnitAndBelow`: departmanın ve altındaki departmanların üyeleri. Kök departmanda `All`'a indirgenir.
- Departmana bağlı olmayan üyelik oluşamayacağı için "departmanı yok" durumu yalnızca bozuk veride olur; güvenli varsayılan `Own`'dır.

**K6 — Rol ataması iki düzeydedir ve toplanır.** Etkin roller = doğrudan roller ∪ kullanıcının departmanından gelen roller. Biri diğerini ezmez; kullanıcı bazında "departman rolünü kapat" yoktur (farklı yetki isteyen kullanıcı ayrı bir departmana alınır).

- `BusinessUnitRole(UnitId, RoleId, InheritToChildren)`; `InheritToChildren` varsayılan **kapalı** (merkeze verilen rol kendiliğinden bayilere inmez).
- `Role.AssignLevel` (`User` / `Unit` / `Both`); mevcut roller `User` olarak backfill edilir, yani hiçbiri kendiliğinden departmana atanamaz. Departmana atama yöneticinin açık işaretlemesiyle başlar.
- **Sistem rolü ("Yönetici") departmana atanamaz.** `AdministratorGuard` yalnızca doğrudan atamalara baktığı için bu kural son yönetici korumasını korur.
- Atayan, kendi etkin izinlerini aşan rolü (doğrudan veya departman) atayamaz; mevcut "işlemi yapanın sınırı" departman atamasına da uygulanır. Departman yöneticisi yalnızca kendi departmanı ve altında, `AssignLevel` izin verdiği rollerle atama yapar.
- Rol silinirken/devredilirken departman atamaları aynı işlemde taşınır.
- Departman rolü değişimi etkilenen kullanıcı sayısıyla birlikte denetim kaydına yazılır.

**K7 — Görünürlük sahip bazlıdır (kullanıcı tarafından onaylandı).** Kayıt, **sahibinin güncel departmanıyla** görünür; kayıtlara `OwningUnitId` eklenmez. Böylece iş tablolarında sütun/migration gerekmez ve tek noktadan (`GetOwnersAsync`) uygulanır. Bilinen sonuçlar:

- Kullanıcı departman değiştirirse kayıtları da yeni departmanla gider. Bu nedenle departman değişikliği onay ister, etkilenen kayıt sayısını gösterir ve gerekçe alır; kayıtları eski departmanda bırakmak için ADR-0004 devri ayrı komut olarak kullanılır.
- Pasif/ayrılmış üyelik departmanda kalır (silinmez) ve sahip kümesinde sayılır; böylece bayiden ayrılan çalışanın kayıtları bayide kalır. Pasif üyeliğin başka departmana taşınması engellenir veya açık uyarı ister.
- Sahibi olmayan kayıt (`OwnerId` boş) departman kapsamında görünmez; bayi kayıtlarında sahip zorunludur.
- Kayıt bazlı `OwningUnitId` (tarihsel kesinlik, sahipsiz departman kuyruğu) **ertelenmiştir**; gerekirse ayrı karar ve her tenant tablosu için migration ister.

**K8 — Kapsam dışı ortak kayıt: asgari kimlik kapatılabilir olmalı.** Bugün kapsam dışı firma/kişi genel uçlardan asgari kimlikle görünür (ADR-0010). Bayi izolasyonunda bu, merkezin ve diğer bayilerin müşteri adlarını sızdırır. Rolün kapsamı yanında **"kapsam dışı ortak kayıtlar: asgari kimlikle göster / gizle"** seçeneği olur; bayi rolleri için "gizle" önerilir. Arama, lookup, sayım ve mükerrer uyarısı aynı seçeneği izler.

**K9 — Mükerrer ve benzersizlik çakışması bilgi sızdırmaz.** Bayi, kapsamı dışındaki bir kaydın var olduğunu mükerrer uyarısından veya benzersizlik hatasından öğrenemez (ADR-0013). Kapsam dışı eşleşme ayrıntı vermeden yetkili incelemeye düşer. Otomatik birleştirme yapılmaz.

**K10 — Olay işleyicileri ve toplu çıktılar.** Olay aboneliğinde `GetOwnersAsync` bugün `null` (tenant geneli) döner (ADR-0007). Bu bilinçli kalır; ancak bayi verisini tüketen rapor, dışa aktarma ve arka plan işleri kullanıcı kapsamını ayrıca uygulamak zorundadır ve testle doğrulanır.

## 4. Veri modeli

Hepsi `ITenantEntity` + `ICompanyShared` (rol ve ekip gibi tenant genelinde ortak), RLS'e `TenantIsolationSql.AddTable` ile eklenir, tenant dışa aktarma/silme kapsamındadır.

| Tablo | İçerik ve kurallar |
|---|---|
| `core.BusinessUnits` | `Id`, `TenantId`, `ParentId` (null=kök), `Name`, `Code`, `Kind` (metadata: merkez/bayi/diğer; kodda tür koşulu yok), `Path` (türetilmiş yol), `Depth`, `IsActive`, `CreatedAt`. Tenant başına **tek kök**; `UNIQUE(TenantId, ParentId, Name)`; döngü reddedilir; derinlik ≤ 4 (ürün ilk fazda iki seviye sunar); fiziksel silme yok, kapatma var |
| `core.BusinessUnitMembers` | `MembershipId` (bileşik FK), `UnitId`; `UNIQUE(TenantId, MembershipId)` (K4) |
| `core.BusinessUnitRoles` | `UnitId`, `RoleId`, `InheritToChildren`; `UNIQUE(TenantId, UnitId, RoleId)` |
| `Roles` (mevcut) | `AssignLevel`, rol bazlı "kapsam dışı ortak kayıt" seçeneği (K8); varsayılanlar geriye uyumlu |

Departman kapatma: açık üyelik veya rol ataması varken engellenir ya da açık devir ister. Departman taşıma (üst departman değişimi) `Path`'i transaction içinde yeniden hesaplar, görünürlüğü değiştirdiği için onay ve denetim kaydı ister.

## 5. Kullanıcı kartı ve ekranlar

- **Kullanıcı kartı:** departman (yol olarak, ör. "Merkez › Marmara Bayi"), **etkin kapsam özeti** ("Departman ve altı — 14 kullanıcının kayıtları"), doğrudan roller ve departmandan gelen roller (kaynak rozetiyle), ekipler. `UserDto`'ya departman alanları ve rol kaynağı eklenir.
- **Kullanıcı listesi:** departman sütunu ve departman filtresi (sunucu tarafı).
- **Davet/düzenleme:** departman seçimi zorunlu (varsayılan kök).
- **Departman ekranı:** ağaç, üyeler, departman rolleri sekmesi, departman kapatma/taşıma.
- **Rol ekranı:** kayıt kapsamı seçimi (Tümü / Departman ve altı / Departman / Ekip / Kendi), atanabilir düzey, "bu rol kimlere atanmış" (departmanlar ve kullanıcılar ayrı), kapsam dışı ortak kayıt seçeneği.
- Menü ve düğmeler yetkiye göre sunulur; yetki yine sunucuda uygulanır.

## 6. Yetki, denetim, olay

- Yetki anahtarları çekirdek izin adlandırmasına uyar (departman okuma, departman yönetimi, departmana rol atama ayrı anahtarlardır); kayıt boru hattından geçer, uçlarda `.RequirePermission(...)`.
- Denetim: departman oluşturma/taşıma/kapatma, üyelik değişimi (eski/yeni departman, gerekçe), departmana rol atama/kaldırma (etkilenen kullanıcı sayısı), kapsam ve atanabilir düzey değişimi.
- Olay: departman/üyelik/rol değişimi mevcut olay altyapısıyla (ADR-0007) aynı transaction içinde yayımlanır; tüketici tenant bağlamını doğrular.
- Bellekte tutulan erişim hesabı istek başına bir kez yapılır (mevcut `MembershipDataScope` davranışı); departman veya rol değişikliği bir sonraki istekte geçerli olur.

## 7. Geçiş ve geriye uyumluluk

- Yalnızca ekleme: üç yeni tablo, `Roles` için iki sütun (varsayılanlı). Eski kod yeni yapıyı görmeden çalışır; kesintisizdir (tek veritabanı bütün tenant'ları etkiler).
- Backfill: her tenant için kök departman, tüm mevcut üyelikler köke; mevcut roller `AssignLevel=User`. Yeni tenant açılırken kök departman kendiliğinden oluşur.
- Geri dönüş: yeni kapsamlar ve departman rolleri kullanılmadığı sürece mevcut davranış değişmez; kök-only ağaç mevcut modelle aynı görünürlüğü verir.
- README'deki yetki ve kapsam bölümleri uygulama dilimlerinde güncellenir.

## 8. Test planı (dört test kuralı)

- **Mutlu yol:** bayi kullanıcısı kendi departmanındaki kayıtları görür; merkez (departman ve altı/kök) bütün bayileri görür; departmandan gelen rol izin verir; kullanıcı kartı etkin kapsamı doğru gösterir.
- **Hata/doğrulama:** olmayan veya pasif departman, döngü, ikinci kök, derinlik aşımı, departmansız üyelik, sistem rolünün departmana atanması, `AssignLevel` dışı rol, bitiş<başlangıç gibi aralıklar reddedilir.
- **Tenant izolasyonu:** A tenant'ının departmanı/rolü/üyeliği B'de kullanılamaz; ID ile doğrudan erişim, arama, sayım ve rapor B'nin departmanlarını döndürmez.
- **Yetki:** bayi A kullanıcısı bayi B kayıtlarını **liste, ID ile okuma, arama, sayım, lookup, rapor, dışa aktarma ve ortak firma/kişi uçlarından** göremez; kapsam dışı ortak kayıt "gizle" modunda hiç dönmez; departman yöneticisi yetkisini aşan rol atayamaz; yetkisiz kullanıcı departman/rol/kapsam değiştiremez.
- **Özel senaryolar:** doğrudan + departman + ekip kapsamının birleşimi; departman değişince görünürlüğün eski/yeni departmanda doğru değişmesi; pasif üyenin kayıtlarının bayide kalması; rol silme/devirde departman atamalarının taşınması; son yönetici koruması; olay işleyicisi ve toplu çıktıların kapsamı; mükerrer ve benzersizlik çakışmasında bilgi sızmaması.

## 9. Kapsam dışı ve sonraki kararlar

Bayinin **ticari** katmanı ayrı kararda ele alınır (ADR-0027 adayı). Bu belgeden ona devredilen sonuçlar:

- Hibrit model: **departman = erişim ve organizasyon**, **iş ortağı profili (Firma uzantısı) = ticari ilişki**; ikisi bayi departmanı ↔ iş ortağı profili bağıyla bağlanır.
- Dış kimlik, bayi portalı ve genel paylaşım motoru (önceki analizdeki en riskli dilim) büyük ölçüde gereksizleşir (K2).
- Yeniden kullanılacaklar: `CompanyRelation` (firma–firma ilişkisi, tarih aralığı, döngü kontrolü), `Lead` kaynak alanları (`Source`, `Referrer`, `ExternalSource`/`ExternalSubmissionId`), `OrderHandover` (satış devri), mükerrer mekanizması (ADR-0013). `Lead.Referrer` serbest metninden iş ortağı **tahmin edilmez**.
- Adlandırma: "kanal" Destek ve iletişim sözleşmelerinde iletişim kanalıdır; yeni kavram "satış kanalı" olarak adlandırılır. Yol haritasında `Ç13` güvenlik epic'idir; yeni epic başka numarayla anılır.
- Kayıt bazlı `OwningUnitId`, alt bayi ağı, komisyon, kota ve prim kampanyaları sonraya bırakılır.
- Hukuk: bayi çalışanının müşteri verisini görmesi için aydınlatma ve veri işleme ilişkisi ürün/hukuk kararıdır; kod bunu çözmez.

## 10. Uygulama dilimleri

| Dilim | İçerik | Durum |
|---|---|---|
| 0 | Bu ADR'nin onayı | Kabul edildi (2 Kasım 2026) |
| 1 | `BusinessUnit` ağacı, üyelik, kök backfill, departman ekranı, kullanıcı kartında departman ve listede filtre (kapsam henüz değişmez) | **Yapıldı** (birim ve entegrasyon testleriyle doğrulandı: `BusinessUnitTests`; kullanıcı yönetimi, tenant izolasyonu, tablo envanteri, dışa aktarma ve denetim sınıfları geçti; tam paket CI'da). Rol ataması ve kapsam hesabı bu dilimde yok |
| 2 | Rolün departmana atanması, `AssignLevel`, kapsamların birleşimi, `Unit`/`UnitAndBelow` kapsamları, kullanıcı kartında etkin kapsam ve rol kaynağı | **Yapıldı** (birim ve entegrasyon testleriyle doğrulandı: `RecordScopeRulesTests`, `BusinessUnitRoleTests` ve kapsam/rol/kullanıcı/tenant/denetim regresyon sınıfları; tam paket CI'da). Sınırlama: kapsam dışı ortak firma/kişi hâlâ asgari kimlikle görünür, bayi izolasyonu dilim 3'te tamamlanır |
| 3 | Kapsam dışı ortak kayıt seçeneği, mükerrer/benzersizlik sızıntı denetimi, arama/lookup/rapor/dışa aktarma doğrulaması | **Kısmen yapıldı.** Yapıldı (testle doğrulandı: `SharedRecordHidingTests`, `RoleHidingTests`): rol bazlı "kapsam dışı ortak kayıtları gizle" (K8) ortak kayıt uçlarında liste/sayım/arama/süzgeç/lookup/tekil okuma/değiştirme/silme ve mükerrer önerisinde; adsız ve kapsam içi/dışı için aynı çakışma iletisi (K9). Yapılmadı / sınırlı: vergi numarasının kullanımda olduğu iletiden çıkarılabilir (tenant içi benzersizlik; tam gizleme için bayi başına benzersizlik ya da inceleme kuyruğu gerekir); rapor/dışa aktarma/lookup kapsamı yeni testle ayrıca değil, mevcut boru hattı kapsam testleriyle doğrulandı; tenant genelinde çalışan arka plan işleri (K10) gözden geçirilmedi |
| 4 | Departman değişikliği akışı (onay, etkilenen kayıt sayısı, devir bağlantısı), pasif üyelik kuralları | **Yapıldı** (testle doğrulandı: `BusinessUnitChangeTests`): önizleme, kayıt kararı (kullanıcıyla taşı / eski departmanda kalan üyeye devret), gerekçe, tek işlemde devir + taşıma + denetim, devre dışı üye kuralları, kapsam sınırı. Sınırlar: yönetici olmayan departman yöneticisi diğer bayilerin departman adlarını listeden hâlâ görebilir; kayıt sayımı işlemi yapanın görebildiği türlerle sınırlıdır (eksikse karar zorunlu) |

## 11. Kararlar

| Karar | Öneri | Durum |
|---|---|---|
Tümü 2 Kasım 2026'da ADR'nin kabulüyle onaylanmıştır.

| Karar | Seçim | Durum |
|---|---|---|
| Sahip bazlı görünürlük (kayıt bazlı değil) | Sahip bazlı | Kabul edildi |
| Departmandan gelen rol alt departmanlara inmesin (açık seçenekle) | Varsayılan kapalı | Kabul edildi |
| Kullanıcı tek departmana bağlı | Tek departman | Kabul edildi |
| Kullanıcı bazında departman rolünü kapatma yok | Yok | Kabul edildi |
| Bayi rollerinde kapsam dışı ortak kayıt gizli | Gizle | Kabul edildi |
| Kullanıcı için departman zorunlu | Zorunlu (varsayılan kök) | Kabul edildi |
| Departman tablolarının sınıfı `ICompanyShared` | Ortak | Kabul edildi |
