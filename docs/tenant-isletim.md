# Tenant veritabanı işletim rehberi (ADR-0009)

Bu belge, tenant başına ayrı iş veritabanı mimarisinin (ADR-0009) işletimini anlatır: yerel kurulum,
yeni tenant kurulumu (provisioning), tenant migration'ları, gizli/yetki yapılandırması ve yedekleme.

## Mimari özet

- **Platform veritabanı (bir tane):** `Modulix`. Tenant kayıtları + tenant→DB defteri (`platform.Tenants`),
  kullanıcılar/kimlik, üyelikler, modül erişimleri, oturumlar, e-posta kuyruğu.
- **Tenant veritabanı (tenant başına bir tane):** `modulix_tenant_<benzersiz>`. O tenant'ın bütün iş verisi;
  Row-Level Security altında (ADR-0002, ikinci katman).
- Bağlantılar iki şablondan türetilir: uygulama (`modulix_app`, yalnızca veri) ve migration
  (`modulix_migrator`, DDL + DB oluşturma). Tenant bağlantısı yalnızca veritabanı adı değiştirilerek üretilir;
  kimlik bilgisi tenant başına saklanmaz.

## Yerel kurulum

```bash
# SQL Server + Mailpit
docker compose up -d --wait sqlserver mailpit
# Veritabanı, şemalar, kullanıcılar ve migrator'a dbcreator (yeni tenant DB'leri oluşturabilsin)
docker compose run --rm db-init
```

Windows'ta tek adım: `powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1` — ayrıca platform
migration'larını uygular ve örnek tenant'ları (acme, beta) kurar; her örnek tenant kendi iş veritabanını
provisioning ile alır.

Bağlantı cümleleri `db-up.ps1` tarafından user-secrets'a yazılır (kod deposuna şifre girmez); CI'da ortam
değişkenlerinden gelir. İki ad kullanılır: `ConnectionStrings:Modulix` (app) ve `ConnectionStrings:ModulixMigrations`
(migrator). Uygulama süreci her iki bağlantıyı da bilir: veri erişimi app ile, yeni tenant DB oluşturma/migration
ise migrator ile yapılır (yetkiler SQL düzeyinde ayrıdır).

## Gizli ve yetki yapılandırması

- `modulix_app`: yalnızca `SELECT/INSERT/UPDATE/DELETE/EXECUTE`; DDL yok, güvenlik politikasını kapatamaz.
  `core.AuditLog` üzerinde `UPDATE/DELETE` ayrıca reddedilir (append-only).
- `modulix_migrator`: `db_owner` (her DB'de) + sunucu düzeyinde `dbcreator` (yeni tenant DB'si oluşturabilmek için).
- Şifreler `.env` + user-secrets/ortam değişkeninde; kodda, logda veya PR'da yer almaz. Üretimde secret yöneticisi
  (ör. Key Vault) referansı tercih edilir; yerel ve üretim yapılandırması ayrıdır.

## Yeni tenant kurulumu (provisioning)

Platform yönetimi yeni tenant açarken (`POST /api/platform/tenants` veya `IPlatformAdministration.CreateTenantAsync`):

1. Platform kaydı oluşur (`ProvisioningStatus = Pending`), benzersiz güvenli DB adı atanır.
2. `ITenantProvisioningService.PrepareAsync`: tenant DB'si oluşturulur, tenant migration'ları uygulanır, uygulama
   kullanıcısına yetki verilir (`Provisioning`).
3. Başlangıç verisi yüklenir (yönetici rolü, varsayılan şirket, modüller, davet).
4. `CompleteAsync`: sağlık kontrolünden sonra `Active`. İş API'lerine erişim bundan sonra açılır.

Durumlar: `Pending → Provisioning → Active`; hata → `Failed` (DB **silinmez**, güvenle yeniden denenir);
idari askı → `Suspended`. Kurulum idempotenttir ve aynı tenant için eşzamanlı ikinci kurulum atomik durum
geçişiyle engellenir. Platform ve tenant DB işlemleri tek atomik işlem değildir; ara durumlar yönetilir.

## Tenant migration'ları

Platform ve tenant migration'ları ayrıdır (`Persistence/Migrations/Platform` ve `.../Tenant`).

```bash
# Platform veritabanı migration'ları
dotnet run --project src/Modulix.Api -- migrate

# Tenant iş veritabanları migration'ları (kurulu bütün tenant'lar)
dotnet run --project src/Modulix.Api -- migrate-tenants
# Yalnızca seçili tenant'lar (pilot → grup): slug'larla
dotnet run --project src/Modulix.Api -- migrate-tenants acme beta
```

Runner: tek tenant/seçili grup hedefler, aynı DB'de eşzamanlı migration'ı `sp_getapplock` ile engeller, eşzamanlılık
sınırı uygular (varsayılan 4), başarısız tenant'ları raporlar ve yeniden denenebilir; her tenant'ın şema sürümünü
(`platform.Tenants.SchemaVersion`) günceller. Açılışta bütün tenant'lar otomatik migrate **edilmez**.

Yeni tenant tablosu eklerken: tenant migration setine girer ve RLS politikasına eklenir
(`TenantIsolationSql.AddTable`), aksi halde `TableInventoryTests` kırılır.

```bash
# Yeni tenant migration'ı
dotnet ef migrations add <Ad> --context ModulixDbContext --project src/Modulix.Infrastructure \
  --startup-project src/Modulix.Api --output-dir Persistence/Migrations/Tenant
# Yeni platform migration'ı
dotnet ef migrations add <Ad> --context PlatformDbContext --project src/Modulix.Infrastructure \
  --startup-project src/Modulix.Api --output-dir Persistence/Migrations/Platform
```

## Arka plan işleri

E-posta işleyicisi platform kuyruğunu işler. Olay işleyicisi (outbox/teslimler tenant DB'sindedir) kurulu her
tenant'ın bağlamında ayrı çalışır; tenant kimliği platform defterinden yeniden çözülür, job payload'ına şifre/bağlantı
konmaz. Dosya depolama, webhook ve AI/RAG henüz yok; eklendiğinde aynı sözleşmeye (doğrulanabilir tenant kimliği,
DB'nin güvenilir kaynaktan çözülmesi) uyacaktır.

## Yedekleme ve geri yükleme

Her tenant veritabanı ayrı yedeklenir ve taşınabilir; platform veritabanı ayrıca yedeklenir. Tutarlılık notları:

- Bir tenant geri yüklenince, o tenant'ın platform kaydı (DB adı, durum, şema sürümü) ile uyumlu olmalıdır.
- Tenant logoları tenant DB'sindedir (DB dışı dosya yoktur); dış sistemlerle (SMTP, ileride webhook) tutarlılık
  yedeklemede ayrıca düşünülür.
- Platform yedeği ile tenant yedekleri aynı zamana yakın alınmalıdır; tenant→DB defteri ile fiili veritabanları
  ayrışmamalıdır.

## Test veritabanlarının temizliği

Entegrasyon testleri her tenant için gerçek bir `modulix_tenant_<...>` veritabanı oluşturur; test fabrikası
(`ModulixApiFactory`) yalnızca kendi oluşturduğu veritabanlarını Dispose'ta siler. Artakalanları toplu silmek için
(yalnızca test ön ekli veritabanları hedeflenir):

```sql
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(name) + N';'
FROM sys.databases WHERE name LIKE 'modulix_tenant_%';
EXEC sys.sp_executesql @sql;
```
