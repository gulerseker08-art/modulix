# Tema tokenları

Kaynak: `src/Modulix.Web/wwwroot/css/app.css` başındaki `:root, [data-theme="light"]` ve `[data-theme="dark"]` blokları. Bileşenler yalnızca bu adları kullanır.

## Marka renkleri

| Token | Açık | Koyu | Kullanım |
|---|---|---|---|
| `--mx-brand-navy` | `#101D35` | — | Ana marka rengi, logo yazısı, aktif modül başlığı |
| `--mx-brand-navy-light` | `#173B68` | — | Menü vurgusu, avatar |
| `--mx-brand-primary` | `#087DB8` | `#3AA6DE` | Birincil buton, bağlantı, odak, aktif sekme |
| `--mx-brand-primary-hover` | `#066A9C` | `#63BAE6` | Hover/active |
| `--mx-brand-teal` | `#00C2C7` | `#2DD4D8` | Koyu zeminde marka vurgusu (aktif modül ikonu) |
| `--mx-brand-teal-dark` | `#008F98` | `#2DC3C9` | Açık zeminde erişilebilir turkuaz (logo "x") |

## Yüzey ve metin

| Token | Açık | Koyu |
|---|---|---|
| `--mx-background` | `#F5F8FC` | `#0B1424` |
| `--mx-surface` | `#FFFFFF` | `#111D33` |
| `--mx-surface-muted` | `#EEF5FA` | `#17284A` |
| `--mx-surface-hover` | `#F3F7FB` | `#15243F` |
| `--mx-border` | `#DCE5F0` | `#233657` |
| `--mx-border-strong` | `#B8C6D8` | `#36507A` |
| `--mx-text-primary` | `#101D35` | `#E6EDF7` |
| `--mx-text-secondary` | `#5F6E84`* | `#9AABC3` |
| `--mx-text-label` | `#334155` | `#C3CFE0` |
| `--mx-text-on-brand` | `#FFFFFF` | `#06121F` |

\* Referans değer `#64748B`, sayfa zemininde 4.47:1 kontrast verdiği için WCAG AA'yı (4.5:1) sağlayan en yakın ton `#5F6E84` seçildi.

## Durum renkleri (semantik, marka renklerinden ayrı)

`--mx-success #10B981`, `--mx-warning #F59E0B`, `--mx-danger #EF4444`, `--mx-info #0EA5E9`, metin olarak hata için `--mx-danger-text`.

Bu doygun renkler küçük metinde kullanılmaz. Rozet ve uyarılarda açık zemin + koyu metin çiftleri kullanılır:

| Ton | Zemin / metin (açık) | Kontrast |
|---|---|---|
| `--mx-tone-neutral-*` | `#EEF2F7` / `#475569` | 6.7:1 |
| `--mx-tone-brand-*` | `#E3F1F9` / `#075E8A` | 6.1:1 |
| `--mx-tone-info-*` | `#E0F2FE` / `#075985` | AA |
| `--mx-tone-success-*` | `#D1FAE5` / `#065F46` | 6.8:1 |
| `--mx-tone-warning-*` | `#FEF3C7` / `#92400E` | 6.4:1 |
| `--mx-tone-danger-*` | `#FEE2E2` / `#991B1B` | 6.8:1 |

Koyu temada her ton koyu zemin + açık metin olarak yeniden tanımlıdır (en düşük 8:1 civarı).

## Tipografi

Yazı tipi **Inter** (Google Fonts, `latin-ext` alt kümesi Türkçe karakterleri içerir; OFL lisanslı). Yüklenemezse Segoe UI / system-ui'ye düşer.

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

## Boşluk, köşe, gölge

- Boşluk (4px tabanlı): `--mx-space-1` 4px · `-2` 8px · `-3` 12px · `-4` 16px · `-5` 20px · `-6` 24px · `-8` 32px.
- Köşe: input 8px, button 9px, card 12px, drawer 14px, modal 14px, badge 6px (`--mx-radius-*`).
- Gölge: `--mx-shadow-card` (kart), `--mx-shadow-raised` (hover), `--mx-shadow-popover` (açılır menü/panel). Glow ve ağır gölge yok.
- Odak: `--mx-focus-ring` (3px, birincil rengin %28'i).
- Kabuk: `--mx-header-height` 64px, `--mx-sidebar-width` 248px, `--mx-sidebar-collapsed-width` 72px, `--mx-control-height` 40px (dokunmatik ekranda 44px).

## Grafik serileri

`--chart-c0` … `--chart-c5`: birincil mavi, turkuaz, amber, lacivert, kırmızı, gri. Koyu temada açık tonları tanımlıdır.

## Geriye uyumluluk

Eski adlar (`--mx-indigo`, `--mx-lilac`, `--mx-cream`, `--mx-ink`, `--mx-muted`, `--mx-teal`, `--mx-radius`, `--mx-shadow`…) yeni tokenlara takma addır; mevcut CSS çalışmaya devam eder. Yeni kodda kullanılmaz, dokunulan kurallarda yeni adlara geçilir.
