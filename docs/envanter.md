# D0 envanteri: kanıtlanan ve eksik (Ç0 + Ç23)

- **Tarih:** 25 Ekim 2026, `main` = PR #32 sonrası.
- **Amaç:** Yol haritasındaki "beyan edilen mevcut işler"i (bölüm 3) koddaki ve testteki kanıtla eşlemek; RLS'i ve kayıt boru hattını atlayan yolları bulmak; bulguları kalıcı testlere (Ç23 kapıları) bağlamak.
- **Yöntem:** Her beyan için kodu ve onu kanıtlayan testi bulduk. Kanıt yalnızca gerçek SQL Server'da çalışan entegrasyon testi veya mimari testtir; in-memory test RLS kanıtı sayılmaz (Ç23.1). Durumlar:
  - **Kanıtlandı:** test, kabul ölçütünü doğrudan sınıyor.
  - **Kısmi:** davranış var ama ölçütün bir kısmı testsiz veya eksik.
  - **Yok:** kodda yok.

## 1. Beyan edilen işler

| Beyan | Durum | Kanıt (testler) | Not |
| --- | --- | --- | --- |
| SQL Server, iki kullanıcı (uygulama / migration) | Kanıtlandı | `DatabaseSetupTests` | Uygulama kullanıcısı şema değiştiremez, `db_owner` değil |
| RLS, tenant izolasyonu | Kanıtlandı | `TenantIsolationTests` (11), `TableInventoryTests` (yeni) | Ham SQL ile başka tenant'a ekleme/taşıma, bağlantı içinde tenant değiştirme, politikayı kapatma reddedilir |
| Giriş, aktif tenant seçimi, her istekte doğrulama | Kanıtlandı | `AuthAndTenantTests` (11) | Üyelik kapanınca veya firma kapanınca bir sonraki istekte 403 |
| Modül kataloğu ve hakları | Kanıtlandı | `ModuleCatalogTests` (21), `ModulesAndRolesTests` (9), `PlatformAdministrationTests` | Kapalı modülün izni rolde olsa da geçersiz |
| İzinler, roller (Y1, Y3) | Kanıtlandı | `RoleManagementTests`, `AdministrationTests`, `RoleTests` | Son yönetici kuralı test edildi |
| Kayıt boru hattı | Kısmi | `RecordPipelineTests` (Core 23, entegrasyon 10), `CrmTests` | Sıra ve "ya hepsi ya hiçbiri" kanıtlı. İş komutu (Ç0.4) ve zorunlu sürüm (Ç0.5) yok, bkz. B1, B2 |
| Y2 kullanıcı ve kayıt devri | Kanıtlandı | `UserManagementTests` (12), `RecordHandoverTests` | |
| Y4 platform yönetimi | Kanıtlandı | `PlatformAdministrationTests`, `EndpointProtectionTests` (yeni) | Platform uçları firma yöneticisine 403 |
| Y5 denetim kaydı | Kanıtlandı | `AuditLogTests`, `RecordHistoryAndSignInTests`, `RecordPipelineTests` | Uygulama kullanıcısı denetim kaydını değiştiremez ve silemez |
| Y6 firma ayarları | Kanıtlandı | `SettingsTests` (9) | Ayar yazımı kayıt boru hattından geçer |
| Y7 hesap güvenliği, iki adımlı doğrulama | Kanıtlandı | `AccountSecurityTests`, `TwoFactorTests` | |
| Y8 ekip, kayıt kapsamı, hassas alanlar | Kanıtlandı | `DataScopeTests`, `SensitiveFieldTests`, `EventTests` | Hassas alan olay zarfında da gizli |
| Y9 özel alan, liste, sektör profili | Kanıtlandı | `CustomFieldTests`, `ChoiceListTests`, `SectorProfileTests` ve Core kural testleri | |
| E-posta kuyruğu | Kanıtlandı | `EmailTests` (9) | Eşzamanlı işleyiciler, yeniden deneme, gerçek SMTP (Mailpit) |
| CRM firma, kişi, fırsat, aktivite | Kanıtlandı | `CrmTests` (11), `CrmRecordStoreValidationTests` (9) | |
| Ç3a ortak firma ve kişi, ilk adım | Kanıtlandı | `SharedRecordTests` (5) | Ç3a.2, Ç3a.5, Ç3a.6 kalanları yok |
| Ç1 olay altyapısı | Kanıtlandı | `EventTests` (20), `EventSubscriberRulesTests` | ADR-0007 kabul testlerinin hepsi |
| Lojistik | Yok | yalnızca `LogisticsModule` (izin tanımları) | Entity, depo, uç nokta yok; test projesi boş |
| AI | Yok | proje boş | |
| Uçtan uca test (E2E) | Yok | `Modulix.E2ETests` boş | Ç23.1'in kritik akış katmanı |

