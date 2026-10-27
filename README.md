# Modulix

[![CI](https://github.com/gulerseker08-art/modulix/actions/workflows/ci.yml/badge.svg)](https://github.com/gulerseker08-art/modulix/actions/workflows/ci.yml)

AI destekli, modüler, multi-tenant kurumsal iş yönetim platformu: birden fazla sektörün iş süreçleri aynı SaaS üzerinde. Pilot sektör lojistik; sıra Çekirdek → CRM → Lojistik → Antrepo → İK → Muhasebe. Tüm ekranlar mobil uyumlu.

Mimari kararlar `docs/adr` klasöründe, yol haritası [`docs/yol-haritasi.md`](docs/yol-haritasi.md) dosyasındadır.

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

## Veritabanı (Görev 2 / ADR-0009)

SQL Server 2025 Developer, Docker içinde çalışır; bilgisayarınıza ayrıca SQL Server kurmanız gerekmez.

Mimari: **ortak platform veritabanı + tenant başına ayrı iş veritabanı** (ADR-0009, Faz 3). İşletim ayrıntıları
`docs/tenant-isletim.md`; geçmiş veri taşıma/cutover sözleşmesi `docs/tenant-veri-tasima.md`.

| Ayar | Değer |
| --- | --- |
| Sunucu | `localhost,14333` (1433 değil; yerel SQL Server'ınızla çakışmasın diye) |
| Platform veritabanı | `Modulix` (kullanıcılar/kimlik, tenant kayıtları + tenant→DB defteri, üyelikler, modül erişimleri, oturumlar, e-posta kuyruğu) |
| Tenant veritabanı | `modulix_tenant_<benzersiz>` (tenant başına bir tane; o tenant'ın iş verisi, Row-Level Security altında) |
| Şemalar | `platform`, `core`, `meta`, `crm`, `logistics`, `ai` |
| `modulix_app` | Uygulamanın kullanıcısı. Veri okur/yazar; tablo veya güvenlik politikası değiştiremez; `core.AuditLog`'u değiştiremez. |
| `modulix_migrator` | Migration kullanıcısı (db_owner) + sunucuda `dbcreator` (yeni tenant veritabanları oluşturur). |
| `sa` | Yönetici; SSMS ile bakmak için. |

Tenant bağlantısı aktif tenant'tan `ITenantConnectionResolver` ile çözülür (şablon bağlantının yalnızca veritabanı adı değişir); istemci veritabanı adı veya bağlantı seçemez, aktif tenant yoksa varsayılan iş veritabanına düşülmez. Yeni tenant açılışında iş veritabanı otomatik kurulur (provisioning; `Pending → Provisioning → Active`).

Şifreler repo kökündeki `.env` dosyasındadır. Bu dosya ilk çalıştırmada rastgele şifrelerle oluşturulur ve git'e gönderilmez. Uygulama bağlantı cümlelerini `dotnet user-secrets` üzerinden okur (`Modulix` = app, `ModulixMigrations` = migrator); kod deposunda şifre bulunmaz.

**SSMS ile bağlanmak:** Sunucu `localhost,14333`, SQL Server Authentication, kullanıcı `sa`, şifre `.env` içindeki `MSSQL_SA_PASSWORD`. Bağlantı penceresinde "Trust server certificate" seçin.

**Yeni migration eklemek** (iki bağlam, ADR-0009):

```powershell
# Platform tablosu
dotnet ef migrations add <Ad> --context PlatformDbContext --project src/Modulix.Infrastructure --startup-project src/Modulix.Api --output-dir Persistence/Migrations/Platform
# Tenant tablosu (ayrıca RLS'e ekleyin: TenantIsolationSql.AddTable)
dotnet ef migrations add <Ad> --context ModulixDbContext --project src/Modulix.Infrastructure --startup-project src/Modulix.Api --output-dir Persistence/Migrations/Tenant
powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1
```

Migration'ları `dotnet ef database update` ile değil, `db-up.ps1` ile uygulayın. `db-up.ps1` platform migration'larını (`dotnet run --project src/Modulix.Api -- migrate`) uygular; tenant iş veritabanlarını güncellemek için `dotnet run --project src/Modulix.Api -- migrate-tenants` kullanın. Böylece yalnızca migration kullanıcısı şema değiştirir.

## Giriş ve aktif tenant (Görev 4)

Kimlik doğrulama ASP.NET Core Identity ve şifreli, `HttpOnly`, `Secure`, `SameSite=Strict` bir oturum çereziyle yapılır. Kullanıcı birden fazla tenant'a üye olabilir.

| Uç nokta | Ne yapar |
| --- | --- |
| `POST /api/auth/login` | Giriş. Tek üyelik varsa o tenant otomatik seçilir. 5 hatalı denemede hesap 15 dakika kilitlenir. |
| `GET /api/auth/me` | Kullanıcı, etkin üyelikleri ve aktif tenant. |
| `POST /api/auth/active-tenant` | Aktif tenant seçimi. Yalnızca etkin üyeliği olan tenant seçilebilir. |
| `POST /api/auth/active-company` | Aktif işletici şirket seçimi (ADR-0008). Yalnızca o oturumda erişilebilir şirket seçilebilir. |
| `GET /api/tenant/current` | Aktif tenant; yoksa 403. |
| `POST /api/auth/logout` | Çıkış. |

Aktif tenant her istekte veritabanından yeniden doğrulanır: üyelik devre dışı bırakılır veya tenant kapatılırsa erişim bir sonraki istekte kalkar. İstemcinin gönderdiği bir başlık veya parametre tenant belirlemez.

### İşletici şirketler ve aktif şirket (ADR-0008)

Bir tenant birden fazla **işletici şirket** (`core.Companies`) barındırabilir; bunlar sizin belge kesen, hesap tutan tüzel kişilerinizdir (müşteri firma `core.party` değildir). Her tenant'ın en az bir **varsayılan şirketi** vardır. Aktif şirket oturumda sunucu tarafında tutulur (`modulix:company_id` claim'i) ve her istekte erişilebilir şirketlere karşı yeniden doğrulanır; açık bir seçim yoksa veya erişilemezse varsayılan şirkete düşer.

Erişilebilir şirketler: **yönetici** (sistem rolü) tenant'ın bütün şirketlerine erişir; yönetici olmayan kullanıcı yalnızca üyeliğine atanan şirketleri görür (`core.MembershipCompanies`), hiç atama yoksa yalnızca ortak veriyi görür. Erişilebilir şirketler ve aktif şirket `GET /api/tenant/access` ile gelir; arayüzdeki şirket seçici yalnızca birden fazla şirkete erişim varsa görünür.

| Uç nokta | Ne yapar | İzin |
| --- | --- | --- |
| `GET /api/companies` | İşletici şirketleri listeler | `core.companies.manage` |
| `POST /api/companies`, `PUT /api/companies/{id}` | Şirket ekler, yeniden adlandırır (varsayılan şirket silinmez/kapatılmaz) | `core.companies.manage` |
| `GET`/`POST /api/companies/{id}/branches`, `PUT /api/companies/{id}/branches/{branchId}` | Şirketin şubelerini listeler, ekler, yeniden adlandırır | `core.companies.manage` |
| `GET`/`PUT /api/memberships/{id}/companies` | Bir üyeliğin şirket görevleri (tarih ve varsayılan şube ile) | `core.companies.manage` |

Şirket/şube oluşturma-yeniden adlandırma ve şirket görevi değişikliği denetim kaydına yazılır.

**Geçici vekâlet (adım 5):** Bir şirket görevi `ValidFrom`/`ValidUntil` taşıyabilir (ikisi de boş = süresiz; `ValidUntil` dolu = vekâlet). Süresi dolunca erişim kendiliğinden kapanır — erişilebilir şirketler sorgusu yalnızca o an geçerli görevleri sayar; ayrı bir "doldu" denetim kaydı yazılmaz (ADR-0008 kararı birebir). Geçmişte biten vekâlet reddedilir.

**Şube/lokasyon (adım 5):** Şube (`core.Branches`) idari bir birimdir; **kapsam sınırı değildir** ve hiçbir kaydı süzmez. Bir üyeliğin şirket görevi isteğe bağlı varsayılan şube taşıyabilir; şube, görevlendirilen şirkete ait olmalıdır.

**Şirket başına ayar (adım 5):** Bir ayar tanımı `SettingDefinition.PerCompany` ile şirket başına yapılabilir işaretlenebilir; o zaman değeri aktif işletici şirkete göre saklanır/çözülür (tenant geneli ayarlar eskisi gibi çalışır). Çerçeve kuruldu; ayarın kendisi (numaralama, para birimi, takvim, belge şablonu) ilgili modülün işidir, çekirdek henüz hiçbir ayarı `PerCompany` yapmaz.

**Kayıt şirket kapsamı (adım 3):** Bir kayıt türü şirket kapsamlıysa (`IRecordStore.CompanyField` dolu; ör. ileride fatura, cari hareket) kayıt boru hattı şirketi de uygular: okumada yalnızca erişilebilir şirketlerin kayıtları döner (yönetici ve şirket bağlamı olmayan arka plan/sistem tenant genelini görür), kapsam dışı kayıt "bulunamadı" görünür; oluştururken `CompanyId` verilmemişse aktif şirkete düşer ve erişilebilir olmalıdır; güncelleme kaydın şirketini değiştiremez (taşıma ayrı bir işlemdir). Olay zarfı yalnızca şirket kapsamlı kaydın `CompanyId`'sini taşır (CRM'deki müşteri-firma `companyId`'si sızmaz). Şu an bütün tenant tabloları "ortak"tır; mekanizma ilk şirket kapsamlı modül (Muhasebe/Depo) geldiğinde devreye girer.

**Örnek kullanıcılar** (yalnızca geliştirme, `scripts\db-up.ps1` oluşturur; şifre `Modulix.Demo1`):

| Kullanıcı | Üyelik |
| --- | --- |
| `ayse@acme.demo` | Acme Lojistik |
| `mehmet@beta.demo` | Beta Taşımacılık |
| `deniz@ortak.demo` | İkisi de (tenant seçmesi gerekir) |
| `platform@modulix.demo` | Hiçbiri; platform yöneticisi (girişte `/platform`'a gider) |

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
| `core.roles.manage` | Rolleri ve izinlerini tanımlar (Y3) |
| `core.settings.manage` | Firma ayarlarını yazar (`core.tenant-setting`); okumak için izin gerekmez |
| `core.audit.read` | Denetim kaydını okur (ekranı Y5'te) |

- Her tenant'ta bir **sistem rolü** vardır: "Yönetici". İzin listesi tutmaz; tenant için açık tüm modüllerin tüm izinlerini verir, yeni açılan modülün izinleri de kendiliğinden gelir. Adı ve izinleri değiştirilemez (409). `IRoleService.EnsureSystemRoleAsync` oluşturur; `SystemRole` migration'ı eski "Yönetici" rollerini sistem rolüne çevirdi ve `core.roles.manage` olan rollere diğer üç yönetim iznini ekledi.
- **Son yönetici korunur:** etkin üyeliği olan son yöneticinin Yönetici rolü kaldırılamaz (409). Kural `AdministratorGuard` içindedir; aynı anda birbirinin yetkisini kaldırmaya çalışan iki yöneticiden yalnızca biri geçer.
- Rol oluşturma, rol düzenleme ve rol atamaları denetim kaydına yazılır (`core.role`, `core.membership`).
- Gezinme: sol üstteki **ModuliX** logosu "Tüm Uygulamalar" panelini açar (ana uygulamalar → alt uygulamalar); sol menü route'tan bulunan aktif modülün sayfalarını gösterir. Liste `Modulix.Web/Services/AppCatalog.cs` içindeki ortak tanımdan gelir; görünürlüğü tenant modül sahipliği, platform yöneticiliği ve izinlerle `AppNavigation` hesaplar. Yeni ekran/modül bu listeye satır eklemektir. Menüde gizlemek yetkilendirme değildir; route (`AuthGate`) ve sunucu denetimleri aynen geçerlidir.
- Arayüzde yönetim izinlerinden biri olan kullanıcı menüde **Yönetim** bölümünü görür (`/yonetim`). Modül açıp kapatmak için şimdilik bir uç nokta yoktur (platform yöneticisi rolü sonraki görev); kodda `IModuleEntitlements.SetEnabledAsync` kullanılır.

### Kullanıcı yönetimi (Y2)

Ekran: **Yönetim → Kullanıcılar** (`/yonetim/kullanicilar`). Uç noktalar `/api/users` altındadır ve `core.users.manage` ister; kimlik üyelik kimliğidir, başka tenant'ın üyeliği 404 döner.

- **Davet:** `POST /api/users/invitations` (e-posta, ad, roller). E-posta kayıtlı değilse şifresiz hesap ve "Davet bekliyor" üyeliği oluşur, kişiye `/davet?uyelik=…&token=…` bağlantısı gider; kişi şifresini belirler ve doğrudan giriş yapar. Bağlantı 7 gün geçerli ve tek kullanımlıktır (şifre belirlenince hesabın güvenlik damgası değişir). E-posta başka bir tenant'ta kayıtlıysa üyelik hemen etkin olur ve bilgi e-postası gider; şifre değişmez. Zaten üyeyse 409. Daveti yeniden gönderme (1 dakikada bir) ve iptal etme vardır; iptal edilen davetin bağlantısı çalışmaz.
- **Sınır:** tenant başına saatte en fazla 50 davet (`Users:MaxInvitationsPerHour`); aşılırsa 429.
- **Devre dışı bırakma:** `POST /api/users/{id}/disable` ve `/enable`. Kişi kendini devre dışı bırakamaz; son etkin yönetici korunur. Devre dışı üye bir sonraki isteğinde 403 alır; diğer tenant'lardaki üyelikleri ve kayıtları kalır.
- **Şifre sıfırlama ve kilit:** yönetici kişiye 24 saat geçerli bir bağlantı gönderir (`/sifre-sifirla`); şifreyi görmez ve belirlemez. 5 hatalı denemeyle kilitlenen hesabın kilidi açılabilir. Hesap birden fazla tenant'ta kullanılıyorsa ikisi de hepsini etkiler; e-postada isteyen tenant yazılır.
- **Yönetici kuralı:** Yönetici rolünü vermek veya almak ve bir yöneticinin hesabında işlem yapmak yalnızca yöneticiye açıktır (403). Böylece `core.users.manage` izni olan biri kendini yönetici yapamaz. Rol atamasında kural yönetici kilidi altında denetlenir.
- **Kayıt devri (Y2b, [ADR-0004](docs/adr/0004-kayit-devri.md)):** devre dışı bırakırken kişinin sorumlu olduğu kayıtlar başka bir etkin üyeye devredilebilir (`POST /api/users/{id}/disable` gövdesi `{ "transferToMembershipId": … }`); devre dışı kişinin kayıtları sonradan da devredilir (`/transfer-records`). Sayılar: `GET /api/users/{id}/owned-records`. CRM'de firmalar, açık fırsatlar ve açık aktiviteler devredilir; kapanmış olanlar eski sorumlusunda kalır. Devir kayıt boru hattından geçer: işlemi yapanın kayıtları düzenleme izni olmalı, her kayıt denetim kaydına düşer; devir ve devre dışı bırakma birlikte yapılır ya da hiç yapılmaz. Yeni bir modül, sorumlu alanı `ownerId` olan kayıt türlerini `IModule.OwnedRecords` ile bildirir. Yeni sorumluya kayıt başına atama e-postası değil, devrin tek özeti gider ([Olaylar](#olaylar-ç1)).
- Davet, iptal, durum değişikliği, şifre sıfırlama isteği ve kilit açma `core.membership` kaydı olarak denetim kaydına yazılır. Son giriş, giriş ve tenant seçiminde güncellenir.
- **E-postadaki bağlantılar** `App:PublicUrl` adresiyle oluşturulur (ör. `https://app.firmaniz.com`). Canlı ortamda zorunludur: isteğin adresine güvenilirse saldırgan, kendi sunucusuna giden şifre sıfırlama bağlantıları ürettirebilir. Development'ta boşsa isteğin adresi kullanılır. Yerelde e-postalar Mailpit'te görünür (http://localhost:8025).

### Roller ve izinler (Y3)

Ekran: **Yönetim → Roller** (`/yonetim/roller`). Uç noktalar `/api/roles` altındadır ve `core.roles.manage` ister.

- **İzin matrisi:** rol formunda izinler modüle ve kaynağa göre gruplanır (Firmalar, Sevkiyatlar…; sütunlar görüntüleme ve düzenleme). Satır adı `PermissionDefinition.ResourceName`'den gelir; verilmezse anahtardaki kaynak adı kullanılır. Düzenleme seçilince görüntüleme de seçilir. Yalnızca tenant için açık modüllerin izinleri görünür; kapalı modülün izni rolde korunur ve modül açılınca yeniden geçerli olur.
- **Yetki yükseltmeye karşı kural:** yönetici olmayan kişi bir role yalnızca kendisinde olan izni ekleyebilir veya rolden çıkarabilir (403). Böylece `core.roles.manage` izni olan biri kendine başka izin veremez. Yönetici her izni verir.
- **Silme:** `DELETE /api/roles/{id}`. Üyesi olan rol için `?moveTo={rolId}` gerekir (yoksa 409): üyeler o role taşınır, taşıma ve silme tek işlemdedir. Üye taşımak rol atamasıdır, bu yüzden `core.users.manage` de gerekir; hedef Yönetici rolüyse işlemi yapan yönetici olmalıdır. Sistem rolü silinemez. Silme ve taşıma denetim kaydına yazılır.
- **Kopyalama:** "Kopyala", yeni rol formunu aynı izinlerle ve "(kopya)" adıyla açar.
- `GET /api/roles` her rolün üye sayısını (`memberCount`) da döner. Rol değişikliği, rolün üyelerinin bir sonraki isteğinde geçerli olur.
- **Hazır roller (Y3b, [ADR-0005](docs/adr/0005-rol-sablonlari.md)):** modüller önerdikleri rolleri `IModule.RoleTemplates` ile bildirir (CRM "Satış temsilcisi", Lojistik "Operasyon"); çekirdek açık modüllerin tüm görüntüleme izinleriyle "Salt okur" şablonunu ekler. `GET /api/roles/templates` yalnızca açık modüllerin şablonlarını döner. Yeni rol formunda "Şablondan başla"; yalnızca Yönetici rolü varken rol listesi şablonları kart olarak önerir.
- **Kim neye erişebilir:** rol detayında "Bu rolde kimler var" (`GET /api/roles/{id}/members`, `core.roles.manage` veya `core.users.manage`); kullanıcı detayında "Erişim": kişinin geçerli izinleri ve her izni veren roller (`GET /api/users/{id}/access`, `core.users.manage`). Hesap sunucunun her istekte uyguladığıyla aynıdır (`MembershipPermissions`).

### Platform yönetimi (Y4)

Platformu işleten ekip için ayrı bir alan: menüde **Platform** (`/platform`), yalnızca platform yöneticisine görünür. Uç noktalar `/api/platform` altındadır.

- **Platform yöneticisi** kullanıcı düzeyinde bir bayraktır (`IsPlatformAdministrator`); firmaya bağlı değildir. Arayüzden atanamaz, komut satırıyla atanır:
  `dotnet run --project src/Modulix.Api -- platform-admin kisi@ornek.com` (kaldırmak için sonuna `--remove`). Hesap yoksa oluşturulur ve şifre belirleme bağlantısı konsola yazılır. Bayrak her istekte veritabanından okunur; kaldırılınca bir sonraki istekte 403.
- **Firma verisini görmez:** RLS atlanmaz. Platform yöneticisi yalnızca platform tablolarını (firmalar, üyelikler, modül hakları) ve sayıları görür; firmaya yazılan her şey (roller, denetim kaydı) o firmanın bağlamında yazılır.
- **Firmalar** (`/platform/firmalar`): ad, kısa ad, durum, etkin üye ve yönetici sayısı, açık modüller; arama ve durum filtresi.
- **Firma açma** tek işlemdir: firma, Yönetici sistem rolü, seçilen modüller, seçilen hazır roller ve ilk yöneticinin daveti (Y2 akışı) birlikte oluşur. Biri başarısız olursa (ör. kısa ad kullanılıyor, 409) hiçbiri oluşmaz ve e-posta gitmez.
- **Firma detayı:** yöneticiler ve bekleyen davetler, "Yönetici davet et" (yöneticisi kalmayan firmanın kurtarma yolu), ad değiştirme, kapatma ve yeniden açma. Kapatmada veri silinmez; üyeler bir sonraki isteklerinde 403 alır, firma seçim listesinden çıkar, davet bağlantıları çalışmaz.
- Açma, ad değiştirme, kapatma ve yeniden açma firmanın denetim kaydına `core.tenant` olarak, işlemi yapan platform yöneticisi adına yazılır; yönetici daveti `core.membership` kaydıdır.
- **Modül hakları (Y4b)** (`/platform/moduller`): firma × modül tablosu, her hücrede aç/kapa anahtarı; firma detayında da aynı anahtarlar. `PUT /api/platform/tenants/{id}/modules/{modül}` gövde `{ "enabled": false }`. Kapatmak veri silmez: modülün menüsü ve uç noktaları o firmada kapanır, rollerdeki izinler korunur ve modül yeniden açılınca geçerli olur. Değişiklik firmanın bir sonraki isteğinde geçerlidir ve firmanın denetim kaydına `core.tenant` olarak (`modules` önce/sonra) yazılır. Çekirdek modül kapatılamaz (400).
- **Modül kataloğu (Y4b)** (`/platform/katalog`, `GET /api/platform/catalog`): kayıtlı modüller, izinleri (kaynak ve işlem), hazır rolleri ve kaç açık firmada açık oldukları. Salt okunur; modül eklemek kodla olur.
- **Olaylar** (`/platform/olaylar`): olay teslimlerinin durumu, yeniden dene ve atla; ayrıntılar aşağıdaki [Olaylar](#olaylar-ç1) bölümünde.

### Denetim kaydı (Y5)

**Yönetim → Denetim kaydı** (`/yonetim/denetim`), `core.audit.read` izni ister (Yönetici rolünde vardır). Uç noktalar `/api/audit` altındadır ve yalnızca okur; denetim kaydını değiştiren veya silen bir yol yoktur, veritabanında da uygulama rolüne `UPDATE` ve `DELETE` yasaktır.

- **Liste:** en yeni üstte; tarih, kullanıcı, kayıt türü, kayıt, işlem (Oluşturdu, Değiştirdi, Sildi, Dışa aktardı) ve kaynak (Arayüz, Arka plan işi, İçe aktarma, AI). Satıra tıklayınca değişen alanlar eski ve yeni değerleriyle açılır; değerler okunur biçimdedir (seçenek etiketleri, kullanıcı adları, bağlı kaydın adı).
- **Filtreler:** kullanıcı, kayıt türü, işlem, kaynak ve tarih aralığı (`GET /api/audit?userId=&entityKey=&action=&source=&from=2026-01-01&to=2026-01-31&page=1`). Tarihler Türkiye saatiyle gün olarak yorumlanır, bitiş günü dahildir.
- **Gizli değerler:** okur, kayıt türünün görüntüleme iznine sahip değilse (ör. CRM izni olmayan biri firma kaydı) satırı ve değişen alanların adlarını görür, değerler "gizli" yazılır. Yönetici her şeyi görür.
- **Kullanıcı adı:** firmaya üye olmayan bir platform yöneticisinin işlemleri "Modülix platform yöneticisi" olarak görünür; sistem işlemleri "Sistem". Bir olay abonesinin yaptığı değişiklikte kişi "Sistem", kaynak "Arka plan işi · abonelik anahtarı"dır (ör. `Arka plan işi · crm.party-renamed`).
- **CSV** (`GET /api/audit/export`, aynı filtreler): noktalı virgülle ayrılmış, UTF-8 BOM'lu (Türkçe Excel doğrudan açar), her değişen alan bir satır. En fazla 50.000 kayıt; fazlası 400 döner ve tarih aralığı daraltılır. `=`, `+`, `-` veya `@` ile başlayan hücreler kesme işaretiyle başlatılır. Dışa aktarmanın kendisi de denetim kaydına (`core.audit`, filtreler ve satır sayısıyla) yazılır.
- **Kayıt geçmişi (Y5b):** CRM firma detayında "Değişiklik geçmişi" bölümü; kim, ne zaman, hangi alan eski → yeni. `GET /api/records/{tür}/{id}/history?page=1` (sayfa başına 20). Kaydı görebilen herkes görür, `core.audit.read` gerekmez; kaydı göremeyen 403 veya 404 alır.
- **Oturum olayları (Y5b):** kayıt türü "Oturum" (`core.account`): giriş, hatalı şifre, hesap kilitlenmesi, kilitliyken deneme, şifre sıfırlama ve davet kabulü; IP adresi ve tarayıcı bilgisiyle (kişisel veri, aydınlatma metnine eklenmeli). Başarılı giriş yalnızca seçilen firmaya yazılır (iki firması olan kişi için firma seçildiğinde). Hatalı şifre, kilitlenme ve şifre sıfırlama kişinin etkin üye olduğu tüm firmalara yazılır; davet kabulü daveti kabul edilen firmalara. Kayıtlı olmayan e-postayla deneme hiçbir firmaya yazılmaz, yalnızca uygulama günlüğüne (e-posta olmadan) düşer.
- Modüller kayıt türlerini `IModule.AuditedEntities` ile tanımlar (alan etiketleri, seçenekler, kullanıcı ve bağlantı alanları, görüntüleme izni).

### Firma ayarları (Y6)

**Yönetim → Firma ayarları** (`/yonetim/ayarlar`): firma profili (görünen ad, ticari unvan, vergi dairesi ve no, adres, telefon, e-posta) ve bölgesel ayarlar (saat dilimi, para birimi, tarih ve sayı biçimi). Her bölüm ayrı kaydedilir.

- Ayarlar kodda tiplidir: modüller `IModule.Settings` ile tanımlar (anahtar `modül.grup.ad`, tip, varsayılan, uzunluk, seçenekler, biçim). Tanımsız anahtar yazılamaz, değer tanıma uymalıdır; modülü kapalı ayar görünmez ve yazılamaz. Kaydedilmemiş ayarın değeri varsayılandır (yeni firma hiçbir şey kaydetmeden çalışır).
- `GET /api/settings`: her üye okur. `PUT /api/settings` (`core.settings.manage`): gövde `{ "values": { "core.regional.currency": { "value": "EUR", "version": 1 } } }`. Hepsi birlikte kaydedilir ya da hiçbiri: tanımsız anahtar 400, geçersiz değer 422 (alan adı ayar anahtarıdır), okunduktan sonra değişmiş sürüm 409.
- Her değişen ayar denetim kaydına "Firma ayarı" olarak, ayarın adıyla ve eski → yeni değerle yazılır; değişmeyen değer yazılmaz. Genel kayıt uç noktası (`/api/records/core.tenant-setting`) da aynı kurallara uyar.
- **Ayarlar ekranlarda kullanılır.** Tarih ve saatler firmanın saat diliminde, seçili tarih/sayı biçiminde gösterilir (Türkçe `24.10.2026 14:30` · `1.234,50`, İngilizce GB `24/10/2026` · `1,234.50`, İngilizce ABD `10/24/2026 2:30 PM`). Denetim kaydının tarih filtresi gün sınırlarını, CSV de saatleri firmanın saat dilimine göre alır. Yeni fırsatın para birimi varsayılan para birimidir (mevcut kayıtlar değişmez). Görünen ad üst çubukta ve firma seçiminde kayıtlı adın yerine gösterilir.
- **Logo:** `PUT /api/settings/logo` (`core.settings.manage`) gövde olarak dosyanın kendisini alır; yalnızca PNG veya JPEG, en fazla 256 KB. Tür uzantıdan değil içerikten anlaşılır; SVG kabul edilmez (betik taşıyabilir). `GET /api/settings/logo` her üyeye dosyayı döner (`nosniff`, önbellek sürüme bağlı), `DELETE` kaldırır. Logo veritabanında `core.TenantLogos` tablosunda (RLS) durur, değişikliği denetim kaydına "Firma · Logo" olarak yazılır ve üst çubukta görünür.

### Hesap güvenliği (Y7a)

**Hesabım** (`/hesabim`, üst çubukta kişinin adı): ad, e-posta, şifre ve açık oturumlar. Bilgiler kişiye aittir; birden fazla firmada çalışan kişinin değişikliği hepsinde geçerlidir ve her firmanın denetim kaydına "Hesap" olarak yazılır.

- `GET /api/account`, `PUT /api/account/profile` (ad), `POST /api/account/password` (mevcut ve yeni şifre; bu oturum açık kalır, diğerleri kapanır; hatalı mevcut şifre girişteki gibi sayılır ve hesabı kilitler).
- `POST /api/account/email` (yeni adres ve mevcut şifre): yeni adrese 24 saat geçerli onay bağlantısı (`/eposta-onayla`) gider. `POST /api/auth/confirm-email-change` e-postayı ve giriş adını değiştirir, tüm oturumları kapatır, eski adrese bilgi verir. Adres başka hesapta kullanılıyorsa bu yalnızca bağlantıda söylenir.
- **Şifremi unuttum** (`/sifremi-unuttum`, `POST /api/auth/forgot-password`): hesap olsun olmasın 202 döner. Aynı hesaba 15 dakikada en fazla bir bağlantı gider; bir IP adresinden saatte 10 istek kabul edilir, fazlası 429 (`Account:ForgotPasswordPerIpPerHour`, `Account:ForgotPasswordInterval`). Daveti bekleyen kişiye gitmez.
- **Oturumlar** sunucuda da tutulur (`platform.UserSessions`): çerez oturum kimliğini taşır, her istekte oturumun kapatılmadığı denetlenir (son etkinlik en fazla 5 dakikada bir yazılır). `GET /api/account/sessions`, `DELETE /api/account/sessions/{id}`, `POST /api/account/sessions/close-others`. Çıkış oturumu kapatır; 30 günden eski kayıtlar girişte silinir.
- Yönetici (`core.users.manage`) `POST /api/users/{üyelik}/close-sessions` ile kişinin tüm oturumlarını kapatır (diğer firmalardaki de kapanır; Yönetici rolündekini yalnızca yönetici kapatabilir). Devre dışı bırakmak oturumları kapatmaz: o firmaya erişim zaten hemen kesilir.

### İki adımlı doğrulama (Y7b)

Doğrulama uygulamasıyla (TOTP: Google/Microsoft Authenticator vb.) çalışır; hesaba aittir, kişinin bütün firmalarında geçerlidir. "Bu cihazı hatırla" yoktur.

- **Kurulum** Hesabım → İki adımlı doğrulama: mevcut şifre → QR kod ve anahtar (`POST /api/account/two-factor/setup`) → uygulamadaki kod (`/enable`) → 10 kurtarma kodu bir kez gösterilir (indirilebilir). `GET /api/account/two-factor` durumu ve kalan kod sayısını verir; `/recovery-codes` (şifre) kodları yeniler, `/disable` (şifre ve kod) kapatır.
- **Giriş**: şifre doğruysa ve doğrulama açıksa `POST /api/auth/login` 202 döner; ikinci adım `POST /api/auth/login/two-factor` (6 haneli kod ya da kurtarma kodu). Hatalı kodlar hatalı şifre gibi sayılır ve hesabı kilitler (423). Kurtarma koduyla girişte kişiye e-posta gider ve Hesabım'a yönlendirilir.
- **Firma zorunluluğu**: Ayarlar → Güvenlik → "İki adımlı doğrulama zorunlu" (`core.security.require-two-factor`). Açıkken doğrulaması kapalı üye firmanın verisine erişemez (403 `two-factor-required`), ekranlar Hesabım'a yönlendirir; kurunca devam eder. Ayarı açan kişinin kendi doğrulaması açık olmalıdır; zorunlu firmada üye doğrulamasını kapatamaz. Kullanıcı listesinde "İki adımlı" sütunu ve süzgeci vardır.
- **Kayıp telefon ve kodlar**: yalnızca platform yöneticisi sıfırlar (Platform sayfası, `POST /api/platform/users/reset-two-factor`): doğrulama kapanır, kişinin tüm oturumları kapatılır.
- Denetim kaydına "Hesap" olarak açma, kapatma, kod yenileme, kurtarma koduyla giriş ve sıfırlama yazılır (sıfırlamada işlemi yapan platform yöneticisidir).

### Ekipler ve kayıt kapsamı (Y8a)

Roller "hangi tür kaydı", kayıt kapsamı "hangi kayıtları" belirler. Kapsam rol başınadır: **Tüm kayıtlar**, **Ekibinin kayıtları** (kendisinin ve ekip arkadaşlarının sorumlu olduğu kayıtlar) ya da **Yalnızca kendi kayıtları**. Kişinin birden fazla rolü varsa en geniş kapsam geçerlidir; Yönetici her zaman tüm kayıtları görür. Mevcut roller "Tüm kayıtlar" ile başlar; "Satış temsilcisi" şablonu "Yalnızca kendi kayıtları" önerir.

- Kapsam yalnızca sorumlu alanı olan kayıt türlerine uygulanır (`IRecordStore.OwnerField`; CRM'de firma, fırsat, aktivite). Boru hattı (`IDataScopeService`) listede kapsamı sorguya koyar (`RecordQuery.Owners`), böylece sayfalama ve toplam doğru kalır; kapsam dışındaki tek kayıt 404 döner. Sorumlu kapsam dışına atanamaz (422, `ownerId`). Sorumlusu boş kayıt yalnızca "Tüm kayıtlar"da görünür.
- **Ekipler** (Yönetim → Ekipler, `core.users.manage`): `/api/teams` (liste, ekle, düzenle, sil), `PUT /api/teams/{id}/members`, kullanıcı ayrıntısında `GET/PUT /api/users/{üyelik}/teams`. Bir kişi birden fazla ekipte olabilir; ekip silinince yalnızca ekip ilişkisi kalkar. Değişiklikler denetim kaydına "Ekip" olarak yazılır.
- `GET /api/tenant/members?assignable=true`: kişinin sorumlu atayabileceği üyeler. `GET /api/tenant/access` ve kullanıcının "Erişim" görünümü kapsamı (ve ekipleri) gösterir.
- Yönetici olmayan rol yöneticisi kendi kapsamından geniş kapsam veremez (403).

### Hassas alanlar (Y8b)

Bazı alanlar kaydı görebilen herkese açık değildir. Modül hassas alanlarını ve onları açan izni `IModule.SensitiveFields` ile tanımlar; CRM'de fırsatın **tutarı** ve **olasılığı** `crm.opportunity-amounts.read` ("Fırsat tutarları" satırı) ister. Para birimi hassas değildir. Bu izin "Satış temsilcisi" ve "Salt okur" şablonlarında vardır; migration bugün fırsatları gören her role izni ekler, kimse görünürlük kaybetmez.

- İzni olmayan kişiye alan hiç gönderilmez (tek kayıt, liste, `/api/records`). Boru hattı (`IFieldAccessService`) alanı çıkarır; Yönetici her şeyi görür.
- Yazma: gizli alana değer vermek 403'tür; boş gönderilen gizli alan yok sayılır ve kayıttaki değer korunur (ekran onu boş gönderir). Gizli alana göre süzmek 422'dir.
- Denetim kaydında ve kayıt geçmişinde alan satırı görünür ama değeri "Gizli" yazar (`AuditFieldChangeDto.Hidden`).
- Ekranlar: izni olmayan için fırsat listesinde Tutar ve Olasılık sütunları, huni kartlarındaki tutar, toplamlar ve düzenleyicideki iki alan gösterilmez.

### Özel alanlar (Y9a)

Firma kendi alanlarını ekleyebilir (ör. fırsatta "Araç tipi"). Tanım ekranı Yönetim → Özel alanlar'dır (`/yonetim/ozel-alanlar`) ve `core.customization.manage` ("Özelleştirme") ister. Modül hangi kayıt türlerinin özelleştirilebileceğini `IModule.CustomizableEntities` ile söyler; CRM'de firma, kişi, fırsat ve aktivite.

- Tipler: kısa metin, uzun metin, sayı, tarih, evet/hayır, seçim listesi, çoklu seçim. Kayıt türü başına en fazla 30 alan. Anahtar etiketten bir kez üretilir ve değişmez; etiket sonradan değiştirilebilir.
- Tanımlar `meta.CustomFields` tablosundadır (RLS'li). Değerler kaydın kendi tablosunda `CustomFields` JSON sütununda durur; kayıt verisinde `custom` nesnesi olarak gelir ve gider.
- Boru hattı değerleri tipine göre doğrular ve normalleştirir; bilinmeyen alan ve yanlış tip 422'dir. Zorunlu alan eklerken doldurulmalıdır; güncellemede yalnızca boş gönderilirse hata verir (huni sürüklemesi engellenmez). Güncellemede `custom` gönderilmezse değerler korunur.
- Alan silinmez, arşivlenir: değerleri kayıtta kalır, geri alındığında yeniden görünür. Seçenekler de pasifleştirilir; pasif seçenek yalnızca zaten seçili olan kayıtta kalabilir.
- Seçim listesi alanına göre süzme: `custom.<anahtar>=<seçenek>` (`/api/records`).
- Denetim kaydı alan tanımlarını "Özel alan" olarak, kayıt değişikliklerini alanın etiketiyle yazar.
- `GET /api/custom-fields` her üyeye açıktır (ekranlar formları buradan kurar). Ekranlarda alanlar formun sonunda "Ek bilgiler" bölümünde çıkar; "Listede göster" seçilenler listede sütun olur; firma ayrıntısı dolu değerleri gösterir.

### Düzenlenebilir listeler (Y9b)

Fırsat aşaması, firma durumu, firma kaynağı ve aktivite tipi artık firmanın düzenleyebildiği listelerdir. Ekran Yönetim → Listeler'dir (`/yonetim/listeler`, `core.customization.manage`). Değerin adı ve sırası değiştirilir, değer pasife alınır, yeni değer eklenir. Satış hunisinin sütunları aşama sırasıyla çizilir.

- Modül listelerini `IModule.ChoiceLists` ile tanımlar. Hazır değerlerin anahtarı eski enum adıdır (ör. `Proposal`), bu yüzden API ve kayıtlar değişmez. Migration sayı kolonlarını bu metin anahtarlara çevirir.
- Kod değere değil kategoriye bakar. Fırsat aşamasında Açık / Kazanıldı / Kaybedildi, firma durumunda Potansiyel / Müşteri / Pasif vardır. `open=true` süzgeci ve devir, kategorisi Kazanıldı ya da Kaybedildi olmayan aşamaları açık sayar. Yeni değer bir kategori seçer. Hazır değerin kategorisi değişmez. Her kategoride en az bir etkin değer kalmalıdır.
- Değer silinmez: listeden çıkarılan değer pasif kalır. Pasif değer yeni kayıtta seçilemez ama onu taşıyan kayıt aynen kaydedilebilir. Listede olmayan değer 422 döner.
- Değer verilmezse (ya da `null` veya boş gönderilirse) yeni kayıtta listenin varsayılanı atanır: tanımın varsayılanı etkinse odur, değilse ilk etkin değerdir. Güncellemede verilmeyen değer değişmez. Tipli CRM istekleri de (`status`, `stage`, `type`) bir hazır değeri varsaymaz; böylece ör. "Görev" pasife alınınca tip göndermeyen istek hata almaz.
- Tabloya (`meta.ChoiceLists`, RLS'li) yalnızca firmanın düzenlediği liste yazılır. Düzenlenmemiş listede koddaki varsayılan geçerlidir. Değişiklikler denetim kaydına "Liste" olarak yazılır. Kayıt geçmişi değerleri firmanın adlarıyla gösterir.
- `GET /api/choice-lists` her üyeye açıktır. Düzenleme `GET /api/customization/lists` ve `PUT /api/customization/lists/{anahtar}` üzerinden yapılır; gövdede liste tamamıyla, sırasıyla verilir.

### Sektör profilleri (Y9c)

Profil, bir sektör için hazır başlangıç paketidir: modüller, özel alanlar ve liste değerleri. Dört profil kodda tanımlıdır (`Modulix.Api/Customization/SectorProfiles.cs`): Genel, Lojistik firması, Klinik, Üretici. Tanımlar açılışta modül kataloğuna göre doğrulanır; bilinmeyen modül, kayıt türü, liste veya kategori varsa uygulama açılmaz.

- Profil yalnızca ekler. Aynı adlı alan (arşivdekiler dahil) ve aynı adlı liste değeri (büyük/küçük harf fark etmez) atlanır; firmanın mevcut alanları ve değerleri değişmez. Yeni liste değeri kendi kategorisinin son değerinin arkasına eklenir. Alanlar ve değerler Y9a/Y9b servisleriyle eklenir, yani aynı kurallar ve denetim kayıtları geçerlidir. Hepsi tek işlemdir.
- Profil yeniden uygulanabilir; o gün eksik olanlar eklenir. Profil tanımı sonradan değişirse firmalara kendiliğinden yansımaz.
- Firmanın profili `platform.Tenants.ProfileKey` (ve uygulama zamanı) olarak tutulur; uygulama firmanın denetim kaydına `core.tenant` "Sektör profili" olarak yazılır.
- Platform yöneticisi firmayı açarken profil seçer (`CreateTenantRequest.Profile`); ekran profilin modüllerini önceden işaretler, değiştirilebilir. Firma detayından mevcut firmaya önizlemeyle uygular; bu yolda profilin kapalı modülleri de açılır (`GET /api/platform/profiles`, `GET`/`POST /api/platform/tenants/{id}/profile/{anahtar}`).
- Firma yöneticisi Yönetim → Sektör profili'nden uygular (`/yonetim/sektor-profili`, `core.customization.manage`; `GET /api/customization/profiles`, `GET /{anahtar}/preview`, `POST /{anahtar}/apply`). Modül açamaz: kapalı modülün alanları ve değerleri eklenmez, önizlemede ayrıca belirtilir.
- Menü sırası profilden gelmez: şimdilik yalnızca CRM'in ekranları var.

### Ortak firma ve kişi kaydı (Ç3a)

Firma (taraf) ve kişi çekirdekte tek kayıttır ([ADR-0006](docs/adr/0006-ortak-firma-ve-kisi.md)). Modüller kendi bilgilerini bu kayda bağlı uzantılarda tutar: CRM firması ve CRM kişisi bu kayıtların CRM uzantısıdır. Lojistik ve sonraki modüller yeni firma tablosu açmaz, aynı kaydı kullanır.

- Kayıtlar `core.Parties` ve `core.People` tablolarında durur; ikisi de RLS'lidir. Genel uç: `/api/records/core.party` ve `/api/records/core.person`.
  - Taraf alanları: `kind` (`Organization` / `Individual`), `name`, `taxOffice`, `taxNumber`, `phone`, `email`, `website`, `city`, `address`. Filtre: `taxNumber`.
  - Kişi alanları: `firstName`, `lastName`, `email`, `phone`, `title`, `partyId` (çalıştığı taraf). Filtreler: `partyId`, `email`.
- **CRM API'si değişmedi:** `crm.company` ad, vergi no, telefon, e-posta, web sitesi, şehir ve adresi aynı adlarla alır ve döndürür; bu alanlar tarafa yazılır. Yanıtta `partyId` da gelir. CRM kişisinin ad, soyad, unvan, e-posta ve telefonu ortak kişidedir (`personId`). Durum, kaynak, sektör, etiketler, sorumlu, notlar ve özel alanlar CRM'de kalır.
- **Vergi numarası kesin eşleşmedir:** vergi numarası tenant içinde tekildir. Yeni CRM firması, aynı vergi numaralı bir tarafla açılırsa yeni taraf oluşmaz, firma o tarafa bağlanır. Boş gönderilen taraf alanları taraftaki değeri silmez. Tarafın zaten bir CRM firması varsa 422 döner (`taxNumber`). Başka bir tarafın vergi numarasını vermek de 422'dir.
- **Mevcut kayda bağlama:** genel uçta oluştururken `partyId` (firma) veya `personId` (kişi) verilebilir. Bağlantı sonradan değiştirilemez (422).
- **Çalıştığı firma:** CRM kişisinin firması verildiğinde ya da değiştiğinde, ortak kişinin `partyId`'si o firmanın tarafına eşitlenir.
- **Yetki:** modül, ortak kaydı hangi izinleriyle okuyup yazdığını `IModule.SharedRecordAccess` ile bildirir. CRM'de taraf için `crm.companies.read` / `.write`, kişi için `crm.contacts.read` / `.write` gerekir. Bu izinlerden birine sahip olan, açık modüllerden birinde ortak kaydı okur ya da yazar. Bildirmeyen modülün izinleri ortak kayda erişim vermez. Kayıt kapsamı (Y8a) uzantıya uygulanır, ortak kayda uygulanmaz.
- **Silme:** ortak kayda başvuran alanlar `IModule.SharedRecordReferences` ile bildirilir. Kullanılan taraf ya da kişi silinemez; 409 yanıtı nerede kullanıldığını söyler (ör. "1 CRM firması, 2 kişi"). CRM firması silinince taraf kalır.
- **Sürüm:** CRM firmasının ve kişisinin sürümü, kendi sürümüyle ortak kaydın sürümünden oluşur. Başka bir modül ortak kaydı değiştirdiyse eski sürümle gelen CRM güncellemesi 409 alır.
- **Denetim:** değişiklik, hangi kayıt üzerinden yapıldıysa onun denetim kaydına yazılır. CRM formundan değişen ünvan `crm.company`, ortak kayıt üzerinden değişen ünvan `core.party` kaydında görünür.
- `SharedRecords` migration'ı mevcut firmaları taraflara, kişileri ortak kişilere taşır; kimlikler aynı kalır.
- Henüz yok: kullanıcının (üyeliğin) kişiye bağlanması, benzer kayıt uyarısı ve birleştirme aracı.

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
| Firmalar | `status` (Prospect, Customer, Inactive), `ownerId`, `partyId` |
| Kişiler | `companyId`, `personId` |
| Fırsatlar | `companyId`, `contactId`, `stage`, `open=true` (kazanılmamış ve kaybedilmemiş), `ownerId` |
| Aktiviteler | `companyId`, `contactId`, `opportunityId`, `open=true`, `dueBefore=<ISO zaman>`, `ownerId` |

- Firma, fırsat ve aktivitenin bir sorumlusu vardır; oluştururken boş bırakılırsa kaydı oluşturan kullanıcı atanır. Sorumlu, kuruluşun etkin üyesi olmalıdır.

- Tüm işlemler kayıt boru hattından geçer: modül tenant için kapalıysa veya izin yoksa 403, başka tenant'ın kaydı 404, başka tenant'ın kaydına bağlanmak 422 döner. Bağlı kaydı olan firma silinemez (409). Audit ve outbox kaydı otomatik yazılır.
- **Sürüm zorunludur (B1):** `/api/crm/...` ve `/api/records/...` üzerinde güncelleme (`version` gövdede) ve silme (`?version=`) okunan sürümü ister; sürümsüz istek 428 (`code: VersionRequired`) alır, değişmiş sürüm 409 kalır. Zorunluluk yalnızca bu HTTP uç noktalarındadır; kayıt devri ve olay aboneleri gibi arka plan işleri boru hattını doğrudan çağırır, etkilenmez.
- Aynı kayıtlara genel uçtan da ulaşılır (`/api/records/crm.company` vb.); aynı izinler geçerlidir.
- Arayüz API ile aynı adresten sunulur: `https://localhost:7026` → giriş, kuruluş seçimi, Firmalar / Kişiler / Fırsatlar / Aktiviteler. Telefonda listeler kart görünümüne döner.
- Firma adına tıklayınca firma detay sayfası açılır: kişiler, fırsatlar ve aktiviteler tek ekranda; aktivite ve fırsat oradan eklenir. Fırsatlar sayfası varsayılan olarak satış hunisini (aşama sütunları, sürükle bırak) gösterir. Aktiviteler sayfası "Bugün" görünümüyle açılır (bugüne kadar yapılması gereken açık işlerim).

**Yeni bir sektör modülü eklemek** (CRM'i örnek alın):

1. `Domain`: entity'ler `ITenantEntity` uygular. `Persistence`: `IEntityTypeConfiguration` sınıfları, şema = modül anahtarı (aksi halde uygulama açılmaz).
2. `Records`: her entity için bir kayıt deposu (`CrmRecordStore` kalıbı); `IModule.ConfigureServices` içinde kaydedilir.
3. `Program.cs` → `ModulixModules.Create()` listesine modülü ekleyin, uç noktaları `MapCrmEndpoints` gibi eşleyin.
4. Migration üretin ve her tablo için `TenantIsolationSql.AddTable` ekleyin; eklenmezse `Every_tenant_table_is_protected_by_row_level_security` testi kırılır.
5. Müşteri, tedarikçi, taşıyıcı ya da kişi tutan modül yeni firma veya kişi tablosu açmaz. Ortak kayda (`Party`, `Person`) bağlanan bir uzantı entity'si yazar, `SharedRecordAccess` ve `SharedRecordReferences` bildirir (CRM'deki `Company` / `CompanyRecordStore` kalıbı, ADR-0006).

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

## Olaylar (Ç1)

Modüller birbirinin değişikliğine ve bildirimlere olaylarla tepki verir ([ADR-0007](docs/adr/0007-olay-altyapisi.md)). Bir modül başka modülün tablosuna yazmaz; olaya abone olur ve tepkisini kendi kayıtlarında, kayıt boru hattından gösterir.

- **Outbox:** kayıt boru hattı her oluşturma, güncelleme ve silmede `platform.OutboxMessages`'a bir olay yazar; kayıt, denetim kaydı ve olay aynı işlemdedir (işlem geri alınırsa olay da yoktur). Olay türleri `RecordCreated`, `RecordUpdated`, `RecordDeleted` (`DomainEventTypes`, sürüm 1). Zarf (`EventEnvelope`, `Modulix.Core.Contracts.Events`): olay kimliği, tenant, tür ve sürüm, kayıt türü ve kimliği, sıra numarası (kaydın yeni sürümü; silmede son sürüm + 1), işlemi yapan kişi ve kaynak (denetim kaydıyla aynı), correlation (isteğin izi), causation ve derinlik (abonenin değişikliğinden doğan olayda), neden (ör. `transfer`), şirket kapsamlı kaydın işletici şirketi `CompanyId` (ortak kayıtta boş, [ADR-0008](docs/adr/0008-sirket-ve-sube-kapsami.md)) ve değişen alanların `{ alan: { old, new } }` farkı.
- **Abonelik:** modül `IModule.EventSubscriptions` ile bildirir, abone sınıfını `ConfigureServices`'te scoped kaydeder:
  ```csharp
  EventSubscription.For<PartyRenamedSubscriber>(
      "crm.party-renamed", [DomainEventTypes.RecordUpdated], [SharedEntities.Party], [CrmModule.Permissions.CompaniesWrite])
  ```
  Anahtar modül anahtarıyla başlar ve değişmez; kayıt türleri boşsa bütün türler; izinler modülün kendi izinleridir. Katalog açılışta denetler (bilinmeyen olay veya kayıt türü, başka modülün izni, olayın güncel sürümünü okuyamayan abone uygulamayı açmaz). Abone `IEventSubscriber.HandleAsync` uygular, yalnızca Contracts servislerini kullanır ve kayıtları `IRecordPipeline`'dan değiştirir (`IModuleData`/`DbContext` kullanmaz; mimari test denetler).
- **Abonenin kimliği:** abone olayı tetikleyen kişi adına değil "sistem" adına çalışır: olayın tenant'ının bağlamında (RLS), yalnızca aboneliğin bildirdiği izinlerle, tenant geneli kayıt kapsamıyla. Aboneliğin modülü firmada kapalıysa teslim yapılmaz. Hassas alanlar (Y8b) aboneliğin izniyle görülemiyorsa farktan çıkarılır, adları `HiddenFields`'ta verilir; outbox tam farkı saklar.
- **Teslim:** işleyici her 5 saniyede dağıtılmamış olayları yazılış sırasıyla eşleşen aboneliklere `platform.EventDeliveries` satırı olarak dağıtır (dağıtım uygulama kilidiyle tek örnekte), sonra teslimleri kiralayıp aboneyi çalıştırır. Abone açık bir veritabanı işleminde çalışır: yaptığı değişiklik, kuyruğa yazdığı e-posta ve teslimin "başarılı" işareti birlikte yazılır ya da hiçbiri. Teslim en az bir kezdir; abone idempotent yazılır (olay kimliği). Kira 2 dakikadır, abone çalışırken 30 saniyede bir uzatılır; aboneye 5 dakika verilir. Birden fazla uygulama örneği çalışabilir; kirası dolan örneğin geç sonucu yazılmaz.
- **Sıra:** aynı kaydın olayları bir aboneye sıra numarasıyla teslim edilir; önceki teslim başarılı veya atlanmış olmadan sonraki alınmaz. Sırası önemsiz abone `Ordered = false` der. Bir turda tek firmadan en fazla 20 teslim alınır; bir firmanın birikmesi veya hatası diğerlerini bekletmez.
- **Hata:** abone istisna fırlatırsa yalnızca o teslim 1 dk, 5 dk, 15 dk, 1 sa, 4 sa arayla (±%20) yeniden denenir; 6. denemeden sonra veya `PermanentEventFailureException` ile `Failed` olur. Başarısız sıralı teslim aynı kaydın sonraki teslimlerini bekletir. Firma kapalıysa, aboneliğin modülü kapandıysa veya abonelik koddan kaldırıldıysa teslim atlanır (`Skipped`). Abonenin değişikliği yeni olay üretir; derinliği 5'i aşan olay yazılır ama dağıtılmaz (uyarı loglanır). Loglara ve `LastError`'a olay kimliği, tenant, kayıt türü ve kimliği yazılır; payload ve alan değeri yazılmaz.
- **Platform ekranı** (`/platform/olaylar`, yalnızca platform yöneticisi): bekleyen, bekletilen ve başarısız teslimler firma, abonelik, olay türü, durum ve tarihe göre süzülür; satırda olay türü, kayıt türü ve kimliği, abonelik, deneme sayısı, son hata ve beklettiği teslim sayısı görünür, payload gösterilmez. **Yeniden dene** (deneme sayacı sıfırlanır, hemen zamanlanır) ve **Atla**, tek tek veya süzülmüş liste için (en fazla 500); çalışmakta veya bitmiş teslim değişmez. İşlem teslimin firmasının denetim kaydına `core.event-delivery` olarak yazılır. API: `GET /api/platform/events/summary`, `GET /api/platform/events/deliveries?tenantId=&subscription=&eventType=&state=Failed&from=&to=&page=`, `POST /api/platform/events/deliveries/retry` ve `/skip` gövde `{ "ids": [...] }` veya `{ "filter": { ... } }`.
- **Gözlemleme:** `Modulix.Events` ölçümleri: `modulix.events.undispatched.age` ve `modulix.events.deliveries.pending.age` (saniye), `modulix.events.deliveries.failed`, `modulix.events.deliveries.completed` (abonelik ve sonuç etiketiyle). `/health` dağıtılmamış olay 15 dakikadan eskiyse `Degraded` döner.
- **Saklama:** başarılı ve atlanmış teslimler ile bütün teslimleri bitmiş olaylar 7 gün, başarısız teslimler ve olayları 30 gün sonra saatlik temizlikte silinir; dağıtılmamış veya bekleyen olay silinmez. Kalıcı geçmiş denetim kaydıdır.
- **Atama e-postası** (`core.assignment-email`, çekirdek): firma, fırsat veya aktivite (modüllerin `OwnedRecords` türleri) başkasına atanınca yeni sorumluya "Size bir kayıt atandı" e-postası gider: kayıt türü, kaydın adı, atayan kişi ve kayda bağlantı (`App:PublicUrl` ayarlıysa; adres `OwnedRecordType.Path`). Kendine atama, sorumluyu boşaltma ve kayıt devri e-posta göndermez. Alıcı etkin üye değilse veya kaydı kendi yetkisiyle göremiyorsa e-posta yazılmaz. E-posta teslimle aynı işlemde yazıldığı için olay tekrar teslim edilse de ikinci e-posta oluşmaz. **Kayıt devri** (Y2b) olaylara `transfer` nedeni yazar ve yeni sorumluya tek özet e-postası gönderir ("3 firma, 1 açık fırsat").
- **Ayarlar** (`Events` bölümü): `WorkerEnabled` (varsayılan `true`; testler ve tek seferlik komutlar kapatır), `PollIntervalSeconds` (5), `BatchSize` (tur başına teslim, 100). İşleyici şimdilik API süreci içinde çalışır.
- `OlayZarfi` migration'ı o ana kadar birikmiş olayları dağıtılmış işaretler; geçmiş atamalar için e-posta gitmez.

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

# API'yi çalıştır; sağlık kontrolü: /health ("Healthy" dönmeli; olaylar 15 dakikadan uzun bekliyorsa "Degraded")
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
