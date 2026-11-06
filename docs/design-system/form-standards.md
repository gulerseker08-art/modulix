# Form standartları

Elle yazılmış formlar ile metadata'dan (özel alanlar, `CustomFieldInputs`) oluşan alanlar aynı sınıfları kullanır; bu sayede tasarım kalitesi farklılaşmaz.

## Düzen: bölüm kartları (referans "Firma kartı" / "Fırsat" ekranları)

Kayıt formu kart çerçevesini kendisi taşımaz (`record-form sectioned`); alanlar ikon başlıklı **bölüm kartlarına** (`FormSection`) bölünür. Üstte yapışkan kayıt çubuğu (`form-toolbar`: başlık, İptal / Kaydet) durur; altında yazdıkça güncellenen özet, geniş ekranda yan yana kutulardan oluşan **gösterge şeridi** olarak görünür ve form tam genişlik kullanır. Üst çubuğu olan formda alttaki `form-actions` içinden yalnızca Sil görünür (Kaydet / İptal yinelenmez); çekmecede üst çubuk gizlidir, alttaki çubuk kullanılır.

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
- **Özet**: `record-summary > summary-card` (`summary-identity` avatar + ad + durum, `summary-heading`, `summary-facts`); formdaki değerlerden canlı doldurulur. Kayıt formunda yalnızca `summary-facts` kutular hâlinde çizilir, 992px altında ve çekmecede gizlenir; detay sayfalarında (Firma, Kişi) solda özet kartı olarak kalır.
- Çekmecede araç çubuğu gizlenir (çekmecenin kendi başlığı vardır), kartlar çerçevesiz ve çizgiyle ayrılır; Kaydet / İptal alta yapışıktır.
- Kartlar arası 12px, kart içi 16px; başlık ince çizgiyle gövdeden ayrılır, ikon kutusu 28px ve tonaldir (degrade yok). Kart `overflow` kullanmaz, açılır listeler taşabilir.

Detay sayfalarında da aynı kart kullanılır: Fırsat (Temel bilgiler, Detaylar, Ürün / hizmet kalemleri, Kişiler, Rakipler, Teklifler, tarihçe, sağ panel kartları), Firma / Kişi genel sekmesi, Lead talep bilgileri.

## Metadata'dan çizilen formlar (ADR-0021)

Ekleme/düzenleme formları yerleşimi koddan değil **firmanın form düzeninden** alır. Editör her alanın girdisini `LayoutForm` bileşeninin `Field` şablonunda anahtara göre verir; sekmeler, bölüm kartları, sıra, genişlik, zorunluluk ve görünürlük `GET /api/form-layouts` ile gelen düzenden çizilir.

```razor
<LayoutForm Layout="layout" Shows="Shows" CustomFields="CustomFields" CustomValues="form.Custom" Errors="errors">
    <Field Context="f">
        @switch (f.Key)
        {
            case "name":
                <label class="@f.LabelClass()" for="name">@f.Label</label>
                <input id="name" class="form-control" required="@f.Required" aria-required="@f.AriaRequired()" @bind="form.Name" />
                <FieldError Field="name" Errors="errors" />
                break;
        }
    </Field>
</LayoutForm>
```

- Düzen `FormLayouts.ForAsync(kayıtTürü)` ile yüklenir; yüklenemezse form kaydetmeyi kapatır ve uyarı gösterir (varsayılan düzen istemcide yoktur).
- Varsayılan yerleşim modülün tanımındadır (`CrmForms`). Yeni alan eklerken: tanıma alanı ekle (anahtar = kayıt alanının teknik adı), editörde `case` yaz. Yalnızca ekranda anlamı olan alanlar (`isAllDay`, `logAsDone`) tanımda **sabit**tir; zorunlu kılınamaz.
- İzne/duruma bağlı alanlar (`FixedCompanyId`, tutar izni) `Shows` süzgeciyle atlanır; bütün alanları gizli bölüm çizilmez.
- Özel alanlar aynı düzende yer alır (`custom.{anahtar}`), `CustomFieldInputs Only="…"` ile tek tek çizilir; zorunluluk düzenden artırılabilir.
- Firmanın zorunlu kıldığı alan sunucuda da denetlenir; `FieldError` sunucu iletisini alanın altında gösterir.
- Yönetim ekranı: `Pages/Admin/FormDesigner.razor` (`/yonetim/form-tasarimi`) — sekme/bölüm ekleme ve silme (içerik ilk sekmeye/bölüme taşınır), ok düğmeleriyle sıralama, "taşı" listeleri, genişlik, zorunlu, görünür, varsayılana dön.

