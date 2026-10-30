# ADR-0009: Tenant başına ayrı iş veritabanı (Faz 3)

- **Durum:** Geri alındı, 30 Ekim 2026 (ADR-0020: tek paylaşımlı veritabanına dönüş); kabul edilmişti 26 Ekim 2026
- **Karar:** Ortak platform veritabanı + tenant başına ayrı iş veritabanı
- **İlgili:** ADR-0001 (SQL Server), ADR-0002 (tenant stratejisi; bu ADR onun "Faz 3" adımını uygular)

## Problem

ADR-0002 tek veritabanı + `TenantId` + Row-Level Security ile başladı ve tenant başına ayrı
veritabanını (Faz 3) ileriye bıraktı; bağlantı seçimini `ITenantConnectionResolver` arkasına
koydu. İzolasyonu fiziksel sınıra taşımak, tenant'ları ayrı ayrı yedekleyip taşıyabilmek ve bir
tenant'ın yükünün diğerini etkilememesini sağlamak için artık bu adımı uyguluyoruz.

## Karar

İki tür veritabanı olur:

- **Platform veritabanı (bir tane, merkezi):** tenant kayıtları ve yaşam döngüsü, ortak kullanıcı
  kimlikleri, kullanıcı–tenant üyelikleri, platform yöneticileri, modül erişimleri/kotalar ve
  **her tenant'ın hangi veritabanında olduğu + kurulum durumu + şema sürümü**.
- **Tenant başına iş veritabanı (tenant başına bir tane):** o tenant'ın bütün iş verisi — şirket,
  şube, roller/iş yetkileri, ayarlar, özel alan/seçim listesi tanımları, CRM ve diğer modül
  kayıtları, iş audit'i, outbox/olay teslimleri ve e-posta kuyruğu.

Bir tenant'ın bütün modülleri başlangıçta aynı tenant veritabanında çalışır (modül başına DB yok).

## Tasarım

### Veri erişim katmanı

- `PlatformDbContext` — platform veritabanına bağlanır; kimlik (Identity) tabloları buradadır.
- `ModulixDbContext` — **tenant** bağlamıdır; bağlantısı `ITenantConnectionResolver` ile aktif
  tenant'ın veritabanına çözülür. Query filter + `SaveChanges` tenant kuralları + Row-Level
  Security (ADR-0002, ikinci katman) korunur: tenant DB'si tek tenant içerse de kod hatası veya
  yanlış oturum değeri veriyi sızdıramaz.
- `ITenantConnectionResolver` artık platform kaydından (tenant→DB defteri) çözer; şablon bağlantı
  cümlesinin yalnızca veritabanı adını değiştirir, kimlik bilgisi tenant başına saklanmaz.
- `ITenantDatabaseRegistry` tenant→DB eşleştirmesini ve kurulum durumunu okur (kısa süreli önbellek;
  üyelik/askıya alma değişince geçersizleşir — bkz. "Güvenlik").
- `ITenantProvisioningService` yeni tenant DB'si oluşturur, migrate eder, başlangıç verisini yükler.
- `TenantMigrationRunner` seçili tenant'ları migrate eder (CLI: `migrate-tenants`).

