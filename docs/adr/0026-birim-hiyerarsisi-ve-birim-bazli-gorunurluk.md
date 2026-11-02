# ADR-0026 — Birim hiyerarşisi, birim bazlı rol ataması ve kayıt görünürlüğü (bayi yönetiminin erişim katmanı)

Durum: **taslak, onay bekliyor** (2 Kasım 2026). Yalnızca kodla ve testle doğrulananlar "yapıldı" yazılır; bu belge henüz hiçbir şeyi uygulamaz.

## 1. Bağlam ve amaç

Bayi (kanal/iş ortağı) sürecini Dynamics CRM'deki gibi **departman/birim ağacıyla** yönetmek istiyoruz: merkez kök birimdir, bayiler onun alt birimleridir, bayi çalışanları ana uygulamada normal kullanıcıdır ve kayıt görünürlüğü kullanıcının birimine göre belirlenir.

Bu belge **yalnızca erişim ve organizasyon katmanını** kapsar: birim ağacı, kullanıcı–birim üyeliği, rolün birime atanması, birim bazlı kayıt kapsamı ve kullanıcı kartı. Bayinin **ticari** tarafı (iş ortağı profili, roller, satış kanalı, yönlendirme/inceleme, fırsat katkısı, komisyon) ayrı bir karardır (bölüm 9, ADR-0027 adayı). Birim ağacı bayiye özgü değildir; şube, depo, antrepo gibi yapılar da aynı yeteneği kullanır, bu yüzden Core'dadır ve sektöre özel mantık içermez.

Tenant başına ayrı veritabanı yoktur (ADR-0020); tek paylaşımlı `Modulix` veritabanı, `TenantId` + EF filtresi + RLS.

## 2. Mevcut durum ve boşluk matrisi (kod kanıtıyla)

