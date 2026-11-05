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

Seçilen: `Modulix.Shared/Localization/{tr,en,az}.json` (düz anahtar → metin). `.resx` seçilmedi:

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

## Uygulama durumu

- **Aşama 2 (4 Kasım 2026):** `LanguageOptions` (Core) ve `LanguageCodes` (Shared; Shared Core'a başvuramadığı için ikisi ayrı tutulur, eşitliği `LanguagePreferenceTests` denetler), `core.regional.language` ayarı, `ModulixUser.PreferredLanguage` + migration, `PUT /api/account/language`, `MeResponse.PreferredLanguage`, Hesabım → Dil, istemcide `UiLanguage.Code`. Sunucu tarafı çözümleme servisi (e-posta ve arka plan işleri için alıcının dili) ilk tüketicisi olan aşama 3'te eklenir; o zamana kadar yazılacak servisin kullanacağı saf kural `LanguageOptions.Resolve`'dur.

## Açık noktalar

- `az` çevirisini kim yapacak ve doğrulayacak (anadili konuşan gözden geçirme)? Makine çevirisi yalnızca taslak sayılır.
- Sağdan sola diller kapsam dışıdır (üç dil de soldan sağa).
- Rapor ve sözleşme şablonu gibi kullanıcının yazdığı içeriklerin çoklu dil sürümü bu ADR'nin dışındadır.
