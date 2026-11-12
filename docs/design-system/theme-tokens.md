# Tema tokenları

Kaynak: `src/Modulix.Web/wwwroot/css/app.css` başındaki `:root, [data-theme="light"]` ve `[data-theme="dark"]` blokları. Bileşenler yalnızca bu adları kullanır.

## Marka renkleri

Modulix paleti **lacivert / mavi / turkuaz** üzerine kuruludur (referans CRM tasarımı, 31.10.2026 kararı; önceki indigo/lila/krem denemesi geri alındı). Logo (`wwwroot/img/modulix-logo*.png`, `modulix-mark*.png`) aynı paleti taşır: iki çapraz şeritten oluşan "X", sol yarı lacivert, sağ üst turkuaz, sağ alt mavi; koyu tema için `-dark` sürümleri.

| Token | Açık | Koyu | Kullanım |
|---|---|---|---|
| `--mx-brand-navy` | `#101D35` | — | Ana marka rengi: logo yazısı, aktif modül başlığı, ana metin |
| `--mx-brand-navy-light` | `#173B68` | — | Aktif modül başlığı gradyanı, avatar, logo işaretinin sol çizgisi |
| `--mx-brand-primary` | `#087DB8` | `#3BAEE6` | Ana etkileşim rengi: birincil buton, bağlantı, odak, aktif sekme, aktif menü çizgisi |
| `--mx-brand-primary-hover` | `#066A9C` | `#6CC6F2` | Hover/active |
| `--mx-brand-teal` | `#00C2C7` | `#2DD4D8` | Marka vurgusu: koyu zeminde (aktif modül ikonu), logo işaretinin sağ çizgisi |
| `--mx-brand-teal-dark` | `#008F98` | `#5EEAEE` | Açık zeminde erişilebilir turkuaz (logo "x", büyük metin 3.9:1) |

Birincil renk beyaz metinle 4.53:1 verir; 14px düğme metni için AA sınırındadır, bu yüzden düğme metni 600 ağırlıktadır ve daha açık bir mavi birincil olarak kullanılmaz.

## Yüzey ve metin

| Token | Açık | Koyu |
|---|---|---|
| `--mx-background` | `#F5F8FC` | `#0B1322` |
| `--mx-surface` | `#FFFFFF` | `#121D33` |
| `--mx-surface-muted` | `#EEF5FA` (seçili menü, hafif vurgu) | `#192842` |
| `--mx-surface-control` | `#E6EDF5` (ikincil düğme dolgusu) | `#1E2F4D` |
| `--mx-surface-hover` | `#F3F7FB` | `#16233B` |
| `--mx-border` | `#DCE5F0` | `#243654` |
| `--mx-border-strong` | `#B9C8DA` | `#3A5078` |
| `--mx-text-primary` | `#101D35` | `#E6EDF7` |
| `--mx-text-secondary` | `#5D6B81`* | `#9FB0C8` |
| `--mx-text-label` | `#2B3A55` | `#C9D4E5` |
| `--mx-text-on-brand` | `#FFFFFF` | `#07111F` |

\* Referanstaki `#64748B` sayfa zemininde 4.47:1, seçili menü zemininde 4.32:1 kontrast verdiği için WCAG AA'yı (4.5:1) sağlayan en yakın ton `#5D6B81` seçildi (sayfa zemininde 5.1:1, seçili menü zemininde 4.9:1).

## Durum renkleri (semantik, marka renklerinden ayrı)

`--mx-success #10B981`, `--mx-warning #F59E0B`, `--mx-danger #EF4444`, `--mx-info #0EA5E9`, metin olarak hata için `--mx-danger-text`.

Bu doygun renkler küçük metinde kullanılmaz. Rozet ve uyarılarda açık zemin + koyu metin çiftleri kullanılır:

| Ton | Zemin / metin (açık) | Kontrast |
|---|---|---|
| `--mx-tone-neutral-*` | `#EEF2F7` / `#475569` | 6.7:1 |
| `--mx-tone-brand-*` | `#E6F2FA` / `#0B5C87` | 6.4:1 |
| `--mx-tone-info-*` | `#E0F2FE` / `#075985` | AA |
| `--mx-tone-success-*` | `#D1FAE5` / `#065F46` | 6.8:1 |
| `--mx-tone-warning-*` | `#FEF3C7` / `#92400E` | 6.4:1 |
| `--mx-tone-danger-*` | `#FEE2E2` / `#991B1B` | 6.8:1 |
| `--mx-tone-violet-*` | `#EDE9FE` / `#5B21B6` | 7.6:1 |
| `--mx-tone-teal-*` | `#DDF6F7` / `#0B6368` | 6.2:1 |

Aktivite aileleri bu tonlara bağlıdır (`fam-*` sınıfları `--fam-bg` / `--fam-fg` verir): görev → teal, arama → violet, toplantı → info, ziyaret → warning, e-posta → success, meşgul (özel) → neutral.

Koyu temada her ton koyu zemin + açık metin olarak yeniden tanımlıdır (en düşük 8:1 civarı; marka tonu `#0E3652` / `#9EDCFF` 8.5:1).

## Tipografi

Yazı tipi **Manrope** (OFL lisanslı, değişken ağırlık 400–800). Dosyalar `wwwroot/fonts/` altından yerel sunulur (`manrope-latin.woff2`, `manrope-latin-ext.woff2`; `latin-ext` Türkçe karakterleri ve ₺ simgesini içerir), `@font-face` bildirimi `app.css` başındadır; dış CDN'e bağımlılık yoktur (on-prem uyumlu). Yüklenemezse Segoe UI / system-ui'ye düşer. Önceki yazı tipi Inter'di (31.10.2026 → seçenek 4 kararı).