## Alan durumları

| Durum | Nasıl |
|---|---|
| Default | `form-control` / `form-select`, 36px yükseklik, 9px köşe, 13px yazı; kayıt formu, bölüm kartı ve çekmecede `--mx-surface-hover` dolgulu, sayfa zemininde (arama, süzgeç) `--mx-surface` |
| Hover | Kenar `--mx-border-strong` |
| Focus | Zemin `--mx-surface`, kenar `--mx-brand-primary` + `--mx-focus-ring` |
| Disabled | `disabled` — `--mx-surface-muted` zemin, ikincil metin |
| Read-only | `readonly` — `--mx-surface-muted` zemin, metin birincil renkte |
| Required | Etikete `required` sınıfı (kırmızı `*`) ve inputa `required`/`aria-required="true"` |
| Invalid | Blazor `invalid` sınıfı ya da `aria-invalid="true"` — kırmızı kenar + iç çizgi, `--mx-tone-danger-bg` zemin; ileti alanın altında (`FieldError`, `validation-message`) |
| Loading | Düğmede `<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>` ve `disabled` |
| Success | Kaydedildikten sonra `Notice` ile `role="status"` ileti |

Durum yalnızca renkle anlatılmaz: hata iletisi metin olarak görünür, zorunluluk `*` ile ve erişilebilir adla belirtilir.

## Kaydetme davranışı

- Aksiyon sırası: **Kaydet** (birincil), gerekiyorsa **Kaydet ve Kapat / Kaydet ve Yeni**, **İptal**; yıkıcı işlem (**Sil**) en sonda `btn-outline-danger`. Üst araç çubuğunda İptal solda, Kaydet sağda ve en sonda.
- Kaydetme sürerken düğme `disabled` olur (çift kayıt önlenir) ve spinner gösterilir.
- Sunucu doğrulama hataları alan anahtarıyla `FieldError`'a, genel hata `Notice`'e yazılır.
- Kaydedilmemiş değişiklik: `<form … @oninput="() => dirty = true">` ve `form-toolbar-actions` içinde ilk öğe `<UnsavedChanges IsDirty="() => dirty" />`. Çubukta durum yazısı görünür, sayfadan ayrılırken onay istenir. `await Saved/Deleted.InvokeAsync(…)` satırından hemen önce `dirty = false;` yazılır; yoksa kayıttan sonra yanlış onay çıkar. `LookupPicker` seçim ve temizlemede de forma `input` olayı yayar. Sayfa içinde kapanan formda (İptal) onay istenmez.
- Merkezi toast bugün **yok**; eklenecek iş kalemi olarak `migration-report.md`'de.

## Lookup

`LookupPicker` kullanılır: yazdıkça sunucu taraflı arama (debounce), klavye ile gezinme, seçimi temizleme, seçili kayda gitme, boş sonuç ve hata durumu. Hangi alanlarda aranacağı yönetici tarafından `Yönetim → Alan ve lookup ayarları` ekranından belirlenir (metadata). Arama her zaman sunucuda tenant ve yetki süzgeciyle yapılır.

## Sekmeler

Kayıt sayfalarında bölümler `TabBar` + `TabState` ile sekmelere ayrılır (Firma, Kişi, Fırsat). Kurallar:

