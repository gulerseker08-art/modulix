/*
  Modulix - veritabanini inceleme ve tenant izolasyonunu elle deneme.
  SSMS: Sunucu localhost,14333 | SQL Server Authentication | sa | sifre: .env icindeki MSSQL_SA_PASSWORD
  Baglanti penceresinde "Trust server certificate" isaretli olmali.

  Bolumleri tek tek secip F5 ile calistirin.
*/
USE [Modulix];
GO

/* 1) Tablolar ve semalari */
SELECT s.name AS sema, t.name AS tablo
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
ORDER BY s.name, t.name;

/* 2) Uygulanan migration'lar */
SELECT MigrationId, ProductVersion FROM [platform].[__EFMigrationsHistory] ORDER BY MigrationId;

/* 3) Tenant'lar, kullanicilar ve uyelikler (Status: 1 = etkin, 2 = kapali / devre disi)
      Sifreler duz metin degil, PasswordHash kolonunda ozet olarak durur. */
SELECT Id, Name, Slug, Status, CreatedAt FROM [platform].[Tenants] ORDER BY Name;

SELECT u.Email, u.DisplayName, t.Name AS Tenant, m.Status AS UyelikDurumu, u.AccessFailedCount, u.LockoutEnd
FROM [platform].[Memberships] AS m
JOIN [platform].[Users]   AS u ON u.Id = m.UserId
JOIN [platform].[Tenants] AS t ON t.Id = m.TenantId
ORDER BY u.Email, t.Name;

/* 4) Veritabani kullanicilari ve rolleri */
SELECT dp.name AS kullanici, r.name AS rol
FROM sys.database_role_members AS rm
JOIN sys.database_principals AS r  ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals AS dp ON dp.principal_id = rm.member_principal_id
WHERE dp.name LIKE N'modulix%';

/* 5) Row-Level Security politikasi ve korunan tablolar */
SELECT name, is_enabled, is_schema_bound FROM sys.security_policies;

SELECT OBJECT_SCHEMA_NAME(target_object_id) + N'.' + OBJECT_NAME(target_object_id) AS tablo,
       predicate_type_desc, operation_desc
FROM sys.security_predicates;
GO

/* 6) Izolasyon deneyi. RLS sa dahil herkese uygulanir.
      Not: Bu oturumda tenant degerini degistirebilirsiniz; uygulama ise degeri
      "salt okunur" yazdigi icin ayni baglantida degistiremez. */

-- 6a) Oturumda tenant yok: hicbir ayar gorunmez (beklenen: 0)
EXEC sys.sp_set_session_context @key = N'TenantId', @value = NULL;
SELECT COUNT(*) AS gorunen_ayar_sayisi FROM [core].[TenantSettings];

-- 6b) Acme olarak bir ayar yaz ve oku
DECLARE @acme uniqueidentifier = (SELECT Id FROM [platform].[Tenants] WHERE Slug = N'acme');
DECLARE @beta uniqueidentifier = (SELECT Id FROM [platform].[Tenants] WHERE Slug = N'beta');

EXEC sys.sp_set_session_context @key = N'TenantId', @value = @acme;
IF NOT EXISTS (SELECT 1 FROM [core].[TenantSettings] WHERE [Key] = N'locale')
    INSERT INTO [core].[TenantSettings] ([Id], [TenantId], [Key], [Value], [UpdatedAt])
    VALUES (NEWID(), @acme, N'locale', N'tr-TR', SYSDATETIMEOFFSET());
SELECT N'Acme oturumu' AS oturum, * FROM [core].[TenantSettings];

-- 6c) Beta oturumuna gec: Acme'nin ayari gorunmez (beklenen: bos)
EXEC sys.sp_set_session_context @key = N'TenantId', @value = @beta;
SELECT N'Beta oturumu' AS oturum, * FROM [core].[TenantSettings];
GO

-- 6d) Acme oturumundayken Beta adina yazma denemesi: HATA beklenir
--     ("The attempted operation failed because the target object ... has a block predicate ...")
DECLARE @acme uniqueidentifier = (SELECT Id FROM [platform].[Tenants] WHERE Slug = N'acme');
DECLARE @beta uniqueidentifier = (SELECT Id FROM [platform].[Tenants] WHERE Slug = N'beta');
EXEC sys.sp_set_session_context @key = N'TenantId', @value = @acme;
INSERT INTO [core].[TenantSettings] ([Id], [TenantId], [Key], [Value], [UpdatedAt])
VALUES (NEWID(), @beta, N'locale', N'en-US', SYSDATETIMEOFFSET());
GO