Platform ve tenant entity'leri ayrı migration setlerindedir (`Persistence/Migrations/Platform` ve
`Persistence/Migrations/Tenant`). **Platform DB ile tenant DB arasında cross-database foreign key
veya doğrudan join yoktur.** Üyelik/rol/şirket atamaları tenant DB'sinde `MembershipId`'yi düz
`Guid` olarak taşır (FK yok); üyelik kimliğinin kendisi platform DB'sindedir. İki kaynak arasındaki
birleştirme bellek içinde iki adımda yapılır (platformdan ad/çözümleme, tenant'tan atama).

### Güvenli tenant çözümleme (istek akışı)

1. Kullanıcı kimliği doğrulanır. 2. İstenen tenant oturum claim'inden belirlenir. 3. Kullanıcının o
tenant'ta **etkin üyeliği** platform DB'sinden doğrulanır. 4. Tenant'ın durumu erişime uygun mu
denetlenir. 5. Veritabanı yalnızca platform defterinden çözülür. 6. İstek boyunca sabit tenant
bağlamıyla veri erişimi yapılır.

- Header/route/body'deki `TenantId` tek başına yetkilendirme değildir (değişmedi, ADR-0002).
- İstemci DB adı veya bağlantı cümlesi seçemez.
- Tenant bulunamazsa/kurulumu tamamlanmamışsa **varsayılan DB'ye düşülmez; işlem reddedilir.**
- Platform yöneticiliği otomatik olarak bütün iş verisine sınırsız erişim vermez; iş verisine
  erişim yine tenant bağlamı ve açık işlem (audit) üzerinden olur.
- DbContext/bağlantı tenant'a göre istek başına kurulur; havuzlanan bağlantıda oturum değeri salt
  okunur yazılır ve bağlantı havuza dönünce temizlenir (karışma olmaz).
- Üyelik iptali ve askıya alma: defter önbelleği kısa ömürlüdür ve değişiklikte geçersizleştirilir.

### Yeni tenant oluşturma (durum makinesi)

`Pending → Provisioning → Active`; hata olursa `Failed`; idari kapatma `Suspended`.

- Platform tenant kaydı oluşturulur (benzersiz, güvenli DB adı üretilir).
- Tenant DB'si oluşturulur, tenant migration'ları uygulanır, başlangıç verisi yüklenir, yönetici
  üyeliği hazırlanır, sağlık kontrolünden sonra `Active` olur.
- Kurulum **idempotent**'tir, aynı tenant için eşzamanlı iki kurulum engellenir (uygulama seviyesi
  kilidi + `Provisioning` durumu), hata sonrası güvenle yeniden denenir, başarısızlıkta DB
  **otomatik silinmez**, kurulum bitmeden iş API'lerine erişim açılmaz.
- DB oluşturma/migration yetkisi (`modulix_migrator`) uygulama veri erişim yetkisinden
  (`modulix_app`) ayrıdır. Platform ve tenant DB işlemleri tek atomik işlem varsayılmaz; ara
  durumlar ve yeniden deneme yönetilir.

### Migration yönetimi

- Platform ve tenant migration'ları ayrıdır. `migrate` komutu platform DB'sini; `migrate-tenants`
  komutu tenant DB'lerini uygular.
- Runner: tek tenant/seçili grup hedefler, DB bazında sürüm/sonuç izler, aynı DB'de eşzamanlı
  migration'ı engeller, eşzamanlılık sınırı vardır, başarısız tenant'ları raporlar ve yeniden
  denenebilir, pilot → grup ilerlemesine uygundur. Açılışta bütün tenant'lar otomatik migrate
  **edilmez**.

### Tenant bağlamına bağlı bileşenler

- Arka plan işleri (e-posta gönderici, olay dağıtıcı) doğrulanabilir tenant kimliği taşır ve
  çalışırken DB eşleştirmesini defterden yeniden çözer. Payload'a şifre/bağlantı cümlesi konmaz.
  Outbox, olay teslimleri ve e-posta kuyruğu artık tenant DB'sindedir; işleyiciler etkin tenant'lar
  üzerinde döner.
- Cache anahtarları tenant'la ön eklenir. Dosya depolama/webhook/AI henüz yok; sözleşme belgelenir,
  gereksiz altyapı kurulmaz.

## Veri sahipliği tablosu

