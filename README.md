# Modulix

[![CI](https://github.com/gulerseker08-art/modulix/actions/workflows/ci.yml/badge.svg)](https://github.com/gulerseker08-art/modulix/actions/workflows/ci.yml)

AI destekli, modüler, multi-tenant kurumsal iş yönetim platformu. Faz 1: Core + CRM + Lojistik, tüm ekranlar mobil uyumlu.

Mimari kararlar `docs/adr` klasöründedir.

## Gereksinimler

| Araç | Neden | Kontrol komutu |
| --- | --- | --- |
| .NET 10 SDK | Derleme ve testler | `dotnet --version` (10. ile başlamalı) |
| Git | Sürüm kontrolü | `git --version` |
| Docker Desktop | Yerel SQL Server | `docker --version` |

## İlk kurulum (yalnızca bir kez)

Bu klasörün içeriğini boş repo klasörünüze kopyalayın, sonra repo kökünde çalıştırın:

**Windows**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\init-solution.ps1
```

**macOS / Linux**

```bash
bash scripts/init-solution.sh
```

Betik solution'ı, 9 kaynak ve 6 test projesini oluşturur, referansları kurar, derler ve mimari testleri çalıştırır. İnternet bağlantısı gerekir (NuGet paketleri iner).

Beklenen son durum: derleme hatasız biter ve `Modulix.ArchitectureTests` içindeki tüm testler başarılı olur.

## Veritabanı (Görev 2)

SQL Server 2025 Developer, Docker içinde çalışır; bilgisayarınıza ayrıca SQL Server kurmanız gerekmez.

| Ayar | Değer |
| --- | --- |
| Sunucu | `localhost,14333` (1433 değil; yerel SQL Server'ınızla çakışmasın diye) |
| Veritabanı | `Modulix` |
| Şemalar | `platform`, `core`, `meta`, `crm`, `logistics`, `ai` |
| `modulix_app` | Uygulamanın kullanıcısı. Veri okur/yazar; tablo veya güvenlik politikası değiştiremez. |
| `modulix_migrator` | Yalnızca migration komutunun kullanıcısı (db_owner). |
| `sa` | Yönetici; SSMS ile bakmak için. |

Şifreler repo kökündeki `.env` dosyasındadır. Bu dosya ilk çalıştırmada rastgele şifrelerle oluşturulur ve git'e gönderilmez. Uygulama bağlantı cümlelerini `dotnet user-secrets` üzerinden okur; kod deposunda şifre bulunmaz.

**SSMS ile bağlanmak:** Sunucu `localhost,14333`, SQL Server Authentication, kullanıcı `sa`, şifre `.env` içindeki `MSSQL_SA_PASSWORD`. Bağlantı penceresinde "Trust server certificate" seçin.

**Yeni migration eklemek:**

```powershell
dotnet ef migrations add <Ad> --project src/Modulix.Infrastructure --startup-project src/Modulix.Api --output-dir Persistence/Migrations
powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1
```

Migration'ları `dotnet ef database update` ile değil, `db-up.ps1` (içinde `dotnet run --project src/Modulix.Api -- migrate`) ile uygulayın; böylece yalnızca migration kullanıcısı şema değiştirir.

## Giriş ve aktif tenant (Görev 4)

Kimlik doğrulama ASP.NET Core Identity ve şifreli, `HttpOnly`, `Secure`, `SameSite=Strict` bir oturum çereziyle yapılır. Kullanıcı birden fazla tenant'a üye olabilir.

| Uç nokta | Ne yapar |
| --- | --- |
| `POST /api/auth/login` | Giriş. Tek üyelik varsa o tenant otomatik seçilir. 5 hatalı denemede hesap 15 dakika kilitlenir. |
| `GET /api/auth/me` | Kullanıcı, etkin üyelikleri ve aktif tenant. |
| `POST /api/auth/active-tenant` | Aktif tenant seçimi. Yalnızca etkin üyeliği olan tenant seçilebilir. |
| `GET /api/tenant/current` | Aktif tenant; yoksa 403. |
| `POST /api/auth/logout` | Çıkış. |

Aktif tenant her istekte veritabanından yeniden doğrulanır: üyelik devre dışı bırakılır veya tenant kapatılırsa erişim bir sonraki istekte kalkar. İstemcinin gönderdiği bir başlık veya parametre tenant belirlemez.

**Örnek kullanıcılar** (yalnızca geliştirme, `scripts\db-up.ps1` oluşturur; şifre `Modulix.Demo1`):

| Kullanıcı | Üyelik |
| --- | --- |
| `ayse@acme.demo` | Acme Lojistik |
| `mehmet@beta.demo` | Beta Taşımacılık |
| `deniz@ortak.demo` | İkisi de (tenant seçmesi gerekir) |

Elle denemek için Visual Studio'da `src/Modulix.Api/Modulix.Api.http` dosyasını açın.

## Modüller, roller ve izinler (Görev E4)

Her modül kendini tek bir `IModule` uygulamasıyla tanıtır (`Modulix.Core.Contracts.Modules`): anahtar (ör. `crm`), ad ve izin listesi. İzin anahtarları modül anahtarıyla başlar (`crm.companies.read`). Modüller `src/Modulix.Api/Program.cs` içinde `AddModulixModules(...)` ile kaydedilir; kaydedilmeyen modül mimari testi kırar.

| Kavram | Nerede | Kural |
| --- | --- | --- |
| Modül hakkı (entitlement) | `platform.ModuleEntitlements` | Tenant başına açık/kapalı. Kaydı olmayan modül kapalıdır; `core` her zaman açıktır. Kapatma veri silmez. |
| Rol | `core.Roles` | Tenant'a özel; ad ve izin listesi. Row-Level Security ile korunur. |
| Rol ataması | `core.MembershipRoles` | Rol üyeliğe atanır; aynı kişinin her tenant'ta ayrı rolleri olur. Başka tenant'ın rolü veritabanı düzeyinde de atanamaz. |

Geçerli izin = kullanıcının rollerindeki izinler ∩ tenant için açık modüller. Rol veya modül değişikliği bir sonraki istekte geçerli olur. Modül uç noktaları `.RequirePermission(CrmModule.Permissions.CompaniesRead)` veya `.RequireModule("crm")` ile korunur; izin yoksa 403 döner.

| Uç nokta | Ne yapar | İzin |
| --- | --- | --- |
| `GET /api/tenant/modules` | Kayıtlı modüller ve aktif tenant için açık olanlar | Aktif tenant |
| `GET /api/tenant/access` | Oturumdaki kullanıcının açık modülleri ve geçerli izinleri | Aktif tenant |
| `GET /api/roles` | Rolleri listeler | `core.roles.manage` veya `core.users.manage` |
| `POST /api/roles`, `PUT /api/roles/{id}` | Rol ekler, düzenler | `core.roles.manage` |
| `GET /api/roles/permissions` | Rollere atanabilecek izinler (yalnızca açık modüllerin) | `core.roles.manage` |
| `GET`/`PUT /api/memberships/{id}/roles` | Bir üyeliğin rolleri | `core.users.manage` |

Örnek veride Acme'de CRM ve Lojistik, Beta'da yalnızca Lojistik açıktır; örnek kullanıcıların hepsi kendi tenant'larında "Yönetici" (sistem) rolündedir.

### Yönetim izinleri ve "Yönetici" rolü (Y1)

| İzin | Ne verir |
| --- | --- |
| `core.users.manage` | Kullanıcıları davet eder, rollerini atar, devre dışı bırakır, şifre sıfırlama bağlantısı gönderir (Y2) |
| `core.roles.manage` | Rolleri ve izinlerini tanımlar |
| `core.settings.manage` | Firma ayarlarını yazar (`core.tenant-setting`); okumak için izin gerekmez |
| `core.audit.read` | Denetim kaydını okur (ekranı Y5'te) |

- Her tenant'ta bir **sistem rolü** vardır: "Yönetici". İzin listesi tutmaz; tenant için açık tüm modüllerin tüm izinlerini verir, yeni açılan modülün izinleri de kendiliğinden gelir. Adı ve izinleri değiştirilemez (409). `IRoleService.EnsureSystemRoleAsync` oluşturur; `SystemRole` migration'ı eski "Yönetici" rollerini sistem rolüne çevirdi ve `core.roles.manage` olan rollere diğer üç yönetim iznini ekledi.
- **Son yönetici korunur:** etkin üyeliği olan son yöneticinin Yönetici rolü kaldırılamaz (409). Kural `AdministratorGuard` içindedir; aynı anda birbirinin yetkisini kaldırmaya çalışan iki yöneticiden yalnızca biri geçer.
- Rol oluşturma, rol düzenleme ve rol atamaları denetim kaydına yazılır (`core.role`, `core.membership`).
- Arayüzde yönetim izinlerinden biri olan kullanıcı menüde **Yönetim** bölümünü görür (`/yonetim`). Modül açıp kapatmak için şimdilik bir uç nokta yoktur (platform yöneticisi rolü sonraki görev); kodda `IModuleEntitlements.SetEnabledAsync` kullanılır.

### Kullanıcı yönetimi (Y2)

Ekran: **Yönetim → Kullanıcılar** (`/yonetim/kullanicilar`). Uç noktalar `/api/users` altındadır ve `core.users.manage` ister; kimlik üyelik kimliğidir, başka tenant'ın üyeliği 404 döner.

- **Davet:** `POST /api/users/invitations` (e-posta, ad, roller). E-posta kayıtlı değilse şifresiz hesap ve "Davet bekliyor" üyeliği oluşur, kişiye `/davet?uyelik=…&token=…` bağlantısı gider; kişi şifresini belirler ve doğrudan giriş yapar. Bağlantı 7 gün geçerli ve tek kullanımlıktır (şifre belirlenince hesabın güvenlik damgası değişir). E-posta başka bir tenant'ta kayıtlıysa üyelik hemen etkin olur ve bilgi e-postası gider; şifre değişmez. Zaten üyeyse 409. Daveti yeniden gönderme (1 dakikada bir) ve iptal etme vardır; iptal edilen davetin bağlantısı çalışmaz.
- **Sınır:** tenant başına saatte en fazla 50 davet (`Users:MaxInvitationsPerHour`); aşılırsa 429.
- **Devre dışı bırakma:** `POST /api/users/{id}/disable` ve `/enable`. Kişi kendini devre dışı bırakamaz; son etkin yönetici korunur. Devre dışı üye bir sonraki isteğinde 403 alır; diğer tenant'lardaki üyelikleri ve kayıtları kalır.
- **Şifre sıfırlama ve kilit:** yönetici kişiye 24 saat geçerli bir bağlantı gönderir (`/sifre-sifirla`); şifreyi görmez ve belirlemez. 5 hatalı denemeyle kilitlenen hesabın kilidi açılabilir. Hesap birden fazla tenant'ta kullanılıyorsa ikisi de hepsini etkiler; e-postada isteyen tenant yazılır.
- **Yönetici kuralı:** Yönetici rolünü vermek veya almak ve bir yöneticinin hesabında işlem yapmak yalnızca yöneticiye açıktır (403). Böylece `core.users.manage` izni olan biri kendini yönetici yapamaz. Rol atamasında kural yönetici kilidi altında denetlenir.
- **Kayıt devri (Y2b, [ADR-0004](docs/adr/0004-kayit-devri.md)):** devre dışı bırakırken kişinin sorumlu olduğu kayıtlar başka bir etkin üyeye devredilebilir (`POST /api/users/{id}/disable` gövdesi `{ "transferToMembershipId": … }`); devre dışı kişinin kayıtları sonradan da devredilir (`/transfer-records`). Sayılar: `GET /api/users/{id}/owned-records`. CRM'de firmalar, açık fırsatlar ve açık aktiviteler devredilir; kapanmış olanlar eski sorumlusunda kalır. Devir kayıt boru hattından geçer: işlemi yapanın kayıtları düzenleme izni olmalı, her kayıt denetim kaydına düşer; devir ve devre dışı bırakma birlikte yapılır ya da hiç yapılmaz. Yeni bir modül, sorumlu alanı `ownerId` olan kayıt türlerini `IModule.OwnedRecords` ile bildirir.
- Davet, iptal, durum değişikliği, şifre sıfırlama isteği ve kilit açma `core.membership` kaydı olarak denetim kaydına yazılır. Son giriş, giriş ve tenant seçiminde güncellenir.
- **E-postadaki bağlantılar** `App:PublicUrl` adresiyle oluşturulur (ör. `https://app.firmaniz.com`). Canlı ortamda zorunludur: isteğin adresine güvenilirse saldırgan, kendi sunucusuna giden şifre sıfırlama bağlantıları ürettirebilir. Development'ta boşsa isteğin adresi kullanılır. Yerelde e-postalar Mailpit'te görünür (http://localhost:8025).

## CRM modülü

İlk sektör modülü ve diğerlerinin şablonu. Firma, kişi, fırsat ve aktivite; tablolar `crm` şemasında, Row-Level Security ile korunur.

| Uç nokta | Ne yapar | İzin |
| --- | --- | --- |
| `GET /api/crm/companies?search=&page=&pageSize=` | Firmalar (sayfalı, aramalı) | `crm.companies.read` |
| `GET /api/crm/companies/{id}` | Tek firma | `crm.companies.read` |
| `POST`, `PUT /{id}`, `DELETE /{id}?version=` | Ekle, düzenle, sil | `crm.companies.write` |
| `/api/crm/contacts`, `/api/crm/opportunities`, `/api/crm/activities` | Aynı kalıp | `crm.<kaynak>.read` / `.write` |
| `GET /api/tenant/members` | Kuruluşun etkin üyeleri (sorumlu seçimi) | Aktif tenant |

Listeler filtre alır; bilinmeyen filtre veya geçersiz değer 422 döner. `ownerId=me` isteği yapan kullanıcıdır.

| Kaynak | Filtreler |
| --- | --- |
| Firmalar | `status` (Prospect, Customer, Inactive), `ownerId` |
| Kişiler | `companyId` |
| Fırsatlar | `companyId`, `contactId`, `stage`, `open=true` (kazanılmamış ve kaybedilmemiş), `ownerId` |
| Aktiviteler | `companyId`, `contactId`, `opportunityId`, `open=true`, `dueBefore=<ISO zaman>`, `ownerId` |

- Firma, fırsat ve aktivitenin bir sorumlusu vardır; oluştururken boş bırakılırsa kaydı oluşturan kullanıcı atanır. Sorumlu, kuruluşun etkin üyesi olmalıdır.

- Tüm işlemler kayıt boru hattından geçer: modül tenant için kapalıysa veya izin yoksa 403, başka tenant'ın kaydı 404, başka tenant'ın kaydına bağlanmak 422 döner. Bağlı kaydı olan firma silinemez (409). Audit ve outbox kaydı otomatik yazılır.
- Aynı kayıtlara genel uçtan da ulaşılır (`/api/records/crm.company` vb.); aynı izinler geçerlidir.
- Arayüz API ile aynı adresten sunulur: `https://localhost:7026` → giriş, kuruluş seçimi, Firmalar / Kişiler / Fırsatlar / Aktiviteler. Telefonda listeler kart görünümüne döner.
- Firma adına tıklayınca firma detay sayfası açılır: kişiler, fırsatlar ve aktiviteler tek ekranda; aktivite ve fırsat oradan eklenir. Fırsatlar sayfası varsayılan olarak satış hunisini (aşama sütunları, sürükle bırak) gösterir. Aktiviteler sayfası "Bugün" görünümüyle açılır (bugüne kadar yapılması gereken açık işlerim).

**Yeni bir sektör modülü eklemek** (CRM'i örnek alın):

1. `Domain`: entity'ler `ITenantEntity` uygular. `Persistence`: `IEntityTypeConfiguration` sınıfları, şema = modül anahtarı (aksi halde uygulama açılmaz).
2. `Records`: her entity için bir kayıt deposu (`CrmRecordStore` kalıbı); `IModule.ConfigureServices` içinde kaydedilir.
3. `Program.cs` → `ModulixModules.Create()` listesine modülü ekleyin, uç noktaları `MapCrmEndpoints` gibi eşleyin.
4. Migration üretin ve her tablo için `TenantIsolationSql.AddTable` ekleyin; eklenmezse `Every_tenant_table_is_protected_by_row_level_security` testi kırılır.

## E-posta

Uygulama e-postayı hemen göndermez: `IEmailQueue.Enqueue(...)` e-postayı `platform.EmailMessages` kuyruğuna yazar, arka plandaki işleyici (her 10 saniyede) SMTP ile gönderir. Kuyruğa yazma, çağıranın kaydıyla aynı işlemdedir; kayıt geri alınırsa e-posta da gitmez. Aktif tenant varsa e-posta o tenant'a bağlanır; yoksa platform e-postasıdır (ör. şifre sıfırlama). `IEmailQueue` `Modulix.Core.Contracts` içindedir, modüller de kullanabilir.

- Gönderilemeyen e-posta 1 dk, 5 dk, 15 dk ve 1 saat arayla tekrar denenir; 5. denemeden sonra veya sunucu alıcıyı kalıcı olarak reddederse (5xx) `Failed` olur ve hata `LastError` alanında kalır.
- Birden fazla uygulama örneği aynı anda çalışabilir; her e-posta tek bir örnek tarafından alınır. Kayıtlara alıcı adresi değil, yalnızca e-postanın kimliği yazılır.
- Kullanıcı girdisini HTML gövdeye eklemeden önce kodlayın (`WebUtility.HtmlEncode`).

**Yerelde:** `scripts\db-up.ps1` SQL Server'ın yanında **Mailpit**'i de başlatır. Development ayarları e-postaları ona gönderir; hiçbir e-posta dışarı çıkmaz, hepsi **http://localhost:8025** adresinde görünür.

```powershell
# SMTP ayarlarını sınamak için deneme e-postası (sonucu yazar)
dotnet run --project src/Modulix.Api -- test-email ayse@acme.demo
```

**Gerçek sunucu (canlı ortam):** `appsettings.json` içindeki `Email` bölümü; şifre kod deposuna yazılmaz.

| Ayar | Örnek | Not |
| --- | --- | --- |
| `Email:FromAddress` | `noreply@firmaniz.com` | Gönderen adres; SPF/DKIM kayıtları bu alan adı için yapılmalı |
| `Email:FromName` | `Modülix` | |
| `Email:Smtp:Host`, `Port` | `smtp.sendgrid.net`, `587` | Herhangi bir SMTP hizmeti (SendGrid, Amazon SES, Mailgun, Office 365) |
| `Email:Smtp:Security` | `Auto` | `Auto`, `StartTls`, `SslOnConnect`; `None` yalnızca Mailpit için |
| `Email:Smtp:UserName` | `apikey` | Boşsa kimlik doğrulama yapılmaz |
| `Email:Smtp:Password` | | `dotnet user-secrets set Email:Smtp:Password ...` veya ortam değişkeni `Email__Smtp__Password` |

`FromAddress` veya `Smtp:Host` boşsa işleyici başlamaz ve uyarı yazar; e-postalar kuyrukta bekler, ayarlar tamamlanıp uygulama yeniden başlatılınca gönderilir.

## Sürekli entegrasyon (Görev 3)

`main` dalına her push'ta ve her pull request'te GitHub Actions (`.github/workflows/ci.yml`):

1. SQL Server'ı yereldekiyle aynı `compose.yaml` ve `init.sql` ile açar; şifreler her çalışmada yeniden üretilir.
2. Release modunda derler. Yüksek veya kritik güvenlik açığı olan bir paket varsa derleme durur.
3. Migration'ları uygular; mimari, birim ve entegrasyon testlerini çalıştırır.

CI tüm dallarda çalışır. Yeni migration'ı GitHub üzerinde üretmek için **Actions → EF migration olustur → Run workflow**.

Sonuç GitHub'da reponun **Actions** sekmesinde ve README'deki rozette görünür. Bir çalışma yaklaşık 5–8 dakika sürer; özel repolarda bu süre aylık ücretsiz dakika kotasından düşer.

## Günlük komutlar

```powershell
# Bilgisayar açıldıktan veya git pull sonrası: veritabanını başlat ve güncelle
powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1

dotnet build
dotnet test tests/Modulix.ArchitectureTests
dotnet test tests/Modulix.Core.Tests
dotnet test tests/Modulix.IntegrationTests

# API'yi çalıştır; sağlık kontrolü: /health ("Healthy" dönmeli)
dotnet run --project src/Modulix.Api
```

## Proje yapısı ve bağımlılık kuralları

| Proje | Görev | Referans verebileceği projeler |
| --- | --- | --- |
| `Modulix.Core.Contracts` | Modüllerin görebildiği tek Core projesi | — |
| `Modulix.Core` | Tenant, yetki, entitlement, kayıt boru hattı, audit, outbox, metadata | Core.Contracts |
| `Modulix.Infrastructure` | EF Core, SQL Server, RLS, dosya, e-posta | Core, Core.Contracts |
| `Modulix.Modules.Crm` | Firma, kişi, fırsat, aktivite | Core.Contracts |
| `Modulix.Modules.Logistics` | Sevkiyat, taşıyıcı, maliyet | Core.Contracts |
| `Modulix.AI` | LLM katmanı, tool sözleşmesi | Core.Contracts |
| `Modulix.Shared` | API DTO'ları ve doğrulama kuralları | — |
| `Modulix.Api` | Host, endpoint'ler | Hepsi |
| `Modulix.Web` | Blazor WebAssembly, responsive arayüz | Shared |

Bu kurallar `tests/Modulix.ArchitectureTests` ile her test çalışmasında denetlenir. Örneğin CRM projesine Lojistik referansı eklenirse test kırılır. Yeni bir proje eklemeden önce kural tablosunu (`ProjectReferenceRulesTests.AllowedReferences`) güncelleyin.

## Sorun giderme

- **"A compatible .NET SDK was not found"**: `global.json` .NET 10 SDK istiyor. `dotnet --list-sdks` ile kurulu sürümleri kontrol edin.
- **PowerShell betik çalıştırmayı engelliyor**: Komutu yukarıdaki gibi `-ExecutionPolicy Bypass` ile çalıştırın.
- **Docker "Engine" çalışmıyor**: Docker Desktop'ı açın, "Engine running" yazısını bekleyin.
- **`.env` silindi, SQL Server artık açılmıyor**: Veritabanı eski şifreyle kalır. Yerel veriyi silip baştan kurmak için `docker compose down -v`, ardından `scripts\db-up.ps1`.
- **Port 14333 dolu**: `.env` içindeki `MSSQL_PORT` değerini değiştirip `scripts\db-up.ps1` çalıştırın.
- **"Solution zaten var"**: Betik yalnızca boş repoda çalışır. Yeniden denemek için oluşan `src`, `tests`, `Modulix.sln*`, `.gitignore`, `.editorconfig` dosyalarını silin.
