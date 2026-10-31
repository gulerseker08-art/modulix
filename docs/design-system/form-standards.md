# Form standartları

Elle yazılmış formlar ile metadata'dan (özel alanlar, `CustomFieldInputs`) oluşan alanlar aynı sınıfları kullanır; bu sayede tasarım kalitesi farklılaşmaz.

## Düzen

```razor
<form class="record-form" @onsubmit="SaveAsync" @onsubmit:preventDefault>
    <h2>Temel bilgiler</h2>
    <div class="form-grid">
        <div>
            <label class="form-label required" for="name">Firma resmi unvanı</label>
            <input id="name" class="form-control" @bind="form.Name" aria-describedby="name-error" />
            <FieldError Field="name" Errors="errors" />
        </div>
        <div class="span-all">…geniş alan…</div>
    </div>
    <div class="form-actions">
        <button type="submit" class="btn btn-primary" disabled="@saving">Kaydet</button>
        <button type="button" class="btn btn-outline-secondary" @onclick="Cancel">İptal</button>
    </div>
</form>
```

- `form-grid`: telefonda 1, 768px üstünde 2, 1200px üstünde 3 kolon. Tüm satırı kaplayan alan `span-all`.
- İlgili alanlar mantıksal kartlara bölünür (`record-form` ya da `detail-section` başına bir başlık). Kartlar arası 16–24px, kart içi 24px.
- Detay sayfasında ana içerik solda, özet/ilgili kayıtlar sağda; dar ekranda alt alta.

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

- Aksiyon sırası: **Kaydet** (birincil), gerekiyorsa **Kaydet ve Kapat / Kaydet ve Yeni**, **İptal**; yıkıcı işlem (**Sil**) en sonda `btn-outline-danger`.
- Kaydetme sürerken düğme `disabled` olur (çift kayıt önlenir) ve spinner gösterilir.
- Sunucu doğrulama hataları alan anahtarıyla `FieldError`'a, genel hata `Notice`'e yazılır.
- Kaydedilmemiş değişiklik uyarısı ve merkezi toast bugün **yok**; eklenecek iş kalemi olarak `migration-report.md`'de.

## Lookup

`LookupPicker` kullanılır: yazdıkça sunucu taraflı arama (debounce), klavye ile gezinme, seçimi temizleme, seçili kayda gitme, boş sonuç ve hata durumu. Hangi alanlarda aranacağı yönetici tarafından `Yönetim → Alan ve lookup ayarları` ekranından belirlenir (metadata). Arama her zaman sunucuda tenant ve yetki süzgeciyle yapılır.

## Sekmeler

Kayıt sayfalarında bölümler `TabBar` + `TabState` ile sekmelere ayrılır (Firma, Kişi). Kurallar:

- Sekme listesi yetkiye göre kurulur; görme izni olmayan bölümün sekmesi hiç eklenmez (sunucu yine denetler).
- Sekme ilk seçildiğinde çizilir ve verisini o an yükler; başka sekmeye geçince DOM'da gizli kalır. Böylece açık bir düzenleme formu sekme değiştirince kaybolmaz.
- Etkin sekme birincil renk alt çizgi ve koyu metinle gösterilir; sayı rozeti sunucudaki toplamdır.
- Telefonda sekme çubuğu yatay kayar.
- Durum geçişleri gibi sayfa içi küçük seçimler için `filters` + `chip` (`aria-pressed`) kullanılmaya devam eder.
