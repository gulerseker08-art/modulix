# Tema tokenları

Kaynak: `src/Modulix.Web/wwwroot/css/app.css` başındaki `:root, [data-theme="light"]` ve `[data-theme="dark"]` blokları. Bileşenler yalnızca bu adları kullanır.

## Marka renkleri

Modulix varsayılan paleti **lacivert / indigo / turkuaz / lila / krem**'dir (yol haritası K6 ve Ç15a.1). Logo işareti (`modulix-mark.svg`) aynı paleti taşır.

| Token | Açık | Koyu | Kullanım |
|---|---|---|---|
| `--mx-brand-navy` | `#111827` | — | Lacivert: logo yazısı, aktif modül başlığı |
| `--mx-brand-navy-light` | `#1F2937` | — | Aktif modül başlığı gradyanı, avatar |
| `--mx-brand-primary` | `#4F46E5` | `#818CF8` | İndigo: birincil buton, bağlantı, odak, aktif sekme |
| `--mx-brand-primary-hover` | `#4338CA` | `#A5B4FC` | Hover/active |
| `--mx-brand-teal` | `#10B981` | `#34D399` | Koyu zeminde marka vurgusu (aktif modül ikonu) |
| `--mx-brand-teal-dark` | `#0F8F7F` | `#2DD4BF` | Açık zeminde erişilebilir turkuaz (logo "x", büyük metin 4.0:1) |

## Yüzey ve metin

| Token | Açık | Koyu |
|---|---|---|
| `--mx-background` | `#F4F6FB` (krem) | `#0B0F1F` |
| `--mx-surface` | `#FFFFFF` | `#131730` |
| `--mx-surface-muted` | `#EEF0FF` (lila) | `#1A1F3D` |
| `--mx-surface-hover` | `#F1F3FB` | `#181D38` |
| `--mx-border` | `#E8EBF3` | `#272D50` |
| `--mx-border-strong` | `#C5CBE0` | `#3D4573` |
| `--mx-text-primary` | `#111827` | `#E5E7F3` |
| `--mx-text-secondary` | `#5F6B7E`* | `#9CA3C0` |
| `--mx-text-label` | `#374151` | `#C5C9E0` |
| `--mx-text-on-brand` | `#FFFFFF` | `#0B0F1F` |

\* Eski değer `#6B7280`, krem zeminde 4.47:1 kontrast verdiği için WCAG AA'yı (4.5:1) sağlayan en yakın ton `#5F6B7E` seçildi (krem zeminde 5.0:1, lila zeminde 4.8:1).

## Durum renkleri (semantik, marka renklerinden ayrı)

`--mx-success #10B981`, `--mx-warning #F59E0B`, `--mx-danger #EF4444`, `--mx-info #0EA5E9`, metin olarak hata için `--mx-danger-text`.

Bu doygun renkler küçük metinde kullanılmaz. Rozet ve uyarılarda açık zemin + koyu metin çiftleri kullanılır:

| Ton | Zemin / metin (açık) | Kontrast |
|---|---|---|
| `--mx-tone-neutral-*` | `#EEF2F7` / `#475569` | 6.7:1 |
| `--mx-tone-brand-*` | `#EEF0FF` / `#3730A3` | 8.8:1 |
| `--mx-tone-info-*` | `#E0F2FE` / `#075985` | AA |
| `--mx-tone-success-*` | `#D1FAE5` / `#065F46` | 6.8:1 |
| `--mx-tone-warning-*` | `#FEF3C7` / `#92400E` | 6.4:1 |
| `--mx-tone-danger-*` | `#FEE2E2` / `#991B1B` | 6.8:1 |
| `--mx-tone-violet-*` | `#EDE9FE` / `#5B21B6` | 7.6:1 |
| `--mx-tone-teal-*` | `#DDF6F6` / `#0F5F63` | 6.6:1 |

Aktivite aileleri bu tonlara bağlıdır (`fam-*` sınıfları `--fam-bg` / `--fam-fg` verir): görev → teal, arama → violet, toplantı → info, ziyaret → warning, e-posta → success, meşgul (özel) → neutral.

Koyu temada her ton koyu zemin + açık metin olarak yeniden tanımlıdır (en düşük 8:1 civarı).

## Tipografi

Yazı tipi **Public Sans** (Google Fonts, `latin-ext` alt kümesi Türkçe karakterleri içerir; OFL lisanslı). Yüklenemezse Segoe UI / system-ui'ye düşer.

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

`--chart-c0` … `--chart-c5`: indigo, turkuaz, amber, lacivert, kırmızı, gri. Koyu temada açık tonları tanımlıdır.

## Geriye uyumluluk

Eski adlar (`--mx-indigo`, `--mx-lilac`, `--mx-cream`, `--mx-ink`, `--mx-muted`, `--mx-teal`, `--mx-radius`, `--mx-shadow`…) yeni tokenlara takma addır; mevcut CSS çalışmaya devam eder. Yeni kodda kullanılmaz, dokunulan kurallarda yeni adlara geçilir.
