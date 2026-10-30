# ADR-0020: Tek paylaşımlı veritabanına dönüş

- **Durum:** Kabul edildi, 30 Ekim 2026
- **Karar:** Bütün tenant'lar tek `Modulix` veritabanındadır; ADR-0009 (tenant başına ayrı iş veritabanı) geri alınmıştır
- **İlgili:** ADR-0001 (SQL Server), ADR-0002 (tenant stratejisi; bu ADR onun Faz 1 modeline döner), ADR-0009 (geri alındı)

## Problem

ADR-0009, 26 Ekim 2026'da iş verisini tenant başına ayrı veritabanına taşıdı. Proje kuralları belgesi ise
veritabanı modelini değiştirilemez bir karar olarak şöyle sabitler: **tek, paylaşımlı veritabanı; tüm tenant'lar aynı
veritabanındadır; tenant başına ayrı veritabanı yoktur.** Platform tabloları globaldir, bütün iş tablolarında
`tenant_id` zorunludur ve izolasyon iki katmanlıdır (ORM filtresi + Row-Level Security). Mimari on-prem'e hazır olmalı,
tek imajla ve yalnızca yapılandırmayla çalışmalıdır.

ADR-0009'un getirdiği işletim yükü de bu kuralla çelişir: tenant kurulumu (provisioning) ve durum makinesi,
tenant→DB defteri ve bağlantı çözümleme, tenant başına migration runner (`migrate-tenants`), tenant sayısıyla büyüyen
arka plan döngüleri (outbox, olay ekranı, sağlık kontrolü) ve platform ile tenant veritabanları arasında atomik işlem
olmaması. Yol haritası (Ç24.4) tenant başına veritabanını ancak ölçümle gerekçelendirilen bir ölçek adımı olarak görür;
bugün böyle bir ölçüm yoktur.

ADR-0009'dan bu yana korunması gereken gerçek tenant verisi oluşmadı; bu nedenle veri taşıma aracı gerekmez.

## Karar

ADR-0002'nin tek veritabanı modeline dönülür:

- **Tek veritabanı:** `Modulix`. Platform tabloları (`platform` şeması: kullanıcılar/kimlik, tenant kayıtları,
  üyelikler, modül erişimleri, oturumlar, e-posta kuyruğu) tenant kimliği taşımaz ya da taşısa da RLS dışındadır.
  Tenant tabloları (`core`, `meta`, modül şemaları) `TenantId` taşır; EF global query filter (katman 1) ve SQL Server
  Row-Level Security (katman 2) her tabloda zorunludur.
- **Tenant çözümleme değişmez:** aktif tenant oturumdaki üyelikten sunucuda doğrulanır; istemciden gelen başlık veya
  parametre tenant belirlemez. Aktif tenant yoksa filtre ve RLS hiçbir satır döndürmez, yazma reddedilir (ADR-0002).
  ADR-0009'un "nöbetçi veritabanı" yaklaşımı kalkar; varsayılan bir iş veritabanına düşme sorunu tek veritabanında
  zaten yoktur, koruma satır düzeyindedir.
- **Kullanıcı ayrımı korunur:** `modulix_app` yalnızca veri okur/yazar ve politikayı kapatamaz; `modulix_migrator`
  şema değiştirir. Migrator'a artık sunucu düzeyinde `dbcreator` verilmez.
- **Tenant yaşam döngüsü sadeleşir:** kurulum durumu (`Pending/Provisioning/Active/Failed/Suspended`), veritabanı adı
  ve şema sürümü alanları kalkar. Tenant oluşturma, platform kaydı ile tenant başlangıç verisini (sistem rolü,
  varsayılan şirket, şablon roller, denetim kaydı) aynı veritabanına yazar.
- **Veri sahipliği:** ADR-0009'daki tablo sahipliği (hangi tablo platform, hangisi tenant) aynen korunur; yalnızca
  fiziksel yer tek veritabanıdır. Outbox ve olay teslimleri `platform` şemasında kalır, `TenantId` taşır ve RLS
  dışındadır (işleyici tenant bağlamını açıkça kurar, ADR-0007).
- **Migration disiplini:** tek veritabanı bütün tenant'ları etkilediği için migration'lar geriye uyumlu ve kesintisiz
  olmalıdır (önce ekle, sonra taşı, en son kaldır; uzun süren veri adımları ayrı). RLS açıkken migration'daki veri
  geri doldurma adımları politikayı geçici kapatır (OpportunityManagement örneği).
- **Dışa aktarma ve silme:** bir tenant'ın bütün verisi `TenantId` ile eksiksiz dışa aktarılabilir ve silinebilir
  olmalıdır; bu, tenant başına yedeğin yerini alır (Dilim 3).

## Uygulama planı (dilimler)