| Veri | Sahip | Tablo/şema | Not |
| --- | --- | --- | --- |
| Tenant kaydı + yaşam döngüsü | Platform | `platform.Tenants` | DB adı, kurulum durumu, şema sürümü burada |
| Ortak kullanıcı kimlikleri | Platform | `platform.Users` (+ Identity) | Tek kimlik; birden çok tenant'a üye olabilir |
| Kullanıcı–tenant üyeliği | Platform | `platform.Memberships` | `UserId`+`TenantId`; FK yalnızca platform içinde |
| Platform yöneticisi | Platform | `platform.Users.IsPlatformAdministrator` | — |
| Modül erişimleri/kotalar | Platform | `platform.ModuleEntitlements` | Abonelik/paket kapsamı |
| Açık oturumlar | Platform | `platform.UserSessions` | Kullanıcıya bağlı, tenant'lar arası |
| E-posta kuyruğu | Platform | `platform.EmailMessages` | Tek işleyici; giriş anındaki (tenant'sız) e-postaları da taşır |
| Şirket / şube | Tenant | `core.Companies` / `core.Branches` | RLS |
| Üyeliğin şirket görevi | Tenant | `core.MembershipCompanies` | `MembershipId` düz Guid (FK yok) |
| Roller / rol atamaları | Tenant | `core.Roles` / `core.MembershipRoles` | `MembershipId` düz Guid (FK yok) |
| Ekipler / ekip üyeleri | Tenant | `core.Teams` / `core.TeamMembers` | `MembershipId` düz Guid (FK yok) |
| Tenant ayarları / logo | Tenant | `core.TenantSettings` / `core.TenantLogos` | RLS |
| Özel alan / seçim listesi | Tenant | `meta.CustomFields` / `meta.ChoiceLists` | RLS |
| Ortak firma/kişi | Tenant | `core.Parties` / `core.People` | RLS |
| CRM kayıtları | Tenant | `crm.*` | RLS |
| İş audit | Tenant | `core.AuditLog` | RLS; uygulama değiştiremez (append-only) |
| Outbox / olay teslimleri | Tenant | `platform.OutboxMessages` / `platform.EventDeliveries` | Tenant DB'sinde; şema adı korunur, RLS dışı (TenantId taşır) |

E-posta kuyruğu platform veritabanındadır (tek işleyici; giriş anındaki tenant'sız e-postalar da). Outbox ve
olay teslimleri her tenant'ın kendi veritabanındadır (şema adı tarihsel olarak `platform`; fiziksel yer tenant
DB'sidir). Aynı kullanıcının adı yalnızca platform'da tutulur; tenant DB'si kişiyi `MembershipId`/`UserId` ile
gösterir ve görünen adı platform'dan çözer (çift kaynak yok, senkronizasyon gerekmez).

## Sonuçlar

- Her tenant ayrı yedeklenip taşınabilir; "DB dışı" dosyalar (logo tenant DB'sinde) ve dış
  sistemlerle tutarlılık yedeklemede birlikte düşünülür.
- Yeni tenant tablosu eklerken tenant migration setine girer ve RLS'e eklenir (ADR-0002 kuralı).
- **Kapsam dışı (bu PR):** mevcut ortak DB'den tenant DB'lerine geçmiş veri taşıma (dry-run/apply)
  ve cutover aracı. Sözleşmesi ve prosedürü `docs/tenant-veri-tasima.md`'de belgelenir; ayrı PR'da
  uygulanacaktır. Bu nedenle tenant baseline'ı sıfır kurulum içindir; eski ortak DB'den yükseltme
  taşıma aracının sorumluluğundadır.

## Bilinen sınırlamalar

- **Çapraz-DB atomikliği:** platform ve tenant veritabanları tek atomik işlem değildir (brief Bölüm 7).
  Üyelik + rol/denetim, tenant kaydı + denetim, modül hakkı + denetim gibi akışlar iki veritabanına ayrı
  yazılır; hata sonrası telafi/yeniden deneme uygulanır (ör. davet telafisi, `Failed` durumundan yeniden
  kurulum). E-posta kuyruğu platform veritabanındadır; olay abonesinin kuyruğa yazdığı e-posta teslim
  onaylandıktan sonra yazılır (çapraz-DB olduğu için teslimle tek işlem değildir: en kötü durumda e-posta
  en fazla bir kez yazılır, tekrarlanmaz).
- **Platform geneli olay ekranı ve arka plan işleyicileri** kurulumu tamamlanmış tenant'lar üzerinde döner
  (O(tenant)); mevcut ölçekte yeterli. Çok sayıda tenant için olay ekranı tenant süzgeci zorunlu kılacak
  veya yalnızca teslimi olan tenant'ları tarayacak biçimde iyileştirilmelidir.
- **Dosya depolama, webhook, AI/RAG** henüz yok; eklendiğinde doğrulanabilir tenant kimliği taşıma ve DB'yi
  güvenilir kaynaktan çözme sözleşmesine uyacaktır (bkz. `docs/tenant-isletim.md`).
