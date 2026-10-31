# Form standartları

Elle yazılmış formlar ile metadata'dan (özel alanlar, `CustomFieldInputs`) oluşan alanlar aynı sınıfları kullanır; bu sayede tasarım kalitesi farklılaşmaz.

## Düzen: bölüm kartları (referans "Firma kartı" / "Fırsat" ekranları)

Kayıt formu kart çerçevesini kendisi taşımaz (`record-form sectioned`); alanlar ikon başlıklı **bölüm kartlarına** (`FormSection`) bölünür. Üstte başlık ve İptal / Kaydet araç çubuğu, geniş ekranda solda yazdıkça güncellenen özet kartı bulunur.

```razor
<form class="record-form sectioned" novalidate @onsubmit="SaveAsync" @onsubmit:preventDefault>
    <div class="form-toolbar">
        <div><h2>Firma kartı</h2><p>ABC Lojistik A.Ş.</p></div>
        <div class="form-toolbar-actions">
            <button type="button" class="btn btn-outline-secondary" @onclick="Cancel">İptal</button>
            <button type="submit" class="btn btn-primary" disabled="@saving">Kaydet</button>
        </div>
    </div>

    <div class="record-layout">
        <aside class="record-summary"><div class="summary-card">…canlı özet…</div></aside>
        <div class="record-main">
            <FormSection Title="Temel bilgiler" Icon="building">
                <div class="form-grid">
                    <div class="span-6">
                        <label class="form-label required" for="name">Firma adı</label>
                        <input id="name" class="form-control" required aria-required="true" @bind="form.Name" />
                        <FieldError Field="name" Errors="errors" />
                    </div>
                    <div class="span-3">…Tür…</div>
                    <div class="span-3">…Durum…</div>
                </div>
            </FormSection>

            <FormSection Title="Genel iletişim bilgileri" Icon="phone">
                <div class="form-grid">
                    <div class="span-4">
                        <label class="form-label" for="phone">Telefon</label>
                        <div class="input-icon"><AppIcon Name="phone" Size="16" /><input id="phone" class="form-control" type="tel" @bind="form.Phone" /></div>
                    </div>
                </div>
            </FormSection>

            <div class="form-actions">…Kaydet / İptal / Sil…</div>
        </div>
    </div>
</form>
```

- **`FormSection`** (`Components/FormSection.razor`): `Title`, `Icon` (AppIcon), isteğe bağlı `Description`, sağda `Actions`, ek sınıf için `Class` (ör. sağ panelde `side-card`). Gövde genellikle `form-grid` ya da salt okunur `detail-grid` taşır; kart içindeki `detail-grid` kendi çerçevesini tekrarlamaz. Aksiyon verilince içerik `<ChildContent>` içine yazılır.
- **`form-grid`** 12 kolonludur: alan genişliği `span-3`, `span-4`, `span-6`, `span-8`, `span-12` (= `span-all`) ile verilir. Sınıfsız alan 768px üstünde yarım, 1200px üstünde üçte bir satır alır; telefonda her alan tek kolondur. Çekmecede (`Drawer`) en çok iki kolona böler.
- **`input-icon`**: telefon, e-posta, web sitesi, adres gibi alanlarda input'un solunda ikon (`AppIcon`, 16px).
- **Özet kartı**: `record-summary > summary-card` (`summary-identity` avatar + ad + durum, `summary-heading`, `summary-facts`); formdaki değerlerden canlı doldurulur, 992px altında gizlenir.
- Çekmecede araç çubuğu gizlenir (çekmecenin kendi başlığı vardır), kartlar çerçevesiz ve çizgiyle ayrılır; Kaydet / İptal alta yapışıktır.
- Kartlar arası 16px, kart içi 20–24px; ikon kutusu 36px, marka tonunda.

Detay sayfalarında da aynı kart kullanılır: Fırsat (Temel bilgiler, Detaylar, Ürün / hizmet kalemleri, Kişiler, Rakipler, Teklifler, tarihçe, sağ panel kartları), Firma / Kişi genel sekmesi, Lead talep bilgileri.

## Alan durumları

| Durum | Nasıl |
|---|---|
| Default | `form-control` / `form-select`, 40px yükseklik, 8px köşe |
| Hover | Kenar `--mx-border-strong` |
| Focus | Kenar `--mx-brand-primary` + `--mx-focus-ring` |
| Disabled | `disabled` — `--mx-surface-muted` zemin, ikincil metin |
| Read-only | `readonly` — `--mx-surface-hover` zemin |
| Required | Etikete `required` sınıfı (kırmızı `*`) ve inputa `required`/`aria-required="true"` |
| Invalid | Blazor `invalid` sınıfı ya da `aria-invalid="true"` — kırmızı kenar + iç çizgi; ileti alanın altında (`FieldError`, `validation-message`) |
| Loading | Düğmede `<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>` ve `disabled` |
| Success | Kaydedildikten sonra `Notice` ile `role="status"` ileti |

Durum yalnızca renkle anlatılmaz: hata iletisi metin olarak görünür, zorunluluk `*` ile ve erişilebilir adla belirtilir.

## Kaydetme davranışı

- Aksiyon sırası: **Kaydet** (birincil), gerekiyorsa **Kaydet ve Kapat / Kaydet ve Yeni**, **İptal**; yıkıcı işlem (**Sil**) en sonda `btn-outline-danger`. Üst araç çubuğunda İptal solda, Kaydet sağda ve en sonda.
- Kaydetme sürerken düğme `disabled` olur (çift kayıt önlenir) ve spinner gösterilir.
- Sunucu doğrulama hataları alan anahtarıyla `FieldError`'a, genel hata `Notice`'e yazılır.
- Kaydedilmemiş değişiklik uyarısı ve merkezi toast bugün **yok**; eklenecek iş kalemi olarak `migration-report.md`'de.

## Lookup

`LookupPicker` kullanılır: yazdıkça sunucu taraflı arama (debounce), klavye ile gezinme, seçimi temizleme, seçili kayda gitme, boş sonuç ve hata durumu. Hangi alanlarda aranacağı yönetici tarafından `Yönetim → Alan ve lookup ayarları` ekranından belirlenir (metadata). Arama her zaman sunucuda tenant ve yetki süzgeciyle yapılır.

## Sekmeler

Kayıt sayfalarında bölümler `TabBar` + `TabState` ile sekmelere ayrılır (Firma, Kişi, Fırsat). Kurallar:

- Sekme listesi yetkiye göre kurulur; görme izni olmayan bölümün sekmesi hiç eklenmez (sunucu yine denetler).
- Sekme ilk seçildiğinde çizilir ve verisini o an yükler; başka sekmeye geçince DOM'da gizli kalır. Böylece açık bir düzenleme formu sekme değiştirince kaybolmaz.
- Etkin sekme birincil renk alt çizgi ve koyu metinle gösterilir; sayı rozeti sunucudaki toplamdır.
- Telefonda sekme çubuğu yatay kayar.
- Durum geçişleri gibi sayfa içi küçük seçimler için `filters` + `chip` (`aria-pressed`) kullanılmaya devam eder.
