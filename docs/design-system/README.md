# Modulix tasarım sistemi

Modulix'in bütün modülleri tek bir ürün ailesi gibi görünmelidir. Bu klasör, arayüzün ortak tasarım dilini ve yeni ekran geliştirirken uyulacak kuralları anlatır.

| Belge | İçerik |
|---|---|
| [theme-tokens.md](theme-tokens.md) | Renk, tipografi, boşluk, köşe, gölge tokenları; açık/koyu tema |
| [component-catalog.md](component-catalog.md) | Ortak bileşenler ve CSS sınıfları, hangisi var hangisi yok |
| [form-standards.md](form-standards.md) | Form düzeni, alan durumları, kaydetme davranışı |
| [visual-qa-checklist.md](visual-qa-checklist.md) | Her ekran için görsel kontrol listesi |
| [migration-report.md](migration-report.md) | Ekran envanteri, yapılanlar, kalanlar, test sonuçları |

## Teknik temel

- Arayüz: Blazor WebAssembly (.NET 10), `src/Modulix.Web`. Yeni UI kütüphanesi eklenmez.
- Stil: Bootstrap 5.3.3 + `wwwroot/css/app.css`. Bootstrap değişkenleri (`--bs-*`) Modulix tokenlarına bağlıdır; bu yüzden `btn`, `form-control`, `table`, `alert` gibi Bootstrap sınıfları otomatik olarak Modulix görünümünü alır.
- Kabuk: `Layout/MainLayout.razor` (üst çubuk, içerik) + `Layout/NavMenu.razor` (sol menü). Bileşene özel stil `*.razor.css` (scoped CSS) dosyalarındadır.
- İkonlar: tek ikon seti, `Components/AppIcon.razor` (Lucide, satır içi SVG, `currentColor`).
- Menü: `Services/AppCatalog.cs` metadata'sı; `AppNavigation` tenant'ın açık modüllerine ve kullanıcının izinlerine göre süzer. Menüde gizlemek yetkilendirme değildir; yetki sunucuda uygulanır.

## Altın kurallar

1. **Sabit renk yazma.** Bileşen CSS'i yalnızca `--mx-*` tokenlarını kullanır. Yeni bir anlam gerekiyorsa önce `theme-tokens.md`'ye token ekle; koyu tema değerini de tanımla.
2. **Kendi kabuğunu yapma.** Her oturumlu sayfa `MainLayout` içinde çalışır. Yeni modül yalnızca `AppCatalog`'a kayıt ekler; sol menü ve uygulama paneli kendiliğinden oluşur.
3. **Sayfa başlığı için `PageHeader` kullan.** `<PageHeader Title="…" Icon="…" Subtitle="…">aksiyonlar</PageHeader>`. Birincil aksiyon sağda ve en sonda.
4. **Logo metnini elle yazma.** Logo yalnızca `BrandLogo` bileşeninden gelir. Ürün adı her yerde **Modulix** yazılır (M büyük, diğerleri küçük, `ü` yok).
5. **Formlar bölüm kartlarıyla kurulur**: `record-form sectioned` + `form-toolbar` (İptal / Kaydet), `FormSection` kartları (ikon + başlık), 12 kolonlu `form-grid` (`span-3/4/6/8/12`), `form-label`, `form-control`/`form-select`, `input-icon`, `FieldError`, `form-actions`. Ayrıntı: `form-standards.md`.
6. **Liste ekranları `record-table`** kullanır; telefonda kart listesine dönüşmesi için hücrelere `data-label` verilir.
7. **Yazı tipi Manrope, düğmeler ve gridler seçenek 4'e göredir** (3.11.2026 kararı; bütün modüller): birincil `btn-primary` (çizgili lacivert, hap biçimli, ekranda en çok bir; 12.11.2026'da degrade + parlamanın yerine geçti), ikincil `btn-outline-secondary`, üçüncül `btn-tonal`, araç çubuğu `btn-ghost`; liste ekranı araç çubuğu (`list-toolbar`) = `ViewSelector` ("Görünüm: … ▾") + arama + birincil eylem (Filtrele/Sütunlar özellikleri gelince eklenir); `record-table` büyük harf başlıklı kart. Ayrıntı: `theme-tokens.md`, `component-catalog.md`.
8. **Her kayıt formu sekmelidir** (`LayoutForm` + `TabBar`; tek sekmeli form da sekme çubuğuyla çizilir). Sekmeler `FormSection` kartlarını gruplar; sekmeler arası girdi kaybolmaz; alan yerleşimi metadata'dandır (ADR-0021).
9. **Terim: "Müşteri adayı".** Arayüz metinlerinde "Lead" yazılmaz (menü, başlık, düğme, ileti, izin adı); kodda, API yolunda ve URL'de teknik ad `lead` kalır.
10. **Erişilebilirlik**: her ikon düğmesinde erişilebilir ad (`aria-label`, `title` ya da `visually-hidden` metin); durum yalnızca renkle anlatılmaz (metin, ikon ya da çizgi eşlik eder).