Geri dönüş `git revert` ile yapılmaz: ADR-0009'dan sonra 17 tenant migration'ı ve CRM dilimleri iki bağlamlı yapının
üzerine yazıldı. Hedef eski model, yol bugünkü kodun ileriye doğru düzenlenmesidir. Her dilim tek başına derlenir,
testleri geçer ve ayrı PR olarak incelenir.

### Dilim 1 — Fiziksel birleştirme (uygulandı)

- `ModulixDbContext` sabit `Modulix` bağlantısına bağlanır; `ITenantConnectionResolver`, `ITenantDatabaseRegistry`,
  `ITenantProvisioningService`, `TenantMigrationRunner`, `TenantDatabaseNaming` ve `migrate-tenants` komutu kaldırılır.
- `PlatformDbContext` ve `ModulixDbContext` **şimdilik iki sınırlı bağlam olarak kalır**; ikisi de aynı veritabanına
  bağlanır. Migration zincirleri ayrı geçmiş tablolarıyla (`platform.__EFMigrationsHistory`,
  `core.__EFMigrationsHistory`) aynı veritabanında yaşar; `migrate` komutu ikisini de uygular.
- `Tenant` entity'sinden `DatabaseName`, `ProvisioningStatus`, `SchemaVersion` ve kurulum/askı yöntemleri kalkar
  (platform migration'ı kolonları düşürür). "Kurulumu tamamlanmış tenant" süzgeçleri `Status == Active` olur.
- Outbox ve olay teslimleri (`platform.OutboxMessages`, `platform.EventDeliveries`) `ITenantEntity` değildir ve RLS dışıdır;
  ADR-0009'daki fiziksel ayrımın yerini `ModulixDbContext`'teki aktif tenant sorgu filtresi ve ham SQL'deki `TenantId`
  koşulu alır (işleyici, olay ekranı ve kira kalp atışı tenant bağlamında çalışır).
- Uygulama kullanıcısının `core.AuditLog` üzerindeki `UPDATE/DELETE` yasağı, provisioning SQL'inden tenant
  migration'ına taşınır (rol yoksa atlanır).
- Testler: fiziksel ayrım testleri (`TenantDatabaseIsolationTests`) kaldırılır; tek veritabanında satır düzeyi izolasyon
  testleri (`TenantIsolationTests`) ADR-0002 biçimine döner; `TableInventoryTests` tek veritabanındaki her tabloyu
  platform (RLS dışı), muaf (kuyruk) ve tenant (RLS'li) olarak sınıflandırır; test fabrikası tenant veritabanı
  oluşturmaz.
- İşletim: `init.sql`'den `dbcreator` kalkar, `db-up.ps1` yalnızca `migrate` çağırır, `docs/tenant-isletim.md` tek
  veritabanı rehberine dönüşür, `docs/tenant-veri-tasima.md` kaldırılır, README ve CLAUDE.md güncellenir.

### Dilim 2 — Bağlam birleştirme (uygulandı)

- İki `DbContext` tek `ModulixDbContext`'te birleşir (Identity tabloları dahil); `IPlatformConfiguration` işareti ve
  tasarım zamanı fabrikalarından biri kalkar.
- İki migration zinciri tek zincir olarak yeniden üretilir (tek baseline; gerçek veri olmadığı için sıfır kurulum).
  Geçmiş tablosu tek olur.
- `MembershipId` taşıyan tenant tablolarında (`core.MembershipCompanies`, `core.MembershipRoles`, `core.TeamMembers`)
  `platform.Memberships`'e yabancı anahtar geri gelir; tenant oluşturma ve davet akışları tek işlemde çalışır.
- Platform ad çözümlemeleri (üye adı, firma adı) iki adımlı bellek içi birleştirme yerine tek sorgu olur.

### Dilim 3 — Sadeleştirme ve işletim (uygulandı)

- Olay dağıtıcısı, teklif süresi işleyicisi, olay ekranı ve sağlık kontrolü tenant başına döngü yerine tek veritabanı
  taraması yapar (tenant bağlamı yine kayıt başına açıkça kurulur).
- Tenant verisi dışa aktarma ve silme komutu (`TenantId` ile eksiksiz) ve yedekleme runbook'u tek veritabanına göre
  yazılır.
- Eski `modulix_tenant_*` veritabanlarının temizliği işletim rehberinde belgelenir.

## Sonuçlar

- Tenant kurulumu anında tamamlanır; platform ve tenant yazımları aynı veritabanında olduğu için ara durum ve telafi
  mantığı gerekmez (Dilim 2'de aynı işlem olur).
- Yeni tenant tablosu eklerken kural değişmez: `ITenantEntity`, tenant migration'ında `TenantIsolationSql.AddTable`,
  `TableInventoryTests` kapısı.
- Tenant başına ayrı yedek ve taşıma kalkar; bunun yerine `TenantId` ile dışa aktarma/silme ve tek veritabanı
  yedeği vardır.
- ADR-0009 "Geri alındı" durumuna geçer; `ITenantConnectionResolver` soyutlaması ileride gerekirse ADR-0002'deki gibi
  yeniden eklenebilir, ancak ölçüm olmadan tenant başına veritabanına dönülmez (yol haritası Ç24.4).

## İleride yapılacaklar

Üç dilim 30 Ekim 2026'da `main`'e alındı (PR #72, #73, #74). Aşağıdakiler bilinçli olarak dışarıda bırakıldı; her biri
kendi dilimi veya ADR'siyle ele alınır.

### Bu ADR'nin sadeleştirme kalıntıları (uygulandı)

- **Ad çözümlemelerini tek sorguya al.** Üye adı / firma adı eşleştirmeleri hâlâ iki ayrı sorguyla bellekte
  birleştiriliyor; tek bağlamda join yeterlidir. Yerler: `MembershipDataScope.TeamOwnersAsync`,
  `PlatformAdministration.ListAdministratorsAsync`, `UserAdministration` üye listeleri, `RecordHandover` ad çözümü.
  Kabul: iki adımlı "önce kimlikleri topla, sonra adları çek" deseni kalmaz; sonuçlar ve sayfalama aynı.
- **Davet ve sektör profili akışlarını tek işleme al.** `UserAdministration.InviteAsync` "önce üyelik, sonra tenant
  tarafı, hata olursa telafi" deseniyle; `SectorProfileService.ApplyAsync` iki ayrı kaydetme adımıyla çalışıyor. Tek
  bağlamda tek transaction yeterlidir; telafi kodu kalkar. Kabul: davet sırasında tenant tarafı başarısız olursa üyelik
  satırı da oluşmaz (geri alma testi), profil uygulaması ya tamamen yazılır ya hiç.
- **ADR-0018 dipnotu.** Ürün/hizmet ADR'sindeki envanter satırı tarihsel kayıt olarak kalır; bu ADR'ye işaret eden dipnot eklendi.
- Ayrıca davet iptali, yeniden davet, devre dışı bırakma ve etkinleştirme akışlarındaki çift kaydetme adımları tek kaydetmeye
  indirildi; `UserAdministration.ListAsync` rol süzgeci alt sorgu oldu. Geri alma testleri: `TransactionalFlowsTests`.

### Kalıcı sınır (yapılmayacak)

- **Teklif süre dolumu işleyicisi** tenant başına dönmeye devam eder (yalnızca CRM modülü açık tenant'larda). Teklif
  tabloları Row-Level Security altında olduğundan çapraz tenant tarama mümkün değildir; tenant bağlamı kayıt başına
  kurulmak zorundadır. Tenant sayısı büyüdüğünde ölçek kapısı yol haritası Ç24.4'tür.

### Ayrı ADR isteyen kurallar (proje kuralları belgesi)

- **"Birim" (BusinessUnit) kapsamı.** Kural belgesi kayıt kapsamını "kendi / takım / birim / şirket / tümü" olarak
  tanımlar. Kodda `RecordScope` üç seviyedir (`Own`, `Team`, `All`); şirket ve şube ADR-0008 ile erişim sınırı olarak
  vardır ama kayıt kapsamı seviyesi değildir, "birim" hiç yoktur. Yeni ADR: birim kavramının şirket/şube modeline
  yerleşimi, `RecordScope.Unit` ve `RecordScope.Company` seviyeleri, kayıt boru hattındaki süzgeç, rol şablonları ve
  dört test türü (mutlu yol, doğrulama, tenant izolasyonu, yetki).
- **TR/EN çeviri altyapısı.** Kural belgesi arayüz metinlerinin çeviri altyapısından gelmesini ister; bugün metinler
  Türkçe olarak Razor dosyalarına ve API hata iletilerine gömülüdür. Yeni ADR: kaynak dosya biçimi (`.resx` veya JSON),
  Blazor WebAssembly'de kültür seçimi, API sorun ayrıntılarının dili, tenant/kullanıcı dil tercihi, mevcut metinlerin
  göçü ve eksik çeviri için test kapısı. Kapsamı büyük olduğu için modül modül (önce Core ve giriş akışı) yapılır.
- **Yapay zekâ denetim kaydı.** "Tüm AI işlemleri denetim kaydına yazılır" kuralı için henüz AI modülü yoktur; ilk AI
  özelliğinin ADR'sinde denetim kaydı girişi (`ai.*` kayıt türleri, istem ve yanıt özetleri, kişisel veri maskesi) tasarım
  koşulu olarak yer alır.