| Gereksinim | Durum | Kanıt | Yapılacak |
|---|---|---|---|
| Birim hiyerarşisi (`BusinessUnit`) | **Yok** | `CLAUDE.md` yetki modelinde `BusinessUnit` ve "birim" kapsamı hedef olarak yazılı; kodda karşılık sınıf bulunamadı. `RecordScope` yalnızca `All=1, Team=2, Own=3` (`Core.Contracts/Authorization/RecordScope.cs`; aynı enum `Shared/Access/AccessContracts.cs:64`'te kopya) | Birim ağacı |
| Şube | Var, düz | `Branch` (`Core/Organization/Company.cs:56`): üst birimi yok, "kapsam sınırı değil, idari birim" | Dokunulmaz |
| Ekip / departman | Var, düz, çok üyeli | `Team` (`Core/Teams/Team.cs:9`, "ekip ya da departman"), `TeamMembers`; kişi birden çok ekipte olabilir | Dokunulmaz; birimle karıştırılmaz (K1) |
| Rol ataması | Yalnızca kullanıcı (üyelik) | `MembershipRole(MembershipId, RoleId)` (`Core/Authorization/MembershipRole.cs`); `MembershipPermissions.GetAsync` (`Infrastructure/Authorization/MembershipPermissions.cs:28`) | Birim düzeyi atama |
| Kapsam hesabı | Rollerin **en küçük sayısı** | `ScopeOf` → `DefaultIfEmpty(Own).Min()` (`MembershipPermissions.cs:57`) | Sahip kümelerinin birleşimi (K5) |
| Kayıt görünürlüğü | Tek noktada, sahip kümesi | `MembershipDataScope.GetOwnersAsync` (`MembershipDataScope.cs:20`): `null`=tümü, `Team`=ekip arkadaşları, diğer=kendisi; olay aboneliğinde `null` | Birim kümeleri aynı noktaya eklenir |
| Ortak firma/kişi kısıtı | Var, aynı sahip kümesini izler | `CrmSharedRecordVisibility` (`Crm/Records/CrmSharedRecordVisibility.cs:28`, ADR-0010): kapsam dışı kayıt **asgari kimlik bilgisiyle** görünür | Kümeye kendiliğinden uyar; asgari kimlik ayarı (K8) |
| Kullanıcı kartında birim | **Yok** | `UserDto` (`Shared/Users/UserContracts.cs:19`) birim alanı taşımaz | Alan + ekran |
| Son yönetici koruması | Yalnızca doğrudan atama | `AdministratorGuard` (`MembershipRoles` üzerinden, satır 54 ve 74) | Sistem rolü birime atanamaz (K6) |
| Rol silme/devir | Yalnızca doğrudan atama | `RoleService` (satır 127 ve 287) | Birim atamaları aynı işlemde taşınır |
| Dış kullanıcı kimliği | **Bulunamadı** | `Core` altında `External/Guest/IsExternal/MembershipType` araması boş; dış erişim yalnızca teklif bağlantısı ve anket portalı (`QuoteShareLink`, `SurveyPortal`) | Gerekmez: bayi çalışanları tenant kullanıcısıdır (K2) |
| Şirket süzgeci | Var, yalnızca uygulamada | ADR-0008 | Birim süzgeci aynı düzeyde (RLS tenant'ta kalır) |
| Üyelik tablosu | Platform şeması, RLS dışı | `CLAUDE.md` | Birim bilgisi tenant tablosunda tutulur (K3) |

## 3. Mimari kararlar

**K1 — Yeni `BusinessUnit` (Core); `Team` olduğu gibi kalır.** `Team` çok üyelidir (bir kişi birden çok ekipte), düzdür ve "ekibimin kayıtları" davranışı vardır. Güvenlik birimi ise **tek birim, hiyerarşi, kayıt kapsamı** ister. `Team`'e hiyerarşi eklemek mevcut `Team` kapsamının anlamını bozar ve "tek birincil" kuralıyla çelişir. İkisi birlikte çalışır: kullanıcının birim kapsamı ile ekip kapsamı birleşir (K5).

**K2 — Bayi çalışanları tenant kullanıcısıdır.** Ayrı dış kimlik, bayi portalı ve genel paylaşım motoru bu karar kapsamında **yoktur**. Bayi izolasyonu birim kapsamıyla sağlanır. Sonuçları: bayi kullanıcıları tenant kullanıcı kotasına girer; bayi verisi tenant'a (merkeze) aittir.

**K3 — Kullanıcı–birim ilişkisi tenant tablosundadır** (`BusinessUnitMember`). Kullanıcının tenant'a özgü örgüt bilgisi RLS dışındaki `platform.Memberships`'e sütun olarak eklenmez. `MembershipId` taşıyan tenant tabloları gibi `platform.Memberships`'e bileşik yabancı anahtarla bağlanır.

**K4 — Tek birim kuralı.** Her üyelik **tam bir** birime bağlıdır; birimsiz üyelik yoktur. Mevcut üyelikler kök birime (merkez) backfill edilir; davette birim verilmezse kök kullanılır. Çoklu birim üyeliği kayıt sahibinin birimini belirsiz yaptığı için bu karar kapsamında yoktur.

**K5 — Kapsam, sahip kümelerinin birleşimidir; `Min` mantığı kalkar.** Kullanıcının etkin rollerinin (doğrudan + birimden gelen) her kapsamı bir sahip kümesi üretir; sonuç bunların birleşimidir; herhangi biri `All` ise `null` (tümü). Yeni kapsamlar mevcut enum değerlerini değiştirmeden eklenir: `Unit = 4`, `UnitAndBelow = 5`. Sayısal sıra genişliği temsil etmez; hesap kümeyle yapılır. `Core.Contracts` ve `Shared` kopyaları birlikte güncellenir.

- `Unit`: aynı birime bağlı **tüm üyeliklerin** (pasif/ayrılmış dahil) kullanıcı kimlikleri.
- `UnitAndBelow`: birimin ve altındaki birimlerin üyeleri. Kök birimde `All`'a indirgenir.
- Birime bağlı olmayan üyelik oluşamayacağı için "birimi yok" durumu yalnızca bozuk veride olur; güvenli varsayılan `Own`'dır.

**K6 — Rol ataması iki düzeydedir ve toplanır.** Etkin roller = doğrudan roller ∪ kullanıcının biriminden gelen roller. Biri diğerini ezmez; kullanıcı bazında "birim rolünü kapat" yoktur (farklı yetki isteyen kullanıcı ayrı bir birime alınır).

- `BusinessUnitRole(UnitId, RoleId, InheritToChildren)`; `InheritToChildren` varsayılan **kapalı** (merkeze verilen rol kendiliğinden bayilere inmez).
- `Role.AssignLevel` (`User` / `Unit` / `Both`); mevcut roller `User` olarak backfill edilir, yani hiçbiri kendiliğinden birime atanamaz. Birime atama yöneticinin açık işaretlemesiyle başlar.
- **Sistem rolü ("Yönetici") birime atanamaz.** `AdministratorGuard` yalnızca doğrudan atamalara baktığı için bu kural son yönetici korumasını korur.
- Atayan, kendi etkin izinlerini aşan rolü (doğrudan veya birim) atayamaz; mevcut "işlemi yapanın sınırı" birim atamasına da uygulanır. Birim yöneticisi yalnızca kendi birimi ve altında, `AssignLevel` izin verdiği rollerle atama yapar.
- Rol silinirken/devredilirken birim atamaları aynı işlemde taşınır.
- Birim rolü değişimi etkilenen kullanıcı sayısıyla birlikte denetim kaydına yazılır.

**K7 — Görünürlük sahip bazlıdır (kullanıcı tarafından onaylandı).** Kayıt, **sahibinin güncel birimiyle** görünür; kayıtlara `OwningUnitId` eklenmez. Böylece iş tablolarında sütun/migration gerekmez ve tek noktadan (`GetOwnersAsync`) uygulanır. Bilinen sonuçlar:

- Kullanıcı birim değiştirirse kayıtları da yeni birimle gider. Bu nedenle birim değişikliği onay ister, etkilenen kayıt sayısını gösterir ve gerekçe alır; kayıtları eski birimde bırakmak için ADR-0004 devri ayrı komut olarak kullanılır.
- Pasif/ayrılmış üyelik birimde kalır (silinmez) ve sahip kümesinde sayılır; böylece bayiden ayrılan çalışanın kayıtları bayide kalır. Pasif üyeliğin başka birime taşınması engellenir veya açık uyarı ister.
- Sahibi olmayan kayıt (`OwnerId` boş) birim kapsamında görünmez; bayi kayıtlarında sahip zorunludur.
- Kayıt bazlı `OwningUnitId` (tarihsel kesinlik, sahipsiz birim kuyruğu) **ertelenmiştir**; gerekirse ayrı karar ve her tenant tablosu için migration ister.

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

Birim kapatma: açık üyelik veya rol ataması varken engellenir ya da açık devir ister. Birim taşıma (üst birim değişimi) `Path`'i transaction içinde yeniden hesaplar, görünürlüğü değiştirdiği için onay ve denetim kaydı ister.

## 5. Kullanıcı kartı ve ekranlar

- **Kullanıcı kartı:** birim (yol olarak, ör. "Merkez › Marmara Bayi"), **etkin kapsam özeti** ("Birim ve altı — 14 kullanıcının kayıtları"), doğrudan roller ve birimden gelen roller (kaynak rozetiyle), ekipler. `UserDto`'ya birim alanları ve rol kaynağı eklenir.
- **Kullanıcı listesi:** birim sütunu ve birim filtresi (sunucu tarafı).
- **Davet/düzenleme:** birim seçimi zorunlu (varsayılan kök).
- **Birim ekranı:** ağaç, üyeler, birim rolleri sekmesi, birim kapatma/taşıma.
- **Rol ekranı:** kayıt kapsamı seçimi (Tümü / Birim ve altı / Birim / Ekip / Kendi), atanabilir düzey, "bu rol kimlere atanmış" (birimler ve kullanıcılar ayrı), kapsam dışı ortak kayıt seçeneği.
- Menü ve düğmeler yetkiye göre sunulur; yetki yine sunucuda uygulanır.

## 6. Yetki, denetim, olay

- Yetki anahtarları çekirdek izin adlandırmasına uyar (birim okuma, birim yönetimi, birime rol atama ayrı anahtarlardır); kayıt boru hattından geçer, uçlarda `.RequirePermission(...)`.
- Denetim: birim oluşturma/taşıma/kapatma, üyelik değişimi (eski/yeni birim, gerekçe), birime rol atama/kaldırma (etkilenen kullanıcı sayısı), kapsam ve atanabilir düzey değişimi.
- Olay: birim/üyelik/rol değişimi mevcut olay altyapısıyla (ADR-0007) aynı transaction içinde yayımlanır; tüketici tenant bağlamını doğrular.
- Bellekte tutulan erişim hesabı istek başına bir kez yapılır (mevcut `MembershipDataScope` davranışı); birim veya rol değişikliği bir sonraki istekte geçerli olur.

## 7. Geçiş ve geriye uyumluluk

- Yalnızca ekleme: üç yeni tablo, `Roles` için iki sütun (varsayılanlı). Eski kod yeni yapıyı görmeden çalışır; kesintisizdir (tek veritabanı bütün tenant'ları etkiler).
- Backfill: her tenant için kök birim, tüm mevcut üyelikler köke; mevcut roller `AssignLevel=User`. Yeni tenant açılırken kök birim kendiliğinden oluşur.
- Geri dönüş: yeni kapsamlar ve birim rolleri kullanılmadığı sürece mevcut davranış değişmez; kök-only ağaç mevcut modelle aynı görünürlüğü verir.
- README'deki yetki ve kapsam bölümleri uygulama dilimlerinde güncellenir.

## 8. Test planı (dört test kuralı)

- **Mutlu yol:** bayi kullanıcısı kendi birimindeki kayıtları görür; merkez (birim ve altı/kök) bütün bayileri görür; birimden gelen rol izin verir; kullanıcı kartı etkin kapsamı doğru gösterir.
- **Hata/doğrulama:** olmayan veya pasif birim, döngü, ikinci kök, derinlik aşımı, birimsiz üyelik, sistem rolünün birime atanması, `AssignLevel` dışı rol, bitiş<başlangıç gibi aralıklar reddedilir.
- **Tenant izolasyonu:** A tenant'ının birimi/rolü/üyeliği B'de kullanılamaz; ID ile doğrudan erişim, arama, sayım ve rapor B'nin birimlerini döndürmez.
- **Yetki:** bayi A kullanıcısı bayi B kayıtlarını **liste, ID ile okuma, arama, sayım, lookup, rapor, dışa aktarma ve ortak firma/kişi uçlarından** göremez; kapsam dışı ortak kayıt "gizle" modunda hiç dönmez; birim yöneticisi yetkisini aşan rol atayamaz; yetkisiz kullanıcı birim/rol/kapsam değiştiremez.
- **Özel senaryolar:** doğrudan + birim + ekip kapsamının birleşimi; birim değişince görünürlüğün eski/yeni birimde doğru değişmesi; pasif üyenin kayıtlarının bayide kalması; rol silme/devirde birim atamalarının taşınması; son yönetici koruması; olay işleyicisi ve toplu çıktıların kapsamı; mükerrer ve benzersizlik çakışmasında bilgi sızmaması.

## 9. Kapsam dışı ve sonraki kararlar

Bayinin **ticari** katmanı ayrı kararda ele alınır (ADR-0027 adayı). Bu belgeden ona devredilen sonuçlar:

- Hibrit model: **birim = erişim ve organizasyon**, **iş ortağı profili (Firma uzantısı) = ticari ilişki**; ikisi bayi birimi ↔ iş ortağı profili bağıyla bağlanır.
- Dış kimlik, bayi portalı ve genel paylaşım motoru (önceki analizdeki en riskli dilim) büyük ölçüde gereksizleşir (K2).
- Yeniden kullanılacaklar: `CompanyRelation` (firma–firma ilişkisi, tarih aralığı, döngü kontrolü), `Lead` kaynak alanları (`Source`, `Referrer`, `ExternalSource`/`ExternalSubmissionId`), `OrderHandover` (satış devri), mükerrer mekanizması (ADR-0013). `Lead.Referrer` serbest metninden iş ortağı **tahmin edilmez**.
- Adlandırma: "kanal" Destek ve iletişim sözleşmelerinde iletişim kanalıdır; yeni kavram "satış kanalı" olarak adlandırılır. Yol haritasında `Ç13` güvenlik epic'idir; yeni epic başka numarayla anılır.
- Kayıt bazlı `OwningUnitId`, alt bayi ağı, komisyon, kota ve prim kampanyaları sonraya bırakılır.
- Hukuk: bayi çalışanının müşteri verisini görmesi için aydınlatma ve veri işleme ilişkisi ürün/hukuk kararıdır; kod bunu çözmez.

## 10. Uygulama dilimleri

| Dilim | İçerik | Durum |
|---|---|---|
| 0 | Bu ADR'nin onayı | Bekliyor |
| 1 | `BusinessUnit` ağacı, üyelik, kök backfill, birim ekranı, kullanıcı kartında birim ve listede filtre (kapsam henüz değişmez) | Yapılmadı |
| 2 | Rolün birime atanması, `AssignLevel`, kapsamların birleşimi, `Unit`/`UnitAndBelow` kapsamları, kullanıcı kartında etkin kapsam ve rol kaynağı | Yapılmadı |
| 3 | Kapsam dışı ortak kayıt seçeneği, mükerrer/benzersizlik sızıntı denetimi, arama/lookup/rapor/dışa aktarma doğrulaması | Yapılmadı |
| 4 | Birim değişikliği akışı (onay, etkilenen kayıt sayısı, devir bağlantısı), pasif üyelik kuralları | Yapılmadı |

## 11. Kararlar

| Karar | Öneri | Durum |
|---|---|---|
| Sahip bazlı görünürlük (kayıt bazlı değil) | Sahip bazlı | **Onaylandı** |
| Birimden gelen rol alt birimlere inmesin (açık seçenekle) | Varsayılan kapalı | Öneri, onay bekliyor |
| Kullanıcı tek birime bağlı | Tek birim | Öneri, onay bekliyor |
| Kullanıcı bazında birim rolünü kapatma yok | Yok | Öneri, onay bekliyor |
| Bayi rollerinde kapsam dışı ortak kayıt gizli | Gizle | Öneri, onay bekliyor |
| Kullanıcı için birim zorunlu | Zorunlu (varsayılan kök) | Öneri, onay bekliyor |
| Birim tablolarının sınıfı `ICompanyShared` | Ortak | Öneri, onay bekliyor |
