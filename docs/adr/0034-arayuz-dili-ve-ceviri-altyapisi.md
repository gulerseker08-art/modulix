# ADR-0034: Arayüz dili (kullanıcı ve tenant bazında) ve çeviri altyapısı

- **Durum:** Taslak (4 Kasım 2026). Kod yok; ADR-0020 "Ayrı ADR isteyen kurallar" başlığındaki TR/EN çeviri kalemini karşılar.
- **Karar:** Arayüz dili **kullanıcı başına** tercih edilir, tenant **varsayılan dil** tanımlar, son çare `tr`. Desteklenen diller `tr`, `en`, `az` (Azerbaycan Türkçesi, Latin). Metinler **kültür verisinden bağımsız JSON sözlükleriyle** çevrilir; aynı dosyaları sunucu ve Blazor WebAssembly okur. Dil, tarih/sayı biçimi (`core.regional.format`) ve saat diliminden ayrı bir tercihtir.

## Mevcut durum

| Konu | Durum | Kanıt |
| --- | --- | --- |
| Tenant bölgesel ayarları | **Mevcut** | `core.regional.time-zone`, `.currency`, `.format` (`CoreModule`, `RegionalOptions`); biçim yalnızca tarih ve sayıdır |
| Tarayıcıda biçim | **Mevcut** | `Regional.cs` biçimleri sabit tablodan alır; tarayıcıda tüm kültür verisi yüklenmez |
| Kullanıcı tercihi (sunucuda) | **Yok** | `ModulixUser` yalnızca `DisplayName` ve platform rollerini taşır; sütun tercihi `localStorage`'dadır |
| Metin çevirisi | **Yok** | Metinler Razor, `*Labels.cs` ve API hata iletilerine Türkçe gömülü |

## Kararlar

### 1. Diller

`tr` (varsayılan ve kaynak dil), `en`, `az`. Kod kümesi sabittir ve `LanguageOptions` içinde (Core) tanımlanır. Yeni dil eklemek: sözlük dosyası + listeye bir satır. Tarih/sayı biçimi listesine `az-AZ` eklenir (`dd.MM.yyyy`, ondalık `,`, binlik `.`).

### 2. Çözümleme sırası

kullanıcı tercihi → tenant varsayılanı → `tr`. Tenant'ın izin verdiği dil kümesi yoktur; tenant yalnızca varsayılan seçer. Kullanıcı kendi dilini istediği zaman değiştirebilir.

### 3. Tenant dili

Yeni ayar `core.regional.language` (`SettingType.Choice`, varsayılan `tr`, grup "Bölgesel ayarlar"). Mevcut `TenantSettings` altyapısını kullanır: yeni tablo yok, yazma kayıt boru hattından ve denetim kaydından geçer.

### 4. Kullanıcı dili: kullanıcı başına, üyelik başına değil

`ModulixUser.PreferredLanguage` (nullable `varchar(5)`, boş = tenant varsayılanı). Gerekçe: dil kişiye aittir, firma değiştirince değişmez; platform tablosu olduğu için RLS dışıdır; üyelik başına tutmak aynı kişiye her firmada yeniden seçim yaptırırdı. Migration geriye uyumlu (nullable kolon). Değer `MeResponse` ile istemciye gelir; profil ekranından değiştirilir. Tarayıcı `localStorage`'ı kaynak olamaz: cihaz değişince kaybolur ve e-posta gibi sunucu çıktıları dili bilemez. Giriş öncesi ekranlarda (giriş, şifre sıfırlama) dil tarayıcı tercihinden başlar, `localStorage`'da tutulur ve girişten sonra kullanıcı tercihi geçerli olur.

### 5. Kaynak biçimi: JSON

