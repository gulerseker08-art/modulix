# Bileşen kataloğu

Durum: **Var** = kullanıma hazır, **Stil** = ayrı Razor bileşeni yok, ortak CSS sınıfıyla standart, **Yok** = henüz geliştirilmedi (bkz. `migration-report.md`).

## Kabuk ve gezinme

| Bileşen | Durum | Nerede / nasıl |
|---|---|---|
| AppShell | Var | `Layout/MainLayout.razor`: sol menü + üst çubuk + `main#main-content`; "İçeriğe geç" bağlantısı |
| AppHeader | Var | MainLayout üst çubuğu: tenant logosu, ortada genel arama, şirket seçici, tema düğmesi, profil menüsü (avatar, ad, kuruluş) |
| Sidebar | Var | `Layout/NavMenu.razor`: aktif modülün sayfaları (AppCatalog + izin süzgeci), aktif öğede vurgu çizgisi, Menüyü Daralt |
| ModuleSwitcher | Var | `Components/AppLauncher.razor` (logoya tıklanınca "Tüm Uygulamalar" paneli) |
| BrandLogo | Var | `Components/BrandLogo.razor` — `Size`, `ShowTagline` |
| ThemeToggle | Var | `Components/ThemeToggle.razor` |
| PageHeader | Var | `Components/PageHeader.razor` — `Title`, `Icon`, `Subtitle`, aksiyonlar `ChildContent` |
| Breadcrumb | Stil | `<ol class="breadcrumb-trail">`; bugün detay sayfaları `back-link` kullanır |
| Global arama | Var | `Components/GlobalSearch.razor`: üst çubukta "Arama yapın..." kutusu; 2+ karakterde 300ms gecikmeyle firma, kişi, fırsat, lead ve teklif liste uç noktalarını (`?search=&pageSize=5`) yalnızca izin olan türler için paralel çağırır; sonuçlar gruplu açılır panelde (ok tuşları, Enter, Escape, dışarı tıklama). Ayrı genel arama uç noktası yoktur; tenant, yetki ve kapsam süzgeçleri sunucudaki liste uç noktalarındadır |
| NotificationCenter | Yok | Bildirim altyapısı yok |

## Temel

| Bileşen | Durum | Nerede / nasıl |
|---|---|---|
| ViewSelector | Var | Liste görünüm seçici: `<ViewSelector Items="…" Value="…" ValueChanged="…" />`: "Görünüm: Ad ▾" düğmesi + açılır menü (`role=menu`, `menuitemradio`, seçili öğede onay işareti, Esc ve dışarı tıklama kapatır). `Items` = `ViewOption(Key, Label, Group?)`; aynı `Group` başlık altında toplanır. `NewView` verilirse menü sonuna "+ Yeni görünüm" eylemi eklenir. Araç çubuğu: `<div class="list-toolbar">` içinde seçici + `form.search` + birincil eylem. Örnekler: `Leads`, `Contracts`, `Orders`, `OperationJobs`, `Tickets`. Görünümler yalnızca süzgeçtir; yetki sunucuda uygulanır. |
| Button | Stil | `btn btn-primary` (birincil: marka degradesi + parlama), `btn-outline-secondary` (ikincil/İptal), `btn-tonal` (üçüncül, pastel), `btn-ghost` (araç çubuğu: Filtrele, Sütunlar), `btn-outline-danger` (yıkıcı), `btn-sm` |
| IconButton | Stil | `<button class="icon-button" aria-label="…">` + `AppIcon` |
| Badge / StatusBadge | Stil + Var | `badge` + `stage-*`, `status-*`, `user-*` tonları; `TenantStateBadge`, `UserStatusBadge` |
| Card / SectionCard | Var | `Components/FormSection.razor` — ikon başlıklı bölüm kartı (`Title`, `Icon`, `Description`, `Actions`, `Class`); form ve detay bölümleri bununla kurulur. Eski stiller: `record-form`, `detail-section`, `detail-grid`, `chart-card`, `tile` |
| Kayıt düzeni + özet kartı | Stil | `record-layout` (solda `record-summary` > `summary-card`: `summary-identity`, `summary-facts`, `summary-numbers`, `summary-contact`; sağda `record-main`) — Firma ve Kişi detayı |
| Detay düzeni + sağ panel | Stil | `detail-layout` (`detail-main` + yapışkan `detail-side` > `side-card`) — Fırsat detayı |
| SummaryCard / KPIStatCard | Stil | `summary` + `summary-item`, `stat-grid` + `stat-card` (+ `stat-icon`); tıklanabilir KPI: `kpi-grid` + `button.stat-card.kpi-card.tone-*` |
| Tabs (kayıt sekmeleri) | Var | `Components/TabBar.razor` + `TabState` (`TabItem(Key, Label, Count)`): sekme ilk seçilişte çizilir, sonra gizli kalır (kaydedilmemiş form kaybolmaz); paneller `role="tabpanel"`, `hidden` |
| Segmented / chip | Stil | `filters` + `chip` (`aria-pressed`), `segmented` |
| Accordion | Stil | `sub-section` + `section-toggle` |
| Notice / Toast | Var | `Components/Notice.razor` (sayfa içi, `role="status"`/`alert`). Merkezi toast kuyruğu yok |
| ConfirmationDialog | Stil | `confirm` satır içi onay deseni |
| EmptyState / ErrorState / Skeleton | Kısmen | `app-state`, `alert`; ortak bileşen yok |
| Drawer | Var | `Components/Drawer.razor` — `Open`, `Label`, `OnClose`; Escape ve arka plan tıklaması kapatır, açılınca odak panele geçer, telefonda tam ekran. İçindeki `record-form`/`detail-section` çerçevesiz çizilir, `form-actions` alta yapışır |
| Modal | Yok | |
| Tooltip / DropdownMenu | Stil | `title` ipucu; profil menüsü `menu-list` deseni |