## 2. Tablo sınıflandırması (Ç23.2)

Veritabanındaki 27 tablonun hepsi sınıflandırıldı. `TableInventoryTests` yeni tablo geldiğinde sınıfı sorar:

- **Platform şeması dışındaki tablolar (15):** hepsi tenant verisidir ve RLS altındadır (okuma filtresi, ekleme ve güncelleme engeli). Şemaları `core`, `meta` ve `crm`.
- **Platform şemasındaki tablolar (12):** RLS dışındadır, her biri gerekçesiyle listelidir.
  - `TenantId` taşıyanlar: `Memberships`, `ModuleEntitlements`, `OutboxMessages`, `EventDeliveries`, `EmailMessages`. Bunlar tenant bağlamı kurulmadan okunmak zorundadır: giriş ve firma listesi, platform yöneticisinin hak yönetimi, tüm tenant'ları tarayan işleyiciler.
  - Kimlik ve oturum tabloları kişiye aittir, firmaya değil.
- **Model:** `ITenantEntity` ile şema uyumlu. Modelde migration'ı olmayan değişiklik yok.

## 3. Domain haritası, mevcut varlıklar (Ç0.1)

| Varlık | Tablo | Sahip | Kapsam | Yazma yolu | Denetim |
| --- | --- | --- | --- | --- | --- |
| Firma (taraf), kişi | `core.Parties`, `core.People` | Çekirdek (ADR-0006) | Tenant | Kayıt boru hattı (`PartyRecordStore`, `PersonRecordStore`) ve CRM uzantısı | Boru hattı |
| CRM firma, kişi, fırsat, aktivite | `crm.*` | CRM | Tenant, sorumlu kapsamı (Y8) | Kayıt boru hattı (CRM kayıt depoları) | Boru hattı |
| Firma ayarı | `core.TenantSettings` | Çekirdek | Tenant | Kayıt boru hattı (`TenantSettingRecordStore`) | Boru hattı |
| Logo | `core.TenantLogos` | Çekirdek | Tenant | `TenantLogoStore` | Kendisi yazar |
| Rol, rol ataması | `core.Roles`, `core.MembershipRoles` | Çekirdek | Tenant | `RoleService` | Kendisi yazar |
| Ekip | `core.Teams`, `core.TeamMembers` | Çekirdek | Tenant | `TeamService` | Kendisi yazar |
| Özel alan, liste | `meta.CustomFields`, `meta.ChoiceLists` | Çekirdek | Tenant | `CustomFieldService`, `ChoiceListService`, `SectorProfileService` | Kendisi yazar |
| Denetim kaydı | `core.AuditLog` | Çekirdek | Tenant | Yalnızca ekleme | Veritabanı UPDATE ve DELETE'i reddeder |
| Firma, üyelik, modül hakkı | `platform.*` | Platform | Platform | `PlatformAdministration`, `UserAdministration`, `ModuleEntitlements` | Firmanın denetim kaydına (`core.tenant`, `core.membership`) |
| Olay, teslim, e-posta | `platform.*` | Platform | Platform (TenantId taşır) | Boru hattı, işleyiciler, `EventAdministration` | Teslim işlemleri `core.event-delivery` |

Her varlığın tek bir yazma sahibi var. Teklif, sevkiyat, personel ve fatura henüz yok (Ç0.1 kabul ölçütü bu varlıklar gelince yeniden denetlenir).

## 4. Atlama yolları (Ç0.3)

| Yol | Sonuç | Kapı |
| --- | --- | --- |
| Modülün tabloya doğrudan yazması | Yok. CRM `IModuleData`'yı yalnızca kayıt depolarında kullanıyor, hiçbir modül kaydetmiyor | `ModuleDataAccessRulesTests` (yeni) |
| Olay abonesinin doğrudan yazması | Yok | `EventSubscriberRulesTests` |
| API'nin `DbContext`'e doğrudan yazması | Yalnızca geliştirme komutları (`seed`, `test-email`) | — |
| Sorgu filtresini kapatma (`IgnoreQueryFilters`) | Uygulama kodunda yok; RLS ikinci katman olarak yine korur | `TenantIsolationTests` |
| Ham SQL | Üç yer: `AdministratorGuard` (kilit), `EventDispatcher` (uygulama kilidi, teslim seçimi). Tenant tablosuna yazmıyor | — |
| Oturumsuz uç nokta | 7 tane, hepsi giriş veya e-posta bağlantısı | `EndpointProtectionTests` (yeni) |
| Rolü olmayan üyenin erişimi | Yalnızca kendi hesabı ve ekranı kuran 7 okuma ucu (firma adı, izinler, modüller, üye adları, ayarlar, özel alan ve liste tanımları) | `EndpointProtectionTests` (yeni) |
| Platform uçları | Platform yöneticisi değilse hepsi 403 | `EndpointProtectionTests` (yeni) |