| Token | Değer | Kullanım |
|---|---|---|
| `--mx-fs-page-title` | 28px / 700 | `h1` |
| `--mx-fs-section-title` | 18px / 650 | `h2` |
| `--mx-fs-card-title` | 16px / 600 | `h3` |
| `--mx-fs-body` | 14px | gövde, buton, input |
| `--mx-fs-label` | 13px / 600 | `form-label` |
| `--mx-fs-helper` | 12px | yardım metni, doğrulama, rozet, tablo başlığı |
| `--mx-fs-table` | 14px | tablo hücresi |
| `--mx-fs-kpi` | 24px / 700 | KPI/özet değerleri |
| `--mx-fs-hero` | 44px / 800 | pano bandının ana rakamı (telefonda 34px) |

## Boşluk, köşe, gölge

- Boşluk (4px tabanlı): `--mx-space-1` 4px · `-2` 8px · `-3` 12px · `-4` 16px · `-5` 20px · `-6` 24px · `-8` 32px.
- Köşe: input 9px, button 10px, card 12px, drawer 16px, modal 16px, badge hap (999px) (`--mx-radius-*`).
- Gölge: `--mx-shadow-control` (etkin sekme gibi küçük denetimler), `--mx-shadow-card` (kart), `--mx-shadow-raised` (hover), `--mx-shadow-popover` (açılır menü/panel): lacivert tabanlı, yumuşak. Parlama gölgesi (`--mx-shadow-glow`) 12.11.2026'da kaldırıldı; düğmeler `--mx-shadow-control` / `--mx-shadow-raised` kullanır.
- Birincil eylem (CTA, 12.11.2026 görsel kararı): `--mx-gradient-cta` (açık `#0A8093` → `#0B6AA6`, beyaz metinle iki uçta da ≥4.5:1; koyu `#2DD4D8` → `#4DB4F2` + koyu metin), `--mx-gradient-cta-hover`, ışıma `--mx-shadow-cta` / `--mx-shadow-cta-hover`. `.btn-primary`, bildirim sayacı bunu kullanır. Etkin sekme çizgisi `--mx-gradient-tab-line` (turkuaz→mavi).
- Kabuk yüzeyleri: `--mx-panel-bg` (içerik paneli degradesi), `--mx-panel-border`, `--mx-radius-panel` (24px), `--mx-shadow-panel`. Kart köşesi `--mx-radius-card` 16px'tir.
- Düğme renkleri: `--mx-fill-primary` artık birincil düğmede kullanılmaz (eski çizgili stil kalktı; token geriye uyumluluk için kalır); `--mx-fill-danger` yıkıcı onay düğmesinin dolgusudur. Değerler: `--mx-fill-primary` (açık temada `--mx-brand-navy` `#101D35`, hover `--mx-brand-navy-light`; koyu temada `#E6EDF7`, hover `#FFFFFF`) ve `--mx-fill-danger` (`#DC2626`, hover `#B91C1C`; koyu temada `#F87171`, hover `#FCA5A5`). Metin `--mx-text-on-brand` (beyaz / `#07111F`); kontrast AA (en düşük: beyaz üstünde `#DC2626` yaklaşık 4,8:1).
- Marka degradesi: `--mx-gradient-brand` (`#087DB8` → `#1F5FCB`; koyu temada `#3BAEE6` → `#6FA8FA` + koyu metin) etkin sekme çizgisi ve sayaç rozeti, etkin çip, bölüm kartı ikonu için; `--mx-gradient-brand-hover` hover durumu; `--mx-nav-active-bg` seçili menü öğesi. Pano bandı için `--mx-gradient-hero` (açık sağ uç `#0A5F8F`, koyu `#125580`; ikincil metin `--mx-text-on-hero-muted` ile AA), kutular `--mx-hero-tile` / `--mx-hero-tile-hover`. Beyaz metinle en düşük kontrast 4.5:1'dir (AA); bu yüzden açık uç `#087DB8`'den açık tutulmaz.
- Pano göstergesi (`dash-hero`, `GoalMeter`): bant `--mx-gradient-hero` ve `--mx-text-on-hero*` tokenlarını kullanır. `--mx-hero-track` gösterge zemini, `--mx-hero-line` ayırıcı, `--mx-hero-fill-realized` ve `--mx-hero-fill-weighted` gösterge dolguları (zeminle en az 3:1). Bant parlama gölgesi almaz.
- Odak: `--mx-focus-ring` (3px, birincil rengin %28'i).
- Kabuk: `--mx-header-height` 64px, `--mx-sidebar-width` 248px, `--mx-sidebar-collapsed-width` 72px, `--mx-control-height` 36px (dokunmatik ekranda 44px; `btn-sm` 30px, dokunmatikte 36px).

## Grafik serileri

`--chart-c0` … `--chart-c5`: mavi, turkuaz, amber, lacivert, kırmızı, gri. Koyu temada açık tonları tanımlıdır.

## Geriye uyumluluk

Eski adlar (`--mx-indigo`, `--mx-lilac`, `--mx-cream`, `--mx-ink`, `--mx-muted`, `--mx-teal`, `--mx-radius`, `--mx-shadow`…) yeni tokenlara takma addır; mevcut CSS çalışmaya devam eder. Adları eski paletten kalmadır (`--mx-indigo` artık birincil maviyi, `--mx-lilac` seçili menü zeminini verir). Yeni kodda kullanılmaz, dokunulan kurallarda yeni adlara geçilir.