## Form

| Bileşen | Durum | Not |
|---|---|---|
| TextField, EmailField, PhoneField, URLField, NumberField, TextArea | Stil | `form-control` + uygun `type`/`inputmode` |
| CurrencyField, PercentageField | Stil | `form-control` + `inputmode="decimal"`; biçim `Services/Regional.cs` |
| DateField, DateTimeField, TimeField | Stil | `type="date" / "datetime-local" / "time"` |
| Select | Stil | `form-select`; seçim listeleri `CrmLookups.Choices` |
| SearchableLookup / RecordPicker | Var | `Components/LookupPicker.razor` + `js/lookup.js`: sunucu taraflı arama, debounce, klavye, temizleme, kayda gitme; aranabilir alanlar `LookupSettings` (metadata) |
| Checkbox / Switch / RadioGroup | Stil | `form-check`, `form-check-input` |
| UserPicker / TeamPicker | Kısmen | Kayıt sahibi/ekip seçimleri ilgili formlarda select olarak |
| AddressField | Var | `AddressesSection.razor` |
| Dinamik (özel) alanlar | Var | `CustomFieldInputs.razor` — aynı form sınıflarıyla çizilir; `Only` ile tek alan (form düzeni içinde) |
| Metadata'dan form (LayoutForm) | Var | `Components/LayoutForm.razor` — `Layout` (`FormLayoutDto`), `Field` şablonu, `Shows`, özel alanlar; sekme / bölüm / alan düzeni firmanın form düzeninden (ADR-0021). Yönetim: `Pages/Admin/FormDesigner.razor` |
| FieldError | Var | Alanın altında doğrulama iletisi |
| FileUpload, RichTextEditor, MultiLookup | Yok | |

## Veri

| Bileşen | Durum | Not |
|---|---|---|
| DataGrid | Stil | `table record-table` + `Pager` + `filters`; telefonda kart listesi. Sunucu taraflı sayfalama/süzme mevcut uç noktalardan |
| Kolon gizleme/sıralama/yeniden boyutlandırma, kayıtlı görünüm, toplu işlem | Kısmen/Yok | Toplu işlem yalnız Aktiviteler'de |
| Satış hunisi | Var | `SalesPipeline.razor` — `Stages`, `Current`, `Status`, `CanChangeStage`, `CanClose`, `OnChoose` |
| Takvim | Var | `ActivityCalendar.razor`: ay ızgarası, hafta/gün saat çizelgesi (`cal-time-*`, çakışanlar yan yana, şimdiki zaman çizgisi), ajanda; renk `FamilyOf` → `fam-*` |
| Günün ajandası | Var | `TodayActivities.razor` |
| Grafikler | Var | `BarChart.razor`, `DonutChart.razor` (`--chart-c*`) |