- Sekme listesi yetkiye göre kurulur; görme izni olmayan bölümün sekmesi hiç eklenmez (sunucu yine denetler).
- Sekme ilk seçildiğinde çizilir ve verisini o an yükler; başka sekmeye geçince DOM'da gizli kalır. Böylece açık bir düzenleme formu sekme değiştirince kaybolmaz.
- Kayıt ve form sekmeleri düğme grubu görünümündedir: etkin sekme yüzey zeminli, gölgeli ve kalın yazılıdır (renk dışında da ayrılır); sayı rozeti sunucudaki toplamdır. Liste görünümleri (`ViewTabs`) alt çizgili kalır.
- Telefonda sekme çubuğu yatay kayar.
### Her kayıt formu sekmelidir (seçenek 4 kararı, 3.11.2026)

- **Metadata'dan çizilen formlar** (`LayoutForm`: Firma, Kişi, Lead, Fırsat, Aktivite, Sözleşme): tek sekme olsa bile `TabBar` çizilir. Modül varsayılan düzenleri (`CrmForms`, `ContractsForms`) çok sekmelidir; ilk sekme `genel`, zorunlu kimlik alanları ilk sekmededir (birim testle denetlenir: `CrmFormDefaultsTests`, `ContractFormDefaultsTests`). Yeni modül formu tanımlarken bölümleri sekmelere dağıt (`FormSectionDefinition.Tab`); `finans` gibi anahtarları firmaların ekleyeceği sekmelere bırak. Firması düzenini daha önce kaydetmişse kendi sekmeleri geçerlidir (tek sekmeli düzen tek sekmeli çubukla çizilir); "Varsayılana dön" yeni varsayılanı getirir.
- **Tek bölümlü kayıt formları:** form gövdesi `<FormTabs Title="…" Label="…">…</FormTabs>` ile sarılır (tek sekmeli çubuk + panel; sekme anahtarı bileşende üretilir). Birim, kategori, vergi oranı, döviz kuru, indirim politikası, teklif kuralı, vekâlet, iş takvimi, kuyruk, departman, ekip, özel alan, adres, lokasyon gibi küçük kayıt formları böyledir.
- **Çok bölümlü metadata dışı kayıt formları** (Yeni talep, Yeni kampanya, Yeni segment, Ürün/hizmet, Fiyat listesi, Teklif başlığı ve kalemi, SLA politikası, Rol, Firma açma, Devir bilgileri, Siparişe ve Sözleşmeye dönüştürme, Sözleşme şablonu): `TabBar` + `TabState` + her sekme için `TabPanel` ve `fieldset` içinde panel; sekme anahtarları `form-` önekli. Sekmelerin `TabItem.Errors` değeri sunucunun döndürdüğü hatalı alanlardan hesaplanır; kayıt denemesinden sonra `tabs.RevealFirstFailing(...)` hatalı ilk sekmeyi açar. Hata renk dışında "!" rozeti ve gizli metinle de belirtilir.
- Sekmeler arası geçişte girilen veri korunur (paneller gizlenir, silinmez). Zorunlu alanlar ilk sekmede durur.
- Paket bileşenleri gibi forma bağlı alt editörler ayrı sekme olabilir (Ürün formunda "Paket bileşenleri"; o sekme açıkken ana formun Kaydet çubuğu gizlenir). Sayfada birden fazla sekmeli form varsa sekme anahtarları ayrı önek alır (Teklif: `form-*` ve `line-*`). Kapsam: kayıt oluşturma/düzenleme formları. Sekmesiz kalanlar: sayfa içi eylem formları (yorum ekle, durum değiştir, teslimat/hizmet çalıştır, onay/imza/yenileme panelleri), önizleme/deneme/süzgeç formları, hesap ve iki adımlı doğrulama ekranları, fiyat hesaplayıcı, seçenek listesi editörü ve adım adım sihirbazlar (Sözleşme sihirbazı kendi adımlarına sahiptir).
- Durum geçişleri gibi sayfa içi küçük seçimler için `filters` + `chip` (`aria-pressed`) kullanılmaya devam eder.