**Yönetim kayıtları:** roller, ekipler, özel alanlar, listeler, logo ve üyelikler kayıt boru hattından değil, kendi servislerinden yazılır. Bunlar iş kaydı değil yapılandırmadır: izin, kapsam ve olay adımları onlara uygulanmaz. Her servis yetkiyi kendisi denetler ve denetim kaydını aynı işlemde yazar. Kural: sorumlusu, alanları ve olayı olan her yeni varlık bir `IRecordStore` ile boru hattına girer; yönetim servisi yalnızca yapılandırma içindir.

## 5. Bulgular ve açık işler

- **B1. Sürüm zorunlu değil (Ç0.5):**
  - Ne oluyor: `/api/records` ve `/api/crm` üzerinden güncelleme ve silme `version` olmadan da kabul ediliyor. Güncelleme kısmi olduğu için yalnızca gönderilen alanlar yazılır, ama iki kişi aynı alanı düzenlerse son yazan sessizce ezer. Ç0.5'in kabul ölçütü ("sessiz veri ezme olmaz") bu yolda sağlanmıyor.
  - Arayüz sürümü her zaman gönderiyor.
  - Öneri: API'de sürümü zorunlu kılmak (sürümsüz istek 428 veya 422). Arka plandaki iç çağıranlar (devir, aboneler) boru hattını doğrudan çağırdığı için etkilenmez. API sözleşmesi değişeceği için karar bekliyor.
- **B2. İş komutu yok (Ç0.4):** onay ve kesinleştirme kuralı olan ilk işlemle (teklif onayı) birlikte "komut alanı" gerekecek. Yani genel güncellemenin yazamadığı, yalnızca komutun değiştirdiği alanlar. Bugün bu kurala tabi alan yok; genel `/api/records` ucu her alanı yazabiliyor.
- **B3. Silme politikası (Ç0.6):** CRM kayıtları fiziksel olarak siliniyor. Bağlı kaydı olan firma ve kullanılan ortak kayıt silinemiyor (409, test edildi). Arşiv ve yasal saklama kuralı yok; belge modülleri gelmeden karar gerekmiyor.
- **B4. Şirket ve şube kapsamı yok (Ç22):** tenant tek işletici şirket gibi davranıyor. Yol haritası sırasındaki 2. madde.
- **B5. Lojistik ve AI boş:** "lojistik pilotu" için entity ve uç nokta yok. Bunlar D2 kapsamında.
- **B6. Uç nokta kapısının sınırı:** gövde isteyen uçlar boş gövdeyle çağrılınca bağlama hatası (400), izin filtresinden önce dönebiliyor. Test bu durumda yalnızca "başarılı dönmedi"yi kanıtlar. GET uçları ve oturum, firma, platform denetimleri tam kanıtlı.
- **B7. Sürüm kapısı (Ç23.4) eksikleri:**
  - CI, yüksek ve kritik açıklı NuGet paketinde duruyor. Migration provası her CI'da boş veritabanına yapılıyor.
  - Yedekten dönüş (restore) provası, kaynak kod güvenlik taraması ve sözleşme farkı denetimi yok (Ç14 ile birlikte).
- **B8. Uçtan uca test yok (Ç23.1):** kritik akış (giriş → kayıt → atama e-postası) için E2E projesi boş.

## 6. Bu çalışmayla eklenen kapılar (Ç23)

- `TableInventoryTests`:
  - Her tablo ya RLS altında ya da gerekçeli platform listesinde.
  - Tenant sınıfı şemayla uyumlu.
  - Modelde migration'sız değişiklik yok.
- `EndpointProtectionTests`:
  - Her `/api` ucu oturum ister (liste dışı).
  - Rolü olmayan üye yalnızca listelenen okumalara erişir.
  - Platform uçları platform yöneticisine özel.
- `ModuleDataAccessRulesTests`: modüller tabloya yalnızca kayıt depolarından erişir, hiç kaydetmez.

Hepsi CI'da mevcut test projeleriyle çalışır; yeni uç nokta, tablo veya modül kodu bu kapılardan geçmeden birleşmez.