Seçilen: `Modulix.Core/Localization/Texts/{tr,en,az}.json` (düz anahtar → metin; derlemeye gömülür, `ITextCatalog` okur). Sözlükler Core'dadır, çünkü sunucu kodu ve modüller yalnızca Core/Core.Contracts'a başvurabilir; arayüz (Web yalnızca Shared'a başvurur) aynı sözlüğü `GET /api/i18n/{dil}` ucundan alır, böylece tek kaynak vardır ve dosya kopyalanmaz. Uç oturumsuzdur (giriş ekranı da çevrilir), yalnızca sabit ürün metinlerini döner, bilinmeyen dil 404 verir; yanıt eksik anahtarları Türkçeden tamamlanmış gelir. `.resx` seçilmedi:

- `.resx` uydu derlemeleri `CultureInfo` ile aranır. Tarayıcıda kültür verisi sınırlı yüklendiği için (`Regional.cs`) `az` kültürünün orada bulunması garanti değildir; kültür bulunamazsa metin sessizce Türkçeye düşer. JSON bir kültür nesnesi gerektirmez, yalnızca kod ister.
- Aynı dosyalar sunucu (API iletileri, e-posta, rapor başlıkları) ve istemci için tek kaynaktır; `.resx` iki ayrı erişim yolu ister.
- Eksik anahtar ve yer tutucu uyuşmazlığı testi JSON'da üç dosyanın anahtar kümesini karşılaştırmakla yapılır; çevirmen koda dokunmadan dosya düzenler.
- Bedeli: kendi yükleyicimiz (`ITextCatalog`, küçük) ve çoğul biçimler için basit bir kuralımız olur; derleme zamanı tür güvenliği yoktur, onun yerine test kapısı vardır.

Anahtar adı `modül.alan.öğe` (`crm.companies.title`). Metin içinde yer tutucu `{0}`; çoğul `anahtar.one` / `anahtar.other` (`az` ve `tr` tek çoğul biçimi kullanır, `en` ikisini).

### 6. Sunucu tarafı metinler

Sunucu çözümlemeyi kendisi yapar: istek bağlamında kullanıcı → tenant → `tr`. `Accept-Language` başlığı kullanılmaz (tenant gibi dil de oturumdan belirlenir). API hata iletileri anahtardan çevrilir; ham kod (`RecordErrorCode`) değişmez. Arka plan işlerinde (e-posta kuyruğu, zamanlanmış rapor) alıcı kullanıcının tercihi, yoksa tenant varsayılanı kullanılır. Kuyruğa yazılan e-postada dil kuyruk satırıyla birlikte saklanır, gönderim anında değişmez.

### 7. Çevrilmeyenler

Kullanıcının girdiği veri, kayıtlı teknik değerler (lookup anahtarları, izin anahtarları, enum adları, API yolları). Terim kuralları (ör. "Müşteri adayı") `tr` sözlüğü içindir; `en` ve `az` kendi terimlerini seçer. Dil değişimi saklanan hiçbir değeri değiştirmez.

### 8. Test kapısı

Mimari/birim test: (a) üç sözlüğün anahtar kümesi aynıdır, (b) yer tutucu sayıları eşleşir, (c) kodda kullanılan her anahtar `tr.json`'da vardır. Entegrasyon: dört test (mutlu yol, doğrulama, tenant izolasyonu, yetki) kullanıcı dilinin kaydı ve çözümleme sırası için.

## Aşamalar

1. Bu ADR.
2. Tercih altyapısı: `core.regional.language`, `ModulixUser.PreferredLanguage` + migration, çözümleme servisi, `MeResponse`, profil ekranı ve yönetim ayarı. Görünür etki: yok (çeviri yok).
3. Çeviri altyapısı: `ITextCatalog`, istemci/sunucu yükleyici, test kapısı, `az-AZ` biçimi.
4. Metin göçü modül modül: giriş akışı ve Core, sonra CRM, Destek, Sözleşme, Siparişler, Pazarlama, Raporlar. `az.json` ve `en.json` göç ettikçe dolar; eksik anahtar `tr`'ye düşer ve test kapısı bunu modül tamamlanınca zorunlu kılar.

### Aşama 4 dilim planı (7 Kasım 2026)

Her dilim ayrı PR'dır: o dilimdeki ekran ve bileşenlerin metinleri, `*Labels.cs` sınıflarındaki ilgili etiketler ve üç sözlük birlikte taşınır. Ortak sözcükler (`common.*`) ilk kullanıldıkları dilimde eklenir ve sonrakilerde yeniden kullanılır.

1. Giriş akışı (yapıldı) · 2. Kabuk ve ana sayfa (yapıldı) · 3. Menü ve uygulama adları (yapıldı)
4. **Ortak liste/form bileşenleri:** sayfalama, görünüm sekmeleri/seçici, sütun seçici, süzgeç, sıralama, satır menüsü, sekme çubuğu, kişisel görünüm şeridi, kayıt geçmişi, form yükleme.
5. Hesap ve oturum: Hesabım (ad, şifre, iki adımlı doğrulama, oturumlar), kuruluş seçimi, bulunamadı sayfası, davet/kullanıcı formları.
6. CRM: Firmalar ve Kişiler (listeler, ayrıntı, düzenleyiciler, ilişkili paneller) + `CrmLabels`.
7. CRM: Müşteri adayları, Fırsatlar, Aktiviteler (takvim dahil).
8. CRM: Teklifler, ürün ve fiyatlandırma (ürünler, fiyat listeleri, indirimler, hesaplama).
9. CRM: satış raporları ve CRM ayarları (hedefler, tahmin, analiz, kurallar, vekâletler, birimler, vergi/kur).
10. Sözleşmeler · 11. Siparişler · 12. Destek · 13. Pazarlama · 14. Depo ve Antrepo · 15. Raporlar
16. Yönetim (kullanıcı, ekip, rol, firma ayarları, özel alanlar, listeler, form tasarımı, içe aktarma, otomasyon, iletişim) — iki PR'a bölünebilir.
17. Platform.
18. Sunucu tarafı: API hata iletileri (§6), doğrulama iletileri, e-posta şablonları; denetim kaydı metinleri (`AuditText`).

## Uygulama durumu

- **Aşama 2 (4 Kasım 2026):** `LanguageOptions` (Core) ve `LanguageCodes` (Shared; Shared Core'a başvuramadığı için ikisi ayrı tutulur, eşitliği `LanguagePreferenceTests` denetler), `core.regional.language` ayarı, `ModulixUser.PreferredLanguage` + migration, `PUT /api/account/language`, `MeResponse.PreferredLanguage`, Hesabım → Dil, istemcide `UiLanguage.Code`. Sunucu tarafı çözümleme servisi (e-posta ve arka plan işleri için alıcının dili) ilk tüketicisi gelene kadar yazılmadı; kullanacağı saf kural `LanguageOptions.Resolve`'dur.
- **Aşama 3 (5 Kasım 2026):** `ITextCatalog` (Core.Contracts) ve `TextCatalog` (Core; gömülü `Localization/Texts/{tr,en,az}.json`, eksik anahtar Türkçeye, o da yoksa anahtara düşer), `GET /api/i18n/{dil}` (oturumsuz, `EndpointProtectionTests` listesine gerekçesiyle eklendi), istemcide `Texts.Get(anahtar)` ve oturumla birlikte sözlük yükleme (`SessionState`), `az-AZ` tarih/sayı biçimi. Pilot ekran: Hesabım → Dil (altı anahtar). Test kapısı `LocalizationRulesTests`: üç sözlüğün anahtar kümesi eşit, yer tutucular eşit, anahtarlar biçimli ve boş değil, Web'de `Texts.Get("...")` ile kullanılan her anahtar `tr.json`'da var. `az` metinleri taslaktır, anadili konuşan biri gözden geçirmelidir. Çoğul biçimler ilk çoğullu metin geldiğinde eklenir. Giriş öncesi dilin tarayıcıdan başlaması (§4) aşama 4 dilim 1'de yapıldı.
- **Aşama 4, dilim 1 (5 Kasım 2026): giriş akışı.** Giriş, Şifremi unuttum, Şifre sıfırlama, Davet kabulü, E-posta onayı sayfaları ve ortak `PasswordForm` sözlüğe geçti (`auth.*`, `common.*`; sözlükler şimdi 40 anahtar). Giriş öncesi dil: `localStorage`'da hatırlanan dil, yoksa tarayıcının dili (`navigator.languages`), ikisi de desteklenmiyorsa Türkçe. Kişi dilini açıkça seçince tarayıcıda hatırlanır, böylece çıkış sonrası giriş ekranı da o dilde açılır. Firma varsayılanı olan oturumlu kullanıcıda sıra değişmez: kullanıcı tercihi → firma varsayılanı → (firma yoksa) tarayıcı dili → `tr`. `MainLayout` metinler yüklenene kadar sayfa gövdesini çizmez (yoksa anahtar adları görünürdü). `Texts.Strong(anahtar, değerler)`: yer tutucuların kalın gösterildiği metin; değerler HTML'e kodlanır. **Çevrilmeyen:** sunucunun döndürdüğü hata iletileri (ör. şifre kuralı ihlali, süresi dolmuş bağlantı); bunlar §6'daki API ileti çevirisi işidir. Kabuk (menü, üst çubuk), ana sayfa ve modül ekranları sonraki dilimlerdedir.
- **Aşama 4, dilim 2 (5 Kasım 2026): kabuk ve ana sayfa.** `MainLayout` (içeriğe geç, şirket seçici, profil menüsü), `NavMenu` (ana sayfa, aktif modül, daralt/genişlet), `AppLauncher` (uygulama paneli), `ThemeToggle`, `GlobalSearch` (kayıt türü adları dahil), `AuthGate` (yetki uyarıları), `LicenseBanner` ve `Home` sözlüğe geçti (`shell.*`, `home.*`, `auth.gate.*`, `common.noResultFor`; kayıt türü adları `crm.<varlık>.title`, ilerideki CRM ekranlarıyla ortak kullanılır). **Çevrilmeyen:** `AppCatalog` metadata'sındaki uygulama, bölüm ve menü bağlantı adları ile "Yakında" kartlarının adları (Türkçe sabit, `Breadcrumb` da bunlardan türer); bunlar modül metadata'sına çeviri anahtarı eklenerek ayrı bir dilimde çevrilir. Menüyü daralt/genişlet metni artık başlıkta ve etikette aynı (cümle düzeni); önceden etikette baş harfler büyüktü.
- **Aşama 4, dilim 3 (7 Kasım 2026): menü ve uygulama adları.** `AppCatalog`'taki `Name` Türkçe kaynak metin olarak kalır (testler ve sözlük yüklenmemişken gösterim buna dayanır); çeviri `AppNames` ile görüntülemede yapılır. Anahtarlar kimlikten türer: `nav.app.<id>` (uygulama/ekran, pasif kartlar dahil), `nav.app.<id>.menu` (ev sahibi menüsündeki özel ad), `nav.group.<id>` (bölüm başlıkları). Sözlükte anahtar yoksa katalogdaki ad gösterilir (`Texts.GetOr`); anahtar adı görünmez. Sol menü, uygulama paneli (arama çevrilmiş adlarla eşleşir) ve konum izi (`Breadcrumb`) bunları kullanır. `AppNamesTests`: her katalog adı için `tr.json`'da anahtar var ve metin katalogla aynı; diller arası eşitliği mevcut mimari test denetler. Yeni uygulama/ekran/bölüm eklenince üç sözlüğe de anahtar eklenir.
- **Aşama 4, dilim 4 (7 Kasım 2026): ortak liste/form bileşenleri.** `Pager`, `FilterToggle`, `SortHeader`, `ErrorSummary`, `ViewTabs`, `ViewSelector`, `ColumnPicker`, `RowMenu`, `TabBar`, `LayoutForm`, `PersonalViewBar` ve `RecordHistory` sözlüğe geçti (`common.*`, 44 yeni anahtar). Metin parametreleri (`Label`, `NewViewText`, `TabsLabel`) artık boş (`null`) olabilir ve boşken çeviriden varsayılan alır; ekranlar özel metin verdikçe kendi dilimlerinde çevrilir. `RecordHistory` içindeki işlem adları ve kaynak adları (`AuditText`) dilim 18'dedir.
- **Aşama 4, dilim 5 (7 Kasım 2026): hesap ve oturum.** `Account` (Hesabım: profil, e-posta, şifre, açık oturumlar), `TwoFactorSection` (iki adımlı doğrulama), `SelectTenant` (kuruluş seçimi) ve `NotFound` sözlüğe geçti (`account.*`, `two-factor.*`, `tenant.select.*`, `not-found.*`; 56 yeni anahtar). `NotFound` önceden İngilizce sabitti. Çevrilmeyen: sunucunun döndürdüğü hata ve doğrulama iletileri (dilim 18). Kurtarma kodları dosyasının adı da dile göre değişir (`two-factor.codesFile`).
- **Aşama 4, dilim 6a (7 Kasım 2026): CRM Firmalar ve Kişiler sayfaları.** `Companies`, `Contacts` (listeler), `CompanyDetail`, `ContactDetail` (ayrıntı), `CompanyMerge`, `ContactMerge` + `MergeWizard` ve ortak `GridColumnPicker` sözlüğe geçti (`crm.companies.*`, `crm.contacts.*`, `crm.company.*`, `crm.contact.*`, `crm.merge.*`; ortak sözcükler `common.field.*`, `common.actions`, `common.open`, `common.edit`, `common.records` vb.; 135 yeni anahtar). Sütun tanımları (`BaseColumns`) statik alandan özelliğe çevrildi: metinler sınıf yüklenirken değil, sayfa açılırken çözülür (statik alan dil yüklenmeden donardı). "Fırsatlar ve leadler" sekmesi terim kuralı gereği "Fırsatlar ve müşteri adayları" oldu. Dilim 6b (sonraki PR): firma/kişi düzenleyicileri ve bağlı bölümler (`CompanyEditor`, `ContactEditor`, adres/iletişim/ilişki bölümleri, `LookupPicker`), `CrmLabels` ve `CrmRelationshipRoles` etiketleri.

## Açık noktalar

- `az` çevirisini kim yapacak ve doğrulayacak (anadili konuşan gözden geçirme)? Makine çevirisi yalnızca taslak sayılır.
- Sağdan sola diller kapsam dışıdır (üç dil de soldan sağa).
- Rapor ve sözleşme şablonu gibi kullanıcının yazdığı içeriklerin çoklu dil sürümü bu ADR'nin dışındadır.
