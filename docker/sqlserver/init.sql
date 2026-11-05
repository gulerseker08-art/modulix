/*
  Modulix veritabani kurulumu (ADR-0002, ADR-0020): tek paylasimli veritabani, butun tenant'lar ayni veritabanindadir.

  - modulix_migrator: tablo olusturur ve degistirir (db_owner). Yalnizca migration komutu kullanir.
  - modulix_app:      uygulamanin kullanicisi. Veri okur/yazar; tablo, sema veya guvenlik
                      politikasi degistiremez. Boylece Row-Level Security'yi devre disi birakamaz.

  Her calistirmada ayni sonucu verir; tekrar calistirmak guvenlidir.
  Sifreler sqlcmd tarafindan ortam degiskenlerinden okunur: $(MODULIX_APP_PASSWORD) vb.
*/
SET NOCOUNT ON;
GO

IF DB_ID(N'Modulix') IS NULL
    CREATE DATABASE [Modulix];
GO

USE [master];
GO

IF SUSER_ID(N'modulix_app') IS NULL
    CREATE LOGIN [modulix_app] WITH PASSWORD = N'$(MODULIX_APP_PASSWORD)', CHECK_POLICY = ON, DEFAULT_DATABASE = [Modulix];
ELSE
    ALTER LOGIN [modulix_app] WITH PASSWORD = N'$(MODULIX_APP_PASSWORD)';
GO

IF SUSER_ID(N'modulix_migrator') IS NULL
    CREATE LOGIN [modulix_migrator] WITH PASSWORD = N'$(MODULIX_MIGRATOR_PASSWORD)', CHECK_POLICY = ON, DEFAULT_DATABASE = [Modulix];
ELSE
    ALTER LOGIN [modulix_migrator] WITH PASSWORD = N'$(MODULIX_MIGRATOR_PASSWORD)';
GO

USE [Modulix];
GO

-- Semalar (README: "Proje yapisi"). Her modul yalnizca kendi semasina yazar.
IF SCHEMA_ID(N'platform')  IS NULL EXEC (N'CREATE SCHEMA [platform] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'core')      IS NULL EXEC (N'CREATE SCHEMA [core] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'meta')      IS NULL EXEC (N'CREATE SCHEMA [meta] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'crm')       IS NULL EXEC (N'CREATE SCHEMA [crm] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'logistics') IS NULL EXEC (N'CREATE SCHEMA [logistics] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'contracts') IS NULL EXEC (N'CREATE SCHEMA [contracts] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'orders')    IS NULL EXEC (N'CREATE SCHEMA [orders] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'support')   IS NULL EXEC (N'CREATE SCHEMA [support] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'marketing') IS NULL EXEC (N'CREATE SCHEMA [marketing] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'warehouse') IS NULL EXEC (N'CREATE SCHEMA [warehouse] AUTHORIZATION [dbo];');
IF SCHEMA_ID(N'ai')        IS NULL EXEC (N'CREATE SCHEMA [ai] AUTHORIZATION [dbo];');
GO

IF USER_ID(N'modulix_migrator') IS NULL
    CREATE USER [modulix_migrator] FOR LOGIN [modulix_migrator];
GO
IF IS_ROLEMEMBER(N'db_owner', N'modulix_migrator') = 0
    ALTER ROLE [db_owner] ADD MEMBER [modulix_migrator];
GO

IF USER_ID(N'modulix_app') IS NULL
    CREATE USER [modulix_app] FOR LOGIN [modulix_app];
GO
IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NULL
    CREATE ROLE [modulix_app_role];
GO
IF IS_ROLEMEMBER(N'modulix_app_role', N'modulix_app') = 0
    ALTER ROLE [modulix_app_role] ADD MEMBER [modulix_app];
GO

-- Uygulama yalnizca veri islemleri yapabilir; DDL yetkisi yok.
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[platform]  TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[core]      TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[meta]      TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[crm]       TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[logistics] TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[contracts] TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[orders]    TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[support]   TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[marketing] TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[warehouse] TO [modulix_app_role];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[ai]        TO [modulix_app_role];
GO

PRINT N'Modulix veritabani hazir.';
GO