## Marka

- Logo: `Components/BrandLogo.razor`. Onaylı logo görselidir (işaret + "Modulix" yazısı + "İŞİN YENİ ZEKÂSI." sloganı); kaynak `docs/design-system/assets/modulix-logo-source.png`. Türetilmiş varlıklar `wwwroot/img/` altındadır (şeffaf arka plan): `modulix-logo.png` (sloganlı), `modulix-logo-wordmark.png` (sloganlı olmayan; üst çubuk için), `modulix-mark.png` (yalnızca işaret) ve her birinin `-dark` sürümü (koyu tema: yazı açık tona, işaretin lacivert yarısı açık çelik maviye çekilir). `favicon.png` (64px) ve `icon-192.png` aynı işaretten üretilir.
- Varlıkları yeniden üretmek için: `python3 scripts/make-logo-assets.py` (pillow + numpy gerekir). Logo değişirse yalnızca kaynak görsel güncellenip betik çalıştırılır.
- `BrandLogo` parametreleri: `Size` (px yükseklik, varsayılan 36), `ShowTagline` (varsayılan kapalı; slogan küçük boyutta okunmaz, büyük alanlarda `ShowTagline="true"` ve daha büyük `Size` ver). Açık/koyu tema ve daraltılmış menüde yalnızca işaret gösterimini CSS seçer (`data-theme`, `sidebar-collapsed`). Logo metni başka yerde elle yazılmaz.
- Favicon `favicon.png`; `index.html` `theme-color` değeri `--mx-brand-navy` ile aynıdır.

## Tema

- Varsayılan açık tema. Kullanıcı üst çubuktaki düğmeyle koyu temaya geçer; tercih tarayıcıda (`localStorage: modulix.theme`) saklanır.
- `wwwroot/js/shell.js` `<head>` içinde, Blazor'dan önce çalışır ve `<html>`'e `data-theme` + `data-bs-theme` yazar; sayfa yanlış temayla yanıp sönmez.
- Bugün platform ya da tenant düzeyinde bir tema politikası yoktur. Eklenirse önceliği kullanıcı tercihinin önündedir; `modulixShell.setTheme` çağrısı tek değişiklik noktasıdır.

## Yeni ekran geliştirme rehberi (kısa)

1. Route'u `AppCatalog`'a ekle (ikon `AppIcon` setinden, izin anahtarı modül anahtarıyla başlar).
2. Sayfayı `AuthGate` ile sar, başlığı `PageHeader` ile kur.
3. Liste: arama/süzgeç için `filters` + `chip`, tablo için `table record-table`, sayfalama için `Pager`.
4. Kayıt formu: `record-form` içinde `form-grid`; alan altında `FieldError`; aksiyonlar `form-actions` (Kaydet `btn-primary`, İptal `btn-outline-secondary`, Sil `btn-outline-danger`).
5. Lookup alanları için `LookupPicker`, özel alanlar için `CustomFieldInputs` kullan; kendi arama kutunu yazma.
6. `visual-qa-checklist.md`'yi açık ve koyu temada, masaüstü ve 390px genişlikte uygula.
