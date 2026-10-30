# Veritabanı işletim rehberi (ADR-0002, ADR-0020)

Bu belge tek paylaşımlı veritabanı mimarisinin işletimini anlatır: yerel kurulum, yeni tenant açılışı,
migration'lar, gizli/yetki yapılandırması, yedekleme ve eski tenant veritabanlarının temizliği.

## Mimari özet

- **Tek veritabanı:** `Modulix`. Bütün tenant'lar aynı veritabanındadır; tenant başına ayrı veritabanı yoktur
  (ADR-0009 geri alındı, ADR-0020).
- **Platform şeması (`platform`):** tenant kayıtları, kullanıcılar/kimlik, üyelikler, modül erişimleri, oturumlar,
  e-posta kuyruğu, outbox ve olay teslimleri. Row-Level Security dışındadır; `TenantId` taşıyanlar tenant bağlamı
  kurulmadan okunmak zorunda olanlardır (giriş, firma listesi, işleyiciler).
- **Tenant şemaları (`core`, `meta`, `crm`, ...):** her tablo `TenantId` taşır. Katman 1: EF global query filter;
  katman 2: SQL Server Row-Level Security (`core.TenantIsolationPolicy`). Uygulama her bağlantı açılışında aktif
  tenant'ı salt okunur oturum değeri olarak yazar; değer yoksa hiçbir tenant satırı görünmez ve yazılamaz.
- **Tek EF bağlamı (Dilim 2):** `ModulixDbContext` kimlik, platform ve tenant tablolarını birlikte taşır. Tek migration
  zinciri vardır (`Persistence/Migrations`, geçmiş tablosu `platform.__EFMigrationsHistory`). `MembershipId` taşıyan tenant
  tabloları `platform.Memberships`'e bileşik yabancı anahtarla bağlıdır.
- **İki bağlantı:** uygulama (`modulix_app`, yalnızca veri) ve migration (`modulix_migrator`, DDL). Tenant başına
  bağlantı veya kimlik bilgisi yoktur.

## Yerel kurulum

```bash
# SQL Server + Mailpit
docker compose up -d --wait sqlserver mailpit
# Veritabanı, şemalar ve kullanıcılar
docker compose run --rm db-init
```

Windows'ta tek adım: `powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1` — ayrıca migration'ları uygular ve
örnek tenant'ları (acme, beta) oluşturur.

Bağlantı cümleleri `db-up.ps1` tarafından user-secrets'a yazılır (kod deposuna şifre girmez); CI'da ortam
değişkenlerinden gelir. İki ad kullanılır: `ConnectionStrings:Modulix` (app) ve `ConnectionStrings:ModulixMigrations`
(migrator). Uygulama süreci veri erişimi için yalnızca app bağlantısını kullanır; migrator bağlantısı `migrate`
komutu içindir.

## Gizli ve yetki yapılandırması

- `modulix_app`: yalnızca `SELECT/INSERT/UPDATE/DELETE/EXECUTE`; DDL yok, güvenlik politikasını kapatamaz.
  `core.AuditLog` üzerinde `UPDATE/DELETE` ayrıca reddedilir (append-only; baseline migration'ı).
- `modulix_migrator`: `db_owner`. Sunucu düzeyinde `dbcreator` gerekmez.
- Şifreler `.env` + user-secrets/ortam değişkeninde; kodda, logda veya PR'da yer almaz. Üretimde secret yöneticisi
  (ör. Key Vault) referansı tercih edilir; yerel ve üretim yapılandırması ayrıdır.

## Yeni tenant açılışı

Platform yönetimi yeni tenant açarken (`POST /api/platform/tenants` veya `IPlatformAdministration.CreateTenantAsync`):

1. Platform kaydı oluşur (`platform.Tenants`, `Status = Active`).
2. Tenant bağlamında başlangıç verisi tek işlemde yazılır: sistem rolü, varsayılan şirket, şablon roller, denetim kaydı.
3. Modül hakları ve yönetici daveti platform bağlamında yazılır.

Ayrı bir kurulum (provisioning) adımı, durum makinesi veya sağlık kontrolü yoktur. Dilim 2 ile adım 1–3 tek işlemdir: hata olursa yarım kurulmuş tenant kalmaz.

## Migration'lar

Tek zincir vardır (`Persistence/Migrations`).

```bash
# Bekleyen migration'lar
dotnet run --project src/Modulix.Api -- migrate
```

ADR-0020 Dilim 2 zinciri sıfırdan üretti (tek baseline). İki zincirli sürümden gelen yerel bir veritabanı yükseltilemez;
geliştirme verisi korunmayacağı için veritabanını sıfırlayıp yeniden kurun:

```bash
docker compose down -v
powershell -ExecutionPolicy Bypass -File scriptsdb-up.ps1
```

Tek veritabanı bütün tenant'ları etkiler; bu yüzden migration'lar **geriye uyumlu ve kesintisiz** yazılır: önce ekle,
sonra veriyi taşı, en son kaldır; uzun süren veri adımları ayrı migration'a alınır. Migration'daki veri geri doldurma
adımları (UPDATE / INSERT...SELECT) RLS yüzünden oturum değeri olmadan 0 satır etkiler; bu adımlar politikayı geçici
kapatıp açar (`ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF/ON)`, OpportunityManagement
örneği).

Yeni tenant tablosu eklerken: migration'a girer ve RLS politikasına eklenir (`TenantIsolationSql.AddTable`),
aksi halde `TableInventoryTests` kırılır.

```bash
# Yeni migration
dotnet ef migrations add <Ad> --project src/Modulix.Infrastructure --startup-project src/Modulix.Api
```

## Arka plan işleri

E-posta işleyicisi platform kuyruğunu işler. Olay işleyicisi, teklif süre dolumu işleyicisi ve olay ekranı açık her
tenant için tenant bağlamını açıkça kurar (oturum değeri → RLS) ve o tenant'ın kayıtlarını işler; bağlamsız sorgu satır
döndürmez. Teklif süre dolumu kilidi tenant başınadır (`sp_getapplock`, kaynak adı tenant kimliğini içerir). Outbox ve teslim tabloları RLS dışıdır; `ModulixDbContext` bu iki tabloya aktif tenant sorgu filtresi uygular, ham SQL de `TenantId` ile süzer. Dilim 3'te
bu döngüler tek veritabanı taramasına sadeleşir.

## Yedekleme, dışa aktarma ve silme

Tek veritabanı yedeklenir. Bir tenant'ın verisi `TenantId` ile eksiksiz dışa aktarılabilir ve silinebilir olmalıdır;
komut ve runbook Dilim 3'te eklenir. O zamana kadar bir tenant'ın verisi migrator bağlantısıyla, oturum tenant'ı
yazılarak (`sp_set_session_context 'TenantId'`) tablo tablo okunabilir.

## Eski tenant veritabanlarının temizliği

ADR-0009 döneminde oluşan `modulix_tenant_<...>` veritabanları artık kullanılmaz; gerçek veri içermiyorlarsa toplu
silinebilir (yalnızca bu ön ekli veritabanları hedeflenir):

```sql
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(name) + N';'
FROM sys.databases WHERE name LIKE 'modulix_tenant_%';
EXEC sys.sp_executesql @sql;
```

Entegrasyon testleri artık tenant veritabanı oluşturmaz; test fabrikası kendi açtığı tenant'ların platform kayıtlarını
Dispose'ta siler, tenant iş satırları rastgele `TenantId` ile veritabanında kalır ve RLS yüzünden kimseye görünmez.
